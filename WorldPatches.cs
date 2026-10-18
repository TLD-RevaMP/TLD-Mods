using System.Collections.Generic;
using HarmonyLib;

namespace TerrainChoice
{
    internal static class WorldPatches
    {
        /// The terrain of the world that is loaded (or loading) now.
        public static TerrainMode Current = TerrainMode.Crossroads;
        public static string CurrentSource = "";

        /// menuhandler.LoadMap(mapClass) instantiates the map prefab and calls its mapSettingScript.FStart, which
        /// starts the road generators. Every world load goes through here: a new game (S.map from the settings), a
        /// loaded save (properties.map), a joiner of the game's own multiplayer (multiMap from RecMapStuff) and a
        /// RevaMP joiner (a new game started from its Welcome).
        [HarmonyPatch(typeof(menuhandler), nameof(menuhandler.LoadMap))]
        private static class DecideOnLoadMap
        {
            [HarmonyPrefix]
            private static void Prefix(ref gamemodesettingsscript.mapClass _mapClass)
            {
                var d = DataFromMenuScript.s;
                if (d == null || d.mainmenu) return;   // the menu's background map
                if (_mapClass.map.tip != menuhandler.mapType.def)
                {
                    Current = TerrainMode.Crossroads; CurrentSource = "not a default map";
                    return;
                }
                TerrainMode mode;
                if (RevampLink.TakeHostMode(out mode)) CurrentSource = "the RevaMP host's";
                else if (d.load) { mode = MapTag.Parse(_mapClass.map.mapName); CurrentSource = "from the save"; }
                else if (d.multi) { mode = MapTag.Parse(_mapClass.map.mapName); CurrentSource = "the host's"; }
                else { mode = Plugin.NewGameTerrain.Value; CurrentSource = "new game setting"; }
                Current = mode;
                var m = _mapClass.map;   // structs: copied back
                m.mapName = MapTag.With(m.mapName, mode);
                _mapClass.map = m;
                Plugin.Log.LogInfo("World terrain: " + MapTag.Label(mode) + " (" + CurrentSource + "), map name '" + m.mapName + "'");
            }
        }

        /// Before the map's generators start (RestartMap -> roadGens[i].FStart), so no road chunk exists yet.
        [HarmonyPatch(typeof(mapSettingScript), nameof(mapSettingScript.FStart))]
        private static class ApplyBeforeGeneration
        {
            [HarmonyPrefix]
            private static void Prefix(mapSettingScript __instance, int _seed)
            {
                if (menuhandler.s == null || __instance != menuhandler.s.currentMainMap) return;
                Apply(__instance, Current, _seed);   // FStart copies _seed into the map only after this
            }
        }

        internal static List<roadGenScript> RoadGens(mapSettingScript ms)
        {
            var list = new List<roadGenScript>();
            if (ms == null) return list;
            if (ms.roadGens != null)
                foreach (var g in ms.roadGens) if (g != null && !list.Contains(g)) list.Add(g);
            foreach (var g in ms.transform.root.GetComponentsInChildren<roadGenScript>(true))
                if (!list.Contains(g)) list.Add(g);
            return list;
        }

        private static void Apply(mapSettingScript ms, TerrainMode mode, int seed)
        {
            OldTerrain.Set(ms, mode == TerrainMode.OldTerrain, seed);
            OldRoad.Set(ms, mode == TerrainMode.OldTerrain ? OldTerrain.Noise : null, RoadGens(ms));
            OldBuildings.Set(ms, mode == TerrainMode.OldTerrain ? OldRoad.Path : null, OldTerrain.Noise);
            if (mode == TerrainMode.Crossroads) return;   // crossroads = the map prefab as it is; the old world had one road
            int changed = 0, skipped = 0;
            foreach (var g in RoadGens(ms))
            {
                // the game's own main-road-only mode: chunks off the main road's column (x != 0) are never generated
                // (roadGenScript.UpdChunksNeeded / UpdDotChunk) and buildings use the main road (poiGenScript)
                if (g.hasMainRoad) { g.hasSideRoad = false; changed++; }
                else skipped++;   // a generator without a main road would end up with no roads at all
            }
            Plugin.Log.LogInfo("Main road only: side roads off on " + changed + " road generator(s)" + (skipped > 0 ? ", " + skipped + " without a main road left as they are" : ""));
        }
    }
}
