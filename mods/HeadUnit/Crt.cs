using System;
using System.Collections.Generic;
using System.Text;

namespace HeadUnit
{
    /// The screen as an old monochrome CRT draws it: one canvas of brightness (0 to 255) on the design's own grid
    /// (800 × 440 for the 20 × 11 cm screen), drawn with boxes, a 5 × 7 character font and pixel icons, then lit in
    /// the model's phosphor colour with a glow, scanlines and a vignette (Compose). Plain C#, no Unity: the unit tests
    /// draw every screen to pictures with it (tests/HeadUnitScreenTest).
    public sealed class Crt
    {
        public const int W = 800, H = 440;
        public const byte Full = 255, Edge = 153, Dim = 153, Faint = 51, DitherLevel = 82, Ink = 0;

        /// Brightness, row by row from the top.
        public readonly byte[] I = new byte[W * H];
        /// Every piece of text drawn since Clear (the probe's ScreenText).
        public readonly List<string> Texts = new List<string>();

        public void Clear() { Array.Clear(I, 0, I.Length); Texts.Clear(); }

        public void Fill(int x, int y, int w, int h, byte v)
        {
            int x0 = Math.Max(0, x), y0 = Math.Max(0, y), x1 = Math.Min(W, x + w), y1 = Math.Min(H, y + h);
            for (int yy = y0; yy < y1; yy++) { int o = yy * W; for (int xx = x0; xx < x1; xx++) I[o + xx] = v; }
        }

        /// A 2 px outline.
        public void Frame(int x, int y, int w, int h, byte v, int t = 2)
        {
            Fill(x, y, w, t, v); Fill(x, y + h - t, w, t, v);
            Fill(x, y + t, t, h - 2 * t, v); Fill(x + w - t, y + t, t, h - 2 * t, v);
        }

        /// A checkerboard of 2 px squares: what the crosshair is on.
        public void Dither(int x, int y, int w, int h, byte v)
        {
            int x0 = Math.Max(0, x), y0 = Math.Max(0, y), x1 = Math.Min(W, x + w), y1 = Math.Min(H, y + h);
            for (int yy = y0; yy < y1; yy++)
            {
                int o = yy * W;
                for (int xx = x0; xx < x1; xx++) if ((((xx >> 1) + (yy >> 1)) & 1) == 0) I[o + xx] = v;
            }
        }

        public void Dot(int x, int y, byte v) { if (x >= 0 && x < W && y >= 0 && y < H) I[y * W + x] = v; }

        /// A line `t` px thick.
        public void Line(float ax, float ay, float bx, float by, byte v, int t = 2)
        {
            float len = (float)Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            int steps = Math.Max(1, (int)Math.Ceiling(len));
            for (int i = 0; i <= steps; i++)
            {
                float f = i / (float)steps;
                int x = (int)Math.Round(ax + (bx - ax) * f) - t / 2, y = (int)Math.Round(ay + (by - ay) * f) - t / 2;
                Fill(x, y, t, t, v);
            }
        }

        // ---- the font: 5 × 7 capitals, digits and punctuation, like a terminal's character ROM ----

        /// What the font can draw of `s`: capitals, accents and Cyrillic spelled in plain letters, the typographer's
        /// dashes and quotes as their plain ones, anything else as '?'.
        public static string Plain(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c0 in s)
            {
                char c = char.ToUpperInvariant(c0);
                if (c < 128) { sb.Append(c >= 32 ? c : ' '); continue; }
                string t = Spelled(c);
                if (t != null) { sb.Append(t); continue; }
                string d = c.ToString().Normalize(NormalizationForm.FormD);
                char plain = d.Length > 0 ? char.ToUpperInvariant(d[0]) : '?';
                sb.Append(plain < 128 && plain >= 32 ? plain : '?');
            }
            return sb.ToString();
        }

