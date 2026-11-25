using System;
using System.Collections.Generic;
using TLDRevamp.Multiplayer;
using UnityEngine;

namespace HeadUnit
{
    /// On every head unit item (added to the model prefabs, so every spawned unit has it). Finds the car it sits in and
    /// that car's shared state (UnitState: the YouTube queue, what plays and where, its volume), and shows it.
    ///
    /// The screen is the car's, in its shared state: the tab (RADIO, MEDIA, NAV) and the map's zoom.
    /// One player changing them changes them for everyone in the car, as on one real screen (a playtest: the host went
    /// to NAV and the passenger's screen stayed on MEDIA). What a player is in the middle of stays their own: the queue
    /// list, FULL, BIG, a link being typed, a search's results (one player browsing the queue doesn't move the others').
    /// The radio's own state (on, station, volume, AM/FM) is the game's, which TLD Revamp shares. The video plays on
    /// whatever tab is open, alongside the game's radio (both have their own volume and switch). Work is done only near
    /// the local player: a unit 2 km away plays nothing here.
    public sealed partial class UnitBehaviour : MonoBehaviour
    {
        public static readonly List<UnitBehaviour> All = new List<UnitBehaviour>();
        public static readonly float ActiveRangeM = 40f;

        public Units.Model Model;
        public radioscript Radio;
        public carscript Car;                // null: not in a car's slot
        public SharedObject StateObj;
        public UnitState State => StateObj != null && StateObj.GameObject != null ? StateObj.GameObject.GetComponent<StateHolder>().State : null;

        public Touch Touch;
        public attachablescript Attachable;
        /// The radio body around the screen shown (Glue: hidden while stuck outside a slot).
        public bool BodyShown = true;
        internal readonly List<Renderer> HiddenRenderers = new List<Renderer>();
        internal readonly List<Collider> TriggerColliders = new List<Collider>();
        private Video _video;
        private MapView _map;

        /// What the screen shows: the car's (UnitState's tab and zoom) once there is a state, set here at once when
        /// this player changes it (the edit goes to the state's owner and comes back).
        public Mode Tab = Mode.Radio;
        private int _zoom = 2;
        /// The car's screen as last seen in its state: only a change there moves this screen (an edit of this player's
        /// still on its way doesn't bounce it back meanwhile).
        private Mode _seenTab; private int _seenZoom; private bool _screenSeen;
        /// This player's own view of the unit (not shared): the queue list, FULL.
        private bool _listOpen, _full;
        /// The tools dialog opened from the MEDIA tab's GET TOOLS (a video that can't play here) or from SEARCH on a
        /// machine without yt-dlp: NOT NOW closes it rather than going on to the search bar.
        private bool _toolsOnly;
        /// The queue list's first row when scrolled (UP / DOWN, the mouse wheel over the list); -1: around what plays.
        private int _listTop = -1;

        /// A question on this unit's screen (it replaces the tab's picture and buttons until answered): the + ADD
        /// screen (the search bar, a search's results) is the car's, followed like the tab (_add, from UnitState.Add);
        /// the offer to download the programs is this machine's own (_tools), since each player downloads their own.
        private enum Dialog { None, Link, Ytdlp, Results }
        private Dialog _dialog => _tools ? Dialog.Ytdlp : _add == AddScreen.Bar ? Dialog.Link : _add == AddScreen.Results ? Dialog.Results : Dialog.None;
        private bool _tools;
        private AddScreen _add, _seenAdd;
        /// The first search result shown (UP / DOWN, the wheel), the car's as the zoom is.
        private int _resultsTop, _seenTop;
        /// A search running on this machine (the car's state says "searching" to everyone).
        private bool _searching;
        /// The bar's text while this player types into it (sent to the car's state a few times a second, and at once
        /// on Enter, Escape and the buttons); everyone else sees the state's.
        private string _link = "";
        private bool _linkSent = true; private float _linkSentAt;
        public static readonly float TextSendS = 0.25f;
        /// The unit whose link is being typed (keys go to it, the game takes none: Controls.InputSelected).
        public static UnitBehaviour TypingInto;
        private float _savedVolume = 0.3f;
        private float _findAt, _glueAt;

        private void Awake()
        {
            var save = GetComponent<tosaveitemscript>();
            Model = save != null ? Units.ById(save.id) : null;
            Radio = GetComponentInChildren<radioscript>(true);   // radio01: on its child "Radiobj"
            Attachable = GetComponent<attachablescript>();
            var s = transform.Find(Look.ScreenName);
            if (s != null) { Touch = gameObject.AddComponent<Touch>(); Touch.Build(this, s); }
        }

        private void OnEnable() => All.Add(this);
        private void OnDisable() { All.Remove(this); _video?.Stop(); if (TypingInto == this) TypingInto = null; }
        private void OnDestroy() { _video?.Dispose(); _map?.Dispose(); if (TypingInto == this) TypingInto = null; }

        /// Has the car's power (the game's radio checks the same).
        private bool Powered => Radio == null || Radio.electronics == null || Radio.electronics.output > mainscript.lightTurnOffValue;

