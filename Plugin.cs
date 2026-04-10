using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace NightTerrors
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class NightTerrorsPlugin : BaseUnityPlugin
    {
        public const string PluginGUID    = "nightterrors";
        public const string PluginName    = "NightTerrors";
        public const string PluginVersion = "1.0.1";

        internal static ManualLogSource Log;
        internal static NightTerrorsPlugin Instance;
        private Harmony _harmony;

        internal static ConfigEntry<int>    CfgTriggerChance;
        internal static ConfigEntry<bool>   CfgFriendlyFire;
        internal static ConfigEntry<bool>   CfgSpawnMonsters;
        internal static ConfigEntry<int>    CfgMonsterCount;
        internal static ConfigEntry<string> CfgScenarioWeights;
        internal static ConfigEntry<string> CfgWeatherPool;
        internal static ConfigEntry<int>    CfgEventDuration;

        void Awake()
        {
            Log      = Logger;
            Instance = this;

            CfgTriggerChance = Config.Bind("General", "TriggerChance", 20,
                "1-in-N chance of the event triggering each time everyone sleeps.");
            CfgFriendlyFire = Config.Bind("General", "FriendlyFire", false,
                "Allow players to damage each other during the event.");
            CfgSpawnMonsters = Config.Bind("General", "SpawnMonsters", true,
                "Spawn monsters at the teleport location.");
            CfgMonsterCount = Config.Bind("General", "MonsterCount", 3,
                "Number of monsters to spawn.");
            CfgScenarioWeights = Config.Bind("General", "ScenarioWeights", "1,1,1,1",
                "Comma-separated weights for: KeepGear, GoNaked, DifferentEquipment, SwapEquipment.");
            CfgEventDuration = Config.Bind("General", "EventDuration", 120,
                "Maximum event duration in seconds. Survivors have their inventory restored and the event ends.");
            CfgWeatherPool = Config.Bind("Weather", "WeatherPool",
                "ThunderStorm,Ashrain,Snow,Twilight_Snow,DeepForest_Mist,SwampRain,Mistlands_darkening",
                "Comma-separated list of environment names to pick from randomly. " +
                "Set to empty string to disable weather effects.");

            _harmony = new Harmony(PluginGUID);
            _harmony.PatchAll();
            Log.LogInfo($"{PluginName} v{PluginVersion} loaded.");
        }

        void OnDestroy() => _harmony?.UnpatchSelf();
    }
}
