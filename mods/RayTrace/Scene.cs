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
        public static float Radius = 300f, RefreshSeconds = 1.5f, MinSize = 0.25f;
        /// The game's terrain tiles (100 m) and roads cast no shadows of their own (ShadowCastingMode.Off): renderers that
        /// big are taken anyway, out to twice the radius, so hills shade the valleys.
        public static float GroundSize = 20f;
        public static bool IncludeGround = true;
        internal static Scene Current;
        public static bool SkipCutout = true;
        /// Frames a mesh's GPU buffers and vertex count must stay the same before the rays see it.
        public static int StableFrames = 30;

        private sealed class Entry
        {
            public Renderer Renderer;
            public MeshFilter Filter;
            public bool Ground;
            public Mesh Mesh;
            public int Stream;
            public int StableSince = int.MaxValue;   // the frame its buffers and vertex count were last seen to change
            public IntPtr VertexBuffer, IndexBuffer;
            public uint VertexOffset, VertexStride, VertexCount, VertexFormat, Index32;
            public readonly List<(uint start, uint count, uint baseVertex, int sub)> Submeshes = new List<(uint, uint, uint, int)>();
        }

        private readonly List<Entry> _entries = new List<Entry>();
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
            if (Time.unscaledTime >= _nextRefresh) { _nextRefresh = Time.unscaledTime + RefreshSeconds; Refresh(camera); }
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
            float r2 = Radius * Radius;
            for (int k = 0; k < _entries.Count; k++)
            {
                var e = _entries[k];
                var rd = e.Renderer;
                if (rd == null || e.Mesh == null || e.Filter == null || !rd.enabled || !rd.gameObject.activeInHierarchy) continue;
                if ((rd.bounds.center - camera).sqrMagnitude > (e.Ground ? 4 * r2 : r2) + rd.bounds.extents.sqrMagnitude) continue;
                // The game rebuilds meshes (its terrain tiles as the player moves) and Unity can give a rebuilt mesh new GPU
                // buffers, freeing the old: the buffers are looked up again every frame, a changed mesh described again
                // (before 2026-11-27 a pointer kept from the last refresh crashed the game once the terrain was in).
                if (e.Filter.sharedMesh != e.Mesh || e.Mesh.vertexCount != e.VertexCount)
                {
                    var again = e.Filter.sharedMesh != null ? Describe(rd, e.Filter.sharedMesh) : null;
                    if (again == null) { e.Mesh = null; continue; }
                    again.Ground = e.Ground; again.Filter = e.Filter; again.StableSince = frame; _entries[k] = e = again;
                }
                IntPtr vb = e.Mesh.GetNativeVertexBufferPtr(e.Stream), ib = e.Mesh.GetNativeIndexBufferPtr();
                if (vb != e.VertexBuffer || ib != e.IndexBuffer || e.StableSince == int.MaxValue) { e.VertexBuffer = vb; e.IndexBuffer = ib; e.StableSince = frame; }
                if (e.VertexBuffer == IntPtr.Zero || e.IndexBuffer == IntPtr.Zero) continue;
                // a mesh the game is still building (its terrain while it loads) waits until it has held still
                if (frame - e.StableSince < StableFrames) continue;
                if (n + e.Submeshes.Count > _capacity) break;
                var m = rd.localToWorldMatrix;
                foreach (var s in e.Submeshes)
                {
                    ref var d = ref g[n++];
                    d.Transform[0] = m.m00; d.Transform[1] = m.m01; d.Transform[2] = m.m02; d.Transform[3] = m.m03;
                    d.Transform[4] = m.m10; d.Transform[5] = m.m11; d.Transform[6] = m.m12; d.Transform[7] = m.m13;
                    d.Transform[8] = m.m20; d.Transform[9] = m.m21; d.Transform[10] = m.m22; d.Transform[11] = m.m23;
                    d.VertexBuffer = e.VertexBuffer; d.IndexBuffer = e.IndexBuffer;
                    d.Key = ((ulong)(uint)e.Mesh.GetInstanceID() << 8) | (uint)s.sub;
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

        private void Refresh(Vector3 camera)
        {
            Current = this; _lastCamera = camera;
            var t0 = Time.realtimeSinceStartup;
            // an entry whose renderer still shows the same mesh is kept (with how long it has held still)
            var previous = new Dictionary<Renderer, Entry>();
            foreach (var old in _entries) if (old.Renderer != null && old.Mesh != null) previous[old.Renderer] = old;
            _entries.Clear(); Skipped = 0;
            var all = UnityEngine.Object.FindObjectsOfType<MeshRenderer>();
            Renderers = all.Length;
            float r = Radius + 50f;
            foreach (var rd in all)
            {
                if (!rd.enabled || rd.isPartOfStaticBatch) continue;
                var b = rd.bounds;
                bool ground = b.size.x >= GroundSize || b.size.z >= GroundSize;
                if (rd.shadowCastingMode == ShadowCastingMode.Off && !ground) continue;
                if (ground && !IncludeGround) continue;
                // the distant terrain (DistantTerrain, DistantTerrain2): its shader pushes it down near the camera, so the
                // mesh itself lies over the near terrain, up to 60 m above it (the rays saw a roof over the road)
                if (ground && (rd.name.StartsWith("DistantTerrain", StringComparison.Ordinal) || rd.GetComponentInParent<newFarTerrainScript>() != null)) continue;
                float reach = ground ? r * 2 : r;
                if (b.size.sqrMagnitude < MinSize * MinSize || (b.center - camera).sqrMagnitude > (reach + b.extents.magnitude) * (reach + b.extents.magnitude)) continue;
                var mf = rd.GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null || mesh.vertexCount == 0) continue;
                if (previous.TryGetValue(rd, out var kept) && kept.Mesh == mesh && kept.VertexCount == (uint)mesh.vertexCount) { _entries.Add(kept); continue; }
                var e = Describe(rd, mesh);
                if (e != null) { e.Ground = ground; e.Filter = mf; _entries.Add(e); } else Skipped++;
            }
            Gathered = _entries.Count;
            RefreshMs = (Time.realtimeSinceStartup - t0) * 1000f;
        }

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
                Renderer = rd, Mesh = mesh, Stream = stream, VertexBuffer = vb, IndexBuffer = ib,
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
