namespace TerrainChoice
{
    public enum TerrainMode
    {
        Crossroads = 0,
        MainRoadOnly = 1,
        OldTerrain = 2,   // the public build v2023.05.02d landscape (OldTerrain.cs) with the main road only
    }

    /// The terrain type travels as a suffix on the map name ("Default - main road only"):
    /// - the save keeps it (saveproperties.map is BinaryFormatter data, so no new field can be added);
    /// - the game's own multiplayer sends the map name to joiners (networkingScript.SendMapStuff);
    /// - default maps are matched by defmapID, never by name (menuhandler.MapIndexFromName), so the suffix is harmless;
    /// - the load screen, the "Map name" text and the lobby list show it, so players see which terrain a world has.
    internal static class MapTag
    {
        private static readonly string[] Suffixes = { "", " - main road only", " - old terrain" };

        public static string Label(TerrainMode m) => m == TerrainMode.MainRoadOnly ? "Main road only" : m == TerrainMode.OldTerrain ? "Old terrain (2023 landscape, main road only)" : "Crossroads (default)";

        public static TerrainMode Parse(string mapName)
        {
            if (!string.IsNullOrEmpty(mapName))
                for (int i = 1; i < Suffixes.Length; i++)
                    if (mapName.EndsWith(Suffixes[i], System.StringComparison.Ordinal)) return (TerrainMode)i;
            return TerrainMode.Crossroads;
        }

        public static string Strip(string mapName)
        {
            if (string.IsNullOrEmpty(mapName)) return mapName;
            for (int i = 1; i < Suffixes.Length; i++)
                if (mapName.EndsWith(Suffixes[i], System.StringComparison.Ordinal)) return mapName.Substring(0, mapName.Length - Suffixes[i].Length);
            return mapName;
        }

        public static string With(string mapName, TerrainMode m)
        {
            int i = (int)m;
            return Strip(mapName ?? "") + (i > 0 && i < Suffixes.Length ? Suffixes[i] : "");
        }

        public static bool Valid(int m) => m >= 0 && m < Suffixes.Length;
    }
}
