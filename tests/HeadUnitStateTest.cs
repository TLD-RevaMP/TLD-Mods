#pragma warning disable CS8981
using System;
using System.Collections.Generic;
using HeadUnit;
using TLDRevamp.Multiplayer;

// The Head Unit example mod's shared state (State.cs): its record read back as written, the shared screen (tab, map
// zoom, hints) and its edits, a record from before the screen was shared (0.2.x) still read, and the queue's edits.
static class HeadUnitStateTest
{
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception("FAIL: " + why); }

    static int Main()
    {
        RoundTrip();
        OldRecord();
        TabEdit();
        ZoomAndHints();
        Queue();
        Console.WriteLine("Head Unit state: " + checks + " checks passed");
        return 0;
    }

    static UnitState Sample()
    {
        var s = new UnitState { Car = 42, Index = 1, Playing = true, AnchorTime = 12.5, AnchorPos = 3f, Tab = Mode.Nav, Zoom = 4, Hints = false };
        s.Queue.Add(new QueueItem { VideoId = "dQw4w9WgXcQ", Title = "one", Duration = 213f, AddedBy = 0 });
        s.Queue.Add(new QueueItem { VideoId = "abcdefghijk", Title = "два", Duration = 0f, AddedBy = 1 });
        return s;
    }

    static RecordReader Reader(RecordWriter w) => new RecordReader(w.ToArray());

    static void RoundTrip()
    {
        var w = new RecordWriter(); Sample().Write(w);
        var r = new UnitState();
        Check(r.Read(Reader(w)), "a record reads back");
        Check(r.Car == 42 && r.Index == 1 && r.Playing && r.AnchorTime == 12.5 && r.AnchorPos == 3f, "the playback fields come back");
        Check(r.Queue.Count == 2 && r.Queue[1].VideoId == "abcdefghijk" && r.Queue[1].Title == "два" && r.Queue[1].AddedBy == 1, "the queue comes back");
        Check(r.Tab == Mode.Nav && r.Zoom == 4 && !r.Hints, "the screen comes back");

        // the screen's last three bytes: tab, zoom, hints. A tab past NAV or a zoom past the farthest is refused, the
        // state left as it was
        var w2 = new RecordWriter(); Sample().Write(w2);
        var good = w2.ToArray();
        foreach (var (at, v, what) in new[] { (3, (byte)7, "tab"), (2, (byte)(UnitState.MaxZoom + 1), "zoom") })
        {
            var bytes = (byte[])good.Clone(); bytes[bytes.Length - at] = v;
            var kept = Sample(); kept.Tab = Mode.Video; kept.Zoom = 1;
            Check(!kept.Read(new RecordReader(bytes)) && kept.Tab == Mode.Video && kept.Zoom == 1 && kept.Queue.Count == 2, "a bad " + what + " refuses the record");
        }
    }

    /// A record written before the screen was shared (0.2.x): the same fields without the last three bytes.
    static void OldRecord()
    {
        var w = new RecordWriter(); Sample().Write(w);
        var bytes = w.ToArray();
        var old = new byte[bytes.Length - 3]; Array.Copy(bytes, old, old.Length);
        var r = new UnitState { Tab = Mode.Video, Zoom = 0, Hints = false };
        Check(r.Read(new RecordReader(old)), "a record without the screen still reads");
        Check(r.Tab == Mode.Radio && r.Zoom == 2 && r.Hints && r.Queue.Count == 2, "and its screen is the default: RADIO, zoom 2, hints on");
    }

    static bool Edit(UnitState s, Action<RecordWriter> write, int from = 0, double now = 100)
    {
        var w = new RecordWriter(); write(w);
        return UnitStateType.Apply(s, Reader(w), from, now);
    }

    static void TabEdit()
    {
        var s = new UnitState();
        Check(Edit(s, w => { w.Byte((byte)Op.Tab); w.Byte((byte)Mode.Nav); }) && s.Tab == Mode.Nav, "a tab edit switches the tab");
        Check(!Edit(s, w => { w.Byte((byte)Op.Tab); w.Byte((byte)Mode.Nav); }), "the same tab again is no change (no record sent)");
        Check(!Edit(s, w => { w.Byte((byte)Op.Tab); w.Byte(3); }) && s.Tab == Mode.Nav, "a tab past NAV is refused");
        Check(!Edit(s, w => w.Byte((byte)Op.Tab)) && s.Tab == Mode.Nav, "a tab edit with no tab is refused");
        Check(Edit(s, w => { w.Byte((byte)Op.Tab); w.Byte((byte)Mode.Radio); }, from: 2) && s.Tab == Mode.Radio, "any player can switch it");
    }

    static void ZoomAndHints()
    {
        var s = new UnitState();
        Check(Edit(s, w => { w.Byte((byte)Op.Zoom); w.Byte(5); }) && s.Zoom == 5, "a zoom edit sets the map's zoom");
        Check(!Edit(s, w => { w.Byte((byte)Op.Zoom); w.Byte(5); }), "the same zoom again is no change");
        Check(!Edit(s, w => { w.Byte((byte)Op.Zoom); w.Byte(UnitState.MaxZoom + 1); }) && s.Zoom == 5, "a zoom past the farthest is refused");
        Check(!Edit(s, w => w.Byte((byte)Op.Zoom)) && s.Zoom == 5, "a zoom edit with no zoom is refused");
        Check(Edit(s, w => { w.Byte((byte)Op.Hints); w.Bool(false); }) && !s.Hints, "a hints edit turns them off");
        Check(!Edit(s, w => { w.Byte((byte)Op.Hints); w.Bool(false); }), "off again is no change");
        Check(!Edit(s, w => w.Byte((byte)Op.Hints)) && !s.Hints, "a hints edit with nothing after it is refused");
        Check(s.Tab == Mode.Radio, "zoom and hints leave the tab alone");
    }

    static void Queue()
    {
        var s = new UnitState();
        Check(Edit(s, w => { w.Byte((byte)Op.Add); w.VarUInt(2); w.String("dQw4w9WgXcQ"); w.String("a"); w.Float(10f); w.String("abcdefghijk"); w.String("b"); w.Float(20f); }, from: 1, now: 5),
            "two videos added");
        Check(s.Queue.Count == 2 && s.Index == 0 && s.Playing && s.AnchorTime == 5 && s.Queue[0].AddedBy == 1, "the first starts at once");
        Check(!Edit(s, w => { w.Byte((byte)Op.Add); w.VarUInt(1); w.String("bad id"); w.String("x"); w.Float(0f); }) && s.Queue.Count == 2, "a bad video id is refused");
        Check(Edit(s, w => w.Byte((byte)Op.Next), now: 6) && s.Index == 1 && s.AnchorPos == 0f, "next");
        Check(!Edit(s, w => w.Byte((byte)Op.Next)), "no next after the last");
        Check(Edit(s, w => w.Byte((byte)Op.Pause), now: 8) && !s.Playing && Math.Abs(s.AnchorPos - 2f) < 1e-4, "pause keeps the position");
        Check(Edit(s, w => { w.Byte((byte)Op.Tab); w.Byte((byte)Mode.Video); }) && s.Queue.Count == 2 && s.Index == 1 && !s.Playing, "a tab edit leaves the queue alone");
        Check(Edit(s, w => w.Byte((byte)Op.Clear)) && s.Queue.Count == 0 && s.Tab == Mode.Video, "clear empties the queue, not the tab");
    }
}

