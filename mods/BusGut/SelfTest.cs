using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace BusGut
{
    // Unattended test (launch option -busguttest): new world; for Bus03 and Bus01 spawns the bus, photographs the aisle,
    // probes it with rays at seat height, guts it, photographs and probes again; writes BusGut-*.png and a report to the
    // game folder, then quits.
    internal static class SelfTest
    {
        public static bool Requested => System.Environment.GetCommandLineArgs().Contains("-busguttest");
        public static bool Running;
        private static readonly List<string> report = new List<string>();

        public static IEnumerator Run(System.Action<string> log)
        {
            Running = true;
            Application.runInBackground = true;
            float giveUp = Time.realtimeSinceStartup + 120f;
            while (menuhandler.s == null && Time.realtimeSinceStartup < giveUp) yield return new WaitForSecondsRealtime(1f);
            yield return new WaitForSecondsRealtime(5f);
            menuhandler.s.PressedStart();
            giveUp = Time.realtimeSinceStartup + 180f;
            while ((mainscript.s == null || mainscript.s.player == null) && Time.realtimeSinceStartup < giveUp) yield return new WaitForSecondsRealtime(1f);
            yield return new WaitForSecondsRealtime(10f);
            log("BusGut test: in the world");

            foreach (string name in new[] { "Bus03Full", "Bus01Full" })
            {
                GameObject prefab = System.Array.Find(itemdatabase.s.items, g => g != null && g.name == name);
                if (prefab == null) { report.Add(name + ": not found"); continue; }
                var before = new HashSet<carscript>(Object.FindObjectsOfType<carscript>());
                kaposztaleves.s.Spawn(prefab, Color.white);
                yield return new WaitForSecondsRealtime(4f);
                carscript bus = Object.FindObjectsOfType<carscript>().FirstOrDefault(c => !before.Contains(c) && Gut.IsBus(c));
                if (bus == null) { report.Add(name + ": spawned, but no bus found"); continue; }
                // hold the bus (and Bus01's jointed rear section) still for the photos
                var held = Object.FindObjectsOfType<Rigidbody>().Where(x => x.transform.root == bus.transform.root || (x.GetComponent<Joint>() != null && x.GetComponent<Joint>().connectedBody == bus.GetComponent<Rigidbody>())).ToList();
                foreach (var h in held) h.isKinematic = true;

                Renderer seats = bus.GetComponentsInChildren<Renderer>(true).FirstOrDefault(r => r.name == "front seats");
                if (seats == null) { report.Add(name + ": no 'front seats' model"); continue; }
                Bounds b = seats.bounds;
                Vector3 fwd = bus.transform.forward, up = bus.transform.up;
                float half = Mathf.Abs(Vector3.Dot(b.extents, new Vector3(Mathf.Abs(fwd.x), Mathf.Abs(fwd.y), Mathf.Abs(fwd.z))));
                // Standing eye height at the back of the front section, looking toward the driver (BusFront local axes).
                Vector3 eye = bus.transform.TransformPoint(name.StartsWith("Bus01") ? new Vector3(0f, 2.4f, -3.2f) : new Vector3(0f, 2.0f, -4.6f));
                Vector3 look = bus.transform.TransformPoint(new Vector3(0f, 1.1f, 4.6f));

                Photo(eye, look, name + "-before");
                int hitsBefore = Probe(bus, b, fwd, up, half);
                string shellBefore = Shell(bus);
                Gut.Apply(bus, out string summary);
                yield return new WaitForSecondsRealtime(1f);
                Photo(eye, look, name + "-after");
                int hitsAfter = Probe(bus, b, fwd, up, half);
                string shellAfter = Shell(bus);
                report.Add(name + ": " + summary);
                report.Add("  aisle probe (9 rays at seat height, front to back): bus parts hit before " + hitsBefore + ", after " + hitsAfter);
                report.Add("  floor and walls solid at 5 points along the bus (floor/left/right): before " + shellBefore + ", after " + shellAfter);
                foreach (var g in Gut.RemovedFrom.GroupBy(x => x).OrderByDescending(x => x.Count())) report.Add("    colliders off under " + g.Key + ": " + g.Count());
                Gut.RemovedFrom.Clear();
                log("BusGut test: " + summary);
                foreach (var h in held) h.isKinematic = false;
            }
            File.WriteAllLines(Path.Combine(BepInEx.Paths.GameRootPath, "BusGut-test.txt"), report.ToArray());
            log("BusGut test: report written");
            Running = false;
            yield return new WaitForSecondsRealtime(1f);
            Application.Quit();
        }

        // Rays along the bus at seat height across its width: how many of the bus's own colliders block them.
        private static int Probe(carscript bus, Bounds seats, Vector3 fwd, Vector3 up, float half)
        {
            int hits = 0;
            Vector3 right = Vector3.Cross(up, fwd).normalized;
            foreach (float x in new[] { -0.8f, -0.4f, 0f, 0.4f, 0.8f, -0.6f, -0.2f, 0.2f, 0.6f })
            {
                Vector3 from = seats.center - fwd * (half + 0.2f) + right * x + up * 0.05f;
                foreach (RaycastHit h in Physics.RaycastAll(from, fwd, half * 2f + 0.4f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (h.collider.transform.root == bus.transform.root) hits++;
                }
            }
            return hits;
        }

        // At 5 points along the front section: does a ray hit the bus floor below, and the left and right walls?
        private static string Shell(carscript bus)
        {
            Transform t = bus.transform;
            int floor = 0, left = 0, right = 0;
            foreach (float z in new[] { -4f, -2f, 0f, 2f, 4f })
            {
                Vector3 p = t.TransformPoint(new Vector3(0f, 1.2f, z));
                if (HitsBus(p, -t.up, 3f, bus)) floor++;
                if (HitsBus(p, -t.right, 2f, bus)) left++;
                if (HitsBus(p, t.right, 2f, bus)) right++;
            }
            return floor + "/" + left + "/" + right;
        }

        private static bool HitsBus(Vector3 from, Vector3 dir, float dist, carscript bus)
        {
            foreach (RaycastHit h in Physics.RaycastAll(from, dir, dist, ~0, QueryTriggerInteraction.Ignore))
            {
                if (h.collider.transform.root == bus.transform.root) return true;
            }
            return false;
        }

        private static void Photo(Vector3 eye, Vector3 look, string name)
        {
            var go = new GameObject("BusGutCamera");
            var cam = go.AddComponent<Camera>();
            cam.transform.position = eye;
            cam.transform.LookAt(look);
            cam.fieldOfView = 80f;
            cam.nearClipPlane = 0.05f;
            var lamp = go.AddComponent<Light>();   // the bus is dark inside
            lamp.type = LightType.Point;
            lamp.range = 14f;
            lamp.intensity = 2.5f;
            var rt = new RenderTexture(1280, 720, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(Path.Combine(BepInEx.Paths.GameRootPath, "BusGut-" + name + ".png"), tex.EncodeToPNG());
            cam.targetTexture = null;
            Object.Destroy(rt);
            Object.Destroy(tex);
            Object.Destroy(go);
        }
    }
}
