using System;
using System.Collections.Generic;
using UnityEngine;

namespace HeadUnit
{
    /// The head unit's touch screen, drawn as an old CRT: the whole face is one canvas (Crt, Screens) drawn again when
    /// what it shows changes, with the picture or the map in its window on a quad just in front. A button is pressed as
    /// the game's own knobs are: look at it (the crosshair) and click. Everything is in metres on the screen's face.
    ///
    /// The buttons have no colliders: a press is found by where the look ray meets the screen's face. Everything is
    /// drawn on the screen's own layer (playtest 2026-10-14: on the Ignore Raycast layer nothing but the picture
    /// showed, and colliders on the unit can get in the way of the game's own look ray, e.g. when mounting it).
    public sealed class Touch : MonoBehaviour
    {
        public static float Reach = 1.6f;      // as far as the game lets a player use a knob

        public sealed class Button
        {
            public string Id;
            public Rect Area;                  // on the screen's face, metres from its centre
            public Action Press;
        }

        public UnitBehaviour Unit;
        public float W, H;
        public Transform Root;
        public MeshRenderer Content;           // the picture or the map, in the window the layout gives it
        public Material ContentMat;
        private MeshRenderer _canvas, _scan;
        private Material _canvasMat, _scanMat;
        private Texture2D _canvasTex, _scanTex;
        private readonly Crt _crt = new Crt();
        private readonly Screens.Layout _layout = new Screens.Layout();
        private byte[] _rgba;
        private string _key;
        private Screens.Area _window;
        private readonly List<Button> _buttons = new List<Button>();

        private static readonly List<Touch> All = new List<Touch>();
        private int _layer;
        private pickupable _pick;

        public static Touch Looked;            // the unit whose button the crosshair is on
        /// The mouse wheel this frame while the crosshair is on a unit's buttons (+1 up, -1 down; 0: none).
        public static int Wheel;
        public static Button LookedButton;
        /// The button clicked last on this screen, and when: drawn inverse for a moment.
        public string PressedId;
        public float PressedAt = -10f;

        /// The game's sprite shader, which tints a texture with a colour (the picture in the screen's phosphor) and
        /// lets the scanlines over it through; null where the build lacks it (the picture then stays in colour).
        private static Shader _tinting;
        private static bool _looked;
        public static string PictureShader => _tinting != null ? _tinting.name : "none";

        public void Build(UnitBehaviour unit, Transform screen)
        {
            Unit = unit;
            W = screen.localScale.x; H = screen.localScale.y;
            _layer = screen.gameObject.layer;
            _pick = unit.GetComponent<pickupable>();
            Root = new GameObject("HeadUnitTouch").transform;
            Root.gameObject.layer = _layer;
            Root.SetParent(screen.parent, false);
            Root.localPosition = screen.localPosition; Root.localRotation = screen.localRotation;
            var ps = screen.parent.lossyScale;
            Root.localScale = new Vector3(1f / Mathf.Max(ps.x, 1e-4f), 1f / Mathf.Max(ps.y, 1e-4f), 1f / Mathf.Max(ps.z, 1e-4f));
            if (!_looked) { _looked = true; _tinting = Shader.Find("Sprites/Default"); Plugin.Log.LogInfo("head unit picture shader: " + PictureShader); }

            // the canvas: mipmapped, so the scanlines even out from the seat instead of shimmering
            _canvasTex = new Texture2D(Crt.W, Crt.H, TextureFormat.RGBA32, true)
                { name = "HeadUnitCrt", filterMode = FilterMode.Trilinear, anisoLevel = 4, wrapMode = TextureWrapMode.Clamp };
            _canvasMat = Look.ScreenMaterial(unit.Radio);
            _canvasMat.mainTexture = _canvasTex;
            _canvas = Quad("Canvas", -0.001f, _canvasMat);
            _canvas.transform.localScale = new Vector3(W, H, 1f);
            _rgba = new byte[Crt.W * Crt.H * 4];

            ContentMat = _tinting != null ? new Material(_tinting) : Look.ScreenMaterial(unit.Radio);
            ContentMat.mainTexture = Look.Pixel(Color.black);
            Content = Quad("Content", -0.002f, ContentMat);
            White(Content);
            Content.enabled = false;

            if (_tinting != null)
            {
                // every third line of the picture darker, as the canvas's
                _scanTex = new Texture2D(1, 3, TextureFormat.RGBA32, true) { name = "HeadUnitScan", filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Repeat };
                _scanTex.SetPixels32(new[] { new Color32(0, 0, 0, 90), new Color32(0, 0, 0, 0), new Color32(0, 0, 0, 0) });
                _scanTex.Apply(true);
                _scanMat = new Material(_tinting) { mainTexture = _scanTex };
                _scan = Quad("Scanlines", -0.0025f, _scanMat);
                White(_scan);
                _scan.enabled = false;
            }
        }

