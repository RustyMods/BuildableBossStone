using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BuildableBossStone.Managers;
using HarmonyLib;
using JetBrains.Annotations;
using PieceManager;
using ServerSync;
using UnityEngine;

namespace BuildableBossStone
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class BuildableBossStonePlugin : BaseUnityPlugin
    {
        internal const string ModName = "BuildableBossStones";
        internal const string ModVersion = "1.1.1";
        internal const string Author = "RustyMods";
        private const string ModGUID = Author + "." + ModName;
        private static readonly string ConfigFileName = ModGUID + ".cfg";
        private static readonly string ConfigFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;
        internal static string ConnectionError = "";
        private readonly Harmony _harmony = new(ModGUID);
        public static readonly ManualLogSource BuildableBossStoneLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);
        private static readonly ConfigSync ConfigSync = new(ModGUID) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };
        private enum Toggle { On = 1, Off = 0 }

        private static GameObject Root = null!;
        private static ConfigEntry<Toggle> _serverConfigLocked = null!;
        private static readonly List<GameObject> PrefabsToRegister = new();
        private static readonly List<Clone> Clones = new();
        private static bool IsClonedBossStone(string name) => m_pieceNames.Contains(name);
        
        private static readonly List<string> m_pieceNames = new();
        private static readonly List<BuildPiece> m_pieces = new();

        private static bool m_initiated;
        private static bool m_snapped;
        public void Awake()
        {
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
        
        [HarmonyPriority(Priority.VeryHigh)]
        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.Awake))]
        private static class FejdStartup_Awake_Patch
        {
            [UsedImplicitly]
            private static void Postfix(FejdStartup __instance)
            {
                Helpers._ZNetScene = __instance.m_objectDBPrefab.GetComponent<ZNetScene>();
                Helpers._ObjectDB = __instance.m_objectDBPrefab.GetComponent<ObjectDB>();
                Setup();
                foreach(Clone? clone in Clones) clone.Create();
                BuildPiece.Patch_FejdStartup(__instance);
            }
        }


        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
        private static class ZNetScene_Awake_Patch
        {
            [UsedImplicitly]
            private static void Prefix(ZNetScene __instance)
            {
                foreach (GameObject prefab in PrefabsToRegister)
                {
                    if (!prefab.GetComponent<ZNetView>()) continue;
                    __instance.m_prefabs.Add(prefab);
                }
            }
        }

        private static void RegisterPrefab(GameObject? prefab)
        {
            if (prefab == null) return;
            PrefabsToRegister.Add(prefab);
        }

        private static void Setup()
        {
            if (m_initiated || Helpers._ZNetScene == null) return;

            Piece? piece = Helpers.GetPrefab("piece_stonecutter")!.GetComponent<Piece>();
            WearNTear? wear = Helpers.GetPrefab("stone_wall_1x1")!.GetComponent<WearNTear>();
            
            foreach (GameObject? prefab in Helpers._ZNetScene.m_prefabs.Where(x => x != null && x.GetComponent<BossStone>()))
            {
                BuildableBossStoneLogger.LogDebug($"Creating prefab: piece_{prefab.name}");
                Clone clone = new Clone(prefab.name, $"piece_{prefab.name}");
                clone.OnCreated += go =>
                {
                    MeshRenderer? renderer = go.transform.GetChild(0).GetComponent<MeshRenderer>();
                    RuneStone? runestone = go.GetComponentInChildren<RuneStone>();
                    m_pieceNames.Add(go.name);
                    Piece component = go.AddComponent<Piece>();
                    GameObject model = SetUnderModel(go);
                    WearNTear wearNTear = go.AddComponent<WearNTear>();
                    wearNTear.m_new = model;
                    wearNTear.m_worn = model;
                    wearNTear.m_broken = model;
                    wearNTear.m_supports = false;
                    wearNTear.m_noRoofWear = true;
                    wearNTear.m_noSupportWear = true;
                    wearNTear.m_materialType = WearNTear.MaterialType.Stone;
                    wearNTear.m_health = 1500f;
                    wearNTear.m_destroyedEffect = wear.m_destroyedEffect;
                    wearNTear.m_hitEffect = wear.m_hitEffect;
                    wearNTear.m_autoCreateFragments = true;
                    component.m_description = runestone.m_text;
                    component.m_placeEffect = piece.m_placeEffect;
                    component.enabled = true;

                    GameObject collider = new GameObject("collider");
                    BoxCollider boxCollider = collider.AddComponent<BoxCollider>();
                    collider.transform.SetParent(go.transform);
                    Bounds bounds = renderer.bounds;
                
                    boxCollider.size = new Vector3(1f, bounds.size.y);
                    boxCollider.center = bounds.center;

                    BuildPiece buildPiece = new BuildPiece(go);
                    buildPiece.Name.English(prefab.name.Replace("_", " "));
                    buildPiece.Category.Set("Boss Stones");
                    buildPiece.Crafting.Set(CraftingTable.Workbench);
                    buildPiece.RequiredItems.Add("Stone", 100, true);
                    buildPiece.RequiredItems.Add("Flint", 20, true);
                    m_pieces.Add(buildPiece);
                };
            }
            
            m_initiated = true;
        }

        [HarmonyPatch(typeof(Piece), nameof(Piece.DropResources))]
        private static class Piece_DropResources_Patch
        {
            [UsedImplicitly]
            private static void Postfix(Piece __instance)
            {
                if (!ObjectDB.instance || !IsClonedBossStone(__instance.name.Replace("(Clone)", string.Empty))) return;
                ItemStand itemStand = __instance.GetComponentInChildren<ItemStand>();
                if (!itemStand) return;
                string trophy = itemStand.GetAttachedItem();
                if (trophy.IsNullOrWhiteSpace()) return;
                GameObject item = ObjectDB.instance.GetItemPrefab(trophy);
                if (!item) return;
                Instantiate(item, __instance.transform.position + Vector3.up, Quaternion.identity);
            }
        }
        
        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class Player_OnSpawned_Patch
        {
            [UsedImplicitly]
            private static void Postfix()
            {
                if (m_snapped) return;
                foreach (var piece in m_pieces) piece.Snapshot();
                m_snapped = true;
            }
        }
        private static GameObject SetUnderModel(GameObject prefab)
        {
            List<Transform> children = new();
            foreach (Transform child in prefab.transform)
            {
                children.Add(child);
            }
            GameObject model = new GameObject("model");
            model.transform.parent = prefab.transform;
            foreach (var child in children)
            {
                child.parent = model.transform;
            }

            return model;
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
        
        public class Clone
        {
            public static readonly Dictionary<string, GameObject> registeredPrefabs = new();
            private GameObject? Prefab;
            private readonly string PrefabName;
            private readonly string NewName;
            private bool Loaded;
            public event Action<GameObject>? OnCreated;

            public Clone(string prefabName, string newName)
            {
                PrefabName = prefabName;
                NewName = newName;
                Clones.Add(this);
            }

            internal void Create()
            {
                if (Loaded) return;
                if (Helpers.GetPrefab(PrefabName) is not { } prefab) return;
                Prefab = Instantiate(prefab, Root.transform, false);
                Prefab.name = NewName;
                RegisterPrefab(Prefab);
                OnCreated?.Invoke(Prefab);
                registeredPrefabs[Prefab.name] = Prefab;
                Loaded = true;
            }
        }
    }
}