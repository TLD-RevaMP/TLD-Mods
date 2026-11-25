using System.Collections.Generic;
using TLDRevamp.Multiplayer;
using UnityEngine;

namespace HeadUnit
{
    public enum Mode : byte { Radio = 0, Video = 1, Nav = 2 }

    /// The + ADD screen (MEDIA): closed, its search bar, or a search's results.
    public enum AddScreen : byte { None = 0, Bar = 1, Results = 2 }

    public sealed class QueueItem
    {
        public string VideoId, Title;
        public float Duration;   // seconds; 0 = unknown until it plays
        public int AddedBy;
    }

    /// What everyone in one car shares on its head unit: the YouTube queue, what plays and where, its volume, and what
    /// the screen shows: the tab (RADIO, MEDIA, NAV), the map's zoom, and the + ADD screen (open or not, what its bar
    /// holds, a search running, the results and the page of them shown). One player changing any of them changes it
    /// for everyone, as one screen would. What stays each player's own (UnitBehaviour): the queue list, FULL, BIG, and
    /// whose keyboard types into the bar. The radio is shared by the game.
    /// Playback is a position on the session clock, never a stream:
    /// position = Playing ? AnchorPos + (SessionTime - AnchorTime) : AnchorPos. Every machine plays the video itself
    /// and keeps to that (Video.cs).
    public sealed class UnitState
    {
        public uint Car;                 // the car's id in the session (MpApi.WorldObjectId); 0 = single player
        public float Volume = 0.6f;      // unused since the volume became each player's own (Plugin.VideoVolume); kept on the wire for older units
        public readonly List<QueueItem> Queue = new List<QueueItem>();
        public int Index;                // the video playing (or paused) in Queue
        public bool Playing;
        public double AnchorTime;        // session time of the last play / pause / seek
        public float AnchorPos;          // video position then
        // the screen (0.3.0), written after the queue: a record from before has none of it and reads as these
        public Mode Tab = Mode.Radio;    // the tab on the screen
        public byte Zoom = 2;            // the NAV map's zoom (MapView.Zooms: 0 nearest .. MaxZoom farthest)
        public bool Hints = true;        // the next bend shown over the other tabs (0.3.0; the screen no longer shows it)
        // the + ADD screen (0.3.2), written after the screen: a record from before has none of it and reads as closed
        public AddScreen Add = AddScreen.None;
        public string AddText = "";      // what the search bar holds: a link or words
        public bool Searching;           // a player's machine is searching for it
        public string Searched = "";     // the words the results are for
        public readonly List<QueueItem> Results = new List<QueueItem>();   // VideoId, Title, Duration
        public byte ResultsTop;          // the first result shown

        /// The farthest zoom (MapView.Zooms has MaxZoom + 1).
        public const byte MaxZoom = 5;
        /// The most results (Ytdlp.SearchCount asks for 15), and the bar's longest text in UTF-8 bytes. With a full
        /// queue the record stays under its 32 KB: 200 queued (~28 KB) and 20 results (~2.8 KB).
        public const int MaxResults = 20, TextBytes = 300;

        public const int MaxQueue = 200;
        /// A title's longest, in UTF-8 bytes: 200 of them stay under the record's 32 KB, in any script (60 Japanese
        /// characters are 180 bytes; the record went over the cap at ~165 such titles and stopped reaching the others).
        public const int TitleBytes = 120;

