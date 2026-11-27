using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace RayTrace
{
    /// The player's camera, ray traced: a command buffer after its opaque geometry (before the transparent objects and
    /// the post-processing) copies the depth and the colour, lets the native plugin trace and composite, and copies its
    /// result back. Each frame's data goes to the native side in its own slot of a ring: the render thread can run a
    /// frame behind the main thread.
    internal sealed class Tracer
    {
        /// Sun shadows: 0 off, 1 beyond the game's own (from ShadowDistance on), 2 everywhere, 3 the shadow mask; 4 shows
        /// the ambient occlusion alone
        public static int Mode = 1;
        /// Ray traced ambient occlusion: AoRays short rays (AoRadius m) per pixel within AoDistance m; AoStrength 1 darkens a
        /// closed-in pixel fully
        public static bool Ao = true;
        /// Ray traced reflections (on surfaces whose smoothness times Scene.Shine is over 0.2, and on glass and water)
        public static bool Refl = true;
        public static float ReflStrength = 0.6f;
        public static int AoRays = 4;
        public static float AoRadius = 1.5f, AoStrength = 0.85f, AoDistance = 80f;
        public static float Strength = 0.55f, MaxDistance = 2000f;
        public static int NewStructuresPerFrame = 48;
        /// testing: 1 only the copies, 2 + the native event, 3 + the structures, 4 + the rays, 5 everything; 50 all but the
        /// composite, 51 all but the copy back into the frame
        public static int Stage = 5;
        /// 0: the native side's own command list, run by Unity (ExecuteCommandList); 1: into Unity's own (crashed, kept to compare)
        public static int Submit = 0;
        /// testing: the camera's depth texture modes before this mod added its own (a depth texture the game did not
        /// already have costs a depth pass of the whole scene)
        public static string OriginalDepthMode = "";

        private const int Ring = 4;
        private Camera _cam;
        private CommandBuffer _cb;
        private RenderTexture _depth, _in, _out;
        private readonly IntPtr[] _frames = new IntPtr[Ring];
        private IntPtr[] _geometries = new IntPtr[Ring];
        private int _slot, _frameIndex;
        private IntPtr _event;
        private readonly Scene _scene = new Scene();
        private bool _shaders;
        public string Info = "no camera yet";
        public int Geometries;

        public void Tick()
        {
            if (!_shaders)
            {
                Native.LoadShaders();
                _event = Native.RT_RenderEventFunc();
                for (int i = 0; i < Ring; i++) _frames[i] = Marshal.AllocHGlobal(Marshal.SizeOf<Native.Frame>());
                _shaders = true;
            }
            var player = mainscript.s != null ? mainscript.s.player : null;
            var cam = player != null ? player.Cam : null;
            if (cam != _cam) Attach(cam);
        }

        private void Attach(Camera cam)
        {
            if (_cam != null && _cb != null) _cam.RemoveCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, _cb);
            Camera.onPreRender -= OnPreRender;
            _cam = cam;
            if (cam == null) { Info = "no camera"; return; }
            OriginalDepthMode = cam.depthTextureMode.ToString();
            cam.depthTextureMode |= DepthTextureMode.Depth;
            _cb ??= new CommandBuffer { name = "RayTrace" };
            cam.AddCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, _cb);
            Camera.onPreRender += OnPreRender;
            Info = "on " + cam.name;
        }

        private void OnPreRender(Camera cam)
        {
            if (cam != _cam || _cb == null) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { Record(cam); Bench.Record(sw.Elapsed.TotalMilliseconds); }
            catch (Exception e) { Info = "record failed: " + e.GetType().Name + " " + e.Message; _cb.Clear(); }
        }

        private static RenderTexture Target(RenderTexture rt, int w, int h, RenderTextureFormat format, bool uav)
        {
            if (rt != null && rt.width == w && rt.height == h && rt.format == format && rt.enableRandomWrite == uav) return rt;
            if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
            rt = new RenderTexture(w, h, 0, format, RenderTextureReadWrite.Linear) { enableRandomWrite = uav, name = "RayTrace", hideFlags = HideFlags.HideAndDontSave };
            rt.Create();
            return rt;
        }

        private unsafe void Record(Camera cam)
        {
            _cb.Clear();
            if (Mode == 0 && !Ao && !Refl) return;
            int w = cam.pixelWidth, h = cam.pixelHeight;
            var colorFormat = cam.allowHDR ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
            // random-write textures all three: vkd3d-proton keeps them in one layout, so the native side needs no
            // transitions of textures Unity tracks
            _depth = Target(_depth, w, h, RenderTextureFormat.RFloat, true);
            _in = Target(_in, w, h, colorFormat, true);
            _out = Target(_out, w, h, colorFormat, true);

            var f = (Native.Frame*)_frames[_slot];
            var camPos = cam.transform.position;
            f->GeometryCount = Geometries = _scene.Write(camPos, out var geometries, ref _geometries, _slot, _frameIndex);
            f->Geometries = geometries;
            f->Width = w; f->Height = h; f->Mode = Mode;
            f->Depth = _depth.GetNativeTexturePtr(); f->ColorIn = _in.GetNativeTexturePtr(); f->ColorOut = _out.GetNativeTexturePtr();
            var inv = (GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix).inverse;
            for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) f->InvViewProj[r * 4 + c] = inv[r, c];
            var vp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix;
            for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) f->ViewProj[r * 4 + c] = vp[r, c];
            // what a reflection that leaves the scene sees: the ambient's sky and ground, the fog's colour at the horizon
            Put(f->SkyTop, RenderSettings.ambientSkyColor.linear); Put(f->SkyHorizon, RenderSettings.fogColor.linear);
            Put(f->SkyGround, RenderSettings.ambientGroundColor.linear);
            f->ReflStrength = ReflStrength;
            f->CameraPos[0] = camPos.x; f->CameraPos[1] = camPos.y; f->CameraPos[2] = camPos.z; f->CameraPos[3] = 1;
            var sun = Sun();
            var toSun = sun != null ? -sun.transform.forward : Vector3.up;
            bool up = sun != null && sun.isActiveAndEnabled && sun.intensity > 0.05f && toSun.y > 0.01f;
            f->SunDir[0] = toSun.x; f->SunDir[1] = toSun.y; f->SunDir[2] = toSun.z; f->SunDir[3] = up ? 1 : 0;
            float shadowDistance = QualitySettings.shadowDistance;
            f->FadeStart = shadowDistance * 0.75f; f->FadeEnd = shadowDistance;
            f->Strength = Strength; f->MaxDistance = MaxDistance;
            Put(f->SunColor, sun != null ? sun.color.linear * sun.intensity : Color.black);
            f->NewStructuresPerFrame = NewStructuresPerFrame; f->FrameIndex = ++_frameIndex; f->Stage = Stage; f->Submit = Submit;
            f->Effects = 1 | (Ao ? 2 : 0) | (Refl ? 4 : 0); f->AoRays = AoRays; f->AoRadius = AoRadius; f->AoStrength = AoStrength; f->AoDistance = AoDistance;

            _cb.Blit(BuiltinRenderTextureType.Depth, _depth);
            _cb.Blit(BuiltinRenderTextureType.CameraTarget, _in);
            if (Stage >= 2 && Stage != 52) _cb.IssuePluginEventAndData(_event, Submit == 1 ? 2 : Native.EventFrame, (IntPtr)f);
            if (Stage == 5 || Stage == 50 || Stage == 52) _cb.Blit(_out, BuiltinRenderTextureType.CameraTarget);
            _slot = (_slot + 1) % Ring;
            Info = $"on {cam.name} {w}x{h}, {Geometries} geometries from {_scene.Gathered} of {_scene.Renderers} renderers ({_scene.Skipped} skipped, gathered in {_scene.RefreshMs:F1} ms), sun {(up ? "up" : "down")}";
        }

        private static unsafe void Put(float* to, Color c) { to[0] = c.r; to[1] = c.g; to[2] = c.b; to[3] = c.a; }

        private static Light _sun;
        private static float _sunLooked;
        /// The scene's sun (RenderSettings.sun), else its brightest directional light (looked for again every 5 s).
        private static Light Sun()
        {
            if (RenderSettings.sun != null) return RenderSettings.sun;
            if (_sun != null && Time.unscaledTime < _sunLooked) return _sun;
            _sunLooked = Time.unscaledTime + 5f;
            _sun = null;
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
                if (l.type == LightType.Directional && l.isActiveAndEnabled && (_sun == null || l.intensity > _sun.intensity)) _sun = l;
            return _sun;
        }
    }
}
