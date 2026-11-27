using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace RayTrace
{
    /// RayTraceNative.dll (Native/rt.cc): its exports and the two structs it reads (layouts as rt.cc's RtGeometry and
    /// RtFrame, 4-byte packed).
    internal static class Native
    {
        public static bool Installed;
        private const string Dll = "RayTraceNative";

        [DllImport(Dll)] public static extern IntPtr RT_RenderEventFunc();
        [DllImport(Dll)] public static extern void RT_SetShaders(byte[] rtLib, int rtLength, byte[] composite, int compositeLength, byte[] normals, int normalsLength);
        [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int RT_Status(StringBuilder buffer, int size);

        public const int EventFrame = 1;

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public unsafe struct Geometry
        {
            public fixed float Transform[12];   // object to world, 3x4 row-major
            public IntPtr VertexBuffer, IndexBuffer;
            public ulong Key;
            public uint VertexOffset, VertexStride, VertexCount, VertexFormat;
            public uint IndexStart, IndexCount, Index32, Flags;   // flags: 1 see-through (glass, water)
            public IntPtr NormalBuffer;                            // the vertex normals' buffer, or zero (face normals then)
            public uint NormalOffset, NormalStride, NormalFormat, Pad2;   // format: 1 float x3, 2 half x4, 3 snorm8 x4
            public float Smoothness, Metallic, AlbedoR, AlbedoG, AlbedoB, Pad3;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public unsafe struct Frame
        {
            public IntPtr Geometries;
            public int GeometryCount, Width, Height, Mode;
            public IntPtr Depth, ColorIn, ColorOut;
            public fixed float InvViewProj[16];   // row-major
            public fixed float CameraPos[4];
            public fixed float SunDir[4];
            public float FadeStart, FadeEnd, Strength, MaxDistance;
            public int NewStructuresPerFrame, FrameIndex, Stage, Submit;
            public int Effects, AoRays;          // effects: 1 sun shadows, 2 ambient occlusion
            public float AoRadius, AoStrength, AoDistance;
            public fixed float ViewProj[16];      // row-major, world to clip
            public fixed float SkyTop[4], SkyHorizon[4], SkyGround[4], SunColor[4];   // linear
            public float ReflStrength;
            public int GiRays;
            public float GiRadius, GiStrength, GiDistance;
        }

        /// Writes RayTraceNative.dll into TheLongDrive_Data/Plugins/x86_64 (only when it changed): Unity gives a plugin its
        /// D3D12 device only when it loads it from there itself.
        public static string Install()
        {
            var dir = Path.Combine(Application.dataPath, "Plugins", "x86_64");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, Dll + ".dll");
            var bytes = Resource("native." + Dll + ".dll");
            bool same = File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);
            if (!same) File.WriteAllBytes(path, bytes);
            Installed = true;
            // Unity loads the plugins in that folder when the engine starts, before any mod: a new one takes effect at
            // the next start of the game
            return (same ? "native plugin in place" : "native plugin updated: restart the game to use it") + " (" + bytes.Length + " bytes)";
        }

        public static void LoadShaders()
        {
            var lib = Resource("native.raytrace.dxil");
            var composite = Resource("native.composite.dxil");
            var normals = Resource("native.normals.dxil");
            RT_SetShaders(lib, lib.Length, composite, composite.Length, normals, normals.Length);
        }

        public static string Status()
        {
            var sb = new StringBuilder(1024);
            RT_Status(sb, sb.Capacity);
            return sb.ToString();
        }

        public static byte[] Resource(string name)
        {
            using var s = typeof(Native).Assembly.GetManifestResourceStream(name) ?? throw new FileNotFoundException(name);
            var m = new MemoryStream(); s.CopyTo(m); return m.ToArray();
        }
    }
}
