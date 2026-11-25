#pragma warning disable CS8981
using System;
using System.Collections.Generic;
using HeadUnit;
using TLDRevamp.Multiplayer;

// The Head Unit example mod's shared state (State.cs): its record read back as written, the shared screen (tab, map
// zoom, hints, the + ADD screen) and its edits, records from before the screen (0.2.x) and before the + ADD screen
// (0.3.0) still read, the queue's edits, and the record's size with everything full.
static class HeadUnitStateTest
{
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception("FAIL: " + why); }

    static int Main()
    {
        RoundTrip();
        OldRecord();
        AddRecord();
        TabEdit();
        ZoomAndHints();
        AddEdits();
        Queue();
        FullRecord();
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

        // the screen's three bytes (tab, zoom, hints), before the closed + ADD screen's. A tab past NAV or a zoom past
        // the farthest is refused, the state left as it was
        var w2 = new RecordWriter(); Sample().Write(w2);
        var good = w2.ToArray();
        foreach (var (at, v, what) in new[] { (3 + ClosedAddBytes, (byte)7, "tab"), (2 + ClosedAddBytes, (byte)(UnitState.MaxZoom + 1), "zoom") })
        {
            var bytes = (byte[])good.Clone(); bytes[bytes.Length - at] = v;
            var kept = Sample(); kept.Tab = Mode.Video; kept.Zoom = 1;
            Check(!kept.Read(new RecordReader(bytes)) && kept.Tab == Mode.Video && kept.Zoom == 1 && kept.Queue.Count == 2, "a bad " + what + " refuses the record");
        }
    }

    /// A closed + ADD screen at the record's end: its byte, an empty text, not searching, no words, no results, page 0.
    const int ClosedAddBytes = 6;

    static byte[] Cut(byte[] bytes, int n) { var b = new byte[bytes.Length - n]; Array.Copy(bytes, b, b.Length); return b; }

    /// Records written before the screen was shared (0.2.x) and before the + ADD screen was (0.3.0): the same fields
    /// without the last ones.
    static void OldRecord()
    {
        var w = new RecordWriter(); Sample().Write(w);
        var bytes = w.ToArray();
        var r = new UnitState { Tab = Mode.Video, Zoom = 0, Hints = false, Add = AddScreen.Bar };
        Check(r.Read(new RecordReader(Cut(bytes, 3 + ClosedAddBytes))), "a record without the screen still reads");
        Check(r.Tab == Mode.Radio && r.Zoom == 2 && r.Hints && r.Add == AddScreen.None && r.Queue.Count == 2, "and its screen is the default: RADIO, zoom 2, hints on, + ADD closed");
        var r2 = new UnitState { Add = AddScreen.Results, AddText = "x" };
        Check(r2.Read(new RecordReader(Cut(bytes, ClosedAddBytes))), "a 0.3.0 record (the screen, no + ADD screen) still reads");
        Check(r2.Tab == Mode.Nav && r2.Zoom == 4 && r2.Add == AddScreen.None && r2.AddText == "" && r2.Results.Count == 0, "with its screen, and + ADD closed");
    }

    static UnitState WithResults()
    {
        var s = Sample();
        s.Add = AddScreen.Results; s.AddText = "lofi beats"; s.Searching = false; s.Searched = "lofi beats"; s.ResultsTop = 1;
        s.Results.Add(new QueueItem { VideoId = "jNQXAC9IVRw", Title = "Me at the zoo", Duration = 19f });
        s.Results.Add(new QueueItem { VideoId = "dQw4w9WgXcQ", Title = "три", Duration = 213f });
        return s;
    }

    /// The + ADD screen in the record: read back, and a bad screen or result refuses the whole record.
    static void AddRecord()
    {
        var w = new RecordWriter(); WithResults().Write(w);
        var r = new UnitState();
        Check(r.Read(Reader(w)), "a record with the + ADD screen reads back");
        Check(r.Add == AddScreen.Results && r.AddText == "lofi beats" && !r.Searching && r.Searched == "lofi beats" && r.ResultsTop == 1, "the + ADD screen comes back");
        Check(r.Results.Count == 2 && r.Results[1].VideoId == "dQw4w9WgXcQ" && r.Results[1].Title == "три" && r.Results[1].Duration == 213f, "its results come back");
        Check(r.Queue.Count == 2 && r.Tab == Mode.Nav, "and the rest with it");

        var bad = new UnitState { Add = AddScreen.Bar }; var wb = new RecordWriter();
        Sample().Write(wb); var bytes = wb.ToArray(); bytes[bytes.Length - ClosedAddBytes] = 3;
        Check(!bad.Read(new RecordReader(bytes)) && bad.Add == AddScreen.Bar, "a + ADD screen past Results refuses the record");
        var s = WithResults(); s.Results[0].VideoId = "not an id"; var wi = new RecordWriter(); s.Write(wi);
        Check(!new UnitState().Read(Reader(wi)), "a result with a bad video id refuses the record");
        var many = WithResults(); while (many.Results.Count <= UnitState.MaxResults) many.Results.Add(new QueueItem { VideoId = "abcdefghijk", Title = "t" });
        var wm = new RecordWriter(); many.Write(wm);
        Check(!new UnitState().Read(Reader(wm)), "more than MaxResults results refuse the record");
    }

    static void Results(RecordWriter w, string words, uint at, params (string Id, string Title, float Duration)[] e)
    {
        w.Byte((byte)Op.AddResults); w.String(words); w.VarUInt(at); w.VarUInt((uint)e.Length);
        foreach (var x in e) { w.String(x.Id); w.String(x.Title); w.Float(x.Duration); }
    }

    /// One player's + ADD screen for everyone: opened, typed into, searched, its results in pieces, paged, closed.
    static void AddEdits()
    {
        var s = new UnitState();
        Check(!Edit(s, w => w.Byte((byte)Op.AddClose)), "closing a closed screen is no change");
        Check(!Edit(s, w => { w.Byte((byte)Op.AddText); w.String("x"); }) && s.AddText == "", "text with the screen closed is refused");
        Check(Edit(s, w => { w.Byte((byte)Op.AddOpen); w.String(""); }, from: 2) && s.Add == AddScreen.Bar && s.Tab == Mode.Video, "+ ADD opens the bar for everyone, on MEDIA");
        Check(!Edit(s, w => { w.Byte((byte)Op.AddOpen); w.String(""); }), "the same bar again is no change");
        Check(Edit(s, w => { w.Byte((byte)Op.AddText); w.String("lofi"); }) && s.AddText == "lofi", "what a player types shows in the bar");
        Check(!Edit(s, w => { w.Byte((byte)Op.AddText); w.String("lofi"); }), "the same text again is no change");
        Check(Edit(s, w => { w.Byte((byte)Op.AddText); w.String(new string('é', 400)); }) && System.Text.Encoding.UTF8.GetByteCount(s.AddText) <= UnitState.TextBytes, "a long text is cut to TextBytes");
        Check(Edit(s, w => { w.Byte((byte)Op.AddText); w.String("lofi beats"); }), "text again");
        Check(Edit(s, w => { w.Byte((byte)Op.AddSearching); w.Bool(true); }) && s.Searching, "a search runs, for everyone to see");
        Check(!Edit(s, w => Results(w, "lofi beats", 1, ("jNQXAC9IVRw", "a", 1f))) && s.Add == AddScreen.Bar, "a later piece before the first is refused");
        Check(!Edit(s, w => Results(w, "lofi beats", 0, ("bad id", "a", 1f))) && s.Add == AddScreen.Bar && s.Searching, "a bad video id refuses the piece");
        Check(Edit(s, w => Results(w, "lofi beats", 0, ("jNQXAC9IVRw", "a", 19f), ("dQw4w9WgXcQ", "b", 213f))), "the first piece of results");
        Check(s.Add == AddScreen.Results && !s.Searching && s.Searched == "lofi beats" && s.Results.Count == 2 && s.ResultsTop == 0, "shows them in place of the bar");
        Check(!Edit(s, w => Results(w, "lofi beats", 0, ("abcdefghijk", "c", 5f))) && s.Results.Count == 2, "another search's first piece, now the results show, is refused");
        Check(!Edit(s, w => Results(w, "lofi beats", 3, ("abcdefghijk", "c", 5f))) && s.Results.Count == 2, "a piece at the wrong place is refused");
        Check(!Edit(s, w => Results(w, "other words", 2, ("abcdefghijk", "c", 5f))) && s.Results.Count == 2, "a piece of another search is refused");
        Check(Edit(s, w => Results(w, "lofi beats", 2, ("abcdefghijk", "c", 5f))) && s.Results.Count == 3 && s.Results[2].Title == "c", "the next piece adds to them");
        var tooMany = new (string, string, float)[UnitState.MaxResults - 2];
        for (int i = 0; i < tooMany.Length; i++) tooMany[i] = ("abcdefghijk", "t", 1f);
        Check(!Edit(s, w => Results(w, "lofi beats", 3, tooMany)) && s.Results.Count == 3, "past MaxResults is refused");
        Check(!Edit(s, w => { w.Byte((byte)Op.AddText); w.String("x"); }) && s.AddText == "lofi beats", "text with the results showing is refused");
        Check(Edit(s, w => { w.Byte((byte)Op.AddPage); w.Byte(2); }) && s.ResultsTop == 2, "a page of results, for everyone");
        Check(!Edit(s, w => { w.Byte((byte)Op.AddPage); w.Byte(2); }), "the same page again is no change");
        Check(!Edit(s, w => { w.Byte((byte)Op.AddPage); w.Byte(3); }) && s.ResultsTop == 2, "a page past the results is refused");
        Check(Edit(s, w => { w.Byte((byte)Op.AddOpen); w.String("lofi beats"); }) && s.Add == AddScreen.Bar && s.AddText == "lofi beats" && s.Results.Count == 0 && s.ResultsTop == 0, "SEARCH AGAIN: back to the bar with the words");
        Check(!Edit(s, w => { w.Byte((byte)Op.AddPage); w.Byte(0); }), "a page with the bar showing is refused");
        Check(Edit(s, w => w.Byte((byte)Op.AddClose)) && s.Add == AddScreen.None && s.AddText == "" && !s.Searching, "closed for everyone, its text gone");
        Check(Edit(s, w => { w.Byte((byte)Op.AddOpen); w.String("x"); }) && Edit(s, w => { w.Byte((byte)Op.Tab); w.Byte((byte)Mode.Nav); }) && s.Add == AddScreen.None && s.AddText == "", "another tab closes it");
        Check(!Edit(s, w => { w.Byte((byte)Op.AddSearching); w.Bool(true); }) && !s.Searching, "searching with the screen closed is refused");
    }

    /// Everything full: 200 queued and 20 results, every title at its longest in a script of 3-byte characters, the
    /// bar's text and words at theirs. The record stays under its cap (UnitStateType.MaxRecordBytes).
    static void FullRecord()
    {
        string title = UnitState.Clip(new string('語', 100), UnitState.TitleBytes), text = UnitState.Clip(new string('語', 200), UnitState.TextBytes);
        var s = new UnitState { Add = AddScreen.Results, AddText = text, Searched = text, Searching = true, ResultsTop = 16 };
        for (int i = 0; i < UnitState.MaxQueue; i++) s.Queue.Add(new QueueItem { VideoId = "dQw4w9WgXcQ", Title = title, Duration = 36000f, AddedBy = 1000 });
        for (int i = 0; i < UnitState.MaxResults; i++) s.Results.Add(new QueueItem { VideoId = "dQw4w9WgXcQ", Title = title, Duration = 36000f });
        var w = new RecordWriter(); s.Write(w);
        int cap = new UnitStateType().MaxRecordBytes;
        Check(w.Length <= cap, $"a full record is {w.Length} bytes, under {cap}");
        Check(new UnitState().Read(Reader(w)), "and reads back");
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