        private void Update()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return;
            // stuck or not (a stick replayed from another machine, a loaded save): the body hidden or shown
            if (Time.realtimeSinceStartup >= _glueAt) { _glueAt = Time.realtimeSinceStartup + 0.5f; bool stuck = Glue.Stuck(this); if (stuck == BodyShown) Glue.Body(this, !stuck); }
            bool near = (pl.transform.position - transform.position).sqrMagnitude < ActiveRangeM * ActiveRangeM;
            if (!near) { _video?.Hold(); return; }

            if (Time.realtimeSinceStartup >= _findAt) { _findAt = Time.realtimeSinceStartup + 1f; Bind(); }
            var s = State;
            FollowScreen(s);
            SendLink(false);
            bool radioOn = Radio == null || Radio.ON;
            bool powered = Powered;

            // the video plays whatever this player looks at (a car without power plays nothing)
            if (s != null && Car != null && powered)
            {
                if (_video == null) _video = new Video(gameObject);
                _video.Follow(s, MpApi.SessionTime, Plugin.VideoVolume.Value, Ended);
            }
            else _video?.Hold();

            if (_full && (Tab != Mode.Video || _video == null || !_video.Ready)) _full = false;
            if (Touch != null) Present(s, powered, radioOn);
        }

        // ---- the screen (Screens.cs draws it) ----

        private readonly ScreenModel _m = new ScreenModel();
        private readonly Dictionary<string, Action> _act = new Dictionary<string, Action>();
        /// The model's name on the screen for a moment when the car gets power.
        private bool _wasPowered; private float _wakeUntil;
        /// The station list's first row when scrolled with the wheel; -1: around the station that plays.
        private int _stationsTop = -1;
        /// CLEAR pressed once: until then a second press clears the car's queue (it is everyone's).
        private float _clearArmedUntil;
        public static readonly float WakeS = 1.2f, ArmedS = 3f, PressedS = 0.15f;

        public static readonly int ListRows = 4, ResultRows = 4, StationRows = 5;

        /// This frame's screen: what it shows (ScreenModel), the action behind each button, and the picture or the
        /// map. YouTube presses are edits to the car's shared state (everyone in the car sees and hears them); the
        /// radio's turn the game's own knobs (the game shares those).
        private void Present(UnitState s, bool powered, bool radioOn)
        {
            var m = _m; m.Reset(); _act.Clear();
            var ph = Model != null ? Model.Screen : Units.Models[0].Screen;
            m.R = (byte)Mathf.RoundToInt(ph.r * 255f); m.G = (byte)Mathf.RoundToInt(ph.g * 255f); m.B = (byte)Mathf.RoundToInt(ph.b * 255f);
            m.Brand = Model != null ? Model.Name : "Head Unit";
            float now = Time.realtimeSinceStartup;
            if (powered && !_wasPowered) _wakeUntil = now + WakeS;
            _wasPowered = powered;
            Texture content = null; float aspect = -1f; bool tint = false;
            if (!powered) m.Screen = ScreenModel.View.Off;
            else if (now < _wakeUntil) m.Screen = ScreenModel.View.Wake;
            else if (_full)
            {
                // TV: the picture over the whole screen; a touch anywhere comes back
                m.Screen = ScreenModel.View.Tv;
                content = _video.Texture; aspect = _video.Aspect; tint = true;
                _act["unfull"] = () => _full = false;
            }
            else
            {
                m.Tab = (int)Tab;
                m.Banner = Panel.Flashing;
                _act["radio"] = () => SetTab(Mode.Radio);
                _act["video"] = () => SetTab(Mode.Video);
                _act["nav"] = () => SetTab(Mode.Nav);
                if (_dialog == Dialog.Ytdlp || (Ytdlp.Downloading && Tab == Mode.Video && _dialog == Dialog.None)) ToolsScreen(m);
                else if (_dialog == Dialog.Link || _dialog == Dialog.Results) SearchScreen(m, s);
                else if (Tab == Mode.Radio) RadioScreen(m, radioOn);
                else if (Tab == Mode.Video && _listOpen && s != null) QueueScreen(m, s);
                else if (Tab == Mode.Video) MediaScreen(m, s, ref content, ref aspect, ref tint);
                else NavScreen(m, ref content);
            }
            if (Touch.Looked == Touch && Touch.LookedButton != null) m.Aimed = Touch.LookedButton.Id;
            if (now - Touch.PressedAt < PressedS) m.Pressed = Touch.PressedId;
            Touch.Present(m, content, aspect, tint && !Plugin.ColourPicture.Value, _act);
        }