        private static string Spelled(char c)
        {
            switch (c)
            {
                case '—': case '–': case '‒': case '−': return "-";
                case '‘': case '’': case '‚': case '′': return "'";
                case '“': case '”': case '„': case '″': return "\"";
                case '…': return "...";
                case '·': case '•': return "-";
                case '×': return "X";
                case ' ': return " ";
                case 'ß': return "SS";
                case 'Æ': return "AE";
                case 'Œ': return "OE";
                case 'Ø': return "O";
                case 'Ł': return "L";
                case 'Đ': return "D";
                case 'Þ': return "TH";
                case 'İ': return "I";
                case 'ı': return "I";
            }
            if (c >= 'А' && c <= 'Я') return Cyr[c - 'А'];
            if (c == 'Ё') return "YO";
            if (c == 'Є') return "YE";
            if (c == 'І') return "I";
            if (c == 'Ї') return "YI";
            return null;
        }

        private static readonly string[] Cyr =
        {
            "A", "B", "V", "G", "D", "E", "ZH", "Z", "I", "Y", "K", "L", "M", "N", "O", "P", "R", "S", "T", "U", "F", "KH", "TS",
            "CH", "SH", "SHCH", "", "Y", "", "E", "YU", "YA",
        };

        /// The characters of `s` that come out as '?' (the probe's Missing).
        public static string Missing(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "") if (c != '?' && Plain(c.ToString()).Contains("?")) sb.Append(c);
            return sb.ToString();
        }

        private static readonly Dictionary<char, byte[]> Glyphs = BuildGlyphs();

        /// A glyph's columns in use (left, width): punctuation and the space are narrower than letters and digits.
        private static void Span(char c, out int left, out int width)
        {
            if (c == ' ') { left = 0; width = 3; return; }
            if (char.IsLetterOrDigit(c) || !Glyphs.TryGetValue(c, out var g)) { left = 0; width = 5; return; }
            int lo = 5, hi = -1;
            foreach (byte row in g) for (int x = 0; x < 5; x++) if ((row & (0x10 >> x)) != 0) { lo = Math.Min(lo, x); hi = Math.Max(hi, x); }
            if (hi < 0) { left = 0; width = 3; return; }
            left = lo; width = hi - lo + 1;
        }

        /// The width of `s` at `scale` (font pixels drawn `scale` px square), without the last letter's spacing.
        public static int Measure(string s, int scale)
        {
            s = Plain(s);
            int w = 0;
            foreach (char c in s) { Span(c, out _, out int cw); w += (cw + 1) * scale; }
            return Math.Max(0, w - scale);
        }

        /// `s` at (x, y), its top-left; up to `maxW` px wide (what doesn't fit isn't drawn, nor kept in Texts). Returns
        /// the width drawn.
        public int Text(int x, int y, string s, int scale, byte v, int maxW = int.MaxValue)
        {
            s = Plain(s);
            if (s.Length == 0) return 0;
            int cx = x, n = 0;
            foreach (char c in s)
            {
                Span(c, out int left, out int cw);
                if (cx + cw * scale - x > maxW) break;
                if (Glyphs.TryGetValue(c, out var g))
                    for (int row = 0; row < 7; row++)
                        for (int col = 0; col < 5; col++)
                            if ((g[row] & (0x10 >> col)) != 0) Fill(cx + (col - left) * scale, y + row * scale, scale, scale, v);
                cx += (cw + 1) * scale;
                n++;
            }
            if (n > 0) Texts.Add(s.Substring(0, n));
            return Math.Max(0, cx - x - scale);
        }

        public void TextCentred(int cx, int y, string s, int scale, byte v, int maxW = int.MaxValue)
        {
            int w = Math.Min(Measure(s, scale), maxW);
            Text(cx - w / 2, y, s, scale, v, maxW);
        }

        public void TextRight(int right, int y, string s, int scale, byte v) => Text(right - Measure(s, scale), y, s, scale, v);

        /// Text in a box's middle: as much of `s` as fits, centred.
        public void Label(int x, int y, int w, int h, string s, int scale, byte v)
        {
            int maxW = w - 2 * scale - 8;
            TextCentred(x + w / 2, y + (h - 7 * scale) / 2, s, scale, v, maxW);
        }

        private static Dictionary<char, byte[]> BuildGlyphs()
        {
            var d = new Dictionary<char, byte[]>();
            void G(char c, params string[] rows)
            {
                var b = new byte[7];
                for (int r = 0; r < 7; r++) for (int x = 0; x < 5; x++) if (rows[r][x] == '#') b[r] |= (byte)(0x10 >> x);
                d[c] = b;
            }
            G(' ', ".....", ".....", ".....", ".....", ".....", ".....", ".....");
            G('!', "..#..", "..#..", "..#..", "..#..", "..#..", ".....", "..#..");
            G('"', ".#.#.", ".#.#.", ".....", ".....", ".....", ".....", ".....");
            G('#', ".#.#.", ".#.#.", "#####", ".#.#.", "#####", ".#.#.", ".#.#.");
            G('$', "..#..", ".####", "#.#..", ".###.", "..#.#", "####.", "..#..");
            G('%', "##...", "##..#", "...#.", "..#..", ".#...", "#..##", "...##");
            G('&', ".##..", "#..#.", "#.#..", ".#...", "#.#.#", "#..#.", ".##.#");
            G('\'', "..#..", "..#..", ".#...", ".....", ".....", ".....", ".....");
            G('(', "...#.", "..#..", ".#...", ".#...", ".#...", "..#..", "...#.");
            G(')', ".#...", "..#..", "...#.", "...#.", "...#.", "..#..", ".#...");
            G('*', ".....", "..#..", "#.#.#", ".###.", "#.#.#", "..#..", ".....");
            G('+', ".....", "..#..", "..#..", "#####", "..#..", "..#..", ".....");
            G(',', ".....", ".....", ".....", ".....", ".##..", "..#..", ".#...");
            G('-', ".....", ".....", ".....", "#####", ".....", ".....", ".....");
            G('.', ".....", ".....", ".....", ".....", ".....", ".##..", ".##..");
            G('/', ".....", "....#", "...#.", "..#..", ".#...", "#....", ".....");
            G('0', ".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###.");
            G('1', "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###.");
            G('2', ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####");
            G('3', "#####", "...#.", "..#..", "...#.", "....#", "#...#", ".###.");
            G('4', "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#.");
            G('5', "#####", "#....", "####.", "....#", "....#", "#...#", ".###.");
            G('6', "..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###.");
            G('7', "#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#...");
            G('8', ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###.");
            G('9', ".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##..");
            G(':', ".....", ".##..", ".##..", ".....", ".##..", ".##..", ".....");
            G(';', ".....", ".##..", ".##..", ".....", ".##..", "..#..", ".#...");
            G('<', "...#.", "..#..", ".#...", "#....", ".#...", "..#..", "...#.");
            G('=', ".....", ".....", "#####", ".....", "#####", ".....", ".....");
            G('>', ".#...", "..#..", "...#.", "....#", "...#.", "..#..", ".#...");
            G('?', ".###.", "#...#", "....#", "...#.", "..#..", ".....", "..#..");
            G('@', ".###.", "#...#", "....#", ".##.#", "#.#.#", "#.#.#", ".###.");
            G('A', ".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#");
            G('B', "####.", "#...#", "#...#", "####.", "#...#", "#...#", "####.");
            G('C', ".###.", "#...#", "#....", "#....", "#....", "#...#", ".###.");
            G('D', "###..", "#..#.", "#...#", "#...#", "#...#", "#..#.", "###..");
            G('E', "#####", "#....", "#....", "####.", "#....", "#....", "#####");
            G('F', "#####", "#....", "#....", "####.", "#....", "#....", "#....");
            G('G', ".###.", "#...#", "#....", "#.###", "#...#", "#...#", ".####");
            G('H', "#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#");
            G('I', ".###.", "..#..", "..#..", "..#..", "..#..", "..#..", ".###.");
            G('J', "..###", "...#.", "...#.", "...#.", "...#.", "#..#.", ".##..");
            G('K', "#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#");
            G('L', "#....", "#....", "#....", "#....", "#....", "#....", "#####");
            G('M', "#...#", "##.##", "#.#.#", "#.#.#", "#...#", "#...#", "#...#");
            G('N', "#...#", "#...#", "##..#", "#.#.#", "#..##", "#...#", "#...#");
            G('O', ".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###.");
            G('P', "####.", "#...#", "#...#", "####.", "#....", "#....", "#....");
            G('Q', ".###.", "#...#", "#...#", "#...#", "#.#.#", "#..#.", ".##.#");
            G('R', "####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#");
            G('S', ".####", "#....", "#....", ".###.", "....#", "....#", "####.");
            G('T', "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#..");
            G('U', "#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###.");
            G('V', "#...#", "#...#", "#...#", "#...#", "#...#", ".#.#.", "..#..");
            G('W', "#...#", "#...#", "#...#", "#.#.#", "#.#.#", "#.#.#", ".#.#.");
            G('X', "#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#");
            G('Y', "#...#", "#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..");
            G('Z', "#####", "....#", "...#.", "..#..", ".#...", "#....", "#####");
            G('[', ".###.", ".#...", ".#...", ".#...", ".#...", ".#...", ".###.");
            G('\\', ".....", "#....", ".#...", "..#..", "...#.", "....#", ".....");
            G(']', ".###.", "...#.", "...#.", "...#.", "...#.", "...#.", ".###.");
            G('^', "..#..", ".#.#.", "#...#", ".....", ".....", ".....", ".....");
            G('_', ".....", ".....", ".....", ".....", ".....", ".....", "#####");
            G('`', ".#...", "..#..", ".....", ".....", ".....", ".....", ".....");
            G('{', "...#.", "..#..", "..#..", ".#...", "..#..", "..#..", "...#.");
            G('|', "..#..", "..#..", "..#..", "..#..", "..#..", "..#..", "..#..");
            G('}', ".#...", "..#..", "..#..", "...#.", "..#..", "..#..", ".#...");
            G('~', ".....", ".....", ".#...", "#.#.#", "...#.", ".....", ".....");
            return d;
        }

        // ---- pixel icons ----

        public enum Glyph { Radio, Tv, Nav, Play, Pause, Prev, Next, SeekDown, SeekUp, Up, Down, Check }

        public static void Size(Glyph g, out int w, out int h)
        {
            switch (g)
            {
                case Glyph.Radio: case Glyph.Tv: case Glyph.Nav: w = 12; h = 12; return;
                case Glyph.Play: case Glyph.Pause: w = 6; h = 11; return;
                case Glyph.Prev: case Glyph.Next: w = 8; h = 11; return;
                case Glyph.SeekDown: case Glyph.SeekUp: w = 12; h = 11; return;
                case Glyph.Up: case Glyph.Down: w = 11; h = 6; return;
                default: w = 12; h = 8; return;
            }
        }

        private static readonly string[] RadioIcon =
        {
            "............", "..........#.", ".........#..", "........#...", ".##########.", ".#........#.",
            ".#.#.#..#.#.", ".#......#.#.", ".#.#.#..#.#.", ".#........#.", ".##########.", "............",
        };
        private static readonly string[] TvIcon =
        {
            "............", "............", ".##########.", ".#........#.", ".#...#....#.", ".#...##...#.",
            ".#...##...#.", ".#...#....#.", ".#........#.", ".##########.", "....#..#....", "...######...",
        };
        private static readonly string[] NavIcon =
        {
            "............", ".....##.....", ".....##.....", "....####....", "....####....", "...######...",
            "...######...", "..###..###..", "..###..###..", ".###....###.", ".###....###.", "............",
        };

        private static bool On(Glyph g, int x, int y)
        {
            int Tri(int r) => Math.Min(r, 10 - r);   // a triangle 11 rows tall: how far its row reaches
            switch (g)
            {
                case Glyph.Radio: return RadioIcon[y][x] == '#';
                case Glyph.Tv: return TvIcon[y][x] == '#';
                case Glyph.Nav: return NavIcon[y][x] == '#';
                case Glyph.Play: return x <= Tri(y);
                case Glyph.Pause: return x < 2 || x >= 4;
                case Glyph.Prev: return x == 0 || (x >= 2 && 7 - x <= Tri(y));
                case Glyph.Next: return x == 7 || (x <= 5 && x <= Tri(y));
                case Glyph.SeekDown: return 5 - x % 6 <= Tri(y);
                case Glyph.SeekUp: return x % 6 <= Tri(y);
                case Glyph.Up: return Math.Abs(x - 5) <= y;
                case Glyph.Down: return Math.Abs(x - 5) <= 5 - y;
                case Glyph.Check:
                    // 2 × 2 steps down to the bottom and up to the top right
                    int[] cx = { 0, 2, 4, 6, 8, 10 }, cy = { 2, 4, 6, 4, 2, 0 };
                    for (int i = 0; i < cx.Length; i++) if (x >= cx[i] && x < cx[i] + 2 && y >= cy[i] && y < cy[i] + 2) return true;
                    return false;
            }
            return false;
        }

        public void Icon(Glyph g, int x, int y, int scale, byte v)
        {
            Size(g, out int w, out int h);
            for (int yy = 0; yy < h; yy++) for (int xx = 0; xx < w; xx++) if (On(g, xx, yy)) Fill(x + xx * scale, y + yy * scale, scale, scale, v);
        }

        public void IconCentred(Glyph g, int x, int y, int w, int h, int scale, byte v)
        {
            Size(g, out int gw, out int gh);
            Icon(g, x + (w - gw * scale) / 2, y + (h - gh * scale) / 2, scale, v);
        }

        // ---- lighting it ----

        private readonly float[] _blurA = new float[W * H], _blurB = new float[W * H];
        private static float[] _vignette;

        /// The canvas lit in the phosphor (r, g, b) into `rgba` (W × H × 4): a soft glow around what is lit, every
        /// third row darker (scanlines), the corners darker. `flipY`: bottom row first, as a Unity texture wants.
        public void Compose(byte r, byte g, byte b, byte[] rgba, bool flipY, bool effects = true)
        {
            if (_vignette == null)
            {
                var v = new float[W * H];
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        float dx = (x - W * 0.5f) / (W * 0.5f), dy = (y - H * 0.5f) / (H * 0.5f);
                        float d = (float)Math.Sqrt(dx * dx + dy * dy) / 1.414f;
                        float t = Math.Max(0f, Math.Min(1f, (d - 0.55f) / 0.45f));
                        v[y * W + x] = 1f - 0.45f * t * t;
                    }
                _vignette = v;
            }
            if (effects) Blur(3);
            for (int y = 0; y < H; y++)
            {
                int src = y * W, dst = (flipY ? H - 1 - y : y) * W * 4;
                float scan = effects && y % 3 == 2 ? 0.68f : 1f;
                for (int x = 0; x < W; x++)
                {
                    float lit = I[src + x] / 255f;
                    if (effects) lit = Math.Max(lit, _blurA[src + x] * 0.42f);
                    float k = lit * scan * (effects ? _vignette[src + x] : 1f);
                    int o = dst + x * 4;
                    rgba[o] = (byte)Math.Min(255f, 5f + r * k);
                    rgba[o + 1] = (byte)Math.Min(255f, 8f + g * k);
                    rgba[o + 2] = (byte)Math.Min(255f, 6f + b * k);
                    rgba[o + 3] = 255;
                }
            }
        }

        /// A box blur of the canvas (radius `r`, across then down) into _blurA, 0 to 1.
        private void Blur(int r)
        {
            float n = 2 * r + 1;
            for (int y = 0; y < H; y++)
            {
                int o = y * W; float sum = 0;
                for (int x = -r; x <= r; x++) sum += I[o + Math.Max(0, Math.Min(W - 1, x))];
                for (int x = 0; x < W; x++)
                {
                    _blurB[o + x] = sum / (n * 255f);
                    sum += I[o + Math.Min(W - 1, x + r + 1)] - I[o + Math.Max(0, x - r)];
                }
            }
            for (int x = 0; x < W; x++)
            {
                float sum = 0;
                for (int y = -r; y <= r; y++) sum += _blurB[Math.Max(0, Math.Min(H - 1, y)) * W + x];
                for (int y = 0; y < H; y++)
                {
                    _blurA[y * W + x] = sum / n;
                    sum += _blurB[Math.Min(H - 1, y + r + 1) * W + x] - _blurB[Math.Max(0, y - r) * W + x];
                }
            }
        }
    }
}
