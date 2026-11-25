using System;
using System.Collections.Generic;
using System.Text;

namespace HeadUnit
{
    /// What the screen shows, as plain data: UnitBehaviour fills it in from the game each frame, Screens.Draw turns it
    /// into the CRT canvas and the touch areas. Plain C#, so the unit tests draw every screen from made-up ones.
    public sealed class ScreenModel
    {
        public enum View { Off, Wake, Radio, Media, Queue, Search, Tools, Nav, Tv }
        public enum Window { Picture, Empty, Loading, Tools, Update, Problem }

        public sealed class Row { public string Id, Lead, Text, Tail; public bool On, Mark; }

        public View Screen;
        public byte R = 138, G = 216, B = 255;   // the phosphor
        public string Brand = "";
        public int Tab;                          // the rail: 0 radio, 1 media, 2 nav
        public string Aimed, Pressed;            // button ids: under the crosshair, just pressed
        public string Banner;                    // a message along the top, a few seconds

        // RADIO
        public bool RadioOn, Am;
        public string Freq = "", Unit = "MHZ", Station = "";
        public int Volume;                       // 0 to 10
        public readonly List<Row> Stations = new List<Row>();
        public int StationsTop, StationsCount;

        // MEDIA
        public string Title = "", Track = "--/--", Time = "--:--";
        public bool Playing, QueueEmpty, NeedsTools, CanTv;
        public int VideoVolume;
        public Window Pane;
        public string Problem = "";
        public float Progress = -1f;
        public string ProgressLabel = "LOADING";

        // the queue, and a search's results
        public readonly List<Row> Rows = new List<Row>();
        public int RowsTop, RowsCount, QueueCount;
        public bool Big, ClearArmed;

        // SEARCH
        public string Bar = "";
        public bool Typing, Cursor, Searching, IsLink, Update, ResultsShown;

        // the tools offer
        public string ToolsWhat = "", ToolsWhere = "", ToolsStatus = "";
        public bool ToolsDownloading;

        // NAV
        public string Scale = "";
        public int North;                        // degrees, where north points on the screen (0: up)
        public bool ZoomInMax, ZoomOutMax;

        public void Reset()
        {
            Screen = View.Off; Brand = ""; Tab = 0; Aimed = Pressed = Banner = null;
            RadioOn = Am = false; Freq = Station = ""; Unit = "MHZ"; Volume = 0; Stations.Clear(); StationsTop = StationsCount = 0;
            Title = ""; Track = "--/--"; Time = "--:--"; Playing = QueueEmpty = NeedsTools = CanTv = false; VideoVolume = 0;
            Pane = Window.Picture; Problem = ""; Progress = -1f; ProgressLabel = "LOADING";
            Rows.Clear(); RowsTop = RowsCount = QueueCount = 0; Big = ClearArmed = false;
            Bar = ""; Typing = Cursor = Searching = IsLink = Update = ResultsShown = false;
            ToolsWhat = ToolsWhere = ToolsStatus = ""; ToolsDownloading = false;
            Scale = ""; North = 0; ZoomInMax = ZoomOutMax = false;
        }