// What State.cs names besides the records: a few Unity types, the multiplayer API's shared type, and yt-dlp's id check.
namespace UnityEngine
{
    public struct Vector3 { public float x, y, z; public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; } }
    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Quaternion identity => new Quaternion(0, 0, 0, 1);
    }
    public struct Color { public float r, g, b, a; public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; } }
    public static class Mathf
    {
        public static float Sqrt(float v) => (float)System.Math.Sqrt(v);
        public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
        public static float Max(float a, float b) => a > b ? a : b;
        public static float Min(float a, float b) => a < b ? a : b;
    }
    public class Object { public static void Destroy(Object o) { } public static void DontDestroyOnLoad(Object o) { } }
    public class Component : Object { public GameObject gameObject; public T GetComponent<T>() where T : Component => gameObject.GetComponent<T>(); }
    public class MonoBehaviour : Component { }
    public sealed class GameObject : Object
    {
        private readonly System.Collections.Generic.List<Component> _c = new System.Collections.Generic.List<Component>();
        public GameObject(string name = null) { }
        public T AddComponent<T>() where T : Component, new() { var t = new T { gameObject = this }; _c.Add(t); return t; }
        public T GetComponent<T>() where T : Component { foreach (var c in _c) if (c is T t) return t; return null; }
    }
}

namespace TLDRevamp.Multiplayer
{
    public abstract class SharedType
    {
        public abstract string Name { get; }
        public virtual int MaxRecordBytes => 8192;
        public abstract UnityEngine.GameObject Create(RecordReader record, UnityEngine.Vector3 position, UnityEngine.Quaternion rotation);
        public abstract void Write(UnityEngine.GameObject obj, RecordWriter record);
        public abstract void Read(UnityEngine.GameObject obj, RecordReader record);
        public virtual bool OnEdit(SharedObject obj, RecordReader edit, int fromPlayer) => false;
    }
    public sealed class SharedObject { public UnityEngine.GameObject GameObject; }
    public static class MpApi { public static double SessionTime => 0; }
}

namespace HeadUnit
{
    public static class Ytdlp
    {
        public static bool ValidVideoId(string id) => id != null && System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_-]{11}$");
    }
}
