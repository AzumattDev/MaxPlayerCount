using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ServerSync;
using Steamworks;

namespace MaxPlayerCount
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class MaxPlayerCountPlugin : BaseUnityPlugin
    {
        internal const string ModName = "MaxPlayerCount";
        internal const string ModVersion = "1.0.0";
        internal const string Author = "Azumatt";
        private const string ModGUID = Author + "." + ModName;
        private static string ConfigFileName = ModGUID + ".cfg";
        private static string ConfigFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;

        private readonly Harmony _harmony = new(ModGUID);

        private static readonly ManualLogSource MaxPlayerCountLogger =
            BepInEx.Logging.Logger.CreateLogSource(ModName);

        private static readonly ConfigSync ConfigSync = new(ModGUID)
            { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };

        public void Awake()
        {
            ConfigSync.IsLocked = true;
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

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo))]
        static IEnumerable<CodeInstruction> MaxPlayersPatch(IEnumerable<CodeInstruction> instructions)
        {
            var found = false;
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldc_I4_S)
                {
                    MaxPlayerCountLogger.LogDebug("Found Ldc_I4_S, changing the value to " + _maxPlayers.Value);
                    yield return new CodeInstruction(OpCodes.Call, ReplacePlayerLimit);
                    found = true;
                }

                yield return instruction;
            }

            if (found is false)
                MaxPlayerCountLogger.LogError("Cannot find <Stdfld someField> in OriginalType.OriginalMethod");
        }

        private static int ReplacePlayerLimit()
        {
            return _maxPlayers.Value;
        }


        #region ConfigOptions

        private static ConfigEntry<int> _maxPlayers = null!;

        private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description,
            bool synchronizedSetting = true)
        {
            ConfigDescription extendedDescription =
                new(
                    description.Description +
                    (synchronizedSetting ? " [Synced with Server]" : " [Not Synced with Server]"),
                    description.AcceptableValues, description.Tags);
            ConfigEntry<T> configEntry = Config.Bind(group, name, value, extendedDescription);
            //var configEntry = Config.Bind(group, name, value, description);

            SyncedConfigEntry<T> syncedConfigEntry = ConfigSync.AddConfigEntry(configEntry);
            syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

            return configEntry;
        }

        private ConfigEntry<T> config<T>(string group, string name, T value, string description,
            bool synchronizedSetting = true)
        {
            return config(group, name, value, new ConfigDescription(description), synchronizedSetting);
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