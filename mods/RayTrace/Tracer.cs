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
        /// Sun shadows: 0 the game's own only; 1 ray traced, into the game's own sun shadow mask (the game's shadow map
        /// keeps only what the rays can't see: leaves, glass, characters); 2 ray traced joined with all the game's own;
        /// 3 the shadow mask; 4 to 7 the other effects alone (testing)
        public static int Mode = 1;
        /// The sun disk's angular radius (degrees) the shadow rays aim at: sharp where a shadow meets its caster, softer
        /// further from it. The real sun's is 0.27.
        public static float SunSize = 0.6f;
        /// Ray traced ambient occlusion: AoRays short rays (AoRadius m) per pixel within AoDistance m; AoStrength 1 darkens a
        /// closed-in pixel fully
        public static bool Ao = true;
        /// Ray traced reflections (on surfaces whose smoothness times Scene.Shine is over 0.2, and on glass and water)
        public static bool Refl = true;
        public static float ReflStrength = 0.8f;
        /// Ray traced bounced light: GiRays rays (GiRadius m) per pixel within GiDistance m; what nearby surfaces bounce onto
        /// a surface tints and brightens it (the sky's light is the game's own ambient already)
        public static bool Gi = true;
        public static int GiRays = 2;
        public static float GiRadius = 8f, GiStrength = 1.5f, GiDistance = 60f;
        public static int AoRays = 4;
        public static float AoRadius = 2.5f, AoStrength = 1f, AoDistance = 80f;
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
        // the sun's pass: the camera's depth copied after its depth texture, then (at the sun's screen-space shadow mask)
        // the mask copied in, the shadow rays joined into it, and the result copied back
        private CommandBuffer _depthCb, _lightCb;
        private RenderTexture _maskDepth, _maskIn, _maskOut;
        private readonly IntPtr[] _maskFrames = new IntPtr[Ring];
        private Light _lightWithCb;
        private bool _maskPass;
        /// testing: whether this frame asked for the sun's pass
        public static bool MaskPass;
        /// testing: 0 the sun's pass; 1 only the mask copied out and back; 2 no copy back; 3 the copy back alone
        public static int MaskTest;
        private bool _rendering;
        private GameObject _keeper;
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
                for (int i = 0; i < Ring; i++) _maskFrames[i] = Marshal.AllocHGlobal(Marshal.SizeOf<Native.Frame>());
                _shaders = true;
            }
            var player = mainscript.s != null ? mainscript.s.player : null;
            var cam = player != null ? player.Cam : null;
            if (cam != _cam) Attach(cam);
        }

        private void Attach(Camera cam)
        {
            if (_cam != null && _cb != null) _cam.RemoveCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, _cb);
            if (_cam != null && _depthCb != null) _cam.RemoveCommandBuffer(CameraEvent.AfterDepthTexture, _depthCb);
            Camera.onPreCull -= OnPreCull;
            Camera.onPreRender -= OnPreRender;
            Camera.onPostRender -= OnPostRender;
            SunBuffer(null);
            _cam = cam;
            if (cam == null) { Info = "no camera"; return; }
            OriginalDepthMode = cam.depthTextureMode.ToString();
            cam.depthTextureMode |= DepthTextureMode.Depth;
            _cb ??= new CommandBuffer { name = "RayTrace" };
            cam.AddCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, _cb);
            _depthCb ??= new CommandBuffer { name = "RayTrace depth" };
            _lightCb ??= new CommandBuffer { name = "RayTrace sun shadows" };
            cam.AddCommandBuffer(CameraEvent.AfterDepthTexture, _depthCb);
            Camera.onPreCull += OnPreCull;
            Camera.onPreRender += OnPreRender;
            Camera.onPostRender += OnPostRender;
            Info = "on " + cam.name;
        }

        // The sun's command buffer runs for every camera that renders with the sun (the cars' mirrors, which render while
        // the player's camera culls): it is on the sun from this camera's culling to the end of its render, and off while
        // any other camera renders. Added later than the culling, it does not run in that frame (2026-11-27).
        private void OnPreCull(Camera cam)
        {
            if (cam != _cam) return;
            _rendering = true;
            SunBuffer(Sun());
        }

        private void SunBuffer(Light sun)
        {
            if (sun == _lightWithCb || _lightCb == null) return;
            if (_lightWithCb != null) _lightWithCb.RemoveCommandBuffer(LightEvent.AfterScreenspaceMask, _lightCb);
            _lightWithCb = sun;
            if (sun != null) sun.AddCommandBuffer(LightEvent.AfterScreenspaceMask, _lightCb);
        }

        private void OnPreRender(Camera cam)
        {
            if (cam != _cam) { SunBuffer(null); return; }
            if (_cb == null) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { Record(cam); Bench.Record(sw.Elapsed.TotalMilliseconds); }
            catch (Exception e) { Info = "record failed: " + e.GetType().Name + " " + e.Message; _cb.Clear(); _depthCb?.Clear(); _lightCb?.Clear(); _maskPass = false; }
        }

        private void OnPostRender(Camera cam)
        {
            if (cam == _cam) { _rendering = false; SunBuffer(null); }
            else if (_rendering) SunBuffer(Sun());   // a camera that rendered within this one's culling
        }

        /// Unity draws a light's screen-space shadow mask only when something casts into its shadow map: with the traced
        /// renderers' shadows taken, a millimetre cube in front of the camera, casting shadows only, keeps it drawn.
        private void Keep(Camera cam, bool on)
        {
            if (!on) { if (_keeper != null) _keeper.SetActive(false); return; }
            if (_keeper == null)
            {
                _keeper = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _keeper.name = "RayTrace shadow keeper";
                UnityEngine.Object.Destroy(_keeper.GetComponent<Collider>());
                UnityEngine.Object.DontDestroyOnLoad(_keeper);
                var r = _keeper.GetComponent<MeshRenderer>();
                r.shadowCastingMode = ShadowCastingMode.ShadowsOnly; r.receiveShadows = false;
                _keeper.transform.localScale = Vector3.one * 0.001f;
            }
            _keeper.SetActive(true);
            _keeper.transform.position = cam.transform.position + cam.transform.forward * 1.5f;
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
            if (Mode == 0 && !Ao && !Refl && !Gi) return;
            int w = cam.pixelWidth, h = cam.pixelHeight;
            var colorFormat = cam.allowHDR ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
            // random-write textures all three: vkd3d-proton keeps them in one layout, so the native side needs no
            // transitions of textures Unity tracks
            _depth = Target(_depth, w, h, RenderTextureFormat.RFloat, true);
            _in = Target(_in, w, h, colorFormat, true);
            _out = Target(_out, w, h, colorFormat, true);

            var f = (Native.Frame*)_frames[_slot];
            var camPos = cam.transform.position;
            var sun = Sun();
            var toSun = sun != null ? -sun.transform.forward : Vector3.up;
            bool up = sun != null && sun.isActiveAndEnabled && sun.intensity > 0.05f && toSun.y > 0.01f;
            // the shadows go into the game's own lighting when its sun has shadows; else (mode 1 and 2 alike) they darken
            // the frame beyond the game's shadow distance
            _maskPass = (Mode == 1 || Mode == 2 || Mode == 3) && up && sun.shadows != LightShadows.None && QualitySettings.shadows != ShadowQuality.Disable && Stage == 5;
            Scene.TakeShadows = _maskPass && Mode == 1; MaskPass = _maskPass;
            Keep(cam, Scene.TakeShadows);
            Scene.CullingMask = cam.cullingMask;
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
            f->SunRadius = Mathf.Tan(SunSize * Mathf.Deg2Rad);
            f->SunDir[0] = toSun.x; f->SunDir[1] = toSun.y; f->SunDir[2] = toSun.z; f->SunDir[3] = up ? 1 : 0;
            float shadowDistance = QualitySettings.shadowDistance;
            f->FadeStart = shadowDistance * 0.75f; f->FadeEnd = shadowDistance;
            f->Strength = Strength; f->MaxDistance = MaxDistance;
            Put(f->SunColor, sun != null ? sun.color.linear * sun.intensity : Color.black);
            f->NewStructuresPerFrame = NewStructuresPerFrame; f->FrameIndex = ++_frameIndex; f->Stage = Stage; f->Submit = Submit;
            f->Effects = 1 | (Ao ? 2 : 0) | (Refl ? 4 : 0) | (Gi ? 8 : 0);
            f->GiRays = GiRays; f->GiRadius = GiRadius; f->GiStrength = GiStrength; f->GiDistance = GiDistance; f->AoRays = AoRays; f->AoRadius = AoRadius; f->AoStrength = AoStrength; f->AoDistance = AoDistance;

            _depthCb.Clear(); _lightCb.Clear();
            if (_maskPass)
            {
                _maskDepth = Target(_maskDepth, w, h, RenderTextureFormat.RFloat, true);
                // Unity's screen-space shadow mask is RGBA8 (CopyTexture copies within a format group only: R8 was refused)
                _maskIn = Target(_maskIn, w, h, RenderTextureFormat.ARGB32, true);
                _maskOut = Target(_maskOut, w, h, RenderTextureFormat.ARGB32, true);
                var m = (Native.Frame*)_maskFrames[_slot];
                *m = *f;
                m->Depth = _maskDepth.GetNativeTexturePtr(); m->ColorIn = _maskIn.GetNativeTexturePtr(); m->ColorOut = _maskOut.GetNativeTexturePtr();
                m->Effects = 1 | 16; m->Strength = sun.shadowStrength;
                f->Effects = (f->Effects & ~1) | 32;   // the frame's pass: no shadow rays again, the mask has them
                _depthCb.Blit(BuiltinRenderTextureType.Depth, _maskDepth);
                if (MaskTest != 3) _lightCb.CopyTexture(BuiltinRenderTextureType.CurrentActive, _maskIn);
                if (MaskTest != 1) _lightCb.IssuePluginEventAndData(_event, Native.EventFrame, (IntPtr)m);
                if (MaskTest != 2) _lightCb.Blit(MaskTest == 1 ? _maskIn : _maskOut, BuiltinRenderTextureType.CurrentActive);
            }
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
