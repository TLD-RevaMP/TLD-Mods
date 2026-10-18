using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace MountAnything
{
    // The Long Drive BETA branch (V2024.11.26b_test). Lets the player mount (glue) any item they hold onto whatever the
    // crosshair points at: a car's body, another item, a building, the ground. It uses the game's own glue attachment
    // (attachablescript + its save_attachable record), which until now only a few items had, so a mounted item is
    // saved, loaded, taken off and shared by RevaMP exactly like the game's own glued items. Every player in a RevaMP
    // session needs this mod (RevaMP checks that at join).
    [BepInPlugin(Guid, "MountAnything", Version)]
    [BepInDependency("tldrevamp.core", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "anonymoustld.mountanything";
        public const string Version = "0.2.1";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<KeyCode> MountKey;
        internal static ConfigEntry<bool> AllowSlotParts;
        internal static ConfigEntry<bool> AlignToSurface;
        internal static ConfigEntry<bool> Place, ShowPreview, ShowHelp, PlaceGameGlue;
        internal static ConfigEntry<KeyCode> TurnLeftKey, TurnRightKey, TiltAwayKey, TiltTowardKey, TiltLeftKey, TiltRightKey, OutKey, InKey, ResetKey, FineKey;
        internal static ConfigEntry<float> AngleStep, FineAngleStep, MoveStep, FineMoveStep;
        internal static ConfigEntry<Color> PreviewColor;

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("Mount", "Enabled", true, "Lets you mount any held item where you look.");
            MountKey = Config.Bind("Mount", "Key", KeyCode.None,
                "Key that mounts the held item at the crosshair. None = the game's own Glue key (G unless you rebound it), " +
                "which then also works for items the game can't glue.");
            AllowSlotParts = Config.Bind("Mount", "AllowSlotParts", true,
                "Also mount car parts that normally only fit their own slot (wheels, engine parts, doors...) anywhere.");
            AlignToSurface = Config.Bind("Mount", "AlignToSurface", false,
                "Start every placement upright: the item's own up side out of the surface (with placement off: turn it so when mounting). " +
                "Off = it starts the way you hold it. The Reset key cycles as held, flat, upright.");

            // Placement keys: none is one of the beta's default bindings on foot (the arrows steer in a car, so they only
            // work standing) or one of RevaMP's (PageUp, PageDown, K, F11).
            Place = Config.Bind("Place", "Enabled", true,
                "Show where the held item will be mounted and let the keys below turn, tilt and move it. Off = the 0.1 behaviour " +
                "(the item keeps the way you hold it; its point nearest the crosshair goes on the spot).");
            ShowPreview = Config.Bind("Place", "ShowPreview", true, "Draw a see-through copy of the item where it will be mounted.");
            ShowHelp = Config.Bind("Place", "ShowHelp", true, "Show the placement keys on screen while you can mount.");
            PlaceGameGlue = Config.Bind("Place", "GameGlueItems", true,
                "Also preview and place the items the game can glue by itself (its Glue key then mounts them at the shown pose).");
            TurnLeftKey = Config.Bind("Place", "TurnLeft", KeyCode.LeftArrow, "Turn the item around the surface's normal.");
            TurnRightKey = Config.Bind("Place", "TurnRight", KeyCode.RightArrow, "Turn the item around the surface's normal.");
            TiltAwayKey = Config.Bind("Place", "TiltAway", KeyCode.UpArrow, "Tip the item's top away from you.");
            TiltTowardKey = Config.Bind("Place", "TiltToward", KeyCode.DownArrow, "Tip the item's top toward you.");
            TiltLeftKey = Config.Bind("Place", "TiltLeft", KeyCode.Comma, "Tip the item's top to the left.");
            TiltRightKey = Config.Bind("Place", "TiltRight", KeyCode.Period, "Tip the item's top to the right.");
            OutKey = Config.Bind("Place", "MoveOut", KeyCode.Equals, "Move the item out of the surface.");
            InKey = Config.Bind("Place", "MoveIn", KeyCode.Minus, "Move the item into the surface.");
            ResetKey = Config.Bind("Place", "Reset", KeyCode.Slash,
                "Square on the surface again (no turn, tilt or offset) and the next start: as held, then flat (lying on its biggest side), then upright (its own up side out).");
            FineKey = Config.Bind("Place", "FineModifier", KeyCode.LeftShift, "Hold for fine steps.");
            AngleStep = Config.Bind("Place", "AngleStep", 15f, "Degrees per press.");
            FineAngleStep = Config.Bind("Place", "FineAngleStep", 1f, "Degrees per press with the fine key held.");
            MoveStep = Config.Bind("Place", "MoveStep", 0.01f, "Metres per press.");
            FineMoveStep = Config.Bind("Place", "FineMoveStep", 0.001f, "Metres per press with the fine key held.");
            PreviewColor = Config.Bind("Place", "PreviewColor", new Color(0.35f, 1f, 0.55f, 0.4f), "Colour of the preview (RGB; A = strength). It is added to what is behind it, so it glows and stays see-through.");

            var harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            RevampLink.Init(harmony);

            // The beta destroys BepInEx's manager object when its scene loads: per-frame work needs its own object.
            var go = new GameObject("MountAnythingRunner");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            go.AddComponent<Runner>();

            Logger.LogInfo("MountAnything " + Version + " loaded. Key: " + (MountKey.Value == KeyCode.None ? "the game's Glue key" : MountKey.Value.ToString()) +
                           (RevampLink.Active ? ". RevaMP found." : "."));
        }
    }
}
