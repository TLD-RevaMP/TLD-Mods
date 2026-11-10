using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace BusGut
{
    // Beta branch addon: empties the game's buses into cargo space (see Gut.cs).
    [BepInPlugin(Guid, "BusGut", Version)]
    [BepInDependency("tldrevamp.core", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "anonymoustld.busgut";
        public const string Version = "0.2.0";

        internal static Plugin Instance;
        internal static ConfigEntry<bool> Enabled;

        private void Awake()
        {
            Instance = this;
            Enabled = Config.Bind("General", "GutBuses", true,
                "Remove the passenger seats, seat frames and handrails/poles from every Bus01 and Bus03, leaving the driver seat, " +
                "dashboard and floor: one open cargo space. Takes effect for buses that load after it's switched on; switching it off " +
                "needs a reload of the save. In multiplayer every player needs the same setting.");
            // The beta destroys BepInEx's manager object, which stops a plugin's Update and coroutines: run on our own.
            var go = new GameObject("BusGutRunner");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            go.AddComponent<Runner>();
            RevampLink.Init();
            Logger.LogInfo("BusGut " + Version + " loaded" + (Enabled.Value ? "" : " (switched off)") +
                (RevampLink.Registered ? ", registered with RevaMP" : ""));
        }

        internal static void Info(string s) => Instance.Logger.LogInfo(s);
        internal static void Warn(string s) => Instance.Logger.LogWarning(s);
    }

    internal class Runner : MonoBehaviour
    {
        private bool started;
        private float nextScan;

        private void Update()
        {
            if (!started)
            {
                started = true;
                if (Dump.Requested) StartCoroutine(Dump.Run(Plugin.Info));
                if (SelfTest.Requested) StartCoroutine(SelfTest.Run(Plugin.Info));
            }
            if (SelfTest.Running || !Plugin.Enabled.Value || Time.realtimeSinceStartup < nextScan) return;
            nextScan = Time.realtimeSinceStartup + 2f;
            foreach (carscript car in Object.FindObjectsOfType<carscript>())
            {
                if (Gut.IsBus(car) && Gut.Apply(car, out string summary)) Plugin.Info("BusGut: " + summary);
            }
        }
    }
}
