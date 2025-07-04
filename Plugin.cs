using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using JetBrains.Annotations;
using Steamworks;

namespace MaxPlayerCount
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    [BepInIncompatibility("org.bepinex.plugins.valheim_plus")]
    public class MaxPlayerCountPlugin : BaseUnityPlugin
    {
        internal const string ModName = "MaxPlayerCount";
        internal const string ModVersion = "1.2.4";
        internal const string Author = "Azumatt";
        private const string ModGUID = Author + "." + ModName;
        private static string ConfigFileName = ModGUID + ".cfg";
        private static string ConfigFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;
        private readonly Harmony _harmony = new(ModGUID);
        public static MaxPlayerCountPlugin instance = null!;
        private static readonly ManualLogSource MaxPlayerCountLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);

        public void Awake()
        {
            instance = this;
            _maxPlayers = config("1 - General", "MaxPlayerCount", 20, "Override the player count that valheim checks for. Default is the vanilla max of 10.");

            Assembly assembly = Assembly.GetExecutingAssembly();
            _harmony.PatchAll(assembly);
            SetupWatcher();
        }

        private void OnDestroy()
        {
            Config.Save();
        }

        private void SetupWatcher()
        {
            FileSystemWatcher watcher = new(Paths.ConfigPath, ConfigFileName);
            watcher.Changed += ReadConfigValues;
            watcher.Created += ReadConfigValues;
            watcher.Renamed += ReadConfigValues;
            watcher.IncludeSubdirectories = true;
            watcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
            watcher.EnableRaisingEvents = true;
        }

        private void ReadConfigValues(object sender, FileSystemEventArgs e)
        {
            if (!File.Exists(ConfigFileFullPath)) return;
            try
            {
                MaxPlayerCountLogger.LogDebug("ReadConfigValues called");
                Config.Reload();
            }
            catch
            {
                MaxPlayerCountLogger.LogError($"There was an issue loading your {ConfigFileName}");
                MaxPlayerCountLogger.LogError("Please check your config entries for spelling and format!");
            }
        }


        [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo))]
        internal class MaxPlayersCount
        {
            [HarmonyTranspiler]
            static IEnumerable<CodeInstruction> MaxPlayersPatch(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);
                for (int i = 0; i < codes.Count; ++i)
                {
                    if (codes[i].opcode == OpCodes.Call && codes[i].operand is MethodInfo method && method.Name == "GetNrOfPlayers")
                    {
                        for (int j = i; j < codes.Count; ++j)
                        {
                            if (codes[j].opcode == OpCodes.Ldc_I4_S)
                            {
#if DEBUG
                                MaxPlayerCountLogger.LogDebug($"Steam/Playfab ZNet.RPC_PeerInfo: Patching player limit {codes[j].operand.ToString()} to {_maxPlayers.Value}");
#endif
                                codes[j].operand = ReplacePlayerLimit();
#if DEBUG
                                MaxPlayerCountLogger.LogDebug($"Steam/Playfab ZNet.RPC_PeerInfo: Changed to {codes[j].operand.ToString()}");
#endif
                                break;
                            }
                        }

                        break;
                    }
                }

                return codes.AsEnumerable();
            }

            internal static int ReplacePlayerLimit(bool playfab = false) => playfab ? _maxPlayers.Value + 1 : _maxPlayers.Value;
        }

        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.Start))]
        public static class FejdStartupPatch
        {
            static void Postfix(FejdStartup __instance)
            {
                MaxPlayerCountLogger.LogInfo($"Patching for backend: {ZNet.m_onlineBackend.ToString()}");
                switch (ZNet.m_onlineBackend)
                {
                    case OnlineBackendType.PlayFab:
                        instance._harmony.Patch(AccessTools.DeclaredMethod(typeof(ZPlayFabMatchmaking), nameof(ZPlayFabMatchmaking.CreateLobby)),
                            transpiler: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(FejdStartupPatch), nameof(MaxPlayerPlayfabTranspiler))));
                        instance._harmony.Patch(AccessTools.DeclaredMethod(typeof(ZPlayFabMatchmaking), nameof(ZPlayFabMatchmaking.CreateAndJoinNetwork)),
                            transpiler: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(FejdStartupPatch), nameof(MaxPlayerPlayfabTranspiler2))));
                        break;
                    case OnlineBackendType.Steamworks:
                        instance._harmony.Patch(AccessTools.DeclaredMethod(typeof(SteamGameServer), nameof(SteamGameServer.SetMaxPlayerCount)),
                            prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(FejdStartupPatch), nameof(SetMaxPlayerSteamPrefix))));
                        break;
                }
            }

            private static void SetMaxPlayerSteamPrefix(ref int cPlayersMax)
            {
                int maxPlayers = _maxPlayers.Value;
                if (maxPlayers >= 1) cPlayersMax = maxPlayers;
            }

            public static IEnumerable<CodeInstruction> MaxPlayerPlayfabTranspiler(IEnumerable<CodeInstruction> instructions)
            {
                foreach (var instruction in instructions)
                {
#if DEBUG
                    MaxPlayerCountLogger.LogDebug($"Playfab ZPlayfabMatchmaking.CreateLobby: {instruction.opcode} {instruction.operand}");
#endif
                    if (instruction.opcode == OpCodes.Ldc_I4_S && (sbyte)instruction.operand == 11)
                    {
#if DEBUG
                        MaxPlayerCountLogger.LogDebug($"Playfab ZPlayfabMatchmaking.CreateLobby: Patching player limit {instruction.operand.ToString()} to {_maxPlayers.Value}");
#endif
                        CodeInstruction newInstruction = new CodeInstruction(OpCodes.Ldc_I4, MaxPlayersCount.ReplacePlayerLimit());
                        yield return newInstruction;

#if DEBUG
                        MaxPlayerCountLogger.LogDebug($"Playfab ZPlayfabMatchmaking.CreateLobby: Changed to {newInstruction.operand}");
#endif
                    }
                    else
                    {
                        yield return instruction;
                    }
                }
            }

            public static IEnumerable<CodeInstruction> MaxPlayerPlayfabTranspiler2(IEnumerable<CodeInstruction> instructions)
            {
                foreach (var instruction in instructions)
                {
#if DEBUG
                    MaxPlayerCountLogger.LogDebug($"Playfab ZPlayfabMatchmaking.CreateAndJoinNetwork: {instruction.opcode} {instruction.operand}");
#endif
                    if (instruction.opcode == OpCodes.Ldc_I4_S &&
                        (sbyte)instruction.operand == 11) // 10 is the default player limit when looking at the IL code on the client, but for dedicated servers the above prints 11 for where the 10 would be. Changing this to 11 fixes the issue.
                    {
#if DEBUG
                        MaxPlayerCountLogger.LogDebug($"Playfab ZPlayfabMatchmaking.CreateAndJoinNetwork: Patching player limit {instruction.operand.ToString()} to {_maxPlayers.Value}");
#endif
                        CodeInstruction newInstruction = new CodeInstruction(OpCodes.Ldc_I4, MaxPlayersCount.ReplacePlayerLimit(true));
                        yield return newInstruction;

#if DEBUG
                        MaxPlayerCountLogger.LogDebug($"Playfab ZPlayfabMatchmaking.CreateAndJoinNetwork: Changed to {newInstruction.operand}");
#endif
                    }
                    else
                    {
                        yield return instruction;
                    }
                }
            }
        }


        #region ConfigOptions

        private static ConfigEntry<int> _maxPlayers = null!;

        private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description)
        {
            ConfigEntry<T> configEntry = Config.Bind(group, name, value, description);

            return configEntry;
        }

        private ConfigEntry<T> config<T>(string group, string name, T value, string description,
            bool synchronizedSetting = true)
        {
            return config(group, name, value, new ConfigDescription(description));
        }

        private class ConfigurationManagerAttributes
        {
            public bool? Browsable = false;
        }

        class AcceptableShortcuts : AcceptableValueBase
        {
            public AcceptableShortcuts() : base(typeof(KeyboardShortcut))
            {
            }

            public override object Clamp(object value) => value;
            public override bool IsValid(object value) => true;

            public override string ToDescriptionString() =>
                "# Acceptable values: " + string.Join(", ", UnityInput.Current.SupportedKeyCodes);
        }

        #endregion
    }
}