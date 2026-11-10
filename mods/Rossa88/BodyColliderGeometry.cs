using System;
using System.Collections.Generic;
using UnityEngine;

namespace Rossa88
{
    // Pure geometry, always in Root coordinates. Clip BEFORE taking a hull, so a hull
    // cannot bridge a wheel opening or the cockpit, even when a triangle crosses it.
    internal static class BodyColliderGeometry
    {
        internal struct Triangle
        {
            public Vector3 A, B, C;
            public Triangle(Vector3 a, Vector3 b, Vector3 c) { A = a; B = b; C = c; }
        }

        internal static Bounds BoundsOf(List<Vector3> points)
        {
            var b = new Bounds(points[0], Vector3.zero);
            for (int i = 1; i < points.Count; i++) b.Encapsulate(points[i]);
            return b;
        }

        internal static List<Vector3> Clip(Triangle t, Bounds box)
        {
            var p = new List<Vector3> { t.A, t.B, t.C };
            for (int axis = 0; axis < 3 && p.Count > 0; axis++)
            {
                p = ClipPlane(p, axis, box.min[axis], true);
                p = ClipPlane(p, axis, box.max[axis], false);
            }
            return p;
        }

        private static List<Vector3> ClipPlane(List<Vector3> p, int axis, float value, bool greater)
        {
            var result = new List<Vector3>();
            if (p.Count == 0) return result;
            Vector3 a = p[p.Count - 1];
            float da = (a[axis] - value) * (greater ? 1f : -1f);
            foreach (Vector3 b in p)
            {
                float db = (b[axis] - value) * (greater ? 1f : -1f);
                if ((da >= 0f) != (db >= 0f)) result.Add(a + (b - a) * (da / (da - db)));
                if (db >= 0f) result.Add(b);
                a = b;
                da = db;
            }
            return result;
        }

        internal static Bounds Box(Vector3 min, Vector3 max) => new Bounds((min + max) * .5f, max - min);

        // Six disjoint slabs cover source minus cut. Contact surfaces may touch;
        // their interiors never overlap. The same operation is used for support boxes.
        internal static List<Bounds> Subtract(Bounds source, Bounds cut)
        {
            Vector3 lo = Vector3.Max(source.min, cut.min), hi = Vector3.Min(source.max, cut.max);
            var result = new List<Bounds>();
            if (lo.x >= hi.x || lo.y >= hi.y || lo.z >= hi.z) { result.Add(source); return result; }
            Vector3 a = source.min, b = source.max;
            for (int axis = 0; axis < 3; axis++)
            {
                if (a[axis] < lo[axis])
                {
                    Vector3 end = b; end[axis] = lo[axis];
                    result.Add(Box(a, end)); a[axis] = lo[axis];
                }
                if (b[axis] > hi[axis])
                {
                    Vector3 start = a; start[axis] = hi[axis];
                    result.Add(Box(start, b)); b[axis] = hi[axis];
                }
            }
            return result;
        }

        private struct Face
        {
            public int A, B, C;
            public Vector3 Normal;
            public Face(int a, int b, int c, List<Vector3> p, Vector3 inside)
            {
                A = a; B = b; C = c;
                Normal = Vector3.Cross(p[b] - p[a], p[c] - p[a]).normalized;
                if (Vector3.Dot(Normal, inside - p[a]) > 0f)
                { B = c; C = b; Normal = -Normal; }
            }
        }

        // Max 48 vertices => max 92 triangular faces for a closed convex hull.
        // Directional extrema preserve the outline; farthest-point sampling adds detail.
        private static List<Vector3> Sample(List<Vector3> source)
        {
            var p = new List<Vector3>();
            for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                    for (int z = -1; z <= 1; z++)
                    {
                        if (x == 0 && y == 0 && z == 0) continue;
                        Vector3 d = new Vector3(x, y, z), best = source[0];
                        float score = Vector3.Dot(best, d);
                        foreach (Vector3 v in source)
                            if (Vector3.Dot(v, d) > score) { best = v; score = Vector3.Dot(v, d); }
                        AddUnique(p, best);
                    }
            while (p.Count < 48)
            {
                float bestDistance = .000004f;
                Vector3 best = Vector3.zero;
                foreach (Vector3 v in source)
                {
                    float distance = float.MaxValue;
                    foreach (Vector3 q in p) distance = Math.Min(distance, (v - q).sqrMagnitude);
                    if (distance > bestDistance) { bestDistance = distance; best = v; }
                }
                if (bestDistance <= .000004f) break;
                p.Add(best);
            }
            return p;
        }

