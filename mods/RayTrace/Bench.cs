using System.Collections.Generic;
using UnityEngine;

namespace RayTrace
{
    /// Testing (bridge): frame times since the last Reset, and what this mod spent of them on the CPU.
    public static class Bench
    {
        private static readonly List<float> _frames = new List<float>(4096);
        private static double _recordMs, _refreshMs; private static int _records, _refreshes; private static float _refreshMax;

        public static string Reset() { _frames.Clear(); _recordMs = _refreshMs = 0; _records = _refreshes = 0; _refreshMax = 0; return "reset"; }

        internal static void Frame(float seconds) { if (_frames.Count < 100000) _frames.Add(seconds * 1000f); }
        internal static void Record(double ms) { _recordMs += ms; _records++; }
        internal static void Refresh(float ms) { _refreshMs += ms; _refreshes++; if (ms > _refreshMax) _refreshMax = ms; }

        public static string Report()
        {
            if (_frames.Count == 0) return "no frames";
            var sorted = new List<float>(_frames); sorted.Sort();
            double sum = 0; foreach (var f in _frames) sum += f;
            float P(float q) => sorted[Mathf.Min(sorted.Count - 1, (int)(q * sorted.Count))];
            return $"frames {_frames.Count}, avg {sum / _frames.Count:F2} ms ({1000 * _frames.Count / sum:F1} fps), p50 {P(0.5f):F2}, p95 {P(0.95f):F2}, p99 {P(0.99f):F2}, max {sorted[sorted.Count - 1]:F1} ms" +
                   $" | mod CPU per frame {(_records > 0 ? _recordMs / _records : 0):F3} ms, scene refresh {(_refreshes > 0 ? _refreshMs / _refreshes : 0):F1} ms avg {_refreshMax:F1} max ({_refreshes}x)" +
                   $" | {Native.Status()}";
        }
    }
}
