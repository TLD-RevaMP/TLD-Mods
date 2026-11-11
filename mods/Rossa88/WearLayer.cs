using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Rossa88
{
    // The model's own parts wear with the car (asked for 2026-11-11: a car spawned in poor condition kept a clean interior,
    // rims and trim, where every part of the game's cars rusts). The game wears a part by swapping its renderers' material
    // for the condition's (partconditionscript: new, used, middle, old, rusty); the model's own textured parts are child
    // renderers outside those lists, so they stayed new. Each such child draws, over its own material, a see-through
    // layer of the game's worn material from the condition script that wears the part it sits on: nothing when new,
    // more of it each step down, almost all rust when rusty. Its texture and shape stay underneath.
    public class WearLayer : MonoBehaviour
    {
        // how much of the worn material shows at condition 0..4 (new, used, middle, old, rusty)
        // (the game's own interiors and rims still look nearly clean at used and middle; rust takes over at old and rusty)
        internal static readonly float[] Cover = { 0f, .15f, .3f, .7f, .9f };
        private static readonly Dictionary<(Material, int), Material> Layers = new Dictionary<(Material, int), Material>();
        // The condition scripts some layer follows: every other car's refreshes (thousands while the world spawns) return at once.
        private static readonly HashSet<partconditionscript> Followed = new HashSet<partconditionscript>();

        public Material own;   // the part's own material (set when the car is built)
        // The game's condition type whose worn materials it takes ("darkmetal", "leather", "karpit", "metals": the config's
        // "wearLike"); empty: the type of the condition script it follows (the body's paint wore a dark dash grey).
        public string tipus;

        internal static void Install(Harmony h) =>
            h.Patch(AccessTools.Method(typeof(partconditionscript), "Refresh", new[] { typeof(bool) }),
                postfix: new HarmonyMethod(typeof(WearLayer), nameof(AfterRefresh)));

        // A condition script refreshed (spawn, damage, repair, load): the layers on the parts it wears follow it.
        private static void AfterRefresh(partconditionscript __instance)
        {
            if (!__instance || !Followed.Contains(__instance)) return;
            foreach (var w in __instance.GetComponentsInChildren<WearLayer>(true))
                if (w.Governing() == __instance) w.Apply(__instance);
        }

        // Awake runs as the car is instantiated, before the game first refreshes its condition (spawn, load).
        private void Awake() { var pc = Governing(); if (pc) Followed.Add(pc); }
        private void Start() { var pc = Governing(); if (pc) Apply(pc); }

        // The condition script that wears the node this part sits on: the nearest one up that lists that node's renderer,
        // else the nearest one up. Worked out each time: a copied part (right wheels from left ones) must follow its own.
        internal partconditionscript Governing()
        {
            var node = transform.parent ? transform.parent.GetComponent<Renderer>() : null;
            partconditionscript nearest = null;
            for (var t = transform.parent; t; t = t.parent)
                foreach (var pc in t.GetComponents<partconditionscript>())
                {
                    if (node && pc.renderers != null && pc.renderers.Contains(node)) return pc;
                    if (!nearest) nearest = pc;
                }
            return nearest;
        }

        internal void Apply(partconditionscript pc)
        {
            var r = GetComponent<Renderer>();
            if (!r) return;
            if (!own) own = r.sharedMaterials.FirstOrDefault();
            if (!own) return;
            int state = Mathf.Clamp(pc.state, 0, 4);
            Material layer = null;
            if (!pc.DontUseWorn && state > 0)
            {
                // that state's own worn material, of this part's type (else the followed script's), coloured as the game
                // colours a part of that type and state (old and rusty: the rusty colour; used and middle: the material's own)
                Material used, middle, old, rusty;
                var cm = string.IsNullOrEmpty(tipus) || !mainscript.s || mainscript.s.conditionmaterials == null ? null
                    : mainscript.s.conditionmaterials.FirstOrDefault(x => x != null && x.tipus == tipus);
                if (cm != null) { used = cm.Used; middle = cm.Middle; old = cm.Old; rusty = cm.Rusty; }
                else if (string.IsNullOrEmpty(tipus)) { used = null; middle = null; old = pc.defOld; rusty = pc.defRusty; }   // a paint's used/middle are tinted by the paint: not for these
                else { used = middle = old = rusty = null; }
                var worn = state == 1 ? used : state == 2 ? middle : state == 3 ? old : rusty;
                layer = Layer(worn, state, state >= 3 ? rusty : worn);
            }
            r.sharedMaterials = layer ? new[] { own, layer } : new[] { own };
        }

        // The worn material as a see-through layer (Standard shader, fade mode), coloured as the game colours old and
        // rusty parts (defRusty's colour), shared by every part with the same material and condition.
        private static Material Layer(Material worn, int state, Material rusty)
        {
            if (!worn || state <= 0 || Cover[state] <= 0f) return null;
            if (Layers.TryGetValue((worn, state), out var done)) return done;
            var m = new Material(worn) { name = worn.name + " (wear layer " + state + ")" };
            m.SetFloat("_Mode", 2f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.DisableKeyword("_ALPHATEST_ON"); m.EnableKeyword("_ALPHABLEND_ON"); m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            m.renderQueue = 3000;
            var c = rusty ? rusty.color : worn.color; c.a = Cover[state];
            m.color = c;
            Layers[(worn, state)] = m;
            return m;
        }
    }
}
