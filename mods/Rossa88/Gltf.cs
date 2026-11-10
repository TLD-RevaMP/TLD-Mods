using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Rossa88
{
    // Loads a glTF 2.0 model (.glb, or .gltf with its .bin and image files next to it) into a Unity object tree:
    // node hierarchy with transforms, triangle meshes (positions, normals, UVs, 16/32-bit indices), and materials
    // (base color + base color texture) made from a template material so they use a shader the game has.
    // glTF is right-handed, Unity left-handed: X is mirrored (positions, normals, rotations) and triangles reversed.
    internal class Gltf
    {
        private Dictionary<string, object> doc;
        private readonly List<byte[]> buffers = new List<byte[]>();
        private string folder;
        private readonly Dictionary<int, Mesh[]> meshCache = new Dictionary<int, Mesh[]>();
        private readonly Dictionary<int, Material> materials = new Dictionary<int, Material>();
        private readonly Dictionary<int, Texture2D> textures = new Dictionary<int, Texture2D>();
        private Material template;
        public readonly List<string> Warnings = new List<string>();

        public static GameObject Load(string path, Material template, out Gltf loader)
        {
            loader = new Gltf { folder = Path.GetDirectoryName(path), template = template };
            byte[] bytes = CarFiles.ReadAllBytes(path);
            if (bytes.Length >= 12 && BitConverter.ToUInt32(bytes, 0) == 0x46546C67) // "glTF"
            {
                int offset = 12;
                while (offset + 8 <= bytes.Length)
                {
                    int length = BitConverter.ToInt32(bytes, offset);
                    uint type = BitConverter.ToUInt32(bytes, offset + 4);
                    if (type == 0x4E4F534A) loader.doc = (Dictionary<string, object>)Json.Parse(Encoding.UTF8.GetString(bytes, offset + 8, length)); // JSON
                    else if (type == 0x004E4942) { var bin = new byte[length]; Buffer.BlockCopy(bytes, offset + 8, bin, 0, length); loader.buffers.Add(bin); } // BIN
                    offset += 8 + length;
                }
            }
            else
            {
                loader.doc = (Dictionary<string, object>)Json.Parse(Encoding.UTF8.GetString(bytes));
                foreach (object b in List(loader.doc, "buffers"))
                {
                    string uri = Str(b, "uri");
                    loader.buffers.Add(uri == null ? new byte[0] : loader.ReadUri(uri));
                }
            }
            return loader.BuildScene();
        }

        private GameObject BuildScene()
        {
            var root = new GameObject("model");
            int sceneIndex = doc.ContainsKey("scene") ? Int(doc, "scene") : 0;
            var scenes = List(doc, "scenes");
            var nodes = scenes.Count > 0 ? List(scenes[sceneIndex], "nodes") : new List<object>();
            foreach (object n in nodes) BuildNode(Convert.ToInt32(n), root.transform);
            return root;
        }

        private void BuildNode(int index, Transform parent)
        {
            var node = (Dictionary<string, object>)List(doc, "nodes")[index];
            var go = new GameObject(Str(node, "name") ?? ("node" + index));
            go.transform.SetParent(parent, false);
            if (node.ContainsKey("matrix"))
            {
                var m = Floats(node, "matrix");
                var mat = new Matrix4x4();
                for (int i = 0; i < 16; i++) mat[i % 4, i / 4] = m[i];   // glTF matrices are column-major
                Decompose(mat, out Vector3 t, out Quaternion r, out Vector3 s);
                Apply(go.transform, t, r, s);
            }
            else
            {
                var t = node.ContainsKey("translation") ? V3(Floats(node, "translation")) : Vector3.zero;
                var r = node.ContainsKey("rotation") ? Q(Floats(node, "rotation")) : Quaternion.identity;
                var s = node.ContainsKey("scale") ? new Vector3(Floats(node, "scale")[0], Floats(node, "scale")[1], Floats(node, "scale")[2]) : Vector3.one;
                Apply(go.transform, t, r, s);
            }
            if (node.ContainsKey("mesh")) AttachMesh(Int(node, "mesh"), go);
            if (node.ContainsKey("children")) foreach (object c in List(node, "children")) BuildNode(Convert.ToInt32(c), go.transform);
        }

        private static void Apply(Transform tr, Vector3 t, Quaternion r, Vector3 s)
        {
            tr.localPosition = t;
            tr.localRotation = r;
            tr.localScale = s;
        }

        // glTF (right-handed) to Unity (left-handed) by mirroring X.
        private static Vector3 V3(float[] f) => new Vector3(-f[0], f[1], f[2]);
        private static Quaternion Q(float[] f) => new Quaternion(f[0], -f[1], -f[2], f[3]);

        private static void Decompose(Matrix4x4 m, out Vector3 t, out Quaternion r, out Vector3 s)
        {
            // mirror X: M' = S M S with S = diag(-1, 1, 1, 1)
            for (int i = 0; i < 4; i++) { m[0, i] = -m[0, i]; m[i, 0] = -m[i, 0]; }
            t = m.GetColumn(3);
            s = new Vector3(m.GetColumn(0).magnitude, m.GetColumn(1).magnitude, m.GetColumn(2).magnitude);
            r = Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1));
        }

        private void AttachMesh(int meshIndex, GameObject go)
        {
            var mesh = (Dictionary<string, object>)List(doc, "meshes")[meshIndex];
            var prims = List(mesh, "primitives");
            for (int p = 0; p < prims.Count; p++)
            {
                var prim = (Dictionary<string, object>)prims[p];
                if (prim.ContainsKey("mode") && Int(prim, "mode") != 4) { Warnings.Add("skipped non-triangle primitive in mesh " + meshIndex); continue; }
                Mesh m = BuildPrimitive(prim);
                if (m == null) continue;
                GameObject target = prims.Count == 1 ? go : new GameObject(go.name + "_" + p);
                if (target != go) target.transform.SetParent(go.transform, false);
                target.AddComponent<MeshFilter>().sharedMesh = m;
                var mr = target.AddComponent<MeshRenderer>();
                mr.sharedMaterial = prim.ContainsKey("material") ? GetMaterial(Int(prim, "material")) : template;
            }
        }

        private Mesh BuildPrimitive(Dictionary<string, object> prim)
        {
            var attr = (Dictionary<string, object>)prim["attributes"];
            if (!attr.ContainsKey("POSITION")) return null;
            Vector3[] pos = ReadVec3(Convert.ToInt32(attr["POSITION"]), true);
            var m = new Mesh();
            if (pos.Length > 65535) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.vertices = pos;
            if (attr.ContainsKey("NORMAL")) m.normals = ReadVec3(Convert.ToInt32(attr["NORMAL"]), true);
            if (attr.ContainsKey("TEXCOORD_0"))
            {
                float[] uv = ReadFloats(Convert.ToInt32(attr["TEXCOORD_0"]), 2);
                var uvs = new Vector2[uv.Length / 2];
                for (int i = 0; i < uvs.Length; i++) uvs[i] = new Vector2(uv[i * 2], 1f - uv[i * 2 + 1]);   // glTF UV origin is top-left
                m.uv = uvs;
            }
            int[] tris = prim.ContainsKey("indices") ? ReadIndices(Int(prim, "indices")) : Sequence(pos.Length);
            for (int i = 0; i + 2 < tris.Length; i += 3) { int a = tris[i + 1]; tris[i + 1] = tris[i + 2]; tris[i + 2] = a; }   // mirrored: reverse winding
            m.triangles = tris;
            if (!attr.ContainsKey("NORMAL")) m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        private static int[] Sequence(int n)
        {
            var a = new int[n];
            for (int i = 0; i < n; i++) a[i] = i;
            return a;
        }

        private Material GetMaterial(int index)
        {
            if (materials.TryGetValue(index, out Material cached)) return cached;
            var src = (Dictionary<string, object>)List(doc, "materials")[index];
            var mat = new Material(template) { name = Str(src, "name") ?? ("material" + index) };
            Color color = Color.white;
            if (src.ContainsKey("pbrMetallicRoughness"))
            {
                var pbr = (Dictionary<string, object>)src["pbrMetallicRoughness"];
                if (pbr.ContainsKey("baseColorFactor"))
                {
                    var f = Floats(pbr, "baseColorFactor");
                    color = new Color(f[0], f[1], f[2], f[3]).gamma;   // glTF colors are linear
                }
                if (pbr.ContainsKey("baseColorTexture"))
                {
                    Texture2D tex = GetTexture(Int(pbr["baseColorTexture"], "index"));
                    if (tex != null) mat.mainTexture = tex; else mat.mainTexture = null;
                }
                else
                {
                    mat.mainTexture = null;
                }
            }
            mat.color = color;
            materials[index] = mat;
            return mat;
        }

        private Texture2D GetTexture(int index)
        {
            if (textures.TryGetValue(index, out Texture2D cached)) return cached;
            Texture2D tex = null;
            try
            {
                var t = (Dictionary<string, object>)List(doc, "textures")[index];
                if (t.ContainsKey("source"))
                {
                    var img = (Dictionary<string, object>)List(doc, "images")[Int(t, "source")];
                    byte[] data = img.ContainsKey("bufferView") ? View(Int(img, "bufferView")) : ReadUri(Str(img, "uri"));
                    tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                    if (!ImageConversion.LoadImage(tex, data)) { Warnings.Add("texture " + index + " unreadable"); tex = null; }
                }
            }
            catch (Exception e)
            {
                Warnings.Add("texture " + index + ": " + e.Message);
            }
            textures[index] = tex;
            return tex;
        }

        // ---------- accessors ----------

        private Vector3[] ReadVec3(int accessor, bool mirror)
        {
            float[] f = ReadFloats(accessor, 3);
            var v = new Vector3[f.Length / 3];
            for (int i = 0; i < v.Length; i++) v[i] = new Vector3(mirror ? -f[i * 3] : f[i * 3], f[i * 3 + 1], f[i * 3 + 2]);
            return v;
        }

        private float[] ReadFloats(int accessorIndex, int components)
        {
            var acc = (Dictionary<string, object>)List(doc, "accessors")[accessorIndex];
            int count = Int(acc, "count");
            var result = new float[count * components];
            if (!acc.ContainsKey("bufferView")) return result;   // all zeros (sparse accessors aren't supported)
            int ct = Int(acc, "componentType");
            bool normalized = acc.ContainsKey("normalized") && (bool)acc["normalized"];
            var bv = (Dictionary<string, object>)List(doc, "bufferViews")[Int(acc, "bufferView")];
            byte[] buf = buffers[Int(bv, "buffer")];
            int size = ct == 5126 ? 4 : ct == 5123 || ct == 5122 ? 2 : 1;
            int baseOffset = (bv.ContainsKey("byteOffset") ? Int(bv, "byteOffset") : 0) + (acc.ContainsKey("byteOffset") ? Int(acc, "byteOffset") : 0);
            int stride = bv.ContainsKey("byteStride") ? Int(bv, "byteStride") : size * components;
            for (int i = 0; i < count; i++)
            {
                for (int c = 0; c < components; c++)
                {
                    int o = baseOffset + i * stride + c * size;
                    float v;
                    switch (ct)
                    {
                        case 5126: v = BitConverter.ToSingle(buf, o); break;
                        case 5123: v = BitConverter.ToUInt16(buf, o); if (normalized) v /= 65535f; break;
                        case 5122: v = BitConverter.ToInt16(buf, o); if (normalized) v = Mathf.Max(v / 32767f, -1f); break;
                        case 5121: v = buf[o]; if (normalized) v /= 255f; break;
                        default: v = (sbyte)buf[o]; if (normalized) v = Mathf.Max(v / 127f, -1f); break;
                    }
                    result[i * components + c] = v;
                }
            }
            return result;
        }

        private int[] ReadIndices(int accessorIndex)
        {
            var acc = (Dictionary<string, object>)List(doc, "accessors")[accessorIndex];
            int count = Int(acc, "count");
            int ct = Int(acc, "componentType");
            var bv = (Dictionary<string, object>)List(doc, "bufferViews")[Int(acc, "bufferView")];
            byte[] buf = buffers[Int(bv, "buffer")];
            int offset = (bv.ContainsKey("byteOffset") ? Int(bv, "byteOffset") : 0) + (acc.ContainsKey("byteOffset") ? Int(acc, "byteOffset") : 0);
            var r = new int[count];
            for (int i = 0; i < count; i++)
            {
                r[i] = ct == 5125 ? (int)BitConverter.ToUInt32(buf, offset + i * 4) : ct == 5123 ? BitConverter.ToUInt16(buf, offset + i * 2) : buf[offset + i];
            }
            return r;
        }

        private byte[] View(int index)
        {
            var bv = (Dictionary<string, object>)List(doc, "bufferViews")[index];
            byte[] buf = buffers[Int(bv, "buffer")];
            int offset = bv.ContainsKey("byteOffset") ? Int(bv, "byteOffset") : 0;
            var r = new byte[Int(bv, "byteLength")];
            Buffer.BlockCopy(buf, offset, r, 0, r.Length);
            return r;
        }

        private byte[] ReadUri(string uri)
        {
            if (uri.StartsWith("data:"))
            {
                int comma = uri.IndexOf(',');
                return Convert.FromBase64String(uri.Substring(comma + 1));
            }
            return File.ReadAllBytes(Path.Combine(folder, Uri.UnescapeDataString(uri)));
        }

        // ---------- JSON helpers ----------

        private static List<object> List(object o, string key)
        {
            var d = (Dictionary<string, object>)o;
            return d.TryGetValue(key, out object v) && v is List<object> l ? l : new List<object>();
        }

        private static string Str(object o, string key)
        {
            var d = (Dictionary<string, object>)o;
            return d.TryGetValue(key, out object v) ? v as string : null;
        }

        private static int Int(object o, string key) => Convert.ToInt32(((Dictionary<string, object>)o)[key]);

        private static float[] Floats(object o, string key)
        {
            var l = List(o, key);
            var f = new float[l.Count];
            for (int i = 0; i < f.Length; i++) f[i] = Convert.ToSingle(l[i]);
            return f;
        }
    }
}
