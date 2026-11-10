using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TerrainChoice
{
    /// Adds a "Terrain" dropdown to the game's own game-mode (new game) settings, as a copy of its biome row
    /// (gamemodesettingsscript.dropdrops[1]), right below it. The copy is set up as a plain string dropdown that
    /// talks only to this mod: the game's settings code never sees it (it isn't in dropdrops), and its language
    /// texts are removed so the game doesn't overwrite them.
    internal static class NewGameUi
    {
        public static readonly string[] Options = { "Roads: with crossroads", "Roads: main road only", "Old terrain (2023), main road" };
        public static GameObject LastCopy;
        public const string LabelText = "Terrain";
        public static string LastReport = "";

        public static bool Inject(gamemodesettingsscript g, out TMP_Dropdown dropdown, out GameObject rowCopy)
        {
            dropdown = null; rowCopy = null;
            var src = g.dropdrops != null && g.dropdrops.Count > 1 ? g.dropdrops[1] : null;
            if (src == null || src.dropdown == null) { Plugin.Log.LogWarning("New-game UI: the biome dropdown wasn't found; using the fallback panel."); return false; }
            if (Plugin.DumpUi.Value) Plugin.Log.LogInfo("Game-mode settings UI:\n" + Dump(g.transform, 0));

            var row = RowOf(src, g);
            var parent = row.parent;
            var holder = new GameObject("TerrainChoiceHolder");
            holder.SetActive(false);   // the copy wakes up only once it's set up
            var copy = Object.Instantiate(row.gameObject, holder.transform, false);
            copy.name = "TerrainChoiceRow";
            foreach (var lt in copy.GetComponentsInChildren<languagetext>(true)) Object.DestroyImmediate(lt);
            var ds = copy.GetComponentInChildren<dropdownsetting>(true);
            if (ds == null || ds.dropdown == null) { Object.Destroy(holder); Plugin.Log.LogWarning("New-game UI: the copied row has no dropdown; using the fallback panel."); return false; }
            ds.gsetting = null; ds.setting = null; ds.settingPlayer = null;
            ds.refreshWhenChange = new dropdownsetting[0];
            ds.words = new int[0];
            ds.useDefInt = false;
            ds.dtype = dropdownsetting.dropdowntype.strings;
            ds.stringWords = Options;
            int labels = 0;
            foreach (var t in copy.GetComponentsInChildren<TMP_Text>(true))
                if (!t.transform.IsChildOf(ds.dropdown.transform)) { t.text = LabelText; labels++; }
            foreach (var t in copy.GetComponentsInChildren<Text>(true))
                if (!t.transform.IsChildOf(ds.dropdown.transform)) { t.text = LabelText; labels++; }

            copy.transform.SetParent(parent, false);
            copy.transform.SetSiblingIndex(row.GetSiblingIndex() + 1);
            Object.Destroy(holder);
            string placed = Place(row as RectTransform, copy.transform as RectTransform, parent);

            ds.SetValueWithoutNotify((int)Plugin.NewGameTerrain.Value);
            ds.dropdown.onValueChanged.AddListener(v =>
            {
                if (MapTag.Valid(v)) Plugin.NewGameTerrain.Value = (TerrainMode)v;
                Plugin.Log.LogInfo("New-game terrain set to " + MapTag.Label(Plugin.NewGameTerrain.Value));
            });
            dropdown = ds.dropdown; rowCopy = copy; LastCopy = copy;
            LastReport = "row '" + Path(row) + "' copied below the biome row (" + placed + ", " + labels + " label(s))";
            Plugin.Log.LogInfo("New-game UI: " + LastReport);
            return true;
        }

        /// The biome dropdown's row: the highest ancestor that holds no other game-mode setting.
        private static Transform RowOf(dropdownsetting src, gamemodesettingsscript g)
        {
            var others = new List<Transform>();
            foreach (var x in g.toggles) if (x != null) others.Add(x.transform);
            foreach (var x in g.sliders) if (x != null) others.Add(x.transform);
            foreach (var x in g.dropdrops) if (x != null && x != src) others.Add(x.transform);
            if (g.imageMapHeight != null) others.Add(g.imageMapHeight.transform);
            var t = src.transform;
            while (t.parent != null && t.parent != g.transform)
            {
                var p = t.parent;
                if (t.GetComponent<LayoutElement>() != null || p.GetComponent<LayoutGroup>() != null) break;   // a row in a layout
                bool shared = false;
                foreach (var o in others) if (o.IsChildOf(p)) { shared = true; break; }
                if (shared) break;
                t = p;
            }
            return t;
        }

        /// In a layout group the copy just takes the next slot. Without one, it goes one row lower and everything
        /// below it in the same container moves down by a row.
        private static string Place(RectTransform row, RectTransform copy, Transform parent)
        {
            if (row == null || copy == null) return "not a RectTransform";
            if (parent.GetComponent<LayoutGroup>() != null) return "layout group " + parent.GetComponent<LayoutGroup>().GetType().Name;
            float h = row.rect.height;
            if (h <= 1f) h = 40f;
            float y = row.anchoredPosition.y;
            copy.anchoredPosition = row.anchoredPosition - new Vector2(0f, h);
            int moved = 0;
            for (int i = 0; i < parent.childCount; i++)
            {
                var c = parent.GetChild(i) as RectTransform;
                if (c == null || c == row || c == copy) continue;
                if (c.anchorMin == row.anchorMin && c.anchorMax == row.anchorMax && c.anchoredPosition.y < y - 1f)
                {
                    c.anchoredPosition -= new Vector2(0f, h);
                    moved++;
                }
            }
            return "no layout group: placed " + h.ToString("0") + " px lower, moved " + moved + " row(s) below it";
        }

        public static string Path(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        public static string Dump(Transform t, int depth)
        {
            var sb = new StringBuilder();
            DumpInto(sb, t, depth);
            return sb.ToString();
        }

        private static void DumpInto(StringBuilder sb, Transform t, int depth)
        {
            if (depth > 12) return;
            sb.Append(' ', depth * 2).Append(t.name).Append(t.gameObject.activeSelf ? "" : " (off)");
            var rt = t as RectTransform;
            if (rt != null) sb.Append(" pos=").Append(rt.anchoredPosition).Append(" size=").Append(rt.rect.size);
            sb.Append(" [");
            foreach (var c in t.GetComponents<Component>())
                if (c != null && !(c is Transform)) sb.Append(c.GetType().Name).Append(' ');
            var tmp = t.GetComponent<TMP_Text>();
            if (tmp != null) sb.Append("text='").Append(tmp.text).Append("' ");
            sb.Append("]\n");
            for (int i = 0; i < t.childCount; i++) DumpInto(sb, t.GetChild(i), depth + 1);
        }
    }
}