        private void RadioScreen(ScreenModel m, bool radioOn)
        {
            m.Screen = ScreenModel.View.Radio;
            _act["pwr"] = Power;
            _act["amfm"] = () => { Radio?.BChangeAMFM?.Turn(); _stationsTop = -1; };
            _act["seek-"] = () => { Seek(-1); _stationsTop = -1; };
            _act["seek+"] = () => { Seek(1); _stationsTop = -1; };
            _act["vol-"] = () => Knob(Radio?.Bvolume, -0.1f);
            _act["vol+"] = () => Knob(Radio?.Bvolume, 0.1f);
            if (Radio == null) return;
            bool am = Radio.amfm;
            var on = Tuned(Radio);
            m.RadioOn = radioOn; m.Am = am;
            m.Freq = Stations.Freq(am, Radio.FR);
            m.Unit = am ? "KHZ" : "MHZ";
            m.Station = on != null ? Stations.Name(am, on, full: false) : "NO STATION";
            m.Volume = Mathf.RoundToInt((Radio.Bvolume != null ? Radio.Bvolume.tunedLerp : 0f) * 10f);

            // every station of the band, five at a time around the one that plays: tap one to tune to it
            var band = Stations.Band(am);
            int maxTop = Mathf.Max(0, band.Count - StationRows);
            int top = _stationsTop >= 0 ? Mathf.Min(_stationsTop, maxTop) : Mathf.Clamp(band.IndexOf(on) - 1, 0, maxTop);
            if (Touch.Looked == Touch && Touch.Wheel != 0 && Touch.LookedButton != null && Touch.LookedButton.Id.StartsWith("st"))
                top = _stationsTop = Mathf.Clamp(top - Touch.Wheel, 0, maxTop);
            for (int i = top; i < band.Count && i < top + StationRows; i++)
            {
                var ch = band[i];
                string id = "st" + i;
                m.Stations.Add(new ScreenModel.Row { Id = id, Lead = Stations.Freq(am, ch.FR), Text = Stations.Name(am, ch, full: false), On = ch == on });
                _act[id] = () => TuneTo(ch.FR);
            }
            m.StationsTop = top; m.StationsCount = band.Count;
        }

        private void MediaScreen(ScreenModel m, UnitState s, ref Texture content, ref float aspect, ref bool tint)
        {
            m.Screen = ScreenModel.View.Media;
            var cur = s?.Current;
            // without the programs, ADD is where they are offered: named for it while the queue can't play here
            bool tools = NeedsTools(s);
            m.QueueEmpty = cur == null;
            m.NeedsTools = tools;
            m.Title = cur?.Title ?? "";
            m.Playing = s != null && s.Playing;
            m.Track = s != null && s.Queue.Count > 0 ? (s.Index + 1).ToString("00") + "/" + s.Queue.Count.ToString("00") : "--/--";
            m.Time = cur != null ? Clock(Mathf.Max(0f, s.Position(MpApi.SessionTime))) : "--:--";
            m.VideoVolume = Mathf.RoundToInt(Plugin.VideoVolume.Value * 10f);
            _act["vol-"] = () => VideoVolume(-0.1f);
            _act["vol+"] = () => VideoVolume(0.1f);
            _act["link"] = () =>
            {
                _toolsOnly = tools;
                if (tools || !Ytdlp.Present || Ytdlp.OfferFfmpeg) _tools = true; else OpenBar("");
            };
            _act["prev"] = () => Edit(w => w.Byte((byte)Op.Prev));
            _act["play"] = () => { var st = State; if (st != null) Edit(w => w.Byte((byte)(st.Playing ? Op.Pause : Op.Play))); };
            _act["next"] = () => Edit(w => w.Byte((byte)Op.Next));
            _act["list"] = () => { _listOpen = true; _listTop = -1; };

            // the picture's window: the picture, or what it waits for
            bool pic = _video != null && _video.Ready;
            if (cur == null) m.Pane = ScreenModel.Window.Empty;
            else if (_video == null || _video.Problem == "loading...")
            {
                float f = Ytdlp.VideoFrac(cur.VideoId), c = Ytdlp.ConvertFrac(cur.VideoId);
                m.Pane = ScreenModel.Window.Loading;
                m.Progress = f >= 0f ? f : c;
                m.ProgressLabel = f < 0f && c >= 0f ? "CONVERTING" : "LOADING";
            }
            else if (tools) m.Pane = ScreenModel.Window.Tools;
            // the video plays nowhere while yt-dlp fails: UPDATE where the picture would be
            else if (YtdlpFailed) { m.Pane = ScreenModel.Window.Update; _act["update"] = Ytdlp.Update; }
            else if (pic) { m.Pane = ScreenModel.Window.Picture; content = _video.Texture; aspect = _video.Aspect; tint = true; }
            else if (_video.Problem != null) { m.Pane = ScreenModel.Window.Problem; m.Problem = _video.Problem; }
            else m.Pane = ScreenModel.Window.Loading;
            m.CanTv = pic && m.Pane == ScreenModel.Window.Picture;
            if (m.CanTv) _act["full"] = () => { if (_video != null && _video.Ready) _full = true; };
        }

        private void QueueScreen(ScreenModel m, UnitState s)
        {
            m.Screen = ScreenModel.View.Queue;
            // the queue: tap one to play it
            if (Touch.Looked == Touch && Touch.Wheel != 0) ScrollList(-Touch.Wheel, s);
            int maxTop = Mathf.Max(0, s.Queue.Count - ListRows);
            int first = _listTop >= 0 ? Mathf.Min(_listTop, maxTop) : Mathf.Clamp(s.Index - 1, 0, maxTop);
            for (int i = first; i < s.Queue.Count && i < first + ListRows; i++)
            {
                int at = i;
                var q = s.Queue[i];
                string id = "q" + (i - first);
                m.Rows.Add(new ScreenModel.Row { Id = id, Lead = (i + 1).ToString("00"), Text = q.Title, Tail = q.Duration > 0f ? Clock(q.Duration) : "", On = i == s.Index });
                _act[id] = () => Edit(w => { w.Byte((byte)Op.Jump); w.VarUInt((uint)at); });
            }
            m.RowsTop = first; m.RowsCount = s.Queue.Count; m.QueueCount = s.Queue.Count;
            m.Big = Panel.Big;
            m.ClearArmed = Time.realtimeSinceStartup < _clearArmedUntil;
            _act["up"] = () => ScrollList(-ListRows, State);
            _act["down"] = () => ScrollList(ListRows, State);
            _act["back"] = () => { _listOpen = false; _listTop = -1; };
            _act["big"] = () => Panel.Big = !Panel.Big;
            _act["clear"] = () =>
            {
                if (Time.realtimeSinceStartup >= _clearArmedUntil) { _clearArmedUntil = Time.realtimeSinceStartup + ArmedS; return; }
                _clearArmedUntil = 0f;
                if (Edit(w => w.Byte((byte)Op.Clear))) { _listOpen = false; Panel.Flash("queue cleared"); } else Panel.Flash("couldn't clear the queue");
            };
        }

