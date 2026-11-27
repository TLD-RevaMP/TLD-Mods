using System;
using BepInEx;
using BepInEx.Configuration;
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
        internal static ConfigEntry<int> ModeCfg;
        internal static ConfigEntry<float> StrengthCfg;
        internal static ConfigEntry<KeyboardShortcut> CycleKey;
        internal static ConfigEntry<bool> AoCfg;
        internal static ConfigEntry<KeyboardShortcut> AoKey;
        internal static readonly string[] ModeNames = { "shadows off", "far shadows (beyond the game's own)", "ray traced shadows everywhere", "shadow mask (testing)", "ambient occlusion alone (testing)", "reflections alone (testing)" };
        internal static ConfigEntry<bool> ReflCfg;
        internal static ConfigEntry<float> ShineCfg;
        internal static ConfigEntry<KeyboardShortcut> ReflKey;

        private void Awake()
        {
            Instance = this; _log = Logger;
            ModeCfg = Config.Bind("RayTrace", "Mode", 1, new ConfigDescription("Sun shadows: 0 off, 1 beyond the game's own shadow distance, 2 everywhere, 3 the shadow mask, 4 the ambient occlusion alone, 5 the reflections alone", new AcceptableValueRange<int>(0, 5)));
            StrengthCfg = Config.Bind("RayTrace", "Strength", 0.55f, new ConfigDescription("How much of a pixel's light the sun is: a shadowed pixel keeps 1 - this", new AcceptableValueRange<float>(0f, 1f)));
            CycleKey = Config.Bind("Keys", "CycleMode", new KeyboardShortcut(KeyCode.F7, KeyCode.LeftControl), "Switches between the shadow modes in game");
            AoCfg = Config.Bind("RayTrace", "AmbientOcclusion", true, "Ray traced ambient occlusion: corners, the ground under cars and where things touch darken");
            AoKey = Config.Bind("Keys", "ToggleAmbientOcclusion", new KeyboardShortcut(KeyCode.F6, KeyCode.LeftControl), "Turns the ray traced ambient occlusion on or off in game");
            Tracer.Ao = AoCfg.Value;
            ReflCfg = Config.Bind("RayTrace", "Reflections", true, "Ray traced reflections on shiny surfaces, glass and water");
            ShineCfg = Config.Bind("RayTrace", "Shine", 1f, new ConfigDescription("Each material's smoothness times this. Reflections start at smoothness 0.7: at 1 glass, water and polished surfaces (a little on glossy paint); 1.3 makes paint shine more", new AcceptableValueRange<float>(0f, 10f)));
            var reflStrength = Config.Bind("RayTrace", "ReflectionStrength", 0.6f, new ConfigDescription("How much of the ray traced reflection is mixed in (1: all the material asks for)", new AcceptableValueRange<float>(0f, 1f)));
            Tracer.ReflStrength = reflStrength.Value; reflStrength.SettingChanged += (_, __) => Tracer.ReflStrength = reflStrength.Value;
            ReflKey = Config.Bind("Keys", "ToggleReflections", new KeyboardShortcut(KeyCode.F5, KeyCode.LeftControl), "Turns the ray traced reflections on or off in game");
            Tracer.Refl = ReflCfg.Value; Scene.Shine = ShineCfg.Value;
            ShineCfg.SettingChanged += (_, __) => Scene.Shine = ShineCfg.Value;
            Tracer.Mode = ModeCfg.Value; Tracer.Strength = StrengthCfg.Value;
            StrengthCfg.SettingChanged += (_, __) => Tracer.Strength = StrengthCfg.Value;
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
        private void OnGUI()
        {
            if (Time.unscaledTime > _toastUntil) return;
            var style = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
            var r = new Rect(20, Screen.height - 70, Screen.width - 40, 40);
            GUI.color = Color.black; GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), _toast, style);
            GUI.color = Color.white; GUI.Label(r, _toast, style);
            if (!Native.Installed || SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Direct3D12)
                GUI.Label(new Rect(r.x, r.y - 30, r.width, 30), "(not on Direct3D 12: start the game with -force-d3d12)", style);
        }

        private readonly Tracer _tracer = new Tracer();
        private float _next;
        private bool _off;

        private float _toastUntil;
        private string _toast = "";

        private void Update()
        {
            Bench.Frame(Time.unscaledDeltaTime);
            if (Plugin.CycleKey != null && Plugin.CycleKey.Value.IsDown())
            {
                Tracer.Mode = (Tracer.Mode + 1) % 6;
                Plugin.ModeCfg.Value = Tracer.Mode;
                _toast = "RayTrace: " + Plugin.ModeNames[Tracer.Mode]; _toastUntil = Time.unscaledTime + 2.5f;
            }
            if (Plugin.ReflKey != null && Plugin.ReflKey.Value.IsDown())
            {
                Tracer.Refl = !Tracer.Refl; Plugin.ReflCfg.Value = Tracer.Refl;
                _toast = "RayTrace: reflections " + (Tracer.Refl ? "on" : "off"); _toastUntil = Time.unscaledTime + 2.5f;
            }
            if (Plugin.AoKey != null && Plugin.AoKey.Value.IsDown())
            {
                Tracer.Ao = !Tracer.Ao; Plugin.AoCfg.Value = Tracer.Ao;
                _toast = "RayTrace: ambient occlusion " + (Tracer.Ao ? "on" : "off"); _toastUntil = Time.unscaledTime + 2.5f;
            }
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
