using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountAnything
{
    /// Where and how the held item will be mounted, shown as a see-through copy of it at that pose, with keys to turn it
    /// around the surface normal, tilt it, move it out of or into the surface, and reset it flat on the surface (after
    /// Xpazeman's "Placing Anywhere" for The Long Dark). Mounting then puts the item exactly at the shown pose; the
    /// game's glue (attachablescript.Attach) and RevampLink.SendPose do the rest, so saving and RevaMP are unchanged.
    ///
    /// The pose: the item's base rotation is the way it is held, or, flat, that turned so its up side points out of the
    /// surface. On top of that: a turn around the surface normal, then tilts around the two surface directions seen from
    /// the player (away/toward, left/right). It is then moved so the middle of its colliders sits on the crosshair and
    /// its lowest point (along the normal) touches the surface's plane there, plus the out/in offset.
    internal static class Placement
    {
        internal enum Act { TurnLeft, TurnRight, TiltAway, TiltToward, TiltLeft, TiltRight, Out, In, Reset }

        // the settings for the item held now (they start fresh with every item picked up)
        /// The rotation the keys start from: as held; flat (lying on its biggest side: its thinnest axis along the
        /// surface's normal); upright (its own up axis along the normal; TLD's models don't all have a meaningful up).
        internal enum Base { Held, Flat, Upright }
        internal static Base Mode;
        internal static Vector3 BaseAxis = Vector3.up;   // the item's own axis turned to the normal this frame (flat, upright)
        private static Vector3 _thin = Vector3.up;      // the item's thinnest axis
        internal static float Turn, TiltAway, TiltLeft, Lift;   // degrees, degrees, degrees, metres
        private static pickupable _item;

        // this frame's placement: world pose, and relative to what the crosshair is on (to draw it there later in the frame)
        internal static Vector3 Pos, Point, Normal;
        internal static Quaternion Rot;
        internal static Quaternion KeyRot = Quaternion.identity;   // what the keys add to the base rotation
        private static Transform _anchor;
        private static Vector3 _anchorPos;
        private static Quaternion _anchorRot;
        private static int _frame = -10;
        internal static bool ForGameGlue;   // the item is one the game glues itself (its glue key was taken over)
        internal static float ActiveUntil;

        /// Set by the unattended test: key presses applied on the next placement update, as if pressed then.
        internal static readonly Queue<KeyValuePair<Act, bool>> TestActs = new Queue<KeyValuePair<Act, bool>>();
        internal static int DrawnMeshes, LastDrawnMeshes, DrawnFrames;
        internal static Vector3 LastPos;   // the pose the last placed mount was put at, relative to its mount point
        internal static Quaternion LastRot;

        /// How far the item's lowest point is out of the surface's plane at this frame's pose (= the out/in setting).
        internal static float Gap() => Support(Rot, Normal) + Vector3.Dot(Normal, Pos - Point);

        /// Placement was worked out for this item this frame or the last one (its glue key is ours then).
        internal static bool ShownFor(pickupable p) => p != null && p == _item && Time.frameCount - _frame <= 1;

        internal static bool Ready(pickupable p) => p != null && p == _item && _frame == Time.frameCount;

        /// Called by the look-at hook when the held item can be mounted at the hit: keys, then this frame's pose.
        internal static void Update(fpscontroller pl, RaycastHit hit, bool gameGlue)
        {
            var p = pl.pickedUp;
            if (p != _item)
            {
                _item = p;
                Mode = Plugin.AlignToSurface.Value ? Base.Upright : Base.Held;
                Turn = TiltAway = TiltLeft = Lift = 0f;
                BuildShape(p.transform);
            }
            if (_frame != Time.frameCount)
            {
                while (TestActs.Count > 0) { var a = TestActs.Dequeue(); Do(a.Key, a.Value); }
                if (KeysAllowed(pl)) ReadKeys();
            }
            ForGameGlue = gameGlue;

            var cam = pl.Cam.transform;
            _cam = pl.Cam;
            var n = hit.normal.sqrMagnitude > 0.5f ? hit.normal.normalized : Vector3.up;
            Compute(p.transform.rotation, cam.forward, cam.up, hit.point, n, out Pos, out Rot);
            Point = hit.point; Normal = n;
            _anchor = hit.collider != null ? hit.collider.transform : null;
            if (_anchor != null)
            {
                _anchorPos = _anchor.InverseTransformPoint(Pos);
                _anchorRot = Quaternion.Inverse(_anchor.rotation) * Rot;
            }
            _frame = Time.frameCount;
            ActiveUntil = Time.unscaledTime + 0.15f;
        }

        internal static void Compute(Quaternion held, Vector3 camFwd, Vector3 camUp, Vector3 point, Vector3 n, out Vector3 pos, out Quaternion rot)
        {
            var f = Vector3.ProjectOnPlane(camFwd, n);
            if (f.sqrMagnitude < 0.04f) f = Vector3.ProjectOnPlane(camUp, n);
            f.Normalize();
            var r = Vector3.Cross(n, f);   // to the player's right along the surface
            var baseRot = held;
            if (Mode != Base.Held)
            {
                // flat: of the thinnest axis' two ends, the one already facing out of the surface more stays out
                BaseAxis = Mode == Base.Upright ? Vector3.up : Vector3.Dot(held * _thin, n) >= 0f ? _thin : -_thin;
                baseRot = Quaternion.FromToRotation(held * BaseAxis, n) * held;
            }
            KeyRot = Quaternion.AngleAxis(TiltAway, r) * Quaternion.AngleAxis(TiltLeft, f) * Quaternion.AngleAxis(Turn, n);
            rot = KeyRot * baseRot;
            // middle on the crosshair, lowest point on the surface's plane, then out/in
            var c = rot * _center;
            pos = point - c + n * (Lift - Support(rot, n) + Vector3.Dot(n, c));
        }

        private static void Do(Act a, bool fine)
        {
            float ang = fine ? Plugin.FineAngleStep.Value : Plugin.AngleStep.Value;
            float mv = fine ? Plugin.FineMoveStep.Value : Plugin.MoveStep.Value;
            switch (a)
            {
                case Act.TurnLeft: Turn = Wrap(Turn - ang); break;
                case Act.TurnRight: Turn = Wrap(Turn + ang); break;
                case Act.TiltAway: TiltAway = Wrap(TiltAway + ang); break;
                case Act.TiltToward: TiltAway = Wrap(TiltAway - ang); break;
                case Act.TiltLeft: TiltLeft = Wrap(TiltLeft + ang); break;
                case Act.TiltRight: TiltLeft = Wrap(TiltLeft - ang); break;
                case Act.Out: Lift = Mathf.Clamp(Lift + mv, -1f, 1f); break;
                case Act.In: Lift = Mathf.Clamp(Lift - mv, -1f, 1f); break;
                case Act.Reset:
                    // square on the surface again (no turn, tilt or offset), and the next way to start from:
                    // as held → flat → upright → as held
                    Mode = NextMode();
                    Turn = TiltAway = TiltLeft = Lift = 0f;
                    break;
            }
        }

        private static Base NextMode() => Mode == Base.Held ? Base.Flat : Mode == Base.Flat ? Base.Upright : Base.Held;

        private static float Wrap(float a)
        {
            a = Mathf.Repeat(a + 180f, 360f) - 180f;
            return Mathf.Abs(a) < 0.001f ? 0f : a;
        }

        // ------------------------------------------------------------------ keys

        private static readonly Dictionary<KeyCode, float> _repeat = new Dictionary<KeyCode, float>();

        /// Not while typing (the game's text fields, RevaMP's chat), in the game's computer, or sitting (the arrows
        /// steer and shift gears in a car).
        private static bool KeysAllowed(fpscontroller pl) =>
            mainscript.s != null && !mainscript.s.SomeInputSelected() && mainscript.s.crsrLocked && !pl.Bsitting && pl.Car == null;

        private static void ReadKeys()
        {
            bool fine = Plugin.FineKey.Value != KeyCode.None && Input.GetKey(Plugin.FineKey.Value);
            Key(Plugin.TurnLeftKey.Value, Act.TurnLeft, fine);
            Key(Plugin.TurnRightKey.Value, Act.TurnRight, fine);
            Key(Plugin.TiltAwayKey.Value, Act.TiltAway, fine);
            Key(Plugin.TiltTowardKey.Value, Act.TiltToward, fine);
            Key(Plugin.TiltLeftKey.Value, Act.TiltLeft, fine);
            Key(Plugin.TiltRightKey.Value, Act.TiltRight, fine);
            Key(Plugin.OutKey.Value, Act.Out, fine);
            Key(Plugin.InKey.Value, Act.In, fine);
            if (Plugin.ResetKey.Value != KeyCode.None && Input.GetKeyDown(Plugin.ResetKey.Value)) Do(Act.Reset, false);
        }

        /// A press is one step; held down, it repeats after a moment.
        private static void Key(KeyCode k, Act a, bool fine)
        {
            if (k == KeyCode.None) return;
            float now = Time.unscaledTime;
            if (Input.GetKeyDown(k)) { _repeat[k] = now + 0.35f; Do(a, fine); }
            else if (Input.GetKey(k) && _repeat.TryGetValue(k, out var at) && now >= at) { _repeat[k] = now + 0.06f; Do(a, fine); }
        }

        // ------------------------------------------------------------------ the item's shape

        // points of the item's colliders, and spheres (centre + radius), in the item's rotation frame and world units
        private static readonly List<Vector3> _pts = new List<Vector3>();
        private static readonly List<Vector4> _spheres = new List<Vector4>();
        private static Vector3 _center;
        internal static Vector3 Center => _center;
        private static readonly List<Renderer> _renderers = new List<Renderer>();

        private static void BuildShape(Transform root)
        {
            _pts.Clear(); _spheres.Clear(); _renderers.Clear();
            var inv = Quaternion.Inverse(root.rotation);
            Vector3 L(Vector3 world) => inv * (world - root.position);
            foreach (var col in root.GetComponentsInChildren<Collider>(true))
            {
                if (col == null || col.isTrigger) continue;
                var t = col.transform;
                var s = t.lossyScale; float smax = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
                switch (col)
                {
                    case BoxCollider b:
                        Corners(b.center, b.size * 0.5f, v => _pts.Add(L(t.TransformPoint(v))));
                        break;
                    case SphereCollider sp:
                        _spheres.Add(Sphere(L(t.TransformPoint(sp.center)), sp.radius * smax));
                        break;
                    case CapsuleCollider cp:
                    {
                        var axis = cp.direction == 0 ? Vector3.right : cp.direction == 1 ? Vector3.up : Vector3.forward;
                        float half = Mathf.Max(0f, cp.height * 0.5f - cp.radius);
                        _spheres.Add(Sphere(L(t.TransformPoint(cp.center + axis * half)), cp.radius * smax));
                        _spheres.Add(Sphere(L(t.TransformPoint(cp.center - axis * half)), cp.radius * smax));
                        break;
                    }
                    case MeshCollider mc when mc.sharedMesh != null:
                    {
                        var m = mc.sharedMesh;
                        if (m.isReadable && m.vertexCount <= 5000) foreach (var v in m.vertices) _pts.Add(L(t.TransformPoint(v)));
                        else Corners(m.bounds.center, m.bounds.extents, v => _pts.Add(L(t.TransformPoint(v))));
                        break;
                    }
                    default:
                    {
                        var b = col.bounds;
                        if (b.size != Vector3.zero) Corners(b.center, b.extents, v => _pts.Add(L(v)));
                        break;
                    }
                }
            }
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                if (r is MeshRenderer || r is SkinnedMeshRenderer) _renderers.Add(r);
            if (_pts.Count == 0 && _spheres.Count == 0)
                foreach (var r in _renderers) Corners(r.bounds.center, r.bounds.extents, v => _pts.Add(L(v)));
            if (_pts.Count == 0 && _spheres.Count == 0) _pts.Add(Vector3.zero);
            var min = Vector3.positiveInfinity; var max = Vector3.negativeInfinity;
            foreach (var v in _pts) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
            foreach (var sp in _spheres) { var c = (Vector3)sp; min = Vector3.Min(min, c - Vector3.one * sp.w); max = Vector3.Max(max, c + Vector3.one * sp.w); }
            _center = (min + max) * 0.5f;
            // the thinnest axis (flat lays the item on its biggest side); about even: its own up axis
            var size = max - min;
            float least = Mathf.Min(size.x, Mathf.Min(size.y, size.z));
            _thin = size.y <= least * 1.1f ? Vector3.up : size.x <= size.z ? Vector3.right : Vector3.forward;
        }

        private static Vector4 Sphere(Vector3 c, float r) => new Vector4(c.x, c.y, c.z, r);

        private static void Corners(Vector3 c, Vector3 e, System.Action<Vector3> add)
        {
            for (int i = 0; i < 8; i++)
                add(c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z));
        }

        /// The lowest the item reaches along `n` turned by `rot` (relative to its origin).
        private static float Support(Quaternion rot, Vector3 n)
        {
            var d = Quaternion.Inverse(rot) * n;
            float m = float.PositiveInfinity;
            foreach (var v in _pts) m = Mathf.Min(m, Vector3.Dot(d, v));
            foreach (var sp in _spheres) m = Mathf.Min(m, Vector3.Dot(d, (Vector3)sp) - sp.w);
            return float.IsInfinity(m) ? 0f : m;
        }

        // ------------------------------------------------------------------ the preview

        private static Material _depth, _mat;
        private static Camera _cam;   // the player's camera: only it draws the copy
        private static bool _camsLogged;
        private static bool _matTried;

        /// The copy is first drawn into the depth buffer only, then coloured where it is the front-most surface, so its
        /// overlapping parts don't add up. The colour is added to what is behind it (a see-through glow). Normal alpha
        /// blending came out nearly solid or nearly invisible in the beta, depending on the shader (its post effects read
        /// the frame's alpha, which additive blending leaves alone); compared in test screenshots, 2026-10-17. The
        /// always-there unlit Internal-Colored is used for both passes; it also takes the mesh's vertex colours.
        private static bool Mats()
        {
            if (_matTried) return _mat != null;
            _matTried = true;
            var sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null) { Plugin.Log.LogWarning("no shader for the placement preview"); return false; }
            Material M(int queue, BlendMode src, BlendMode dst, bool zwrite)
            {
                var m = new Material(sh) { hideFlags = HideFlags.HideAndDontSave, renderQueue = queue };
                m.SetFloat("_SrcBlend", (float)src);
                m.SetFloat("_DstBlend", (float)dst);
                m.SetFloat("_Cull", (float)CullMode.Back);
                m.SetFloat("_ZWrite", zwrite ? 1f : 0f);
                m.SetFloat("_ZTest", (float)CompareFunction.LessEqual);
                return m;
            }
            _depth = M(3100, BlendMode.Zero, BlendMode.One, true);     // leaves the colour as it is
            _mat = M(3101, BlendMode.One, BlendMode.One, false);
            return true;
        }

        private static void SetColour()
        {
            var c = Plugin.PreviewColor.Value;
            _mat.color = new Color(c.r * c.a, c.g * c.a, c.b * c.a, 0f);   // A = strength
        }

        private static void LogCameras(int layer)
        {
            var s = "placement preview: layer " + layer + " (" + LayerMask.LayerToName(layer) + "), player camera " + (_cam != null ? _cam.name : "none") +
                    ", colour space " + QualitySettings.activeColorSpace + "; material blend " + _mat.GetFloat("_SrcBlend") + "/" + _mat.GetFloat("_DstBlend") +
                    " zwrite " + _mat.GetFloat("_ZWrite") + " queue " + _mat.renderQueue + "; cameras:";
            foreach (var c in Camera.allCameras)
                s += " [" + c.name + " depth " + c.depth + " clip " + c.nearClipPlane + "-" + c.farClipPlane + " layer " + ((c.cullingMask & (1 << layer)) != 0) +
                     " path " + c.actualRenderingPath + " hdr " + c.allowHDR + " target " + (c.targetTexture != null ? c.targetTexture.name : "screen") + "]";
            Plugin.Log.LogInfo(s);
        }

        /// Called late in the frame: the see-through copy of the held item where it would be mounted.
        internal static void Draw()
        {
            DrawnMeshes = 0;
            if (!Plugin.ShowPreview.Value || _item == null || Time.frameCount - _frame > 0) return;
            if (!Mats()) return;
            if (!_camsLogged && _renderers.Count > 0 && _renderers[0] != null) { _camsLogged = true; LogCameras(_renderers[0].gameObject.layer); }
            SetColour();
            var root = _item.transform;
            var pos = Pos; var rot = Rot;
            if (_anchor != null) { pos = _anchor.TransformPoint(_anchorPos); rot = _anchor.rotation * _anchorRot; }
            var to = Matrix4x4.TRS(pos, rot, Vector3.one) * Matrix4x4.TRS(root.position, root.rotation, Vector3.one).inverse;
            foreach (var r in _renderers)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                Mesh m = r is SkinnedMeshRenderer sk ? sk.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                if (m == null) continue;
                var mx = to * r.localToWorldMatrix;
                for (int i = 0; i < m.subMeshCount; i++)
                {
                    Graphics.DrawMesh(m, mx, _depth, r.gameObject.layer, _cam, i, null, ShadowCastingMode.Off, false);
                    Graphics.DrawMesh(m, mx, _mat, r.gameObject.layer, _cam, i, null, ShadowCastingMode.Off, false);
                    DrawnMeshes++;
                }
            }
            if (DrawnMeshes > 0) { LastDrawnMeshes = DrawnMeshes; DrawnFrames++; }
        }

        // ------------------------------------------------------------------ the key help

        internal static string Help()
        {
            string K(KeyCode k) => KeyName(k);
            var keys = K(Plugin.TurnLeftKey.Value) + "/" + K(Plugin.TurnRightKey.Value) + " turn   " +
                       K(Plugin.TiltAwayKey.Value) + "/" + K(Plugin.TiltTowardKey.Value) + " tilt   " +
                       K(Plugin.TiltLeftKey.Value) + "/" + K(Plugin.TiltRightKey.Value) + " tilt sideways   " +
                       K(Plugin.OutKey.Value) + "/" + K(Plugin.InKey.Value) + " out/in   " +
                       K(Plugin.ResetKey.Value) + " " + ModeName(NextMode()) +
                       (Plugin.FineKey.Value != KeyCode.None ? "   hold " + K(Plugin.FineKey.Value) + ": fine" : "");
            var now = ModeName(Mode) +
                      (Turn != 0f ? ", turned " + Turn.ToString("0.#") + "°" : "") +
                      (TiltAway != 0f || TiltLeft != 0f ? ", tilted " + TiltAway.ToString("0.#") + "° / " + TiltLeft.ToString("0.#") + "°" : "") +
                      (Lift != 0f ? ", " + (Lift > 0f ? "out " : "in ") + (Mathf.Abs(Lift) * 100f).ToString("0.#") + " cm" : "");
            return now + "\n" + keys;
        }

        private static string ModeName(Base b) => b == Base.Flat ? "flat" : b == Base.Upright ? "upright" : "as held";

        internal static string State() =>
            ModeName(Mode) + ", turn " + Turn.ToString("0.#") + ", tilt " + TiltAway.ToString("0.#") + "/" + TiltLeft.ToString("0.#") +
            ", out " + (Lift * 1000f).ToString("0.#") + " mm";

        internal static string KeyName(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.LeftArrow: return "←";
                case KeyCode.RightArrow: return "→";
                case KeyCode.UpArrow: return "↑";
                case KeyCode.DownArrow: return "↓";
                case KeyCode.Comma: return ",";
                case KeyCode.Period: return ".";
                case KeyCode.Slash: return "/";
                case KeyCode.Equals: return "=";
                case KeyCode.Minus: return "-";
                case KeyCode.LeftShift: case KeyCode.RightShift: return "Shift";
                case KeyCode.LeftControl: case KeyCode.RightControl: return "Ctrl";
                case KeyCode.LeftAlt: case KeyCode.RightAlt: return "Alt";
                case KeyCode.None: return "-";
                default: return k.ToString();
            }
        }
    }
}