        /// Everything drawn, as one string: the canvas is drawn again only when it changes.
        public string Key()
        {
            var sb = new StringBuilder(256);
            sb.Append((int)Screen).Append('|').Append(R).Append(',').Append(G).Append(',').Append(B).Append('|').Append(Brand).Append('|').Append(Tab)
              .Append('|').Append(Aimed).Append('|').Append(Pressed).Append('|').Append(Banner);
            switch (Screen)
            {
                case View.Radio:
                    sb.Append('|').Append(RadioOn).Append(Am).Append(Freq).Append(Unit).Append(Station).Append(Volume).Append('/').Append(StationsTop).Append('/').Append(StationsCount);
                    Rows_(sb, Stations); break;
                case View.Media:
                    sb.Append('|').Append(Title).Append('|').Append(Track).Append(Time).Append(Playing).Append(QueueEmpty).Append(NeedsTools).Append(CanTv).Append(VideoVolume)
                      .Append((int)Pane).Append(Problem).Append(Progress.ToString("F2")).Append(ProgressLabel); break;
                case View.Queue:
                    sb.Append('|').Append(RowsTop).Append('/').Append(RowsCount).Append('/').Append(QueueCount).Append(Big).Append(ClearArmed); Rows_(sb, Rows); break;
                case View.Search:
                    sb.Append('|').Append(Bar).Append('|').Append(Typing).Append(Cursor).Append(Searching).Append(IsLink).Append(Update).Append(ResultsShown)
                      .Append(RowsTop).Append('/').Append(RowsCount); Rows_(sb, Rows); break;
                case View.Tools:
                    sb.Append('|').Append(ToolsWhat).Append('|').Append(ToolsWhere).Append('|').Append(ToolsStatus).Append(ToolsDownloading).Append(Progress.ToString("F2")); break;
                case View.Nav:
                    sb.Append('|').Append(Scale).Append(North).Append(ZoomInMax).Append(ZoomOutMax); break;
            }
            return sb.ToString();
        }

        private static void Rows_(StringBuilder sb, List<Row> rows)
        {
            foreach (var r in rows) sb.Append('|').Append(r.Id).Append(r.Lead).Append(r.Text).Append(r.Tail).Append(r.On).Append(r.Mark);
        }
    }

    /// The screens' layout and drawing, on the design's 800 × 440 grid (4 px a millimetre): the tabs down the
    /// driver's side (96 px), the rest for the tab. Everything that can be touched has a box, and a touch area of the
    /// same rectangle; what is on or chosen is drawn inverse, what the crosshair is on dithered.
    public static class Screens
    {
        public struct Area
        {
            public string Id; public int X, Y, W, H;
            public Area(string id, int x, int y, int w, int h) { Id = id; X = x; Y = y; W = w; H = h; }
        }

        /// What one drawing gives the touch screen: the buttons, and the rectangle the picture or the map shows in
        /// (none: W 0).
        public sealed class Layout
        {
            public readonly List<Area> Buttons = new List<Area>();
            public Area Content;
            public void Clear() { Buttons.Clear(); Content = default; }
        }

        private enum Style { Idle, Aimed, On, Faint }

        public static void Draw(ScreenModel m, Crt c, Layout l)
        {
            c.Clear(); l.Clear();
            switch (m.Screen)
            {
                case ScreenModel.View.Off: return;
                case ScreenModel.View.Wake: Wake(m, c); return;
                case ScreenModel.View.Tv: l.Content = new Area(null, 0, 0, Crt.W, Crt.H); l.Buttons.Add(new Area("unfull", 0, 0, Crt.W, Crt.H)); return;
            }
            Rail(m, c, l);
            switch (m.Screen)
            {
                case ScreenModel.View.Radio: Radio(m, c, l); break;
                case ScreenModel.View.Media: Media(m, c, l); break;
                case ScreenModel.View.Queue: Queue(m, c, l); break;
                case ScreenModel.View.Search: Search(m, c, l); break;
                case ScreenModel.View.Tools: Tools(m, c, l); break;
                case ScreenModel.View.Nav: Nav(m, c, l); break;
            }
            if (!string.IsNullOrEmpty(m.Banner))
            {
                // along the top, over the title; on SEARCH low down, off the bar being typed in
                int by = m.Screen == ScreenModel.View.Search ? 300 : 6, bw = m.Screen == ScreenModel.View.Nav ? 590 : 676;
                c.Fill(110, by, bw, 44, Crt.Full);
                c.Text(124, by + (44 - 21) / 2, m.Banner, 3, Crt.Ink, bw - 28);
            }
        }

        // ---- pieces ----

