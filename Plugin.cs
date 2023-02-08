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
    [BepInIncompatibility("com.odinplusqol.mod")]
    [BepInIncompatibility("org.bepinex.plugins.valheim_plus")]
    public class MaxPlayerCountPlugin : BaseUnityPlugin
    {
        internal const string ModName = "MaxPlayerCount";
        internal const string ModVersion = "1.0.5";
        internal const string Author = "Azumatt";
        private const string ModGUID = Author + "." + ModName;
        private static string ConfigFileName = ModGUID + ".cfg";
        private static string ConfigFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;

        private readonly Harmony _harmony = new(ModGUID);

        private static readonly ManualLogSource MaxPlayerCountLogger =
            BepInEx.Logging.Logger.CreateLogSource(ModName);

        public void Awake()
        {
            _maxPlayers = config("1 - General", "MaxPlayerCount", 10,
                "Override the player count that valheim checks for. Default is the vanilla max of 10.");

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

        [HarmonyPatch(typeof(SteamGameServer), nameof(SteamGameServer.SetMaxPlayerCount))]
        public static class ChangeSteamServerVariables
        {
            private static void Prefix(ref int cPlayersMax)
            {
                int maxPlayers = _maxPlayers.Value;
                if (maxPlayers >= 1) cPlayersMax = maxPlayers;
            }
        }


        [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo))]
        internal class MaxPlayersCount
        {
            [HarmonyTranspiler]
            static IEnumerable<CodeInstruction> MaxPlayersPatch(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode == OpCodes.Call && codes[i].operand is MethodInfo method &&
                        method.Name == "GetNrOfPlayers")
                    {
                        for (int j = i; j < codes.Count; j++)
                        {
                            if (codes[j].opcode == OpCodes.Ldc_I4_S)
                            {
                                codes[j].operand = ReplacePlayerLimit();
                                break;
                            }
                        }

                        break;
                    }
                }

                return codes.AsEnumerable();
            }

            private static int ReplacePlayerLimit() => _maxPlayers.Value;
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
                "# Acceptable values: " + string.Join(", ", KeyboardShortcut.AllKeyCodes);
        }

        #endregion
    }
}