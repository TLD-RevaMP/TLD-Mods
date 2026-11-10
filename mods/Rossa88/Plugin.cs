using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace Rossa88
{
    // Rossa 88: a late-80s flat-twelve style sports car for the beta branch, built on the game's own Car07 (so it drives,
    // saves, breaks down and syncs in RevaMP multiplayer like the game's cars). F1 menu: "Rossa88Full" is the complete car,
    // "Rossa88" the bare shell. Item ids 108800-108899.
    [BepInPlugin(Guid, "Rossa 88", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "anonymoustld.rossa88";
        public const string Version = "0.1.0";

        // the car's files are built into the dll; a copy in a "cars" folder next to it wins (for tuning without a rebuild)
        internal static string CarsFolder => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "cars");

        private void Awake()
        {
            Log.Sink = s => Logger.LogInfo(s);
            try { CarBuild.Install(new Harmony(Guid)); } catch (System.Exception e) { Log.Info("patches failed: " + e); }
            Logger.LogInfo("Rossa 88 " + Version + " loaded");
        }
    }

    internal static class Log
    {
        internal static System.Action<string> Sink = s => Debug.Log("[Rossa 88] " + s);
        internal static void Info(string s) => Sink(s);
    }
}