        private static Style Of(ScreenModel m, string id, bool on, bool faint = false)
        {
            if (faint) return Style.Faint;
            if (id != null && id == m.Pressed) return Style.On;
            if (on) return Style.On;
            return id != null && id == m.Aimed ? Style.Aimed : Style.Idle;
        }

        /// A box in its look; the brightness for what is drawn in it.
        private static byte Box(Crt c, int x, int y, int w, int h, Style look)
        {
            switch (look)
            {
                case Style.On: c.Fill(x, y, w, h, Crt.Full); return Crt.Ink;
                case Style.Aimed: c.Dither(x + 2, y + 2, w - 4, h - 4, Crt.DitherLevel); c.Frame(x, y, w, h, Crt.Full); return Crt.Full;
                case Style.Faint: c.Frame(x, y, w, h, Crt.Faint); return Crt.Faint;
                default: c.Frame(x, y, w, h, Crt.Edge); return Crt.Full;
            }
        }

        /// A touchable box: drawn, and its touch area (none while faint). Returns the brightness for its inside.
        private static byte Button(ScreenModel m, Crt c, Layout l, string id, int x, int y, int w, int h, bool on = false, bool faint = false)
        {
            if (!faint) l.Buttons.Add(new Area(id, x, y, w, h));
            return Box(c, x, y, w, h, Of(m, id, on, faint));
        }

        private static void TextButton(ScreenModel m, Crt c, Layout l, string id, int x, int y, int w, int h, string label, int scale, bool on = false, bool faint = false)
        {
            byte v = Button(m, c, l, id, x, y, w, h, on, faint);
            c.Label(x, y, w, h, label, scale, v);
        }

        private static void IconButton(ScreenModel m, Crt c, Layout l, string id, int x, int y, int w, int h, Crt.Glyph g, int scale, bool on = false, bool faint = false)
        {
            byte v = Button(m, c, l, id, x, y, w, h, on, faint);
            c.IconCentred(g, x, y, w, h, scale, v);
        }

        /// Up to five keys along the bottom of the tab: (id, label or null, icon).
        private static void Keys(ScreenModel m, Crt c, Layout l, IList<(string id, string label, Crt.Glyph icon, bool on)> keys)
        {
            const int x0 = 112, y = 360, w = 672, h = 68, gap = 10;
            int n = keys.Count; if (n == 0) return;
            for (int i = 0; i < n; i++)
            {
                int a = x0 + (w + gap) * i / n, b = x0 + (w + gap) * (i + 1) / n - gap;
                var k = keys[i];
                if (k.label != null) TextButton(m, c, l, k.id, a, y, b - a, h, k.label, 3, k.on);
                else IconButton(m, c, l, k.id, a, y, b - a, h, k.icon, 3, k.on);
            }
        }

        /// A list's scroll bar: a track with its thumb, `top` of `count` with `shown` visible.
        private static void Track(Crt c, int x, int y, int h, int top, int shown, int count)
        {
            c.Frame(x, y, 16, h, Crt.Faint);
            if (count <= shown || count <= 0) { c.Fill(x + 4, y + 4, 8, h - 8, Crt.Full); return; }
            int inner = h - 8, th = Math.Max(16, inner * shown / count), ty = (inner - th) * top / Math.Max(1, count - shown);
            c.Fill(x + 4, y + 4 + ty, 8, th, Crt.Full);
        }

        /// Ten blocks for a level 0 to 10.
        private static void Meter(Crt c, int x, int y, int bw, int bh, int gap, int level)
        {
            for (int i = 0; i < 10; i++)
            {
                int bx = x + i * (bw + gap);
                if (i < level) c.Fill(bx, y, bw, bh, Crt.Full); else c.Frame(bx, y, bw, bh, Crt.Faint);
            }
        }

