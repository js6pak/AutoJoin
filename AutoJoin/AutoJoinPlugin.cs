using System;
using System.Collections;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.IL2CPP;
using HarmonyLib;
using InnerNet;
using Reactor;

namespace AutoJoin
{
    [BepInAutoPlugin]
    [BepInProcess("Among Us.exe")]
    [BepInDependency(ReactorPlugin.Id)]
    public partial class AutoJoinPlugin : BasePlugin
    {
        public Harmony Harmony { get; } = new(Id);

        public override void Load()
        {
            Harmony.PatchAll();
        }

        [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.Start))]
        public static class ConnectPatch
        {
            private static int? _code;

            public static void Postfix()
            {
                Coroutines.Start(CoConnect());
            }

            private static IEnumerator CoConnect()
            {
                bool isHost;
                var pipeClient = new NamedPipeClientStream(".", nameof(AutoJoin), PipeDirection.In);

                try
                {
                    pipeClient.Connect(100);
                    Logger<AutoJoinPlugin>.Info("Joined session");
                    isHost = false;
                }
                catch (TimeoutException)
                {
                    Logger<AutoJoinPlugin>.Info("No session found");
                    pipeClient.Dispose();
                    isHost = true;
                }

                if (isHost)
                {
                    Task.Run(async () =>
                    {
                        while (true)
                        {
                            await using var pipeServer = new NamedPipeServerStream(nameof(AutoJoin), PipeDirection.Out);

                            await pipeServer.WaitForConnectionAsync();

                            while (_code == null)
                            {
                                await Task.Yield();
                            }

                            await using var binaryWriter = new BinaryWriter(pipeServer);
                            binaryWriter.Write(_code.Value);
                            Logger<AutoJoinPlugin>.Info("Client connected to the session");
                        }
                    });
                }
                else
                {
                    using var binaryReader = new BinaryReader(pipeClient);
                    _code = binaryReader.ReadInt32();
                    pipeClient.Close();
                    pipeClient.Dispose();
                }

                AmongUsClient.Instance.GameMode = GameModes.OnlineGame;
                AmongUsClient.Instance.SetEndpoint(ServerManager.Instance.OnlineNetAddress, ServerManager.Instance.OnlineNetPort);
                AmongUsClient.Instance.MainMenuScene = "MMOnline";
                AmongUsClient.Instance.OnlineScene = "OnlineGame";

                if (isHost)
                {
                    AmongUsClient.Instance.GameId = 0;
                    AmongUsClient.Instance.Connect(MatchMakerModes.HostAndClient);

                    while (AmongUsClient.Instance.GameId is 0 or 32)
                    {
                        yield return null;
                    }

                    _code = AmongUsClient.Instance.GameId;
                    Logger<AutoJoinPlugin>.Info($"Hosting {GameCode.IntToGameName(_code.Value)}");
                }
                else
                {
                    AmongUsClient.Instance.GameId = _code!.Value;
                    Logger<AutoJoinPlugin>.Info($"Joining {GameCode.IntToGameName(_code.Value)}");
                    AmongUsClient.Instance.Connect(MatchMakerModes.Client);
                }
            }
        }
    }
}
