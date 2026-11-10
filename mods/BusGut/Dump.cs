using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace BusGut
{
    // Development aid (launch option -busgutdump): writes every bus prefab's object tree - names, components, active
    // state, local position and renderer bounds - to BusGut-dump-<prefab>.txt in the game folder, then quits.
    internal static class Dump
    {
        public static bool Requested => System.Environment.GetCommandLineArgs().Contains("-busgutdump");

        public static IEnumerator Run(System.Action<string> log)
        {
            float giveUp = Time.realtimeSinceStartup + 120f;
            while ((itemdatabase.s == null || itemdatabase.s.items == null) && Time.realtimeSinceStartup < giveUp)
            {
                yield return new WaitForSecondsRealtime(1f);
            }
            int n = 0;
            if (itemdatabase.s != null)
            {
                foreach (GameObject g in itemdatabase.s.items)
                {
                    if (g == null || !g.name.ToLowerInvariant().StartsWith("bus") || g.GetComponentInChildren<carscript>(true) == null) continue;
                    var sb = new StringBuilder();
                    Write(g.transform, 0, sb);
                    File.WriteAllText(Path.Combine(BepInEx.Paths.GameRootPath, "BusGut-dump-" + g.name + ".txt"), sb.ToString());
                    n++;
                }
            }
            log("BusGut: dumped " + n + " bus prefabs");
            yield return new WaitForSecondsRealtime(1f);
            Application.Quit();
        }

        private static void Write(Transform t, int depth, StringBuilder sb)
        {
            string comps = string.Join(",", t.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name));
            var r = t.GetComponent<Renderer>();
            string size = r != null ? " size " + r.bounds.size.ToString("F2") : "";
            sb.Append(' ', depth * 2).Append(t.name).Append(t.gameObject.activeSelf ? "" : " [off]")
              .Append(" @").Append(t.localPosition.ToString("F2")).Append(size).Append(" {").Append(comps).AppendLine("}");
            foreach (Transform c in t) Write(c, depth + 1, sb);
        }
    }
}