        /// The + ADD screen: the bar (touch it to type) and, once searched, the results under it. The car's: everyone
        /// sees what one player types and the results they page.
        private void SearchScreen(ScreenModel m, UnitState s)
        {
            m.Screen = ScreenModel.View.Search;
            bool results = _dialog == Dialog.Results;
            bool typing = TypingInto == this;
            string text = results ? s?.Searched ?? "" : BarText(s);
            m.Bar = text; m.Typing = typing; m.Cursor = typing && Time.realtimeSinceStartup % 1f < 0.5f;
            m.Searching = _searching || (s != null && s.Searching);
            m.IsLink = IsLink(text) || text.Trim().Length == 0;
            m.ResultsShown = results;
            // a link or search here failed in yt-dlp (an old one, mostly): updating it is offered here too
            m.Update = LookupFailed;
            if (m.Update) _act["update"] = Ytdlp.Update;
            _act["done"] = CloseDialog;
            Action edit = () => { string w = State?.Searched ?? ""; OpenBar(w); _link = w; _linkSent = true; TypingInto = this; };
            _act["type"] = () => { if (_dialog == Dialog.Results) edit(); else if (TypingInto == this) StopTyping(); else StartTyping(); };
            _act["add"] = () => { if (_dialog == Dialog.Results) edit(); else AddLink(); };
            _act["paste"] = () => { string p = (GUIUtility.systemCopyBuffer ?? "").Trim(); if (_dialog == Dialog.Results) OpenBar(p); else SetLink(p); };
            _act["del"] = () => { string t = BarText(State); if (t.Length > 0) SetLink(t.Substring(0, t.Length - 1)); };
            if (!results || s == null) return;

            // the car's results, the page the car's screen is on; marked: in the car's queue already
            var list = s.Results;
            if (Touch.Looked == Touch && Touch.Wheel != 0) SetResultsTop(_resultsTop - Touch.Wheel, list.Count);
            int first = Mathf.Clamp(_resultsTop, 0, Mathf.Max(0, list.Count - ResultRows));
            for (int i = first; i < list.Count && i < first + ResultRows; i++)
            {
                var q = list[i];
                var e = new Ytdlp.Entry { Id = q.VideoId, Title = q.Title, Duration = q.Duration };
                bool added = s.Queue.Exists(it => it.VideoId == q.VideoId);
                string id = "r" + (i - first);
                m.Rows.Add(new ScreenModel.Row { Id = id, Lead = added ? null : "+", Text = q.Title, Tail = q.Duration > 0f ? Clock(q.Duration) : "", Mark = added, On = added });
                _act[id] = () =>
                {
                    if (Controls.Send(this, new List<Ytdlp.Entry> { e }) == 1) Panel.Flash("added: " + e.Title);
                    else Panel.Flash("couldn't add: is the unit in a car's radio slot?");
                };
            }
            m.RowsTop = first; m.RowsCount = list.Count;
            _act["up"] = () => SetResultsTop(first - ResultRows, State?.Results.Count ?? 0);
            _act["down"] = () => SetResultsTop(first + ResultRows, State?.Results.Count ?? 0);
        }

        /// The offer to download the programs (what comes from where and how big, nothing more: a player agrees to it
        /// here), and their download.
        private void ToolsScreen(ScreenModel m)
        {
            m.Screen = ScreenModel.View.Tools;
            if (Ytdlp.Downloading) { m.ToolsDownloading = true; m.ToolsStatus = Ytdlp.Status; m.Progress = Ytdlp.DownloadFrac; return; }
            if (Ytdlp.Present) { m.ToolsWhat = Plugin.Convert.Value ? "GET FFMPEG" : $"GET FFMPEG FOR {Plugin.MaxHeight.Value}P"; m.ToolsWhere = "GITHUB  80 MB"; }
            else { m.ToolsWhat = "GET YT-DLP, DENO, FFMPEG"; m.ToolsWhere = "GITHUB  140 MB"; }
            _act["download"] = () => { _tools = false; Ytdlp.Download(); };
            // without yt-dlp nothing plays; without ffmpeg only 360p: then on to the search bar
            _act["notnow"] = () =>
            {
                if (Ytdlp.Present) Ytdlp.FfmpegDeclined = true;
                _tools = false;
                if (Ytdlp.Present && !_toolsOnly) OpenBar("");
            };
        }

