using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using HeadUnit;

// The Head Unit's CRT screens (Crt.cs, Screens.cs), drawn from made-up states the way the game draws them: every
// touch area inside the screen and none overlapping another, the ids the in-game test presses there, the font's
// fallbacks. The pictures go to HU_SCREENS (default bin/screens next to this project) to look at.
static class HeadUnitScreenTest
{
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception("FAIL: " + why); }

    static readonly string Out = Environment.GetEnvironmentVariable("HU_SCREENS") ?? Path.Combine(AppContext.BaseDirectory, "screens");

    static int Main()
    {
        Directory.CreateDirectory(Out);
        Font();
        foreach (var (name, m) in Samples()) Shot(name, m);
        Console.WriteLine($"Head Unit screens: {checks} checks passed, pictures in {Out}");
        return 0;
    }

    static void Font()
    {
        Check(Crt.Plain("Hungária Rádió") == "HUNGARIA RADIO", "accents spelled plain: " + Crt.Plain("Hungária Rádió"));
        Check(Crt.Plain("Кино — Группа крови") == "KINO - GRUPPA KROVI", "Cyrillic spelled in Latin: " + Crt.Plain("Кино — Группа крови"));
        Check(Crt.Plain("İstanbul’da ‘şarkı’") == "ISTANBUL'DA 'SARKI'", "Turkish letters and quotes: " + Crt.Plain("İstanbul’da ‘şarkı’"));
        Check(Crt.Missing("abc|\"&#!?[]{}<>çğıöşü—–’“”") == "", "the characters today's test checks all draw: " + Crt.Missing("abc|\"&#!?[]{}<>çğıöşü—–’“”"));
        Check(Crt.Missing("夜") == "夜", "what can't be spelled is reported");
        Check(Crt.Measure("93.0", 12) < Crt.Measure("9300", 12), "the point is narrower than a digit");
    }

    static ScreenModel Base(ScreenModel.View v, int tab, (byte, byte, byte) ph = default, string brand = "VOLTLINE DH-1")
    {
        var m = new ScreenModel(); m.Reset(); m.Screen = v; m.Tab = tab; m.Brand = brand;
        if (ph != default) { m.R = ph.Item1; m.G = ph.Item2; m.B = ph.Item3; }
        return m;
    }

    static readonly (byte, byte, byte) Blue = (138, 216, 255), Green = (92, 255, 138), Amber = (255, 180, 60);

    static IEnumerable<(string, ScreenModel)> Samples()
    {
        var radio = Base(ScreenModel.View.Radio, 0, Blue); radio.RadioOn = true; radio.Freq = "93.0"; radio.Station = "Hungaria"; radio.Volume = 6;
        string[][] st = { new[] { "91.2", "Funk" }, new[] { "93.0", "Hungaria" }, new[] { "94.1", "Steel Pants" }, new[] { "95.0", "Chat 'til You Die" }, new[] { "96.6", "Angry Greg" } };
        for (int i = 0; i < st.Length; i++) radio.Stations.Add(new ScreenModel.Row { Id = "st" + (i + 3), Lead = st[i][0], Text = st[i][1], On = i == 1 });
        radio.StationsTop = 3; radio.StationsCount = 11; radio.Aimed = "st5";
        yield return ("radio", radio);

        var off = Base(ScreenModel.View.Radio, 0); off.Freq = "93.0"; off.Volume = 0;
        foreach (var r in radio.Stations) off.Stations.Add(r);
        off.StationsTop = 3; off.StationsCount = 11;
        yield return ("radio_off", off);

        var am = Base(ScreenModel.View.Radio, 0, Amber); am.RadioOn = true; am.Am = true; am.Freq = "1080"; am.Unit = "KHZ"; am.Station = "Custom Radio"; am.Volume = 3;
        string[][] ams = { new[] { "630", "Rock" }, new[] { "800", "Oldie Radio" }, new[] { "1080", "Custom Radio" }, new[] { "1330", "King Matthias" } };
        for (int i = 0; i < ams.Length; i++) am.Stations.Add(new ScreenModel.Row { Id = "st" + i, Lead = ams[i][0], Text = ams[i][1], On = i == 2 });
        am.StationsCount = 4;
        yield return ("radio_am_amber", am);

        // the band's widest frequencies: 100.5 lost its 5, and 1080 ran its KHZ into the list (playtest 2026-11-25)
        var wide = Base(ScreenModel.View.Radio, 0, Blue); wide.RadioOn = true; wide.Freq = "100.5"; wide.Station = "Poem Chill"; wide.Volume = 4;
        string[][] ws = { new[] { "96.6", "Angry Greg" }, new[] { "97.5", "Übermensch Radio" }, new[] { "98.4", "Neon Synth" }, new[] { "100.5", "Poem Chill" }, new[] { "105.0", "CB chatter" } };
        for (int i = 0; i < ws.Length; i++) wide.Stations.Add(new ScreenModel.Row { Id = "st" + (i + 7), Lead = ws[i][0], Text = ws[i][1], On = i == 3 });
        wide.StationsTop = 7; wide.StationsCount = 12;
        yield return ("radio_fm_100", wide);

        var media = Base(ScreenModel.View.Media, 1); media.Title = "Desert Highway — Night Drive Mix"; media.Playing = true; media.Track = "04/12"; media.Time = "12:31";
        media.VideoVolume = 7; media.CanTv = true; media.Pane = ScreenModel.Window.Picture; media.Aimed = "list";
        yield return ("media", media);

        var empty = Base(ScreenModel.View.Media, 1, Green); empty.QueueEmpty = true; empty.Pane = ScreenModel.Window.Empty; empty.VideoVolume = 5;
        yield return ("media_empty_green", empty);

        var load = Base(ScreenModel.View.Media, 1); load.Title = "Desert Highway — Night Drive Mix"; load.Track = "04/12"; load.Time = "0:00"; load.VideoVolume = 7;
        load.Pane = ScreenModel.Window.Loading; load.Progress = 0.43f;
        yield return ("media_loading", load);

        var upd = Base(ScreenModel.View.Media, 1); upd.Title = "Desert Highway — Night Drive Mix"; upd.Track = "04/12"; upd.VideoVolume = 7; upd.Pane = ScreenModel.Window.Update;
        yield return ("media_update", upd);

        var tools = Base(ScreenModel.View.Media, 1); tools.Title = "Desert Highway — Night Drive Mix"; tools.Track = "04/12"; tools.NeedsTools = true; tools.Pane = ScreenModel.Window.Tools;
        yield return ("media_tools", tools);

        var queue = Base(ScreenModel.View.Queue, 1); queue.QueueCount = 12; queue.RowsTop = 2; queue.RowsCount = 12; queue.Aimed = "q2";
        string[][] q = { new[] { "03", "Radio Static Blues", "3:47" }, new[] { "04", "Desert Highway — Night Drive Mix", "58:40" }, new[] { "05", "Old Engine, New Tune", "4:21" }, new[] { "06", "Sunset over the Steppe — Lo-fi", "1:12:05" } };
        for (int i = 0; i < q.Length; i++) queue.Rows.Add(new ScreenModel.Row { Id = "q" + i, Lead = q[i][0], Text = q[i][1], Tail = q[i][2], On = i == 1 });
        yield return ("queue", queue);

        var armed = Base(ScreenModel.View.Queue, 1); armed.QueueCount = 12; armed.RowsCount = 12; armed.ClearArmed = true;
        foreach (var r in queue.Rows) armed.Rows.Add(r);
        yield return ("queue_clear", armed);

        var typing = Base(ScreenModel.View.Search, 1); typing.Bar = "desert road trip mu"; typing.Typing = true; typing.Cursor = true;
        yield return ("search_typing", typing);

        var results = Base(ScreenModel.View.Search, 1); results.Bar = "desert road trip music"; results.ResultsShown = true; results.RowsCount = 20; results.Aimed = "r0";
        string[][] rs = { new[] { "Desert Road Trip Music — 2 Hour Mix", "2:01:33" }, new[] { "Desert Highway — Night Drive Mix", "58:40" }, new[] { "Road Trip Classics (Acoustic Covers)", "47:12" }, new[] { "Driving Through the Desert at Dusk", "1:30:00" } };
        for (int i = 0; i < rs.Length; i++) results.Rows.Add(new ScreenModel.Row { Id = "r" + i, Lead = i == 1 ? null : "+", Text = rs[i][0], Tail = rs[i][1], Mark = i == 1, On = i == 1 });
        yield return ("search_results", results);

        var link = Base(ScreenModel.View.Search, 1); link.Bar = "https://www.youtube.com/watch?v=jNQXAC9IVRw&list=PL1234567890"; link.IsLink = true; link.Update = true; link.Banner = "yt-dlp: that video isn't available";
        yield return ("search_link_update", link);

        var offer = Base(ScreenModel.View.Tools, 1); offer.ToolsWhat = "GET YT-DLP, DENO, FFMPEG"; offer.ToolsWhere = "GITHUB  140 MB";
        yield return ("tools_offer", offer);

        var dl = Base(ScreenModel.View.Tools, 1); dl.ToolsDownloading = true; dl.ToolsStatus = "downloading ffmpeg (54 of 80 MB)"; dl.Progress = 0.64f;
        yield return ("tools_downloading", dl);

        var nav = Base(ScreenModel.View.Nav, 2, Amber, "KESTREL RX PRO"); nav.Scale = "250 M"; nav.North = 325;
        yield return ("nav_amber", nav);

        yield return ("wake_green", Base(ScreenModel.View.Wake, 0, Green, "VOLTLINE DH-2 WIDE"));

        var banner = Base(ScreenModel.View.Media, 1, Amber); banner.Title = "Old Engine, New Tune"; banner.Track = "05/24"; banner.Time = "0:12"; banner.CanTv = true;
        banner.Banner = "Spotify: added 12 of 20 songs"; banner.VideoVolume = 4;
        yield return ("media_banner_amber", banner);

        yield return ("tv", Base(ScreenModel.View.Tv, 1));
        yield return ("off", Base(ScreenModel.View.Off, 0));
    }

    static readonly Dictionary<ScreenModel.View, string[]> Wanted = new Dictionary<ScreenModel.View, string[]>
    {
        // the ids tools/headunit.py presses (TLD-RevaMP), kept from the old screen
        [ScreenModel.View.Radio] = new[] { "radio", "video", "nav", "pwr", "vol-", "vol+", "seek-", "seek+", "amfm" },
        [ScreenModel.View.Queue] = new[] { "back", "clear" },
        [ScreenModel.View.Tools] = new[] { "download", "notnow" },
        [ScreenModel.View.Tv] = new[] { "unfull" },
    };

    static void Shot(string name, ScreenModel m)
    {
        var crt = new Crt(); var l = new Screens.Layout();
        Screens.Draw(m, crt, l);
        if (m.Screen == ScreenModel.View.Radio)
        {
            // the frequency and its unit end before the station list: nothing lit in the gap between them
            bool clear = true;
            for (int y = 80; y < 200; y++) for (int x = Screens.FreqRight + 1; x < 428; x++) if (crt.I[y * Crt.W + x] != 0) clear = false;
            Check(clear, $"{name}: the frequency {m.Freq} {m.Unit} stays out of the station list");
            Check(crt.Texts.Contains(Crt.Plain(m.Freq)), $"{name}: the whole frequency {m.Freq} drawn");
        }
        var ids = new HashSet<string>();
        foreach (var b in l.Buttons)
        {
            Check(b.X >= 0 && b.Y >= 0 && b.X + b.W <= Crt.W && b.Y + b.H <= Crt.H, $"{name}: {b.Id} inside the screen");
            Check(ids.Add(b.Id), $"{name}: one {b.Id}");
            Check(b.W >= 56 && b.H >= 56, $"{name}: {b.Id} big enough to aim at ({b.W} x {b.H})");
            foreach (var o in l.Buttons)
                if (o.Id != b.Id && m.Screen != ScreenModel.View.Tv)
                    Check(b.X + b.W <= o.X || o.X + o.W <= b.X || b.Y + b.H <= o.Y || o.Y + o.H <= b.Y, $"{name}: {b.Id} and {o.Id} apart");
        }
        if (Wanted.TryGetValue(m.Screen, out var want) && !m.ToolsDownloading)
            foreach (var id in want) Check(ids.Contains(id), $"{name}: has {id}");
        if (m.Screen == ScreenModel.View.Media && !m.QueueEmpty) foreach (var id in new[] { "prev", "play", "next", "list", "vol-", "vol+" }) Check(ids.Contains(id), $"{name}: has {id}");
        if (m.Screen == ScreenModel.View.Media && m.CanTv) Check(ids.Contains("full"), $"{name}: has full");
        if (m.Screen == ScreenModel.View.Search && m.ResultsShown) foreach (var id in new[] { "r0", "done" }) Check(ids.Contains(id), $"{name}: has {id}");
        if (m.Screen == ScreenModel.View.Nav) foreach (var id in new[] { "zoom-", "zoom+" }) Check(ids.Contains(id), $"{name}: has {id}");

        var rgba = new byte[Crt.W * Crt.H * 4];
        crt.Compose(m.R, m.G, m.B, rgba, flipY: false);
        if (l.Content.W > 0) StandIn(rgba, l.Content, m);
        Png(Path.Combine(Out, name + ".png"), rgba, Crt.W, Crt.H);
    }

    /// The picture or map's place filled with a stand-in (in the game a texture shows there).
    static void StandIn(byte[] rgba, Screens.Area a, ScreenModel m)
    {
        for (int y = a.Y; y < a.Y + a.H; y++)
            for (int x = a.X; x < a.X + a.W; x++)
            {
                float fy = (y - a.Y) / (float)a.H, fx = (x - a.X) / (float)a.W;
                float k = m.Screen == ScreenModel.View.Nav ? (Math.Abs(fx - 0.45f + fy * 0.1f) < 0.012f ? 0.9f : ((x / 16 + y / 16) % 2 == 0 && x % 16 == 0 && y % 16 == 0 ? 0.25f : 0.02f))
                    : fy < 0.62f ? 0.2f + 0.5f * fy : 0.08f;
                if (m.Screen != ScreenModel.View.Nav && Math.Sqrt((fx - 0.66) * (fx - 0.66) * 3 + (fy - 0.6) * (fy - 0.6)) < 0.18) k = 0.85f;
                int o = (y * Crt.W + x) * 4;
                rgba[o] = (byte)(m.R * k); rgba[o + 1] = (byte)(m.G * k); rgba[o + 2] = (byte)(m.B * k);
            }
    }

    static void Png(string path, byte[] rgba, int w, int h)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            for (int y = 0; y < h; y++) { z.WriteByte(0); z.Write(rgba, y * w * 4, w * 4); }
        using var f = File.Create(path);
        f.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        Be(ihdr, 0, w); Be(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 6;
        Chunk(f, "IHDR", ihdr); Chunk(f, "IDAT", ms.ToArray()); Chunk(f, "IEND", Array.Empty<byte>());
    }

    static void Be(byte[] b, int at, int v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }

    static void Chunk(Stream f, string type, byte[] data)
    {
        var len = new byte[4]; Be(len, 0, data.Length); f.Write(len);
        var td = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) td[i] = (byte)type[i];
        Array.Copy(data, 0, td, 4, data.Length);
        f.Write(td);
        var crc = new byte[4]; Be(crc, 0, (int)Crc(td)); f.Write(crc);
    }

    static uint[] _crc;
    static uint Crc(byte[] b)
    {
        if (_crc == null) { _crc = new uint[256]; for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; _crc[n] = c; } }
        uint x = 0xFFFFFFFFu;
        foreach (byte v in b) x = _crc[(x ^ v) & 0xFF] ^ (x >> 8);
        return x ^ 0xFFFFFFFFu;
    }
}