        private MeshRenderer Quad(string name, float z, Material m)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.layer = _layer;
            DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(Root, false);
            go.transform.localPosition = new Vector3(0f, 0f, z);
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = m;
            return r;
        }

        /// The sprite shader multiplies by the mesh's colours, which the game's quad lacks.
        private static void White(MeshRenderer r)
        {
            var mesh = r.GetComponent<MeshFilter>().mesh;
            var c = new Color[mesh.vertexCount];
            for (int i = 0; i < c.Length; i++) c[i] = Color.white;
            mesh.colors = c;
        }

        /// Shows `m`: the canvas drawn again if it changed, the buttons' actions, and `content` (the picture, the map;
        /// null: none) in its window, `aspect` kept (-1: fill it), tinted to the phosphor when `tint`.
        public void Present(ScreenModel m, Texture content, float aspect, bool tint, Dictionary<string, Action> actions)
        {
            string key = m.Key();
            if (key != _key)
            {
                _key = key;
                Screens.Draw(m, _crt, _layout);
                _crt.Compose(m.R, m.G, m.B, _rgba, flipY: true);
                _canvasTex.SetPixelData(_rgba, 0);
                _canvasTex.Apply(true);
                _window = _layout.Content;
                _buttons.Clear();
                foreach (var a in _layout.Buttons) _buttons.Add(new Button { Id = a.Id, Area = Metres(a) });
            }
            foreach (var b in _buttons) b.Press = actions.TryGetValue(b.Id, out var act) ? act : null;

            bool show = content != null && _window.W > 0;
            if (Content.enabled != show) Content.enabled = show;
            bool scan = show && tint && _scan != null;
            if (_scan != null && _scan.enabled != scan) _scan.enabled = scan;
            if (!show) return;
            var area = Metres(_window);
            float w = area.width, h = area.height;
            if (aspect > 0f) { if (w / h > aspect) w = h * aspect; else h = w / aspect; }
            var centre = new Vector3(area.center.x, area.center.y, 0f);
            Content.transform.localPosition = centre + new Vector3(0f, 0f, -0.002f);
            Content.transform.localScale = new Vector3(w, h, 1f);
            if (ContentMat.mainTexture != content) ContentMat.mainTexture = content;
            var want = tint && _tinting != null ? new Color(m.R / 255f, m.G / 255f, m.B / 255f, 1f) : Color.white;
            if (ContentMat.color != want) ContentMat.color = want;
            if (scan)
            {
                _scan.transform.localPosition = centre + new Vector3(0f, 0f, -0.0025f);
                _scan.transform.localScale = new Vector3(w, h, 1f);
                _scanMat.mainTextureScale = new Vector2(1f, Mathf.Max(1f, h / H * Crt.H / 3f));
            }
        }

        /// An area of the 800 × 440 grid on the screen's face, in metres from its centre (y up).
        private Rect Metres(Screens.Area a)
        {
            float x = (a.X / (float)Crt.W - 0.5f) * W, top = (0.5f - a.Y / (float)Crt.H) * H, h = a.H / (float)Crt.H * H;
            return new Rect(x, top - h, a.W / (float)Crt.W * W, h);
        }

        private void OnEnable() => All.Add(this);
        private void OnDisable() => All.Remove(this);

