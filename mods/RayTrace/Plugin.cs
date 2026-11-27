using System;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace RayTrace
{
    /// Tracer.Mode by name (the settings page shows these as words); the numbers are the modes of 0.1.0's config.
    public enum RayMode
    {
        GameShadows = 0, RayTracedShadows = 1, RayTracedAndGameShadows = 2,
        ShadowMaskView = 3, OcclusionView = 4, ReflectionsView = 5, PrimaryRayView = 6, BouncedLightView = 7,
    }

    /// Hardware ray traced effects on Direct3D 12 (start the game with -force-d3d12). The ray tracing runs in
    /// RayTraceNative.dll, which this plugin writes into the game's native plugin folder: Unity loads a plugin from there
    /// with its D3D12 device (UnityPluginLoad), which a DLL loaded any other way never gets.
    [BepInPlugin(Guid, "RayTrace", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "tldrevamp.raytrace";
        public const string Version = "0.1.0";
        internal static Plugin Instance;
        // the game destroys BepInEx's manager object with this plugin on it: the logger is kept on its own
        private static BepInEx.Logging.ManualLogSource _log;
        public static string Status = "starting";
        internal static ConfigEntry<RayMode> ModeCfg;
        internal static ConfigEntry<bool> AoCfg, ReflCfg, GiCfg;
        internal static ConfigEntry<KeyboardShortcut> CycleKey, AoKey, ReflKey, GiKey;
        internal static readonly string[] ModeNames = { "the game's own shadows", "ray traced sun shadows", "ray traced shadows with all the game's own", "shadow mask (testing)", "ambient occlusion alone (testing)", "reflections alone (testing)", "what the primary ray found (testing)", "bounced light alone (testing)" };

        // The settings are the BepInEx config's (BepInEx/config/tldrevamp.raytrace.cfg), which RevaMP's mods list shows as the
        // mod's settings page (a row per entry, its description under it: kept to about three lines). All apply at once.
        private void Awake()
        {
            Instance = this; _log = Logger;
            ModeCfg = Config.Bind("Effects", "Mode", RayMode.RayTracedShadows, "Sun shadows: the game's own, ray traced (the default), or ray traced joined with the game's own. The views after those show one effect alone, for testing. Ctrl+F7 in game.");
            AoCfg = Config.Bind("Effects", "AmbientOcclusion", true, "Corners, the ground under cars and where things touch darken (rays 2.5 m long). Ctrl+F6 in game.");
            ReflCfg = Config.Bind("Effects", "Reflections", true, "Mirror reflections on glass, water, car paint, chrome and polished metal. Ctrl+F5 in game.");
            GiCfg = Config.Bind("Effects", "BouncedLight", true, "Global illumination: light bounced off nearby surfaces tints and brightens what is in shade next to them. Ctrl+F4 in game.");
            CycleKey = Config.Bind("Keys", "CycleMode", new KeyboardShortcut(KeyCode.F7, KeyCode.LeftControl), "Switches to the next mode.");
            AoKey = Config.Bind("Keys", "ToggleAmbientOcclusion", new KeyboardShortcut(KeyCode.F6, KeyCode.LeftControl), "Turns the ambient occlusion on or off.");
            ReflKey = Config.Bind("Keys", "ToggleReflections", new KeyboardShortcut(KeyCode.F5, KeyCode.LeftControl), "Turns the reflections on or off.");
            GiKey = Config.Bind("Keys", "ToggleBouncedLight", new KeyboardShortcut(KeyCode.F4, KeyCode.LeftControl), "Turns the bounced light on or off.");
            var reflStrength = Config.Bind("Tuning", "ReflectionStrength", 0.8f, new ConfigDescription("How much of the ray traced reflection is mixed in (1: all the material asks for).", new AcceptableValueRange<float>(0f, 1f)));
            var shine = Config.Bind("Tuning", "Shine", 1f, new ConfigDescription("Each material's smoothness times this. Above 1 more of the game's materials turn into mirrors (most are 0.5; reflections start at 0.55).", new AcceptableValueRange<float>(0f, 3f)));
            var giStrength = Config.Bind("Tuning", "BouncedLightStrength", 1.5f, new ConfigDescription("How much bounced light is added.", new AcceptableValueRange<float>(0f, 3f)));
            var sunSize = Config.Bind("Tuning", "SunSize", 0.6f, new ConfigDescription("The sun's radius in degrees for the shadow rays: shadows are sharp where they meet their caster and softer further out (the real sun: 0.27).", new AcceptableValueRange<float>(0f, 3f)));
            var strength = Config.Bind("Tuning", "Strength", 0.55f, new ConfigDescription("Only with the game's own shadows switched off in its settings: how dark a ray traced shadow makes the frame.", new AcceptableValueRange<float>(0f, 1f)));

            void Apply()
            {
                Tracer.Mode = (int)ModeCfg.Value; Tracer.Ao = AoCfg.Value; Tracer.Refl = ReflCfg.Value; Tracer.Gi = GiCfg.Value;
                Tracer.ReflStrength = reflStrength.Value; Scene.Shine = shine.Value; Tracer.GiStrength = giStrength.Value;
                Tracer.SunSize = sunSize.Value; Tracer.Strength = strength.Value;
            }
            Apply();
            Config.SettingChanged += (_, __) => Apply();
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
            var style = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, wordWrap = true };
            float w = Screen.width - 40, h = style.CalcHeight(new GUIContent(_toast), w);
            var r = new Rect(20, Screen.height - 30 - h, w, h);
            GUI.color = Color.black; GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), _toast, style);
            GUI.color = Color.white; GUI.Label(r, _toast, style);
        }

        /// Why the rays can't run here, or null: shown for 20 s at start, since installed from a mod list the mod otherwise
        /// just does nothing.
        private static string Notice()
        {
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Direct3D12)
                return "RayTrace does nothing: the game isn't running on Direct3D 12. Add -force-d3d12 to its launch options (Steam: Properties, Launch Options), and turn Unity's graphics jobs off first (see the mod's page).";
            if (!SystemInfo.supportsRayTracing) return "RayTrace does nothing: this graphics card or its driver has no ray tracing (DXR).";
            if (!Native.Installed) return "RayTrace does nothing: " + Plugin.Status;
            try
            {
                var boot = System.IO.Path.Combine(Application.dataPath, "boot.config");
                if (System.IO.File.Exists(boot))
                    foreach (var line in System.IO.File.ReadAllLines(boot))
                        if (line.Replace(" ", "") == "gfx-enable-native-gfx-jobs=1" || line.Replace(" ", "") == "gfx-enable-gfx-jobs=1")
                            return "RayTrace: Unity's graphics jobs are on. On Direct3D 12 the game then crashes about one world load in three (with or without this mod). Set gfx-enable-gfx-jobs=0 and gfx-enable-native-gfx-jobs=0 in TheLongDrive_Data/boot.config.";
            }
            catch (Exception) { }
            return null;
        }

        private void Start()
        {
            var notice = Notice();
            if (notice == null) return;
            Plugin.Log(notice);
            _toast = notice; _toastUntil = Time.unscaledTime + 20f;
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
                Tracer.Mode = (Tracer.Mode + 1) % 8;
                Plugin.ModeCfg.Value = (RayMode)Tracer.Mode;
                _toast = "RayTrace: " + Plugin.ModeNames[Tracer.Mode]; _toastUntil = Time.unscaledTime + 2.5f;
            }
            if (Plugin.GiKey != null && Plugin.GiKey.Value.IsDown())
            {
                Tracer.Gi = !Tracer.Gi; Plugin.GiCfg.Value = Tracer.Gi;
                _toast = "RayTrace: bounced light " + (Tracer.Gi ? "on" : "off"); _toastUntil = Time.unscaledTime + 2.5f;
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
