using System;
using BepInEx;
using UnityEngine;

namespace RayTrace
{
    /// Hardware ray traced effects on Direct3D 12 (start the game with -force-d3d12). The ray tracing runs in
    /// RayTraceNative.dll, which this plugin writes into the game's native plugin folder: Unity loads a plugin from there
    /// with its D3D12 device (UnityPluginLoad), which a DLL loaded any other way never gets.
    [BepInPlugin(Guid, "RayTrace", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "tldmods.raytrace";
        public const string Version = "0.1.0";
        internal static Plugin Instance;
        // the game destroys BepInEx's manager object with this plugin on it: the logger is kept on its own
        private static BepInEx.Logging.ManualLogSource _log;
        public static string Status = "starting";

        private void Awake()
        {
            Instance = this; _log = Logger;
            try { Status = Native.Install(); }
            catch (Exception e) { Status = "install failed: " + e.Message; }
            Logger.LogInfo("RayTrace: " + Status);
            // the game destroys BepInEx's manager object (this plugin's Update never runs): a runner of our own
            var go = new GameObject("RayTrace") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            go.AddComponent<Runner>();
        }

        internal static void Log(string message) => _log?.LogInfo("RayTrace: " + message);
    }

    internal sealed class Runner : MonoBehaviour
    {
        private readonly Tracer _tracer = new Tracer();
        private float _next;
        private bool _off;

        private void Update()
        {
            Bench.Frame(Time.unscaledDeltaTime);
            D3D12Fixes.Tick();
            if (_off || !Native.Installed) return;
            try { _tracer.Tick(); }
            catch (Exception e) { Plugin.Status = "stopped: " + e.GetType().Name + " " + e.Message; Plugin.Log(Plugin.Status); _off = true; return; }
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 5f;
            var now = Native.Status() + " | " + _tracer.Info;
            if (now != Plugin.Status) { Plugin.Status = now; Plugin.Log(now); }
        }
    }
}