        /// The map: roads around the car, the road ahead brightest, heading up (redrawn every frame). Only the map: the
        /// bend and slope text over it was spam (playtest 2026-11-24).
        private void NavScreen(ScreenModel m, ref Texture content)
        {
            m.Screen = ScreenModel.View.Nav;
            if (_map == null) _map = new MapView(Model != null ? Model.Screen : Units.Models[0].Screen);
            _map.SetZoom(_zoom);
            if (Car != null) _map.Draw(Car.transform, IsMyCar() ? Nav.Path : null);
            content = _map.Texture;
            m.Scale = MapView.ZoomText(_zoom).ToUpperInvariant();
            m.ZoomInMax = _zoom <= 0; m.ZoomOutMax = _zoom >= UnitState.MaxZoom;
            if (Car != null)
            {
                // where north (the world's +z) points on a map that turns with the car, in 10° steps
                var f = Car.transform.forward;
                int heading = Mathf.RoundToInt(Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg / 10f) * 10;
                m.North = ((-heading % 360) + 360) % 360;
            }
            _act["zoom-"] = () => SetZoom(_zoom + 1);
            _act["zoom+"] = () => SetZoom(_zoom - 1);
        }

        /// The queue list moved by `rows` (from where it shows now).
        private void ScrollList(int rows, UnitState s)
        {
            if (s == null) return;
            int maxTop = Mathf.Max(0, s.Queue.Count - ListRows);
            int now = _listTop >= 0 ? Mathf.Min(_listTop, maxTop) : Mathf.Clamp(s.Index - 1, 0, maxTop);
            _listTop = Mathf.Clamp(now + rows, 0, maxTop);
        }

        /// Another player changed the car's screen, or this car's state was just found: this screen follows. Each part
        /// only when it changed there, so a press of this player's that is still on its way isn't undone meanwhile.
        private void FollowScreen(UnitState s)
        {
            // out of the car: the next car's screen is taken as it is (and no car, no + ADD screen)
            if (s == null) { _screenSeen = false; _add = AddScreen.None; return; }
            if ((!_screenSeen || s.Tab != _seenTab) && Tab != s.Tab) { Tab = s.Tab; _listOpen = false; _full = false; _listTop = -1; }
            if (!_screenSeen || s.Zoom != _seenZoom) _zoom = s.Zoom;
            if (!_screenSeen || s.Add != _seenAdd) { _add = s.Add; if (_add != AddScreen.None) { _listOpen = false; _full = false; } }
            if (!_screenSeen || s.ResultsTop != _seenTop) _resultsTop = s.ResultsTop;
            _screenSeen = true; _seenTab = s.Tab; _seenZoom = s.Zoom; _seenAdd = s.Add; _seenTop = s.ResultsTop;
        }

        /// The + ADD screen's search bar opened holding `text`, for everyone in the car.
        private void OpenBar(string text)
        {
            _tools = false; _listOpen = false; _full = false;
            if (TypingInto == this) TypingInto = null;
            _link = text; _linkSent = true;
            _add = AddScreen.Bar;
            Edit(w => { w.Byte((byte)Op.AddOpen); w.String(text); });
        }

        /// What the bar holds: what this player is typing, else the car's.
        private string BarText(UnitState s) => TypingInto == this ? _link : s?.AddText ?? "";

        /// The bar's text set (PASTE, DEL): here, and for everyone at once.
        private void SetLink(string t)
        {
            t = UnitState.Clip(t, UnitState.TextBytes);
            _link = t; _linkSent = false;
            SendLink(true);
        }

        /// This player's typing sent to the car's bar: at most every TextSendS while typing, at once when `now`.
        private void SendLink(bool now)
        {
            if (_linkSent || (!now && Time.realtimeSinceStartup - _linkSentAt < TextSendS)) return;
            _linkSent = true; _linkSentAt = Time.realtimeSinceStartup;
            var s = State;
            if (_add != AddScreen.Bar || (s != null && s.AddText == _link)) return;
            string t = _link;
            Edit(w => { w.Byte((byte)Op.AddText); w.String(t); });
        }

        /// The bar touched: this player's keys go into it, starting from what it holds now.
        private void StartTyping() { _link = State?.AddText ?? ""; _linkSent = true; TypingInto = this; }

        /// Typing stopped (the bar touched again, Escape, Enter): the text as it is now sent to everyone.
        private void StopTyping() { if (TypingInto == this) TypingInto = null; SendLink(true); }

        /// The page of results: here at once, and for everyone in the car.
        private void SetResultsTop(int top, int count)
        {
            top = Mathf.Clamp(top, 0, Mathf.Max(0, count - ResultRows));
            if (top == _resultsTop) return;
            _resultsTop = top;
            var s = State;
            if (s == null || s.ResultsTop != top) { byte b = (byte)top; Edit(w => { w.Byte((byte)Op.AddPage); w.Byte(b); }); }
        }

        /// This player switches the tab: here at once, and for everyone in the car through the shared state. Sent unless
        /// the car's screen is already there (not this one's: a press always brings the car's screen to it).
        private void SetTab(Mode m)
        {
            _listOpen = false; _full = false; CloseDialog();
            Tab = m;
            var s = State;
            if (s == null || s.Tab != m) Edit(w => { w.Byte((byte)Op.Tab); w.Byte((byte)m); });
        }

        /// The map's zoom (0 nearest), for everyone in the car.
        private void SetZoom(int z)
        {
            _zoom = Mathf.Clamp(z, 0, UnitState.MaxZoom);
            var s = State;
            if (s == null || s.Zoom != _zoom) { byte b = (byte)_zoom; Edit(w => { w.Byte((byte)Op.Zoom); w.Byte(b); }); }
        }

