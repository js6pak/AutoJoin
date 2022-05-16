using System;
using System.Collections;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.IL2CPP;
using BepInEx.IL2CPP.Utils;
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
        public ConfigEntry<bool> RequireArgument;
        public Harmony Harmony { get; } = new(Id);

        public override void Load()
        {
            RequireArgument = Config.Bind("Features", "Require Command Line Argument", false, "Only auto join if the --auto-join command line argument is present");

            Harmony.PatchAll();
        }

        [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.Start))]
        public static class ConnectPatch
        {
            private static int? _code;

            public static void Postfix(InnerNetClient __instance)
            {
                if (PluginSingleton<AutoJoinPlugin>.Instance.RequireArgument.Value && !Il2CppSystem.Environment.GetCommandLineArgs().Contains("--auto-join")) return;
                if (_code != null) return;

                __instance.StartCoroutine(CoConnect());
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
                    var commandLineArgs = Il2CppSystem.Environment.GetCommandLineArgs();
                    var mapArgIdx = commandLineArgs.IndexOf("--map");
                    if (mapArgIdx != -1 && commandLineArgs.Length > mapArgIdx + 1)
                    {
                        var mapName = commandLineArgs[mapArgIdx + 1];
                        switch (mapName)
                        {
                            case "skeld":
                                SaveManager.GameHostOptions.MapId = 0;
                                break;
                            case "mira":
                                SaveManager.GameHostOptions.MapId = 1;
                                break;
                            case "polus":
                                SaveManager.GameHostOptions.MapId = 2;
                                break;
                            case "airship":
                                SaveManager.GameHostOptions.MapId = 4;
                                break;
                            case "submerged":
                                SaveManager.GameHostOptions.MapId = 5;
                                break;
                        }
                    }

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