        private static void AddUnique(List<Vector3> p, Vector3 v)
        {
            foreach (Vector3 q in p) if ((q - v).sqrMagnitude < .00000001f) return;
            p.Add(v);
        }

        internal static bool Hull(List<Vector3> source, out Vector3[] vertices, out int[] triangles)
        {
            vertices = null; triangles = null;
            List<Vector3> p = Sample(source);
            if (p.Count < 4) return false;
            int b = 1, c = -1, d = -1;
            float best = 0f;
            for (int i = 1; i < p.Count; i++)
                if ((p[i] - p[0]).sqrMagnitude > best) { b = i; best = (p[i] - p[0]).sqrMagnitude; }
            best = 0f;
            for (int i = 1; i < p.Count; i++)
            {
                float area = Vector3.Cross(p[b] - p[0], p[i] - p[0]).sqrMagnitude;
                if (area > best) { c = i; best = area; }
            }
            if (c < 0 || best < 1e-10f) return false;
            Vector3 n = Vector3.Cross(p[b] - p[0], p[c] - p[0]).normalized;
            best = .002f;
            for (int i = 1; i < p.Count; i++)
            {
                float distance = Math.Abs(Vector3.Dot(n, p[i] - p[0]));
                if (distance > best) { d = i; best = distance; }
            }
            if (d < 0) return false; // a thin pane is better represented by a small box
            Vector3 inside = (p[0] + p[b] + p[c] + p[d]) * .25f;
            var faces = new List<Face>
            {
                new Face(0, b, c, p, inside), new Face(0, d, b, p, inside),
                new Face(0, c, d, p, inside), new Face(b, d, c, p, inside)
            };
            for (int i = 0; i < p.Count; i++)
            {
                if (i == 0 || i == b || i == c || i == d) continue;
                var edges = new Dictionary<long, int[]>();
                for (int f = faces.Count - 1; f >= 0; f--)
                {
                    Face face = faces[f];
                    if (Vector3.Dot(face.Normal, p[i] - p[face.A]) <= .000001f) continue;
                    Edge(edges, face.A, face.B); Edge(edges, face.B, face.C); Edge(edges, face.C, face.A);
                    faces.RemoveAt(f);
                }
                foreach (int[] edge in edges.Values) faces.Add(new Face(edge[0], edge[1], i, p, inside));
            }
            // Reject numerical degeneracies instead of asking PhysX to repair a bad hull.
            if (faces.Count < 4 || faces.Count > 92) return false;
            var edgeCounts = new Dictionary<long, int>();
            foreach (Face f in faces)
            {
                // Unity normalizes very short vectors to zero (magnitude <= 1e-5).
                if (f.Normal.sqrMagnitude < .5f || Vector3.Cross(p[f.B] - p[f.A], p[f.C] - p[f.A]).sqrMagnitude < 1e-10f) return false;
                foreach (Vector3 v in p) if (Vector3.Dot(f.Normal, v - p[f.A]) > .0001f) return false;
                CountEdge(edgeCounts, f.A, f.B); CountEdge(edgeCounts, f.B, f.C); CountEdge(edgeCounts, f.C, f.A);
            }
            foreach (int count in edgeCounts.Values) if (count != 2) return false;
            var indices = new List<int>();
            foreach (Face f in faces) { indices.Add(f.A); indices.Add(f.B); indices.Add(f.C); }
            vertices = p.ToArray(); triangles = indices.ToArray();
            return true;
        }

        private static void Edge(Dictionary<long, int[]> edges, int a, int b)
        {
            long key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
            if (edges.ContainsKey(key)) edges.Remove(key);
            else edges.Add(key, new[] { a, b });
        }

        private static void CountEdge(Dictionary<long, int> edges, int a, int b)
        {
            long key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
            edges[key] = edges.TryGetValue(key, out int count) ? count + 1 : 1;
        }
    }
}