        /// A bar of blocks for 0 to 1.
        private static void Blocks(Crt c, int cx, int y, int n, float f)
        {
            int bw = 16, gap = 4, w = n * (bw + gap) - gap, x = cx - w / 2, lit = (int)Math.Round(Math.Max(0f, Math.Min(1f, f)) * n);
            for (int i = 0; i < n; i++) { if (i < lit) c.Fill(x + i * (bw + gap), y, bw, 30, Crt.Full); else c.Frame(x + i * (bw + gap), y, bw, 30, Crt.Faint); }
        }

        /// A list row: lead (dim), text, tail (right-aligned); inverse when on.
        private static void Row(ScreenModel m, Crt c, Layout l, ScreenModel.Row r, int x, int y, int w, int h, Crt.Glyph? mark = null)
        {
            byte v = Button(m, c, l, r.Id, x, y, w, h, r.On);
            bool inv = v == Crt.Ink;
            byte dim = inv ? Crt.Ink : r.Id == m.Aimed ? Crt.Full : Crt.Dim;
            int ty = y + (h - 21) / 2, tx = x + 16;
            if (mark != null) { c.Icon(mark.Value, tx, y + (h - 16) / 2, 2, v); tx += 36; }
            else if (!string.IsNullOrEmpty(r.Lead)) { tx += c.Text(tx, ty, r.Lead, 3, dim) + 18; }
            int tailW = string.IsNullOrEmpty(r.Tail) ? 0 : Crt.Measure(r.Tail, 3) + 18;
            c.Text(tx, ty, r.Text, 3, v, x + w - 16 - tailW - tx);
            if (tailW > 0) c.TextRight(x + w - 16, ty, r.Tail, 3, v);
        }

        // ---- the views ----

        private static void Wake(ScreenModel m, Crt c)
        {
            c.TextCentred(Crt.W / 2, 160, m.Brand, 6, Crt.Full, 760);
            int w = Crt.Measure("READY", 4);
            int x = Crt.W / 2 - (w + 28) / 2;
            c.Text(x, 250, "READY", 4, Crt.Dim);
            c.Fill(x + w + 8, 250, 20, 28, Crt.Full);
        }

        private static void Rail(ScreenModel m, Crt c, Layout l)
        {
            bool dim = m.Screen == ScreenModel.View.Tools;
            string[] ids = { "radio", "video", "nav" };
            string[] names = { "RADIO", "MEDIA", "NAV" };
            Crt.Glyph[] icons = { Crt.Glyph.Radio, Crt.Glyph.Tv, Crt.Glyph.Nav };
            int[] ys = { 10, 153, 297 }, hs = { 133, 134, 133 };
            for (int i = 0; i < 3; i++)
            {
                byte v = Button(m, c, l, ids[i], 10, ys[i], 76, hs[i], on: m.Tab == i, faint: dim && m.Tab != i);
                int top = ys[i] + (hs[i] - 36 - 12 - 14) / 2;
                c.Icon(icons[i], 10 + (76 - 36) / 2, top, 3, v);
                c.TextCentred(48, top + 36 + 12, names[i], 2, v);
            }
            c.Fill(96, 0, 2, Crt.H, Crt.Faint);
        }

        /// Where the tuner's side ends: the station list starts 10 px further right.
        public const int FreqRight = 418;