        /// `s` cut to at most `max` UTF-8 bytes, never inside a character.
        public static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || System.Text.Encoding.UTF8.GetByteCount(s) <= max) return s ?? "";
            int n = 0, i = 0;
            while (i < s.Length)
            {
                int len = char.IsHighSurrogate(s[i]) && i + 1 < s.Length ? 2 : 1;
                int b = System.Text.Encoding.UTF8.GetByteCount(s.Substring(i, len));
                if (n + b > max) break;
                n += b; i += len;
            }
            return s.Substring(0, i);
        }

        /// The last video played to its end and nothing came after (a new one added then starts at once).
        public bool AtEnd(double now) => !Playing && Index == Queue.Count - 1 && Current != null && Current.Duration > 0f && Position(now) >= Current.Duration - 0.5f;

        public float Position(double now) => Playing ? AnchorPos + (float)(now - AnchorTime) : AnchorPos;
        public QueueItem Current => Index >= 0 && Index < Queue.Count ? Queue[Index] : null;

        public void Write(RecordWriter w)
        {
            w.UInt(Car); w.Float(Volume); w.VarUInt((uint)Index); w.Bool(Playing); w.Double(AnchorTime); w.Float(AnchorPos);
            w.VarUInt((uint)Queue.Count);
            foreach (var q in Queue) { w.String(q.VideoId); w.String(q.Title); w.Float(q.Duration); w.VarUInt((uint)q.AddedBy); }
            w.Byte((byte)Tab); w.Byte(Zoom); w.Bool(Hints);
            w.Byte((byte)Add); w.String(AddText); w.Bool(Searching); w.String(Searched);
            w.VarUInt((uint)Results.Count);
            foreach (var q in Results) { w.String(q.VideoId); w.String(q.Title); w.Float(q.Duration); }
            w.Byte(ResultsTop);
        }

        /// The + ADD screen closed: its text and results go with it (they would only fill the record).
        public void CloseAdd() { Add = AddScreen.None; AddText = ""; Searching = false; Searched = ""; Results.Clear(); ResultsTop = 0; }

        public bool Read(RecordReader r)
        {
            uint car = r.UInt(); float vol = r.Float(); int index = (int)r.VarUInt(); bool playing = r.Bool(); double at = r.Double(); float pos = r.Float();
            uint n = r.VarUInt();
            if (r.Bad || n > MaxQueue || float.IsNaN(vol) || float.IsNaN(pos) || double.IsNaN(at)) return false;
            var q = new List<QueueItem>();
            for (uint i = 0; i < n; i++)
            {
                var it = new QueueItem { VideoId = r.String(), Title = r.String(), Duration = r.Float(), AddedBy = (int)r.VarUInt() };
                if (r.Bad || !Ytdlp.ValidVideoId(it.VideoId)) return false;
                q.Add(it);
            }
            // the screen came after the queue (0.3.0): a record from before has none of it
            byte tab = r.Remaining >= 1 ? r.Byte() : (byte)Mode.Radio;
            byte zoom = r.Remaining >= 1 ? r.Byte() : (byte)2;
            bool hints = r.Remaining < 1 || r.Bool();
            if (r.Bad || tab > (byte)Mode.Nav || zoom > MaxZoom) return false;
            // the + ADD screen came after it (0.3.2): a record from before has it closed
            byte add = 0, top = 0; string text = "", searched = ""; bool searching = false;
            var res = new List<QueueItem>();
            if (r.Remaining >= 1)
            {
                add = r.Byte(); text = r.String(); searching = r.Bool(); searched = r.String();
                uint m = r.VarUInt();
                if (r.Bad || add > (byte)AddScreen.Results || m > MaxResults) return false;
                for (uint i = 0; i < m; i++)
                {
                    var it = new QueueItem { VideoId = r.String(), Title = r.String(), Duration = r.Float() };
                    if (r.Bad || !Ytdlp.ValidVideoId(it.VideoId)) return false;
                    res.Add(it);
                }
                top = r.Byte();
                if (r.Bad) return false;
            }
            Tab = (Mode)tab; Zoom = zoom; Hints = hints;
            Add = (AddScreen)add; AddText = text ?? ""; Searching = searching; Searched = searched ?? ""; ResultsTop = top;
            Results.Clear(); Results.AddRange(res);
            Car = car; Volume = Mathf.Clamp01(vol); Index = index; Playing = playing; AnchorTime = at; AnchorPos = Mathf.Max(0f, pos);
            Queue.Clear(); Queue.AddRange(q);
            return true;
        }
    }

    /// The edits a player can ask the unit's owner for (UnitStateType.OnEdit).
    public enum Op : byte
    {
        Volume = 1, Play = 2, Pause = 3, Seek = 4, Next = 5, Prev = 6, Add = 7, Remove = 8, Jump = 9, Clear = 11, Ended = 12, Tab = 13, Zoom = 14, Hints = 15,
        // the + ADD screen (0.3.2)
        AddOpen = 16, AddClose = 17, AddText = 18, AddSearching = 19, AddResults = 20, AddPage = 21,
    }

    /// One shared object per car with a head unit in use: it carries the UnitState. It has no body of its own (an
    /// empty GameObject); the unit in the car's radio slot finds it by the car's id. Whoever first uses a unit in a
    /// car makes it; edits from everyone go to its owner, who applies them and sends the new record.
    public sealed class UnitStateType : SharedType
    {
        public override string Name => "unitstate";
        public override int MaxRecordBytes => 32 * 1024;   // 200 queued videos with titles

        public override GameObject Create(RecordReader record, Vector3 position, Quaternion rotation)
        {
            var go = new GameObject("HeadUnitState");
            var holder = go.AddComponent<StateHolder>();
            if (!holder.State.Read(record)) { Object.Destroy(go); return null; }
            Object.DontDestroyOnLoad(go);
            return go;
        }

        public override void Write(GameObject obj, RecordWriter record) => obj.GetComponent<StateHolder>().State.Write(record);

        public override void Read(GameObject obj, RecordReader record)
        {
            var h = obj.GetComponent<StateHolder>();
            var copy = new UnitState();
            if (copy.Read(record)) { h.State = copy; h.Changed = true; }
        }

        public override bool OnEdit(SharedObject obj, RecordReader edit, int fromPlayer)
        {
            var h = obj.GameObject.GetComponent<StateHolder>();
            bool ok = Apply(h.State, edit, fromPlayer, MpApi.SessionTime);
            if (ok) h.Changed = true;
            return ok;
        }

        /// Apply one edit (the owner, or this machine alone). False: refused, nothing changed.
        internal static bool Apply(UnitState s, RecordReader e, int from, double now)
        {
            var op = (Op)e.Byte();
            switch (op)
            {
                case Op.Volume:
                {
                    float v = e.Float();
                    if (e.Bad || float.IsNaN(v)) return false;
                    s.Volume = Mathf.Clamp01(v);
                    return true;
                }
                case Op.Play:
                    if (s.Current == null || s.Playing) return false;
                    s.AnchorTime = now; s.Playing = true;   // AnchorPos stays: on from where it paused
                    return true;
                case Op.Pause:
                    if (!s.Playing) return false;
                    s.AnchorPos = s.Position(now); s.AnchorTime = now; s.Playing = false;
                    return true;
                case Op.Seek:
                {
                    float to = e.Float();
                    if (e.Bad || to < 0f || s.Current == null) return false;
                    if (s.Current.Duration > 0f) to = Mathf.Min(to, s.Current.Duration);
                    s.AnchorPos = to; s.AnchorTime = now;
                    return true;
                }
                case Op.Next: return JumpTo(s, s.Index + 1, now);
                case Op.Prev:
                    // the first seconds: the one before; later: this one from the start
                    if (s.Position(now) > 5f || s.Index == 0) { s.AnchorPos = 0f; s.AnchorTime = now; return true; }
                    return JumpTo(s, s.Index - 1, now);
                case Op.Jump:
                {
                    int i = (int)e.VarUInt();
                    return !e.Bad && JumpTo(s, i, now);
                }
                case Op.Ended:
                {
                    // a viewer's video ended: only counts for the video still current (two viewers end the same one)
                    int i = (int)e.VarUInt();
                    // the length the viewer's player gave the video (older units don't send it)
                    float len = e.Remaining >= 4 ? e.Float() : 0f;
                    if (e.Bad || i != s.Index || !s.Playing) return false;
                    // A song added from Spotify carries Spotify's length, which can be longer than YouTube's video: the
                    // owner refused every end and the queue stood on the last frame for good. A viewer's length that the
                    // shared position has reached is the video's real length from now on.
                    if (s.Current != null && len > 1f && !float.IsNaN(len) && s.Position(now) >= len - 3f && (s.Current.Duration <= 0f || len < s.Current.Duration))
                        s.Current.Duration = len;
                    // not long before the end the owner knows of (a viewer's player that misreports its length)
                    if (s.Current != null && s.Current.Duration > 0f && s.Position(now) < s.Current.Duration - 3f) return false;
                    if (s.Index + 1 < s.Queue.Count) return JumpTo(s, s.Index + 1, now);
                    s.Playing = false; s.AnchorPos = s.Current?.Duration ?? 0f; s.AnchorTime = now;
                    return true;
                }
                case Op.Add:
                {
                    uint n = e.VarUInt();
                    if (e.Bad || n == 0 || n > 100) return false;
                    // all read first: a bad entry refuses the whole edit (one added halfway left the owner's queue
                    // changed with no record sent, the others out of step)
                    var add = new List<QueueItem>();
                    for (uint k = 0; k < n; k++)
                    {
                        var it = new QueueItem { VideoId = e.String(), Title = e.String(), Duration = e.Float(), AddedBy = from };
                        if (e.Bad || !Ytdlp.ValidVideoId(it.VideoId) || float.IsNaN(it.Duration)) return false;
                        it.Title = UnitState.Clip(it.Title, UnitState.TitleBytes);
                        it.Duration = Mathf.Max(0f, it.Duration);
                        add.Add(it);
                    }
                    bool wasEmpty = s.Queue.Count == 0 || s.Index >= s.Queue.Count || s.AtEnd(now);
                    int firstNew = s.Queue.Count;
                    foreach (var it in add) if (s.Queue.Count < UnitState.MaxQueue) s.Queue.Add(it);
                    if (firstNew == s.Queue.Count) return false;   // the queue is full
                    if (wasEmpty) { s.Index = firstNew; s.AnchorPos = 0f; s.AnchorTime = now; s.Playing = true; }
                    return true;
                }
                case Op.Remove:
                {
                    int i = (int)e.VarUInt();
                    if (e.Bad || i < 0 || i >= s.Queue.Count) return false;
                    s.Queue.RemoveAt(i);
                    if (i < s.Index) s.Index--;
                    else if (i == s.Index) { s.AnchorPos = 0f; s.AnchorTime = now; if (s.Index >= s.Queue.Count) s.Playing = false; }
                    return true;
                }
                case Op.Tab:
                {
                    byte t = e.Byte();
                    if (e.Bad || t > (byte)Mode.Nav || (Mode)t == s.Tab) return false;
                    s.Tab = (Mode)t;
                    s.CloseAdd();   // + ADD is MEDIA's: another tab closes it, for everyone
                    return true;
                }
                case Op.AddOpen:
                {
                    // the search bar opened (+ ADD, SEARCH AGAIN), holding `text`
                    string t = UnitState.Clip(e.String(), UnitState.TextBytes);
                    if (e.Bad || (s.Add == AddScreen.Bar && s.AddText == t && !s.Searching)) return false;
                    s.CloseAdd();
                    s.Add = AddScreen.Bar; s.AddText = t; s.Tab = Mode.Video;
                    return true;
                }
                case Op.AddClose:
                    if (s.Add == AddScreen.None) return false;
                    s.CloseAdd();
                    return true;
                case Op.AddText:
                {
                    string t = UnitState.Clip(e.String(), UnitState.TextBytes);
                    if (e.Bad || s.Add != AddScreen.Bar || t == s.AddText) return false;
                    s.AddText = t;
                    return true;
                }
                case Op.AddSearching:
                {
                    bool on = e.Bool();
                    if (e.Bad || s.Add != AddScreen.Bar || on == s.Searching) return false;
                    s.Searching = on;
                    return true;
                }
                case Op.AddResults:
                {
                    // a search's videos, in pieces that fit an edit (1 KB): the first piece (at 0) opens the results in
                    // place of the bar, the next ones add to them. A piece for a bar closed meanwhile is refused.
                    string words = UnitState.Clip(e.String(), UnitState.TextBytes);
                    uint at = e.VarUInt(), n = e.VarUInt();
                    if (e.Bad || n == 0 || at + n > UnitState.MaxResults) return false;
                    if (at == 0 ? s.Add != AddScreen.Bar : (s.Add != AddScreen.Results || at != s.Results.Count || words != s.Searched)) return false;
                    var got = new List<QueueItem>();
                    for (uint k = 0; k < n; k++)
                    {
                        var it = new QueueItem { VideoId = e.String(), Title = e.String(), Duration = e.Float() };
                        if (e.Bad || !Ytdlp.ValidVideoId(it.VideoId) || float.IsNaN(it.Duration)) return false;
                        it.Title = UnitState.Clip(it.Title, UnitState.TitleBytes);
                        it.Duration = Mathf.Max(0f, it.Duration);
                        got.Add(it);
                    }
                    if (at == 0) { s.Results.Clear(); s.Add = AddScreen.Results; s.Searched = words; s.Searching = false; s.ResultsTop = 0; }
                    s.Results.AddRange(got);
                    return true;
                }
                case Op.AddPage:
                {
                    byte top = e.Byte();
                    if (e.Bad || s.Add != AddScreen.Results || (top > 0 && top >= s.Results.Count) || top == s.ResultsTop) return false;
                    s.ResultsTop = top;
                    return true;
                }
                case Op.Zoom:
                {
                    byte z = e.Byte();
                    if (e.Bad || z > UnitState.MaxZoom || z == s.Zoom) return false;
                    s.Zoom = z;
                    return true;
                }
                case Op.Hints:
                {
                    bool on = e.Bool();
                    if (e.Bad || on == s.Hints) return false;
                    s.Hints = on;
                    return true;
                }
                case Op.Clear:
                    s.Queue.Clear(); s.Index = 0; s.Playing = false; s.AnchorPos = 0f; s.AnchorTime = now;
                    return true;
            }
            return false;
        }

        private static bool JumpTo(UnitState s, int i, double now)
        {
            if (i < 0 || i >= s.Queue.Count) return false;
            s.Index = i; s.AnchorPos = 0f; s.AnchorTime = now; s.Playing = true;
            return true;
        }
    }

    /// Holds a car's UnitState on this machine (the shared object's GameObject).
    public sealed class StateHolder : MonoBehaviour
    {
        public UnitState State = new UnitState();
        public bool Changed;   // the unit redraws when set
    }
}