        /// The car's queue has a video and this machine lacks what plays it (each player downloads their own: one who
        /// never pressed + ADD, a passenger, had none and saw only "yt-dlp is not installed").
        private static bool NeedsTools(UnitState s)
        {
            if (s == null || s.Current == null || Ytdlp.Downloading) return false;
            return !Ytdlp.Present || (Plugin.Convert.Value && !Ytdlp.HasFfmpeg);
        }

        /// The last link or search looked up failed in yt-dlp (Ytdlp.ListFailed).
        private static bool LookupFailed => Ytdlp.ListFailed && Ytdlp.Present && !Ytdlp.Downloading;

        /// yt-dlp is there but couldn't fetch the video playing (an old yt-dlp, mostly: YouTube changes often).
        private bool YtdlpFailed => !Ytdlp.Downloading && Ytdlp.Present && _video != null && _video.Problem != null && _video.Problem.StartsWith("yt-dlp:");

        /// This player's video volume, by `d`: each player hears the car's video as loud as they set it (the config's
        /// Video.Volume, kept on this machine), so nobody turns it up or down for the others. MEDIA's meter shows it.
        private void VideoVolume(float d)
        {
            float v = Mathf.Round(Mathf.Clamp01(Plugin.VideoVolume.Value + d) * 10f) / 10f;
            Plugin.VideoVolume.Value = v;
        }

        /// The dialog closed: the download offer here, the + ADD screen for everyone in the car.
        private void CloseDialog()
        {
            _tools = false;
            if (TypingInto == this) TypingInto = null;
            var s = State;
            bool open = _add != AddScreen.None || (s != null && s.Add != AddScreen.None);
            _add = AddScreen.None;
            if (open) Edit(w => w.Byte((byte)Op.AddClose));
        }

        private static bool IsLink(string t) => Spotify.CleanLink(t) != null || Ytdlp.CleanLink(t) != null;
        private static string Clock(float s) => s >= 3600f ? $"{(int)(s / 3600)}:{(int)(s % 3600 / 60):00}:{(int)(s % 60):00}" : $"{(int)(s / 60)}:{(int)(s % 60):00}";

        /// The bar's text (or `text`): a link is added as before; anything else is searched on YouTube (Ytdlp.Search)
        /// by this machine, and the videos go into the car's results for everyone to pick from.
        private void AddLink() => AddLink(null);
        private void AddLink(string text)
        {
            string t = (text ?? BarText(State)).Trim();
            if (t.Length == 0) return;
            StopTyping();   // the text as it is now, for everyone
            // the bar is the car's, so a player without yt-dlp can press this: the download offered here (the bar
            // stays open for the others)
            if (!Ytdlp.Present) { _toolsOnly = true; _tools = true; return; }
            if (IsLink(t)) { CloseDialog(); Controls.AddLink(this, t); return; }
            if (_searching) return;
            _searching = true;
            Edit(w => { w.Byte((byte)Op.AddSearching); w.Bool(true); });
            Ytdlp.Search(t, list =>
            {
                _searching = false;
                if (_add != AddScreen.Bar) return;   // closed meanwhile (here, or by another player)
                list = list?.FindAll(e => Ytdlp.ValidVideoId(e.Id));
                if (list == null || list.Count == 0)
                {
                    Panel.Flash(list == null ? Ytdlp.Status : "nothing found for that");
                    Edit(w => { w.Byte((byte)Op.AddSearching); w.Bool(false); });
                    return;
                }
                SendResults(t, list);
            });
        }

        /// A search's videos into the car's results, in pieces that fit an edit's 1 KB (as Controls.Send does for the
        /// queue): the first piece shows them in place of the bar, for everyone.
        private void SendResults(string words, List<Ytdlp.Entry> list)
        {
            const int EditBytes = 1000;
            words = UnitState.Clip(words, UnitState.TextBytes);
            int head = 8 + System.Text.Encoding.UTF8.GetByteCount(words);
            int max = Mathf.Min(list.Count, UnitState.MaxResults), at = 0, i = 0;
            while (i < max)
            {
                var b = new List<Ytdlp.Entry>();
                int bytes = head;
                for (; i < max; i++)
                {
                    var e = list[i];
                    string title = UnitState.Clip(e.Title != null && e.Title.Length > 60 ? e.Title.Substring(0, 60) : e.Title, UnitState.TitleBytes);
                    int size = 2 + e.Id.Length + 2 + System.Text.Encoding.UTF8.GetByteCount(title) + 4;
                    if (b.Count > 0 && bytes + size > EditBytes) break;
                    b.Add(new Ytdlp.Entry { Id = e.Id, Title = title, Duration = e.Duration }); bytes += size;
                }
                uint start = (uint)at;
                if (!Edit(w =>
                {
                    w.Byte((byte)Op.AddResults); w.String(words); w.VarUInt(start); w.VarUInt((uint)b.Count);
                    foreach (var e in b) { w.String(e.Id); w.String(e.Title); w.Float(e.Duration); }
                })) return;
                at += b.Count;
            }
        }

