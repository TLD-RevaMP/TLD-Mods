using System;
using System.Collections.Generic;
using System.Linq;
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
        /// The layers the player's camera draws: meshes on other layers only cast the game's shadows (the house's ramp has
        /// an Unlit/Color collider mesh over its planks: the bounced-light rays lit the ramp with its own colour)
        public static int CullingMask = ~0;
        /// The ray traced shadows replace the game's (Tracer mode 1): a traced renderer whose submeshes are all traced as
        /// opaque stops casting into the game's shadow map (put back when this is off, or when it leaves the scene). The
        /// game's map keeps what the rays can't see: cut-out leaves, glass, animated characters.
        public static bool TakeShadows;
        private readonly Dictionary<Renderer, ShadowCastingMode> _taken = new Dictionary<Renderer, ShadowCastingMode>();
        public int Taken => _taken.Count;
        /// Frames a mesh's GPU buffers and vertex count must stay the same before the rays see it.
        public static int StableFrames = 30;
        /// A settled mesh's buffers are looked up again every this many frames (staggered across the meshes): looking up
        /// every mesh's every frame cost 11 ms a frame (2026-11-27). The native side reads a mesh's buffers only when it
        /// builds its structure, and only with the pointer of that frame.
        public static int RecheckFrames = 30;
        /// Renderers sorted per frame while the list is gathered again (one pass at once took 12 ms every refresh).
        public static int RefreshSlice = 1500;

        internal sealed class Sub
        {
            public uint start, count, baseVertex; public int sub;
            public float smoothness, metallic; public Color albedo; public bool seeThrough;
        }

        /// Shine: a material's smoothness times this. Reflections are added from smoothness 0.55, fully from 0.7 (Unity
        /// already reflects in glossy materials itself, blurred): at 1 glass, water, car paint and chrome get them.
        public static float Shine = 1f;

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
            public bool AllOpaque;          // every triangle submesh traced as opaque (none cut out, glass or left out)
            public bool ShadowOnly;         // ShadowCastingMode.ShadowsOnly: unseen, only the shadow rays hit it
            public bool Taken;              // its shadow is ours (TakeShadows)
            public Matrix4x4 Matrix;        // its transform, as last read
            public uint VertexOffset, VertexStride, VertexCount, VertexFormat, Index32;
            public int NormalStream = -1;
            public IntPtr NormalBuffer;
            public uint NormalOffset, NormalStride, NormalFormat;
            public readonly List<Sub> Submeshes = new List<Sub>();
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
            if (!TakeShadows && _taken.Count > 0) GiveShadowsBack(null);
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
                        again.Ground = e.Ground; again.Filter = e.Filter; again.StableSince = frame; again.ShadowOnly = e.ShadowOnly; _entries[k] = e = again;
                    }
                    IntPtr vb = e.Mesh.GetNativeVertexBufferPtr(e.Stream), ib = e.Mesh.GetNativeIndexBufferPtr();
                    e.NormalBuffer = e.NormalStream < 0 ? IntPtr.Zero : e.NormalStream == e.Stream ? vb : e.Mesh.GetNativeVertexBufferPtr(e.NormalStream);
                    if (vb != e.VertexBuffer || ib != e.IndexBuffer || e.StableSince == int.MaxValue) { e.VertexBuffer = vb; e.IndexBuffer = ib; e.StableSince = frame; }
                }
                if (e.VertexBuffer == IntPtr.Zero || e.IndexBuffer == IntPtr.Zero) continue;
                // a mesh the game is still building (its terrain while it loads) waits until it has held still
                if (frame - e.StableSince < StableFrames) continue;
                if (n + e.Submeshes.Count > _capacity) break;
                if (TakeShadows && !e.Taken && e.AllOpaque && !e.Ground && rd != null)
                {
                    e.Taken = true;
                    if (!_taken.ContainsKey(rd) && rd.shadowCastingMode != ShadowCastingMode.Off) { _taken[rd] = rd.shadowCastingMode; rd.shadowCastingMode = ShadowCastingMode.Off; }
                }
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
                    d.IndexStart = s.start; d.IndexCount = s.count; d.Index32 = e.Index32; d.Flags = s.seeThrough ? 1u : e.ShadowOnly ? 2u : 0u;
                    d.NormalBuffer = e.NormalBuffer; d.NormalOffset = e.NormalOffset + s.baseVertex * e.NormalStride; d.NormalStride = e.NormalStride;
                    d.NormalFormat = e.NormalBuffer == IntPtr.Zero ? 0 : e.NormalFormat; d.Pad2 = 0;
                    d.Smoothness = Mathf.Clamp01(s.smoothness * Shine); d.Metallic = s.metallic;
                    d.AlbedoR = s.albedo.r; d.AlbedoG = s.albedo.g; d.AlbedoB = s.albedo.b; d.Pad3 = 0;
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

        /// Testing (bridge): the traced submeshes within `within` m whose smoothness (times Shine) is over `over`.
        public static string Shiny(float within, float over)
        {
            var cam = Current?._lastCamera ?? Vector3.zero;
            var sb = new System.Text.StringBuilder();
            foreach (var e in Current?._entries ?? new List<Entry>())
            {
                if (e.Renderer == null || (e.Renderer.bounds.ClosestPoint(cam) - cam).magnitude > within) continue;
                foreach (var s in e.Submeshes)
                    if (s.smoothness * Shine > over)
                    {
                        var mats = e.Renderer.sharedMaterials;
                        var m = s.sub < mats.Length ? mats[s.sub] : null;
                        sb.Append($"{e.Renderer.transform.root.name}/{e.Renderer.name}#{s.sub} {(m != null ? m.name : "-")} s {s.smoothness:F2} see {s.seeThrough} | ");
                    }
            }
            return sb.ToString();
        }

        /// Testing (bridge): every traced mesh within `within` m of the camera: name, shadow mode, its LOD group's level
        /// (or -), whether it shows, and its height range.
        public static string Near(float within)
        {
            var cam = Current?._lastCamera ?? Vector3.zero;
            var sb = new System.Text.StringBuilder();
            foreach (var e in Current?._entries ?? new List<Entry>())
            {
                if (e.Renderer == null) continue;
                var b = e.Renderer.bounds;
                if ((b.ClosestPoint(cam) - cam).magnitude > within) continue;
                var g = e.Renderer.GetComponentInParent<LODGroup>();
                string lod = "-";
                if (g != null) { var lods = g.GetLODs(); for (int i = 0; i < lods.Length; i++) if (Array.IndexOf(lods[i].renderers, e.Renderer) >= 0) lod = i + "/" + lods.Length; }
                sb.Append($"{e.Renderer.transform.root.name}/{e.Renderer.name} {e.Renderer.shadowCastingMode} lod {lod} shown {e.Shown} vis {e.Renderer.isVisible} y {b.min.y - cam.y:F2}..{b.max.y - cam.y:F2} | ");
            }
            return sb.ToString();
        }

        /// Testing (bridge): the materials of renderers within `within` m: shader, render queue, and the smoothness and
        /// metallic values it carries, by count.
        public static string Materials(float within) => MaterialsOf(within, null);
        /// Testing (bridge): the same for renderers under a root object whose name has `root` in it.
        public static string MaterialsUnder(string root) => MaterialsOf(float.MaxValue, root);

        private static string MaterialsOf(float within, string root)
        {
            var cam = Current?._lastCamera ?? Vector3.zero;
            var seen = new SortedDictionary<string, int>();
            foreach (var rd in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                if (root != null && rd.transform.root.name.IndexOf(root, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if ((rd.bounds.center - cam).magnitude > within) continue;
                foreach (var m in rd.sharedMaterials)
                {
                    if (m == null) continue;
                    string P(string n) => m.HasProperty(n) ? n.TrimStart('_') + " " + m.GetFloat(n).ToString("F2") : null;
                    var props = string.Join(",", new[] { P("_Glossiness"), P("_Smoothness"), P("_Metallic"), P("_GlossMapScale"), P("_Shininess") }.Where(x => x != null))
                                + (m.HasProperty("_MetallicGlossMap") && m.GetTexture("_MetallicGlossMap") != null ? ",glossmap" : "") + (m.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A") ? ",albedoA" : "")
                                + " " + rd.name;
                    string key = m.shader.name + " q" + m.renderQueue + " [" + props + "]";
                    seen.TryGetValue(key, out int n); seen[key] = n + 1;
                }
            }
            var sb = new System.Text.StringBuilder();
            foreach (var kv in seen) sb.Append(kv.Value).Append("x ").Append(kv.Key).Append(" | ");
            return sb.ToString();
        }

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
                if (rd == null || !rd.enabled || rd.isPartOfStaticBatch || (CullingMask & (1 << rd.gameObject.layer)) == 0) continue;
                var b = rd.bounds;
                bool ground = b.size.x >= GroundSize || b.size.z >= GroundSize;
                var cast = _taken.TryGetValue(rd, out var was) ? was : rd.shadowCastingMode;
                if (cast == ShadowCastingMode.Off && !ground) continue;
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
                if (e != null) { e.Ground = ground; e.Filter = mf; e.Center = b.center; e.ShadowOnly = cast == ShadowCastingMode.ShadowsOnly; _next.Add(e); } else _skipping++;
            }
            if (_pendingAt >= _pending.Length)
            {
                (_entries, _next) = (_next, _entries);
                _next.Clear(); _pending = null; _previous.Clear();
                if (_taken.Count > 0) GiveShadowsBack(_entries);
                Gathered = _entries.Count; Skipped = _skipping;
            }
            RefreshMs = (Time.realtimeSinceStartup - t0) * 1000f;
            Bench.Refresh(RefreshMs);
        }
        private int _skipping;

        /// Puts the game's shadows back: all of them (keep null), or those of renderers no longer in `keep`.
        private void GiveShadowsBack(List<Entry> keep)
        {
            HashSet<Renderer> kept = null;
            if (keep != null) { kept = new HashSet<Renderer>(); foreach (var e in keep) if (e.Taken && e.Renderer != null) kept.Add(e.Renderer); }
            var back = new List<Renderer>();
            foreach (var kv in _taken)
            {
                if (kv.Key == null) { back.Add(kv.Key); continue; }
                if (kept != null && kept.Contains(kv.Key)) continue;
                kv.Key.shadowCastingMode = kv.Value; back.Add(kv.Key);
            }
            foreach (var r in back) _taken.Remove(r);
            if (keep == null) foreach (var e in _entries) e.Taken = false;
        }

        /// The game's shadows back (the mod is going away).
        public void Restore() { if (_taken.Count > 0) GiveShadowsBack(null); }

        /// A Standard material with a metallic-gloss map (or smoothness in its albedo's alpha) takes its smoothness from that
        /// texture times _GlossMapScale and ignores _Glossiness (the bus's paint: _Glossiness 1, a mirror, 2026-11-27). The
        /// texture's alpha isn't readable here: about half of the scale.
        private static bool GlossFromMap(Material m) =>
            (m.HasProperty("_MetallicGlossMap") && m.GetTexture("_MetallicGlossMap") != null) || m.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A");

        private static float Smoothness(Material m, bool seeThrough)
        {
            if (GlossFromMap(m)) return m.HasProperty("_GlossMapScale") ? m.GetFloat("_GlossMapScale") * 0.5f : 0.3f;
            if (m.HasProperty("_Glossiness")) return m.GetFloat("_Glossiness");
            if (m.HasProperty("_Smoothness")) return m.GetFloat("_Smoothness");
            return seeThrough ? 0.95f : 0f;
        }

        private static float Metallic(Material m)
        {
            if (m.HasProperty("_MetallicGlossMap") && m.GetTexture("_MetallicGlossMap") != null) return 0.5f;
            return m.HasProperty("_Metallic") ? m.GetFloat("_Metallic") : 0f;
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
                Renderer = rd, Mesh = mesh, Stream = stream, VertexBuffer = vb, IndexBuffer = ib, KeyBase = (ulong)(uint)mesh.GetInstanceID() << 8,
                VertexOffset = (uint)mesh.GetVertexAttributeOffset(VertexAttribute.Position), VertexStride = (uint)mesh.GetVertexBufferStride(stream),
                VertexCount = (uint)mesh.vertexCount, VertexFormat = dxgi, Index32 = mesh.indexFormat == IndexFormat.UInt32 ? 1u : 0u,
            };
            if (mesh.HasVertexAttribute(VertexAttribute.Normal))
            {
                var nf = mesh.GetVertexAttributeFormat(VertexAttribute.Normal); int nd = mesh.GetVertexAttributeDimension(VertexAttribute.Normal);
                uint code = nf == VertexAttributeFormat.Float32 && nd >= 3 ? 1u : nf == VertexAttributeFormat.Float16 && nd == 4 ? 2u : nf == VertexAttributeFormat.SNorm8 && nd == 4 ? 3u : 0u;
                if (code != 0)
                {
                    e.NormalStream = mesh.GetVertexAttributeStream(VertexAttribute.Normal); e.NormalFormat = code;
                    e.NormalOffset = (uint)mesh.GetVertexAttributeOffset(VertexAttribute.Normal); e.NormalStride = (uint)mesh.GetVertexBufferStride(e.NormalStream);
                    e.NormalBuffer = e.NormalStream == stream ? vb : mesh.GetNativeVertexBufferPtr(e.NormalStream);
                }
            }
            var mats = rd.sharedMaterials;
            e.AllOpaque = true;
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                var sm = mesh.GetSubMesh(i);
                if (sm.topology != MeshTopology.Triangles || sm.indexCount < 3) continue;
                var mat = i < mats.Length ? mats[i] : null;
                if (mat == null) { e.AllOpaque = false; continue; }
                int queue = mat.renderQueue;
                bool seeThrough = queue >= 3000;   // glass, water: reflections only (the shadow and occlusion rays pass them)
                if (seeThrough) e.AllOpaque = false;
                if (SkipCutout && queue >= 2450 && !seeThrough) { e.AllOpaque = false; continue; }   // cut-out (leaves: the rays see triangles, not the texture)
                // impostors (Amplify Impostors): flat quads that show a picture of a tree or a rock far away; as geometry
                // they cast rectangles (2026-11-27: rectangles around trees in Volkan's screenshots)
                if (mat.shader != null && mat.shader.name.IndexOf("Impostor", StringComparison.OrdinalIgnoreCase) >= 0) { e.AllOpaque = false; continue; }
                float smooth = Smoothness(mat, seeThrough), metal = Metallic(mat);
                if (seeThrough) smooth = Mathf.Max(smooth, 0.95f / Mathf.Max(Shine, 1f));   // glass and water are smooth whatever the shader says
                e.Submeshes.Add(new Sub
                {
                    start = (uint)sm.indexStart, count = (uint)sm.indexCount, baseVertex = (uint)Math.Max(0, sm.baseVertex), sub = i,
                    smoothness = smooth, metallic = metal,
                    albedo = mat.HasProperty("_Color") ? mat.color : Color.white, seeThrough = seeThrough,
                });
            }
            return e.Submeshes.Count > 0 ? e : null;
        }
    }
}
