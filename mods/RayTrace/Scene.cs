using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace RayTrace
{
    /// The meshes the shadow rays can hit: every enabled MeshRenderer near the camera that casts shadows, opaque
    /// submeshes only (cut-out leaves and glass would cast solid shadows: the rays see triangles, not textures). The
    /// list of renderers is gathered every RefreshSeconds; their transforms are read every frame (cars move).
    internal sealed class Scene
    {
        public static float Radius = 300f, RefreshSeconds = 3f, MinSize = 0.25f;
        /// Within this many metres a renderer's transform and visibility are read every frame; further away every FarFrames
        /// (a far car's shadow follows it a few frames late; reading all 1700 every frame cost 2.5 ms, 2026-11-27).
        public static float NearMetres = 100f;
        public static int FarFrames = 10;
        /// The game's terrain tiles (100 m) and roads cast no shadows of their own (ShadowCastingMode.Off): renderers that
        /// big are taken anyway, out to twice the radius, so hills shade the valleys.
        public static float GroundSize = 20f;
        public static bool IncludeGround = true;
        internal static Scene Current;
        public static bool SkipCutout = true;
        /// Frames a mesh's GPU buffers and vertex count must stay the same before the rays see it.
        public static int StableFrames = 30;
        /// A settled mesh's buffers are looked up again every this many frames (staggered across the meshes): looking up
        /// every mesh's every frame cost 11 ms a frame (2026-11-27). The native side reads a mesh's buffers only when it
        /// builds its structure, and only with the pointer of that frame.
        public static int RecheckFrames = 30;
        /// Renderers sorted per frame while the list is gathered again (one pass at once took 12 ms every refresh).
        public static int RefreshSlice = 1500;

        private sealed class Entry
        {
            public Renderer Renderer;
            public MeshFilter Filter;
            public bool Ground;
            public Mesh Mesh;
            public int Stream;
            public int StableSince = int.MaxValue;   // the frame its buffers and vertex count were last seen to change
            public IntPtr VertexBuffer, IndexBuffer;
            public ulong KeyBase;
            public Vector3 Center;          // where its bounds were at the refresh
            public bool Shown;              // enabled and active, as last read
            public Matrix4x4 Matrix;        // its transform, as last read
            public uint VertexOffset, VertexStride, VertexCount, VertexFormat, Index32;
            public readonly List<(uint start, uint count, uint baseVertex, int sub)> Submeshes = new List<(uint, uint, uint, int)>();
        }

        private List<Entry> _entries = new List<Entry>(), _next = new List<Entry>();
        private MeshRenderer[] _pending;
        private int _pendingAt;
        private Dictionary<Renderer, Entry> _previous = new Dictionary<Renderer, Entry>();
        private float _nextRefresh;
        public int Renderers, Gathered, Skipped;
        public float RefreshMs;

        private int _capacity;
        private readonly List<(IntPtr block, int frame)> _retired = new List<(IntPtr, int)>();

        /// This frame's geometries into unmanaged memory, the ring slot's block. Outgrown blocks are freed 16 frames later:
        /// the render thread can still be reading an older slot.
        public unsafe int Write(Vector3 camera, out IntPtr geometries, ref IntPtr[] ring, int slot, int frame)
        {
            for (int i = _retired.Count - 1; i >= 0; i--)
                if (frame - _retired[i].frame > 16) { Marshal.FreeHGlobal(_retired[i].block); _retired.RemoveAt(i); }
            Refresh(camera);
            int need = 0;
            foreach (var e in _entries) need += e.Submeshes.Count;
            if (ring[slot] == IntPtr.Zero || _capacity < need)
            {
                _capacity = Math.Max(need + need / 2 + 64, _capacity);
                for (int i = 0; i < ring.Length; i++) { if (ring[i] != IntPtr.Zero) _retired.Add((ring[i], frame)); ring[i] = IntPtr.Zero; }
                for (int i = 0; i < ring.Length; i++) ring[i] = Marshal.AllocHGlobal(_capacity * sizeof(Native.Geometry));
            }
            geometries = ring[slot];
            var g = (Native.Geometry*)geometries;
            int n = 0;
            for (int k = 0; k < _entries.Count; k++)
            {
                var e = _entries[k];
                var rd = e.Renderer;
                bool fresh = (e.Center - camera).sqrMagnitude < NearMetres * NearMetres || (k + frame) % FarFrames == 0 || e.StableSince == int.MaxValue;
                if (fresh)
                {
                    if (rd == null || e.Mesh == null || e.Filter == null) { e.Shown = false; continue; }
                    e.Shown = rd.enabled && rd.gameObject.activeInHierarchy;
                    if (e.Shown) e.Matrix = rd.localToWorldMatrix;
                }
                if (!e.Shown) continue;
                // The game rebuilds meshes (its terrain tiles as the player moves) and Unity can give a rebuilt mesh new GPU
                // buffers, freeing the old: a mesh that is settling is looked at every frame, a settled one every
                // RecheckFrames, and a changed mesh is described again
                bool settling = e.StableSince == int.MaxValue || frame - e.StableSince < StableFrames;
                if (settling || (k + frame) % RecheckFrames == 0)
                {
                    if (rd == null || e.Filter == null || e.Mesh == null) { e.Shown = false; continue; }   // gone since its last look
                    if (e.Filter.sharedMesh != e.Mesh || e.Mesh.vertexCount != e.VertexCount)
                    {
                        var again = e.Filter.sharedMesh != null ? Describe(rd, e.Filter.sharedMesh) : null;
                        if (again == null) { e.Mesh = null; continue; }
                        again.Ground = e.Ground; again.Filter = e.Filter; again.StableSince = frame; _entries[k] = e = again;
                    }
                    IntPtr vb = e.Mesh.GetNativeVertexBufferPtr(e.Stream), ib = e.Mesh.GetNativeIndexBufferPtr();
                    if (vb != e.VertexBuffer || ib != e.IndexBuffer || e.StableSince == int.MaxValue) { e.VertexBuffer = vb; e.IndexBuffer = ib; e.StableSince = frame; }
                }
                if (e.VertexBuffer == IntPtr.Zero || e.IndexBuffer == IntPtr.Zero) continue;
                // a mesh the game is still building (its terrain while it loads) waits until it has held still
                if (frame - e.StableSince < StableFrames) continue;
                if (n + e.Submeshes.Count > _capacity) break;
                var m = e.Matrix;
                foreach (var s in e.Submeshes)
                {
                    ref var d = ref g[n++];
                    d.Transform[0] = m.m00; d.Transform[1] = m.m01; d.Transform[2] = m.m02; d.Transform[3] = m.m03;
                    d.Transform[4] = m.m10; d.Transform[5] = m.m11; d.Transform[6] = m.m12; d.Transform[7] = m.m13;
                    d.Transform[8] = m.m20; d.Transform[9] = m.m21; d.Transform[10] = m.m22; d.Transform[11] = m.m23;
                    d.VertexBuffer = e.VertexBuffer; d.IndexBuffer = e.IndexBuffer;
                    d.Key = e.KeyBase | (uint)s.sub;
                    d.VertexOffset = e.VertexOffset + s.baseVertex * e.VertexStride;
                    d.VertexStride = e.VertexStride; d.VertexCount = e.VertexCount - s.baseVertex; d.VertexFormat = e.VertexFormat;
                    d.IndexStart = s.start; d.IndexCount = s.count; d.Index32 = e.Index32; d.Pad = 0;
                }
            }
            return n;
        }

        /// Testing (bridge): the ground meshes within `within` m of the camera, by name, with their height range and shader.
        public static string Ground(float within)
        {
            var cam = Current?._lastCamera ?? Vector3.zero;
            var names = new SortedDictionary<string, int>();
            foreach (var e in Current?._entries ?? new List<Entry>())
            {
                if (!e.Ground || e.Renderer == null) continue;
                var b = e.Renderer.bounds;
                if ((b.ClosestPoint(cam) - cam).magnitude > within) continue;
                var mat = e.Renderer.sharedMaterial;
                string key = $"{e.Renderer.name} [{(mat != null ? mat.shader.name : "-")}] y {b.min.y - cam.y:F1}..{b.max.y - cam.y:F1} cast {e.Renderer.shadowCastingMode}";
                names.TryGetValue(key, out int n); names[key] = n + 1;
            }
            var sb = new System.Text.StringBuilder();
            foreach (var kv in names) sb.Append(kv.Value).Append("x ").Append(kv.Key).Append(" | ");
            return sb.ToString();
        }
        private Vector3 _lastCamera;

        /// The renderers are gathered again every RefreshSeconds: FindObjectsOfType once, then RefreshSlice of them sorted
        /// per frame into the next list, which replaces the current one when it is complete. An entry whose renderer
        /// still shows the same mesh is kept (with how long it has held still).
        private void Refresh(Vector3 camera)
        {
            Current = this; _lastCamera = camera;
            var t0 = Time.realtimeSinceStartup;
            if (_pending == null)
            {
                if (Time.unscaledTime < _nextRefresh) return;
                _nextRefresh = Time.unscaledTime + RefreshSeconds;
                _previous.Clear();
                foreach (var old in _entries) if (old.Renderer != null && old.Mesh != null) _previous[old.Renderer] = old;
                _next.Clear(); _skipping = 0;
                _pending = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
                _pendingAt = 0;
                Renderers = _pending.Length;
            }
            float r = Radius + 50f;
            int end = Math.Min(_pending.Length, _pendingAt + RefreshSlice);
            for (; _pendingAt < end; _pendingAt++)
            {
                var rd = _pending[_pendingAt];
                if (rd == null || !rd.enabled || rd.isPartOfStaticBatch) continue;
                var b = rd.bounds;
                bool ground = b.size.x >= GroundSize || b.size.z >= GroundSize;
                if (rd.shadowCastingMode == ShadowCastingMode.Off && !ground) continue;
                if (ground && !IncludeGround) continue;
                float reach = ground ? r * 2 : r;
                if (b.size.sqrMagnitude < MinSize * MinSize || (b.center - camera).sqrMagnitude > (reach + b.extents.magnitude) * (reach + b.extents.magnitude)) continue;
                // the distant terrain (DistantTerrain, DistantTerrain2): its shader pushes it down near the camera, so the
                // mesh itself lies over the near terrain, up to 60 m above it (the rays saw a roof over the road)
                if (ground && (rd.name.StartsWith("DistantTerrain", StringComparison.Ordinal) || rd.GetComponentInParent<newFarTerrainScript>() != null)) continue;
                var mf = rd.GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null || mesh.vertexCount == 0) continue;
                if (_previous.TryGetValue(rd, out var kept) && kept.Mesh == mesh && kept.VertexCount == (uint)mesh.vertexCount) { kept.Center = b.center; _next.Add(kept); continue; }
                var e = Describe(rd, mesh);
                if (e != null) { e.Ground = ground; e.Filter = mf; e.Center = b.center; _next.Add(e); } else _skipping++;
            }
            if (_pendingAt >= _pending.Length)
            {
                (_entries, _next) = (_next, _entries);
                _next.Clear(); _pending = null; _previous.Clear();
                Gathered = _entries.Count; Skipped = _skipping;
            }
            RefreshMs = (Time.realtimeSinceStartup - t0) * 1000f;
            Bench.Refresh(RefreshMs);
        }
        private int _skipping;

        private static Entry Describe(Renderer rd, Mesh mesh)
        {
            if (!mesh.HasVertexAttribute(VertexAttribute.Position)) return null;
            int stream = mesh.GetVertexAttributeStream(VertexAttribute.Position);
            var format = mesh.GetVertexAttributeFormat(VertexAttribute.Position);
            int dim = mesh.GetVertexAttributeDimension(VertexAttribute.Position);
            uint dxgi;
            if (format == VertexAttributeFormat.Float32 && dim >= 3) dxgi = 6;          // DXGI_FORMAT_R32G32B32_FLOAT
            else if (format == VertexAttributeFormat.Float16 && dim == 4) dxgi = 10;   // DXGI_FORMAT_R16G16B16A16_FLOAT
            else return null;
            IntPtr vb = mesh.GetNativeVertexBufferPtr(stream), ib = mesh.GetNativeIndexBufferPtr();
            if (vb == IntPtr.Zero || ib == IntPtr.Zero) return null;
            var e = new Entry
            {
                Renderer = rd, Mesh = mesh, Stream = stream, VertexBuffer = vb, IndexBuffer = ib, KeyBase = (ulong)(uint)mesh.GetInstanceID() << 8,
                VertexOffset = (uint)mesh.GetVertexAttributeOffset(VertexAttribute.Position), VertexStride = (uint)mesh.GetVertexBufferStride(stream),
                VertexCount = (uint)mesh.vertexCount, VertexFormat = dxgi, Index32 = mesh.indexFormat == IndexFormat.UInt32 ? 1u : 0u,
            };
            var mats = rd.sharedMaterials;
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                var sm = mesh.GetSubMesh(i);
                if (sm.topology != MeshTopology.Triangles || sm.indexCount < 3) continue;
                var mat = i < mats.Length ? mats[i] : null;
                if (mat == null) continue;
                int queue = mat.renderQueue;
                if (queue >= 3000 || (SkipCutout && queue >= 2450)) continue;   // transparent, cut-out
                e.Submeshes.Add(((uint)sm.indexStart, (uint)sm.indexCount, (uint)Math.Max(0, sm.baseVertex), i));
            }
            return e.Submeshes.Count > 0 ? e : null;
        }
    }
}
