using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace BusGut
{
    // Empties the game's big buses (Bus01 incl. its rear section, Bus03) into cargo space. Their bodies are split into
    // separate models, so the passenger fittings can go on their own:
    //   front/rear seats, front/rear seats rod, (front) kapaszkodo / kapaszkodo rear (handrails and poles)
    // The driver seat, cockpit, dashboard, floor, wood trim, steps, doors, windows and all buttons stay.
    // The passenger seat spots (Seats/BusSeat*) are switched off too, so nobody sits in thin air. The collision shapes
    // have no telling names ("Collider (17)"), so a collider is switched off only when it lies entirely inside the space a
    // removed model took; walls and the floor run along the whole bus and never do.
    // Everything is switched off (SetActive(false) / enabled = false), never destroyed: indices the game or a multiplayer
    // mod keeps over a bus's parts stay valid.
    internal static class Gut
    {
        private static readonly Regex Fittings = new Regex(@"^((front|rear) seats( rod)?|(front )?kapaszkodo( rear)?)$", RegexOptions.IgnoreCase);
        private static readonly HashSet<int> done = new HashSet<int>();
        private const float Slack = 0.05f;   // meters a collider may stick out of a removed model's space

        public static int Gutted => done.Count;

        /// Test diagnostics: where each switched-off collider sits (its parent chain below the section root).
        public static readonly List<string> RemovedFrom = new List<string>();

        private static string Owner(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (Transform x = t.parent; x != null && x != root; x = x.parent) parts.Insert(0, x.name);
            return root.name + "/" + string.Join("/", parts);
        }

        // The car script sits on "BusFront" (Bus01, Bus03) or "Bus02"; Bus02 is a van with no passenger fittings.
        public static bool IsBus(carscript car)
        {
            return car != null && car.name.StartsWith("Bus");
        }

        /// Guts a bus once; returns false if it had nothing to remove (the Bus02 van, or already gutted).
        public static bool Apply(carscript car, out string summary)
        {
            summary = "";
            if (!IsBus(car) || !done.Add(car.GetInstanceID())) return false;

            // Bus01's rear section (BusBack) is its own body, held on by a joint: gut it along with the front.
            var roots = new List<Transform> { car.transform };
            Rigidbody body = car.GetComponent<Rigidbody>();
            if (body != null)
            {
                foreach (Joint j in Object.FindObjectsOfType<Joint>())
                {
                    if (j.connectedBody == body && !roots.Contains(j.transform.root)) roots.Add(j.transform.root);
                }
            }

            // 1+2 per section: the fittings' models and the space they take, then every collider entirely inside that
            // space. Measured in the section's own coordinates, so a tilted or moving bus gives the same result on every
            // machine.
            var models = new List<GameObject>();
            int colliders = 0;
            foreach (Transform root in roots)
            {
                var spaces = new List<Bounds>();
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (!Fittings.IsMatch(r.gameObject.name)) continue;
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null && r.gameObject.activeInHierarchy) spaces.Add(InRoot(r.transform, mf.sharedMesh.bounds, root));
                    models.Add(r.gameObject);
                }
                if (spaces.Count == 0) continue;
                foreach (Collider c in root.GetComponentsInChildren<Collider>(true))
                {
                    if (!c.enabled || c.isTrigger || c is WheelCollider || c.GetComponentInParent<seatscript>() != null) continue;
                    // Only the body's own collision shapes: plain colliders in the section's "Colliders" group. Never
                    // parts (hatches, caps), lights, buttons or cargo lying in the bus, even inside the fittings' space.
                    if (c.transform.parent == null || c.transform.parent.name != "Colliders" || c.transform.parent.parent != root) continue;
                    if (c.GetComponents<Component>().Any(x => !(x is Transform) && !(x is Collider))) continue;
                    if (!LocalShape(c, out Bounds shape)) continue;
                    Bounds b = InRoot(c.transform, shape, root);
                    foreach (Bounds space in spaces)
                    {
                        Bounds grown = space;
                        grown.Expand(Slack * 2f);
                        if (grown.Contains(b.min) && grown.Contains(b.max))
                        {
                            c.enabled = false;
                            colliders++;
                            RemovedFrom.Add(Owner(c.transform, root));
                            break;
                        }
                    }
                }
            }
            if (models.Count == 0) return false;
            // 3. Passenger seat spots: every BusSeat* object whose seats are not a driver seat.
            int seats = 0;
            foreach (Transform root in roots)
            {
                foreach (seatscript s in root.GetComponentsInChildren<seatscript>(true))
                {
                    Transform t = s.transform;
                    while (t != null && t != root && !t.name.StartsWith("BusSeat")) t = t.parent;
                    if (t == null || t == root || !t.gameObject.activeSelf) continue;
                    bool driver = false;
                    foreach (seatscript inner in t.GetComponentsInChildren<seatscript>(true)) driver |= inner.driverSeat0 || inner.driverSeat1;
                    if (driver) continue;
                    t.gameObject.SetActive(false);
                    seats++;
                }
            }

            foreach (GameObject m in models) m.SetActive(false);
            summary = car.name + (roots.Count > 1 ? " + rear section" : "") + ": " + models.Count + " fitting models, " + seats + " seat spots, "
                      + colliders + " colliders switched off";
            return true;
        }

        // A collider's own box (in its transform's space).
        private static bool LocalShape(Collider c, out Bounds b)
        {
            switch (c)
            {
                case BoxCollider box: b = new Bounds(box.center, box.size); return true;
                case SphereCollider sph: b = new Bounds(sph.center, Vector3.one * sph.radius * 2f); return true;
                case CapsuleCollider cap:
                {
                    var size = Vector3.one * cap.radius * 2f;
                    size[cap.direction] = Mathf.Max(cap.height, cap.radius * 2f);
                    b = new Bounds(cap.center, size);
                    return true;
                }
                case MeshCollider mc when mc.sharedMesh != null: b = mc.sharedMesh.bounds; return true;
                default: b = default; return false;
            }
        }

        // A box given in t's space, as an axis-aligned box in root's space.
        private static Bounds InRoot(Transform t, Bounds local, Transform root)
        {
            Vector3 c = local.center, e = local.extents;
            var result = new Bounds(root.InverseTransformPoint(t.TransformPoint(c + new Vector3(-e.x, -e.y, -e.z))), Vector3.zero);
            for (int i = 1; i < 8; i++)
            {
                var corner = c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                result.Encapsulate(root.InverseTransformPoint(t.TransformPoint(corner)));
            }
            return result;
        }
    }
}