        private static void Radio(ScreenModel m, Crt c, Layout l)
        {
            // the band: one box, the band that is on inverse within it
            l.Buttons.Add(new Area("amfm", 112, 16, 168, 60));
            Box(c, 112, 16, 168, 60, Of(m, "amfm", false));
            c.Fill(m.Am ? 196 : 112, 16, 84, 60, Crt.Full);
            c.Label(112, 16, 84, 60, "FM", 3, m.Am ? Crt.Full : Crt.Ink);
            c.Label(196, 16, 84, 60, "AM", 3, m.Am ? Crt.Ink : Crt.Full);
            TextButton(m, c, l, "pwr", 336, 16, 76, 60, "PWR", 3, on: m.RadioOn);

            // the frequency, as big as the band's widest (108.0, 1620) allows beside its unit before the station list:
            // the same size all along the band (a fixed size ran into the list at 1080 and lost the 5 of 100.5)
            byte lit = m.RadioOn ? Crt.Full : Crt.Faint;
            int room = FreqRight - 112 - 10 - Crt.Measure(m.Unit, 3), scale = 12;
            while (scale > 4 && Crt.Measure(m.Am ? "8888" : "888.8", scale) > room) scale--;
            int top = 180 - 7 * scale;
            int fw = c.Text(112, top, m.Freq, scale, lit, room);
            c.Text(112 + fw + 10, top + 7 * scale - 21, m.Unit, 3, m.RadioOn ? Crt.Dim : Crt.Faint);
            if (m.RadioOn) c.Text(112, 196, m.Station, 3, Crt.Full, 300);

            IconButton(m, c, l, "seek-", 112, 264, 145, 72, Crt.Glyph.SeekDown, 3);
            IconButton(m, c, l, "seek+", 267, 264, 145, 72, Crt.Glyph.SeekUp, 3);
            TextButton(m, c, l, "vol-", 112, 350, 64, 64, "-", 4);
            Meter(c, 188, 369, 11, 26, 3, m.Volume);
            TextButton(m, c, l, "vol+", 348, 350, 64, 64, "+", 4);

            for (int i = 0; i < m.Stations.Count && i < 5; i++)
            {
                var r = m.Stations[i];
                if (m.RadioOn) Row(m, c, l, r, 428, 16 + i * 80, 330, 72);
                else c.Frame(428, 16 + i * 80, 330, 72, Crt.Faint);
            }
            Track(c, 768, 16, 392, m.StationsTop, 5, m.StationsCount);
        }

        private static void Media(ScreenModel m, Crt c, Layout l)
        {
            if (!m.QueueEmpty) c.Text(112, 20, m.Title, 3, Crt.Full, 672);

            // the picture's window: the picture shows in it, or what it waits for
            c.Frame(110, 56, 516, 292, Crt.Edge);
            int wx = 112, wy = 58, ww = 512, wh = 288, cx = wx + ww / 2;
            switch (m.Pane)
            {
                case ScreenModel.Window.Picture: l.Content = new Area(null, wx, wy, ww, wh); break;
                case ScreenModel.Window.Empty:
                    c.TextCentred(cx, 118, "QUEUE EMPTY", 3, Crt.Dim);
                    TextButton(m, c, l, "link", cx - 130, 172, 260, 96, m.NeedsTools ? "GET TOOLS" : "ADD", 5);
                    break;
                case ScreenModel.Window.Loading:
                    c.TextCentred(cx, 150, m.ProgressLabel + (m.Progress >= 0f ? " " + (int)Math.Round(m.Progress * 100f) + "%" : ""), 4, Crt.Full);
                    Blocks(c, cx, 200, 20, m.Progress < 0f ? 0f : m.Progress);
                    break;
                case ScreenModel.Window.Tools:
                    c.TextCentred(cx, 120, "! TOOLS NEEDED", 4, Crt.Full);
                    TextButton(m, c, l, "link", cx - 150, 180, 300, 84, "GET TOOLS", 4, on: true);
                    break;
                case ScreenModel.Window.Update:
                    c.TextCentred(cx, 120, "! CAN'T PLAY", 4, Crt.Full);
                    TextButton(m, c, l, "update", cx - 170, 180, 340, 84, "UPDATE YT-DLP", 4, on: true);
                    break;
                case ScreenModel.Window.Problem:
                    Wrap(c, wx + 24, wy + 40, ww - 48, m.Problem, 3, Crt.Dim, 6);
                    break;
            }

            // what plays, the volume, TV
            c.Text(642, 58, "TRACK", 2, Crt.Dim);
            c.Text(642, 76, m.Track, 4, Crt.Full, 142);
            c.Text(642, 118, "TIME", 2, Crt.Dim);
            c.Text(642, 136, m.Time, 4, Crt.Full, 142);
            c.Text(642, 178, "VOL", 2, Crt.Dim);
            Meter(c, 684, 178, 8, 14, 2, m.VideoVolume);
            TextButton(m, c, l, "vol-", 642, 200, 66, 60, "-", 4);
            TextButton(m, c, l, "vol+", 718, 200, 66, 60, "+", 4);
            TextButton(m, c, l, "full", 642, 272, 142, 76, "TV", 4, faint: !m.CanTv);

            if (m.QueueEmpty) return;
            var keys = new List<(string, string, Crt.Glyph, bool)>
            {
                ("prev", null, Crt.Glyph.Prev, false),
                ("play", null, m.Playing ? Crt.Glyph.Pause : Crt.Glyph.Play, false),
                ("next", null, Crt.Glyph.Next, false),
                ("list", "QUEUE", default, false),
            };
            if (m.Pane != ScreenModel.Window.Tools) keys.Add(("link", "ADD", default, false));
            Keys(m, c, l, keys);
        }

