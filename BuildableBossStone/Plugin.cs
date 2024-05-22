using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using JetBrains.Annotations;
using ServerSync;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BuildableBossStone
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class BuildableBossStonePlugin : BaseUnityPlugin
    {
        internal const string ModName = "BuildableBossStones";
        internal const string ModVersion = "1.0.5";
        internal const string Author = "RustyMods";
        private const string ModGUID = Author + "." + ModName;
        private static string ConfigFileName = ModGUID + ".cfg";
        private static string ConfigFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;
        internal static string ConnectionError = "";
        private readonly Harmony _harmony = new(ModGUID);
        public static readonly ManualLogSource BuildableBossStoneLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);
        private static readonly ConfigSync ConfigSync = new(ModGUID) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };

        private enum Toggle { On = 1, Off = 0 }

        private static BuildableBossStonePlugin _plugin = null!;
        private static GameObject Root = null!;

        private static Dictionary<string, GameObject> m_registeredBossStones = new();

        public void Awake()
        {
            _plugin = this;
            Root = new GameObject("root");
            Root.SetActive(false);
            DontDestroyOnLoad(Root);
            
            InitConfigs();
            
            Assembly assembly = Assembly.GetExecutingAssembly();
            _harmony.PatchAll(assembly);
            SetupWatcher();
        }

        private void OnDestroy()
        {
            Config.Save();
        }
        
        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
        private static class RegisterBossStones
        {
            private static void Postfix(ZNetScene __instance)
            {
                if (!__instance) return;
                InitBossStones(__instance);
            }
        }
        
        private static void InitBossStones(ZNetScene instance)
        {
            GameObject hammer = instance.GetPrefab("Hammer");
            if (!hammer) return;
            if (!hammer.TryGetComponent(out ItemDrop itemDrop)) return;
            
            GameObject stoneCutter = instance.GetPrefab("piece_stonecutter");
            if (!stoneCutter) return;
            if (!stoneCutter.TryGetComponent(out Piece piece)) return;
            
            foreach (GameObject? prefab in instance.m_prefabs.FindAll(x => x.name.StartsWith("BossStone")))
            {
                if (!prefab.transform.GetChild(0).TryGetComponent(out MeshRenderer renderer)) continue;

                if (!prefab.TryGetComponent(out RuneStone runeStone)) continue;
                var itemStand = prefab.GetComponentInChildren<ItemStand>(true);
                if (!itemStand) continue;
               
                GameObject clone = Instantiate(prefab, Root.transform, false);
                clone.name = "piece_" + prefab.name;

                Piece component = clone.AddComponent<Piece>();

                ConfigEntry<Piece.PieceCategory> categoryConfig = _plugin.config(prefab.name, "Category", Piece.PieceCategory.Misc, "Set category of boss stone");
                component.m_category = categoryConfig.Value;
                categoryConfig.SettingChanged += OnBossStoneConfigChange;
                
                component.m_icon = itemStand.m_supportedItems[0]?.m_itemData.GetIcon();

                ConfigEntry<string> resourceConfig = _plugin.config(prefab.name, "Resources", "Stone:100,Flint:20",
                    "Set the resources to build, [prefab]:[amount]");
                resourceConfig.SettingChanged += OnBossStoneConfigChange;

                List<Piece.Requirement> requirements = new();

                foreach (var data in resourceConfig.Value.Split(','))
                {
                    var info = data.Split(':');
                    if (info.Length != 2) continue;
                    GameObject item = instance.GetPrefab(info[0]);
                    if (!item.TryGetComponent(out ItemDrop resourceDrop)) continue;
                    if (!item) continue;
                    if (!int.TryParse(info[1], out int amount)) continue;
                    requirements.Add(new Piece.Requirement()
                    {
                        m_resItem = resourceDrop,
                        m_recover = true,
                        m_amount = amount
                    });
                }

                component.m_resources = requirements.ToArray();

                ConfigEntry<string> nameConfig = _plugin.config(prefab.name, "Display Name",
                    prefab.name.Replace("BossStone_", "Boss Stone "), "Set the display name of boss stone");
                nameConfig.SettingChanged += OnBossStoneConfigChange;

                component.m_name = nameConfig.Value;
                component.m_description = runeStone.m_text;
                component.m_placeEffect = piece.m_placeEffect;

                component.enabled = true;

                GameObject collider = new GameObject("collider");
                BoxCollider boxCollider = collider.AddComponent<BoxCollider>();
                collider.transform.SetParent(clone.transform);
                var bounds = renderer.bounds;
                
                boxCollider.size = new Vector3(1f, bounds.size.y);
                boxCollider.center = bounds.center;
                
                instance.m_prefabs.Add(clone);
                instance.m_namedPrefabs[clone.name.GetStableHashCode()] = clone; 
                
                itemDrop.m_itemData.m_shared.m_buildPieces.m_pieces.Add(clone);

                m_registeredBossStones[clone.name] = clone;
            }
        }

        private static void OnBossStoneConfigChange(object sender, EventArgs e)
        {
            var instance = ZNetScene.instance;
            if (!instance) return;
            if (sender is ConfigEntry<Piece.PieceCategory> categoryConfig)
            {
                var prefab = instance.GetPrefab("piece_" + categoryConfig.Definition.Section);
                if (!prefab) return;
                if (!prefab.TryGetComponent(out Piece component)) return;
                component.m_category = categoryConfig.Value;
            }

            if (sender is ConfigEntry<string> stringConfigs)
            {
                var prefab = instance.GetPrefab("piece_" + stringConfigs.Definition.Section);
                if (!prefab) return;
                if (!prefab.TryGetComponent(out Piece component)) return;

                if (stringConfigs.Definition.Key == "Display Name")
                {
                    component.m_name = stringConfigs.Value;
                }

                if (stringConfigs.Definition.Key == "Resources")
                {
                    List<Piece.Requirement> requirements = new();

                    foreach (var data in stringConfigs.Value.Split(','))
                    {
                        var info = data.Split(':');
                        if (info.Length != 2) continue;
                        GameObject item = instance.GetPrefab(info[0]);
                        if (!item.TryGetComponent(out ItemDrop resourceDrop)) continue;
                        if (!item) continue;
                        if (!int.TryParse(info[1], out int amount)) continue;
                        requirements.Add(new Piece.Requirement()
                        {
                            m_resItem = resourceDrop,
                            m_recover = true,
                            m_amount = amount
                        });
                    }

                    component.m_resources = requirements.ToArray();
                }
            }
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
                BuildableBossStoneLogger.LogDebug("ReadConfigValues called");
                Config.Reload();
            }
            catch
            {
                BuildableBossStoneLogger.LogError($"There was an issue loading your {ConfigFileName}");
                BuildableBossStoneLogger.LogError("Please check your config entries for spelling and format!");
            }
        }


        #region ConfigOptions

        private static ConfigEntry<Toggle> _serverConfigLocked = null!;

        private void InitConfigs()
        {
            _serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On,
                "If on,the configuration is locked and can be changed by server admins only.");
            _ = ConfigSync.AddLockingConfigEntry(_serverConfigLocked);
        }

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

        public ConfigEntry<T> config<T>(string group, string name, T value, string description,
            bool synchronizedSetting = true)
        {
            return config(group, name, value, new ConfigDescription(description), synchronizedSetting);
        }

        private class ConfigurationManagerAttributes
        {
            [UsedImplicitly] public int? Order = null!;
            [UsedImplicitly] public bool? Browsable = null!;
            [UsedImplicitly] public string? Category = null!;
            [UsedImplicitly] public Action<ConfigEntryBase>? CustomDrawer = null!;
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