        /// Tests (Probe.Search): the bar opened with `text` and ADD/SEARCH pressed; the car's results so far.
        public void TestSubmit(string text) { OpenBar(text); AddLink(text); }
        public string TestResults()
        {
            var s = State;
            if (_dialog != Dialog.Results || s == null) return _searching || (s != null && s.Searching) ? "searching" : "dialog " + _dialog + " " + Panel.Flashing;
            return string.Join(" | ", s.Results.ConvertAll(e => e.VideoId + " " + e.Title + " " + Clock(e.Duration)));
        }

        /// Keys for the unit whose link is being typed (once a frame, Controls).
        public static void TypeTick()
        {
            var u = TypingInto;
            if (u == null) return;
            if (u._dialog != Dialog.Link || !u.isActiveAndEnabled) { TypingInto = null; return; }   // closed, by anyone
            if (Input.GetKeyDown(KeyCode.Escape)) { u.StopTyping(); return; }
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (ctrl && Input.GetKeyDown(KeyCode.V)) { u._link = UnitState.Clip(u._link + (GUIUtility.systemCopyBuffer ?? "").Trim(), UnitState.TextBytes); u._linkSent = false; return; }
            foreach (char c in Input.inputString)
            {
                if (c == '\b') { if (u._link.Length > 0) { u._link = u._link.Substring(0, u._link.Length - 1); u._linkSent = false; } }
                else if (c == '\n' || c == '\r') { u.AddLink(); return; }
                else if (!char.IsControl(c) && System.Text.Encoding.UTF8.GetByteCount(u._link) + 4 <= UnitState.TextBytes) { u._link += c; u._linkSent = false; }
            }
        }

        /// The station the radio is on, by the game's own list (mainscript.radioChs / radioChsAM, each with its name
        /// and frequency), when it is close enough to be heard clearly; null between stations.
        public static string StationName(radioscript r)
        {
            var ch = Tuned(r);
            return ch != null ? Stations.Name(r.amfm, ch, full: true) : null;
        }

        /// The station of the game's list the radio is close enough to to be heard clearly; null between stations.
        public static mainscript.radioCH Tuned(radioscript r)
        {
            var chs = Channels(r);
            if (chs == null) return null;
            float best = float.MaxValue; mainscript.radioCH on = null;
            foreach (var ch in chs) { if (ch == null) continue; float d = Mathf.Abs(ch.FR - r.FR); if (d < best) { best = d; on = ch; } }
            float clear = (r.amfm ? mainscript.s.FRTAM : mainscript.s.FRTFM) * 0.5f;
            return best <= Mathf.Max(clear, (r.amfm ? r.AMStep : r.FMStep) * 0.5f) ? on : null;
        }

        private static List<mainscript.radioCH> Channels(radioscript r) =>
            r == null || mainscript.s == null ? null : r.amfm ? mainscript.s.radioChsAM : mainscript.s.radioChs;

        /// Seek: the next station up (dir 1) or down (-1) the band, round at the ends, as a car radio's << >>.
        private bool Seek(int dir)
        {
            var chs = Channels(Radio);
            if (chs == null || chs.Count == 0 || Radio.Btune == null) return false;
            float step = (Radio.amfm ? Radio.AMStep : Radio.FMStep) * 0.5f, cur = Radio.FR;
            float? pick = null;
            foreach (var ch in chs)
                if (dir > 0 ? ch.FR > cur + step && (pick == null || ch.FR < pick) : ch.FR < cur - step && (pick == null || ch.FR > pick)) pick = ch.FR;
            if (pick == null)   // round the band
                foreach (var ch in chs) if (pick == null || (dir > 0 ? ch.FR < pick : ch.FR > pick)) pick = ch.FR;
            TuneTo(pick.Value);
            return true;
        }

        /// The tuning knob turned so the radio lands on `fr` (the game's scroll-wheel call, so it syncs).
        private void TuneTo(float fr)
        {
            var u = Radio.Btune;
            float min = Radio.amfm ? Radio.AMmin : Radio.FMmin, max = Radio.amfm ? Radio.AMmax : Radio.FMmax;
            float target = Mathf.Lerp(u.tunedFloatMin, u.tunedFloatMax, Mathf.InverseLerp(min, max, fr));
            float speed = u.tuneSpeed == 0f ? 1f : u.tuneSpeed;
            u.Tuned((target - u.tempTunedFloat) / speed, syncinmulti: true);
        }

        /// Turn one of the radio's knobs by a share of its travel, as the mouse wheel does (the game syncs it).
        private static void Knob(usablescript u, float share)
        {
            if (u == null) return;
            float range = u.tunedFloatMax - u.tunedFloatMin, speed = u.tuneSpeed == 0f ? 1f : u.tuneSpeed;
            u.Tuned(range * share / speed, syncinmulti: true);
        }

        /// The radio is on while its volume is above zero: off remembers the volume, on brings it back.
        private void Power()
        {
            var v = Radio?.Bvolume;
            if (v == null) return;
            if (Radio.ON) { _savedVolume = Mathf.Max(0.1f, v.tunedLerp); Knob(v, -1f); }
            else Knob(v, _savedVolume - v.tunedLerp);
        }