        private static void Queue(ScreenModel m, Crt c, Layout l)
        {
            TextButton(m, c, l, "back", 112, 16, 120, 60, "< BACK", 3);
            int tw = c.Text(252, 32, "QUEUE", 4, Crt.Full);
            c.Text(252 + tw + 20, 32, m.QueueCount.ToString(), 4, Crt.Dim);
            if (m.ClearArmed) TextButton(m, c, l, "clear", 540, 16, 244, 60, "CLEAR " + m.QueueCount + "?", 4, on: true);
            else
            {
                TextButton(m, c, l, "big", 558, 16, 96, 60, "BIG", 3, on: m.Big);
                TextButton(m, c, l, "clear", 664, 16, 120, 60, "CLEAR", 3);
            }
            for (int i = 0; i < m.Rows.Count && i < 4; i++) Row(m, c, l, m.Rows[i], 112, 88 + i * 80, 606, 72);
            bool atTop = m.RowsTop <= 0, atEnd = m.RowsTop + 4 >= m.RowsCount;
            IconButton(m, c, l, "up", 728, 88, 56, 64, Crt.Glyph.Up, 3, faint: atTop);
            Track(c, 748, 162, 164, m.RowsTop, 4, m.RowsCount);
            IconButton(m, c, l, "down", 728, 336, 56, 64, Crt.Glyph.Down, 3, faint: atEnd);
        }

        private static void Search(ScreenModel m, Crt c, Layout l)
        {
            // the bar: touch it to type
            byte v = Button(m, c, l, "type", 112, 16, 576, 60, false);
            if (m.Typing && m.Aimed != "type") c.Frame(112, 16, 576, 60, Crt.Full);
            int px = 126, py = 16 + (60 - 21) / 2;
            px += c.Text(px, py, "SEARCH>", 3, Crt.Dim) + 14;
            int room = 688 - 14 - 20 - px;
            string bar = Crt.Plain(m.Bar);
            while (bar.Length > 0 && Crt.Measure(bar, 3) > room) bar = bar.Substring(1);   // the end of a long link
            int bw = c.Text(px, py, bar, 3, v);
            if (m.Cursor) c.Fill(px + bw + (bar.Length > 0 ? 4 : 0), py, 15, 21, Crt.Full);
            TextButton(m, c, l, "del", 698, 16, 86, 60, "DEL", 3, faint: m.ResultsShown);

            if (m.ResultsShown)
            {
                for (int i = 0; i < m.Rows.Count && i < 4; i++)
                    Row(m, c, l, m.Rows[i], 112, 88 + i * 66, 606, 60, m.Rows[i].Mark ? Crt.Glyph.Check : (Crt.Glyph?)null);
                bool atTop = m.RowsTop <= 0, atEnd = m.RowsTop + 4 >= m.RowsCount;
                IconButton(m, c, l, "up", 728, 88, 56, 60, Crt.Glyph.Up, 3, faint: atTop);
                Track(c, 748, 156, 122, m.RowsTop, 4, m.RowsCount);
                IconButton(m, c, l, "down", 728, 286, 56, 60, Crt.Glyph.Down, 3, faint: atEnd);
            }
            else if (m.Searching) c.TextCentred(448, 200, "SEARCHING...", 4, Crt.Dim);

            var keys = new List<(string, string, Crt.Glyph, bool)> { ("paste", "PASTE", default, false) };
            keys.Add(("add", m.ResultsShown ? "EDIT" : m.IsLink ? "ADD" : "SEARCH", default, false));
            if (m.Update) keys.Add(("update", "UPDATE", default, true));
            keys.Add(("done", "DONE", default, false));
            Keys(m, c, l, keys);
        }

