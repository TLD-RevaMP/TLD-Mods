using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TerrainChoice
{
    // The Long Drive BETA branch (V2024.11.26b_test). Lets the host of a new game pick the terrain type:
    //   Crossroads    - the beta's normal world (main road plus side roads and crossroads)
    //   MainRoadOnly  - the beta's own "main road only" mode (roadGenScript.hasSideRoad = false)
    // The choice is stored in the save (as a suffix on the map name, see MapTag), so a loaded world keeps it, and
    // joiners get the host's choice: the game's own multiplayer sends the map name, RevaMP gets it in its Welcome.
    // Every player needs this mod.
    [BepInPlugin(Guid, "TerrainChoice", Version)]
    [BepInDependency("tldrevamp.core", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "anonymoustld.terrainchoice";
        public const string Version = "0.4.1";

        internal static ManualLogSource Log;
        internal static ConfigEntry<TerrainMode> NewGameTerrain;
        internal static ConfigEntry<bool> DumpUi;

        private void Awake()
        {
            Log = Logger;
            NewGameTerrain = Config.Bind("NewGame", "Terrain", TerrainMode.Crossroads,
                "Terrain for the next new game you start or host (also set in the game's new-game settings). " +
                "Loaded saves keep the terrain they were made with; joiners always get the host's.");
            DumpUi = Config.Bind("Debug", "DumpUi", false, "Write the game-mode settings UI hierarchy to the log once.");

            var harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            RevampLink.Init(harmony);
            OldTerrain.PatchRevamp(harmony);

            // The beta destroys BepInEx's manager object when its scene loads: per-frame work needs its own object.
            var go = new GameObject("TerrainChoiceRunner");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            go.AddComponent<Runner>();

            Logger.LogInfo("TerrainChoice " + Version + " loaded. New games: " + NewGameTerrain.Value +
                           (RevampLink.Active ? ". RevaMP found: the host's terrain rides its Welcome." : "."));
        }
    }
}
