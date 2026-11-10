using System;
using System.Collections.Generic;
using UnityEngine;

namespace Rossa88
{
    // Pure managed fitting. Opening/exit boxes stay in their subtracted cells;
    // body boxes may overlap grid seams slightly, but never a wheel/cabin void.
    internal static class ColliderBoxGeometry
    {
        internal struct Shape
        {
            internal Vector3 Center, Size, Right, Up, Forward;
            internal Vector3 Corner(int i) => Center + Right * (Size.x * ((i & 1) == 0 ? -.5f : .5f)) +
                Up * (Size.y * ((i & 2) == 0 ? -.5f : .5f)) + Forward * (Size.z * ((i & 4) == 0 ? -.5f : .5f));
        }

        internal static Shape Fit(List<Vector3> triangles, Bounds cell, float thickness, IList<Bounds> holes = null)
        {
            Shape best = Project(triangles, Vector3.right, Vector3.up, Vector3.forward, 0);
            // Thicken flat panes inward at cell boundaries, as the old box fallback did.
            Vector3 lo = best.Center - best.Size * .5f, hi = best.Center + best.Size * .5f;
            for (int axis = 0; axis < 3; axis++)
                if (hi[axis] - lo[axis] < thickness)
                {
                    lo[axis] = Math.Max(cell.min[axis], Math.Min(best.Center[axis] - thickness * .5f, cell.max[axis] - thickness));
                    hi[axis] = Math.Min(cell.max[axis], lo[axis] + thickness);
                }
            best.Center = (lo + hi) * .5f; best.Size = hi - lo;
            var tried = new List<Shape>();
            for (int i = 0; i + 2 < triangles.Count && tried.Count < 24; i += 3)
            {
                Vector3 edge = triangles[i + 1] - triangles[i];
                Vector3 normal = Vector3.Cross(edge, triangles[i + 2] - triangles[i]);
                if (edge.sqrMagnitude < 1e-10f || normal.sqrMagnitude < 1e-12f) continue;
                edge /= (float)Math.Sqrt(edge.sqrMagnitude); normal /= (float)Math.Sqrt(normal.sqrMagnitude);
                Vector3 forward = Vector3.Cross(edge, normal);
                bool duplicate = false;
                foreach (var old in tried)
                    if (Math.Abs(Vector3.Dot(old.Right, edge)) > .9999f && Math.Abs(Vector3.Dot(old.Up, normal)) > .9999f)
                    { duplicate = true; break; }
                if (duplicate) continue;
                var fit = Project(triangles, edge, normal, forward, thickness); tried.Add(fit);
                if (Volume(fit) >= Volume(best) * .98f) continue;
                // Body grid boundaries are arbitrary. Permit a 2 cm overlap there
                // to follow sloping sheet metal, but never overlap an actual void.
                Bounds limit = cell;
                if (holes != null) limit.Expand(.04f);
                bool safe = true;
                var envelope = new Bounds(fit.Corner(0), Vector3.zero);
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 p = fit.Corner(corner); envelope.Encapsulate(p);
                    if (!Contains(limit, p, .000002f)) { safe = false; break; }
                }
                if (safe && holes != null)
                    foreach (var hole in holes)
                        if (Overlap(envelope, hole)) { safe = false; break; }
                if (safe) best = fit;
            }
            return best;
        }

        private static Shape Project(List<Vector3> points, Vector3 x, Vector3 y, Vector3 z, float thickness)
        {
            Vector3 lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), hi = -lo;
            foreach (var p in points)
            {
                var q = new Vector3(Vector3.Dot(p, x), Vector3.Dot(p, y), Vector3.Dot(p, z));
                lo = Vector3.Min(lo, q); hi = Vector3.Max(hi, q);
            }
            var c = (lo + hi) * .5f;
            return new Shape { Center = x * c.x + y * c.y + z * c.z,
                Size = Vector3.Max(hi - lo, Vector3.one * thickness), Right = x, Up = y, Forward = z };
        }

        internal static float Volume(Shape b) => b.Size.x * b.Size.y * b.Size.z;
        private static bool Overlap(Bounds a, Bounds b) =>
            a.max.x > b.min.x + 1e-5f && a.min.x < b.max.x - 1e-5f &&
            a.max.y > b.min.y + 1e-5f && a.min.y < b.max.y - 1e-5f &&
            a.max.z > b.min.z + 1e-5f && a.min.z < b.max.z - 1e-5f;
        internal static bool Contains(Bounds b, Vector3 p, float epsilon) =>
            p.x >= b.min.x - epsilon && p.x <= b.max.x + epsilon &&
            p.y >= b.min.y - epsilon && p.y <= b.max.y + epsilon &&
            p.z >= b.min.z - epsilon && p.z <= b.max.z + epsilon;
    }
}