        private void OnDestroy()
        {
            foreach (var m in new[] { ContentMat, _canvasMat, _scanMat }) if (m != null) Destroy(m);
            if (_canvasTex != null) Destroy(_canvasTex);
            if (_scanTex != null) Destroy(_scanTex);
            // the coloured copies White made of the game's quad
            foreach (var r in new[] { Content, _scan }) if (r != null) { var f = r.GetComponent<MeshFilter>(); if (f != null && f.sharedMesh != null) Destroy(f.sharedMesh); }
        }

        /// What the screen says now (tests).
        public string ScreenText => string.Join(" | ", _crt.Texts);

        /// The characters of `s` the screen's font can't draw (tests).
        public string Missing(string s)
        {
            string m = Crt.Missing(s);
            return m.Length == 0 ? "the CRT font draws them all" : "the CRT font lacks '" + m + "'";
        }

        /// Why the last look found no button (bridge: `get HeadUnit.Touch.Miss`), or what it found.
        public static string Miss = "";

        /// The button `ray` points at (Looked, LookedButton), or none.
        public static void Find(Ray ray)
        {
            Looked = null; LookedButton = null;
            float best = Reach; string miss = "no screen ahead within " + Reach + " m";
            foreach (var t in All)
            {
                if (t.Root == null || (t._pick != null && t._pick.pickedUp)) continue;   // not while it's carried
                var plane = new Plane(t.Root.forward, t.Root.position);
                if (Vector3.Dot(ray.direction, t.Root.forward) <= 0f || !plane.Raycast(ray, out float d) || d > best) continue;   // the face points -z
                var p = t.Root.InverseTransformPoint(ray.GetPoint(d));
                if (Mathf.Abs(p.x) > t.W * 0.5f || Mathf.Abs(p.y) > t.H * 0.5f) { miss = $"off the screen at {p.x:F3}, {p.y:F3} ({d:F2} m)"; continue; }
                miss = $"on the screen at {p.x:F3}, {p.y:F3}, between buttons";
                foreach (var b in t._buttons)
                    if (b.Area.Contains(new Vector2(p.x, p.y))) { Looked = t; LookedButton = b; best = d; break; }
            }
            if (LookedButton == null) { Miss = miss; return; }
            // something solid between the eye and the screen (a wall): not pressable. The unit's own car doesn't count:
            // its dashboard can stand in front of the radio's face (playtest 2026-10-14, Car05: never pressable)
            var car = Looked.Unit.Car != null ? Looked.Unit.Car.transform : Looked.Unit.transform;
            foreach (var hit in Physics.RaycastAll(ray, best - 0.03f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                if (!hit.collider.transform.IsChildOf(car) && !hit.collider.transform.IsChildOf(mainscript.s.player.transform))
                { Miss = "blocked by " + hit.collider.name + " (" + hit.collider.transform.root.name + ")"; Looked = null; LookedButton = null; return; }
            Miss = "on " + LookedButton.Id;
        }

        /// Where a button is: its centre on the screen's face (world space) and the face's outward direction.
        public bool Where(string id, out Vector3 point, out Vector3 outward)
        {
            point = outward = Vector3.zero;
            foreach (var b in _buttons)
                if (b.Id == id) { point = Root.TransformPoint(new Vector3(b.Area.center.x, b.Area.center.y, 0f)); outward = -Root.forward; return true; }
            return false;
        }

        public IEnumerable<string> ButtonIds() { foreach (var b in _buttons) yield return b.Id; }

        /// What the crosshair is on, and a click on it. Called once a frame (Controls).
        public static void Tick(bool canClick)
        {
            Looked = null; LookedButton = null; Wheel = 0;
            // the player's own camera: the game tags none as MainCamera (Camera.main is null, so no press ever landed)
            var pl = mainscript.s != null ? mainscript.s.player : null;
            var cam = pl != null && pl.Cam != null ? pl.Cam : Camera.main;
            if (cam == null) return;
            Find(cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f)));
            if (LookedButton == null) return;
            if (canClick) { float w = Input.mouseScrollDelta.y; Wheel = w > 0.01f ? 1 : w < -0.01f ? -1 : 0; }
            if (canClick && Input.GetMouseButtonDown(0))
            {
                Looked.PressedId = LookedButton.Id; Looked.PressedAt = Time.realtimeSinceStartup;
                LookedButton.Press?.Invoke();
            }
        }
    }
}