        private static void Tools(ScreenModel m, Crt c, Layout l)
        {
            // a question: two borders
            c.Frame(136, 70, 624, 300, Crt.Full);
            c.Frame(142, 76, 612, 288, Crt.Edge);
            if (m.ToolsDownloading)
            {
                c.Text(170, 104, "GETTING TOOLS", 4, Crt.Full, 560);
                Wrap(c, 170, 150, 560, m.ToolsStatus, 3, Crt.Dim, 3);
                Blocks(c, 448, 300, 20, m.Progress < 0f ? 0f : m.Progress);
                return;
            }
            c.Text(170, 104, m.ToolsWhat, 4, Crt.Full, 560);
            c.Text(170, 150, m.ToolsWhere, 3, Crt.Dim, 560);
            TextButton(m, c, l, "download", 170, 276, 270, 72, "DOWNLOAD", 3, on: true);
            TextButton(m, c, l, "notnow", 456, 276, 270, 72, "NOT NOW", 3);
        }

        private static void Nav(ScreenModel m, Crt c, Layout l)
        {
            l.Content = new Area(null, 98, 0, 602, Crt.H);
            c.Fill(700, 0, 2, Crt.H, Crt.Faint);
            // north: a needle from the box's middle
            c.Frame(712, 16, 72, 72, Crt.Faint);
            double a = m.North * Math.PI / 180.0;
            float nx = 748, ny = 52, dx = (float)Math.Sin(a), dy = (float)-Math.Cos(a);
            c.Line(nx - dx * 22, ny - dy * 22, nx, ny, Crt.Dim, 2);
            c.Line(nx, ny, nx + dx * 26, ny + dy * 26, Crt.Full, 4);
            c.Text(716, 20, "N", 2, Crt.Dim);
            TextButton(m, c, l, "zoom+", 712, 150, 72, 72, "+", 5, faint: m.ZoomInMax);
            TextButton(m, c, l, "zoom-", 712, 232, 72, 72, "-", 5, faint: m.ZoomOutMax);
            // how far ahead the map reaches
            c.Icon(Crt.Glyph.Up, 748 - 11, 372, 2, Crt.Dim);
            c.TextCentred(748, 396, m.Scale, 3, Crt.Full, 92);
        }

        /// `s` in lines up to `w` px wide, at most `lines` of them.
        private static void Wrap(Crt c, int x, int y, int w, string s, int scale, byte v, int lines)
        {
            var words = Crt.Plain(s).Split(' ');
            string line = "";
            int n = 0;
            foreach (var word in words)
            {
                string next = line.Length == 0 ? word : line + " " + word;
                if (Crt.Measure(next, scale) <= w || line.Length == 0) { line = next; continue; }
                c.Text(x, y + n * 10 * scale, line, scale, v, w); n++; line = word;
                if (n >= lines) return;
            }
            if (line.Length > 0 && n < lines) c.Text(x, y + n * 10 * scale, line, scale, v, w);
        }
    }
}