        /// The car whose radio slot holds this unit, and that car's state object (if anyone made one).
        private void Bind()
        {
            Car = GetComponentInParent<carscript>();
            if (Car == null) { StateObj = null; return; }
            uint key = CarKey(Car);
            if (StateObj != null && !StateObj.Removed && State != null && State.Car == key) { _lastKey = key; return; }
            StateObj = FindState(key);
            // The car's key changed under a state this machine owns: a session ended (the host keeps every object,
            // but the car is known by its local number again, so the queue vanished from the host's unit), or one
            // started. The owner moves it to the new key.
            if (StateObj == null && _lastKey != 0 && _lastKey != key)
            {
                var old = FindState(_lastKey);
                if (old != null && old.IsMine) { old.GameObject.GetComponent<StateHolder>().State.Car = key; old.RecordChanged(); StateObj = old; }
            }
            if (StateObj != null) _lastKey = key;
            if (StateObj != null || (key & 0x80000000u) != 0) return;
            // made alone (a local key) before this car got its session id: the owner moves it to the id
            var local = FindState(0x80000000u | (uint)(Car.GetInstanceID() & 0x7FFFFFFF));
            if (local != null && local.IsMine) { local.GameObject.GetComponent<StateHolder>().State.Car = key; local.RecordChanged(); StateObj = local; _lastKey = key; }
        }
        private uint _lastKey;

        public bool IsMyCar()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            return pl != null && Car != null && pl.seat != null && pl.seat.GetComponentInParent<carscript>() == Car;
        }

        /// The car as every machine knows it: its session id; alone, a local number (never sent).
        public static uint CarKey(carscript car)
        {
            if (car == null) return 0;
            uint id = MpApi.InSession ? MpApi.WorldObjectId(car.gameObject) : 0;
            return id != 0 ? id : 0x80000000u | (uint)(car.GetInstanceID() & 0x7FFFFFFF);
        }

        /// The car's state: the one with the lowest id when two players made one at the same moment (the other's owner
        /// removes its own).
        public static SharedObject FindState(uint carKey)
        {
            SharedObject best = null;
            foreach (var so in MpApi.Objects)
            {
                if (so.Removed || so.Type != Plugin.StateType || so.GameObject == null) continue;
                var st = so.GameObject.GetComponent<StateHolder>();
                if (st == null || st.State.Car != carKey) continue;
                if (best == null || Better(so, best)) best = so;
            }
            foreach (var so in new List<SharedObject>(MpApi.Objects))
            {
                if (so == best || so.Removed || so.Type != Plugin.StateType || so.GameObject == null || !so.IsMine || so.Id == 0 || best == null || best.Id == 0) continue;
                var st = so.GameObject.GetComponent<StateHolder>();
                if (st != null && st.State.Car == carKey) so.Remove();
            }
            return best;
        }

        private static bool Better(SharedObject a, SharedObject b)
        {
            if (a.Id != 0 && b.Id == 0) return true;
            if (a.Id == 0) return false;
            return a.Id < b.Id;
        }

        /// Send an edit to the car's state, making the state first if nobody has yet.
        public bool Edit(Action<RecordWriter> write)
        {
            // the car's state looked for first: Bind runs once a second, and a press in between (a player just got in,
            // the car's state not found yet) made a second state, which lost to the first and took the press with it
            if (StateObj == null || StateObj.Removed) Bind();
            if (Car == null) return false;
            if (StateObj == null || StateObj.Removed)
            {
                var go = new GameObject("HeadUnitState");
                DontDestroyOnLoad(go);
                var h = go.AddComponent<StateHolder>();
                // the screen as this player has it (a first video added from MEDIA put everyone's screen on RADIO)
                h.State.Tab = Tab; h.State.Zoom = (byte)Mathf.Clamp(_zoom, 0, UnitState.MaxZoom);
                // the car shared first, so the state carries its session id where it has one at once (with the local
                // key read before, a state made in a session started out under a number only this machine knows)
                if (MpApi.InSession && MpApi.WorldObjectId(Car.gameObject) == 0) MpApi.ShareWorldObject(Car.gameObject);
                h.State.Car = CarKey(Car);
                StateObj = MpApi.Share(Plugin.StateType, go);
            }
            return StateObj.RequestEdit(write);
        }

        private void Ended(int index, float length) => Edit(w => { w.Byte((byte)Op.Ended); w.VarUInt((uint)index); w.Float(length); });

        public string VideoDescribe => _video != null ? _video.Describe() : "no video";

        /// The videos that play or come next on the units near this player (the cache never deletes these). A kept
        /// player's video (Video's pool) is not among them: its file goes too, and coming back to it downloads it again.
        public static HashSet<string> VideosInUse()
        {
            var ids = new HashSet<string>();
            foreach (var u in All)
            {
                if (u == null) continue;
                if (u._video != null && u._video.VideoId != null) ids.Add(u._video.VideoId);
                if (u._video != null && u._video.WantId != null) ids.Add(u._video.WantId);   // already switching to it
                var s = u.State;
                if (s == null) continue;
                for (int i = Mathf.Max(0, s.Index); i < s.Queue.Count && i <= s.Index + 2; i++) ids.Add(s.Queue[i].VideoId);
            }
            return ids;
        }

        public bool VideoReady => _video != null && _video.Ready;
        public string VideoProblem => _video?.Problem;
        public AudioSource VideoAudio => _video?.Audio;
        public MapView Map => _map;
        public bool VideoPlaying => _video != null && _video.Player != null && _video.Player.isPlaying;
        public Texture VideoTexture => _video != null && _video.Ready ? _video.Texture : null;
    }
}
