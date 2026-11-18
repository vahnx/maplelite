using System.Text.RegularExpressions;
using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;

namespace MapleLite.Core.Game;

public enum MinimapState { Full, NameHidden, Collapsed, NotFound }

/// <summary>Reads the map name from the minimap (top-left of the game window), in whichever size it is showing.</summary>
public static class MapReader
{
    static bool Is(int p, int r, int g, int b, int tol = 6) =>
        Math.Abs(((p >> 16) & 255) - r) <= tol && Math.Abs(((p >> 8) & 255) - g) <= tol && Math.Abs((p & 255) - b) <= tol;

    /// <summary>
    /// The minimap has three sizes. Full: map name inside the panel. Collapsed: a grey bar with the name in it.
    /// Middle: the "MINI MAP" title and the map picture, but no name anywhere.
    /// </summary>
    public static MinimapState State(Frame f)
    {
        if (f.W < 320 || f.H < 80) return MinimapState.NotFound;
        // Full size: the name panel (a flat blue-grey) sits where the middle size shows the map picture.
        bool panel = Is(f.At(47, 26), 153, 187, 204, 10) && Is(f.At(47, 60), 153, 187, 204, 10) && Is(f.At(100, 25), 153, 187, 204, 10);
        if (panel && RoomNumber.TextRight(f) >= RoomNumber.TextLeft + 20) return MinimapState.Full;
        if (Is(f.At(5, 5), 221, 221, 221) && Is(f.At(5, 10), 221, 221, 221) && Is(f.At(60, 3), 221, 221, 221)) return MinimapState.Collapsed;
        if (Is(f.At(5, 10), 255, 255, 255) && Is(f.At(60, 17), 255, 255, 255) && Is(f.At(60, 3), 153, 187, 204, 12)) return MinimapState.NameHidden;
        return MinimapState.NotFound;
    }

    /// <summary>
    /// The map picture inside the minimap window (with the dots for you and the others on it), or null when the
    /// minimap is collapsed or not showing. The picture sits in a white frame under a blue-grey dividing line.
    /// </summary>
    public static (int[] px, int w, int h)? Picture(Frame f)
    {
        var state = State(f);
        if (state != MinimapState.Full && state != MinimapState.NameHidden) return null;
        for (int y = 16; y < Math.Min(f.H - 40, 160); y++)
        {
            if (!Is(f.At(8, y), 0x55, 0x77, 0x99) || !Is(f.At(60, y), 0x55, 0x77, 0x99)) continue;
            int top = -1;
            for (int k = 2; k <= 8 && top < 0; k++)
                if (Is(f.At(4, y + k), 255, 255, 255, 3) && Is(f.At(8, y + k), 255, 255, 255, 3) && Is(f.At(4, y + k + 1), 255, 255, 255, 3)) top = y + k + 1;
            if (top < 0) continue;
            int right = 8;
            while (right + 1 < Math.Min(f.W, 420) && Is(f.At(right + 1, y), 0x55, 0x77, 0x99)) right++;
            int bottom = top;
            while (bottom + 1 < f.H && Is(f.At(4, bottom + 1), 255, 255, 255, 3)) bottom++;
            // white frame: 3 px each side, 1 px top and bottom
            int x0 = 6, x1 = right - 4, y0 = top, y1 = bottom - 1;
            // the frame is a pixel thicker on some sides: shave off rows and columns that are still pure white
            bool WhiteRow(int yy) { for (int x = x0; x <= x1; x++) if (!Is(f.At(x, yy), 255, 255, 255, 3)) return false; return true; }
            bool WhiteCol(int x) { for (int yy = y0; yy <= y1; yy++) if (!Is(f.At(x, yy), 255, 255, 255, 3)) return false; return true; }
            while (y1 - y0 > 20 && WhiteRow(y1)) y1--;
            while (y1 - y0 > 20 && WhiteRow(y0)) y0++;
            while (x1 - x0 > 40 && WhiteCol(x1)) x1--;
            while (x1 - x0 > 40 && WhiteCol(x0)) x0++;
            // a map narrower than the window is padded with plain bars left and right: cut those off
            // the bars are the window's pale blue, slightly shaded top to bottom; scenery drawn over the window may cross them
            bool PlainCol(int x) { int pale = 0; for (int yy = y0; yy <= y1; yy++) { int c = f.At(x, yy), r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255; if (b > 200 && r > 110 && b - r > 45 && g > r && g < b) pale++; } return pale >= (y1 - y0 + 1) * 0.6; }
            while (x1 - x0 > 60 && PlainCol(x0)) x0++;
            while (x1 - x0 > 60 && PlainCol(x1)) x1--;
            int w = x1 - x0 + 1, h = y1 - y0 + 1;
            if (w < 40 || h < 20 || h > 400) return null;
            var px = new int[w * h];
            for (int yy = 0; yy < h; yy++)
                for (int xx = 0; xx < w; xx++)
                    px[yy * w + xx] = f.At(x0 + xx, y0 + yy) | unchecked((int)0xFF000000);
            return (px, w, h);
        }
        return null;
    }

    /// <summary>
    /// Where you are on a minimap picture (from <see cref="Picture"/>): the centre of your yellow dot, a small ring of
    /// bright yellow (about FFEE22 and FFDD44) that other players' and NPCs' dots don't use. Null when it isn't there
    /// (for example, cropped out). Terrain in yellow-brown tones is darker and isn't taken for it.
    /// </summary>
    public static (double x, double y)? PlayerDot(int[] px, int w, int h)
    {
        static bool Yellow(int p) { int r = (p >> 16) & 255, g = (p >> 8) & 255, b = p & 255; return r >= 235 && g >= 200 && g <= 245 && b <= 90; }
        var seen = new bool[w * h];
        (double x, double y, int n)? best = null;
        var stack = new Stack<int>();
        for (int i = 0; i < w * h; i++)
        {
            if (seen[i] || !Yellow(px[i])) continue;
            seen[i] = true; stack.Push(i);
            int n = 0; double sx = 0, sy = 0;
            while (stack.TryPop(out int k))
            {
                int x = k % w, y = k / w;
                n++; sx += x; sy += y;
                // the ring has a hole in the middle: join pixels up to 2 apart
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                        int j = yy * w + xx;
                        if (!seen[j] && Yellow(px[j])) { seen[j] = true; stack.Push(j); }
                    }
            }
            // a dot: about 24 px (6x6 ring); not a stray pixel, not a big yellow area
            if (n >= 8 && n <= 80 && (best == null || n > best.Value.n)) best = (sx / n, sy / n, n);
        }
        return best is { } b ? (b.x, b.y) : null;
    }

    /// <summary>Common misreads of the small minimap font.</summary>
    static string Tidy(string text) => Regex.Replace(text, @"\bCit[il1]\b", "City");

    public static async Task<(MinimapState state, string? text)> ReadAsync(IOcr ocr, Frame f)
    {
        var state = State(f);
        if (state == MinimapState.Full)
        {
            int right = RoomNumber.TextRight(f);
            var lines = (await ocr.TryReadAsync(f, RoomNumber.TextLeft, RoomNumber.TextTop, right - RoomNumber.TextLeft + 1, RoomNumber.TextBottom - RoomNumber.TextTop, 2, 10)).Select(l => l.Text)
                .Where(t => t.Length >= 3 && !t.Contains("MINI", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("WORL", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (lines.Count == 0) return (state, null);
            // The room number in angle brackets ("Free Market <1>") is read separately: OCR drops it.
            var room = RoomNumber.Read(f, out _);
            var text = Tidy(string.Join(" - ", lines));
            return (state, room != null ? text + " " + room : text);
        }
        if (state == MinimapState.Collapsed)
        {
            // Title text ends where the buttons begin: find the last dark text column before a wide gap.
            int end = 6, gap = 0;
            for (int x = 6; x < Math.Min(f.W, 300); x++)
            {
                bool ink = false;
                for (int y = 4; y <= 16; y++)
                {
                    int p = f.At(x, y);
                    if (((p >> 16) & 255) < 90 && ((p >> 8) & 255) < 90 && (p & 255) < 90) { ink = true; break; }
                }
                if (ink) { end = x; gap = 0; }
                else if (++gap >= 10 && end > 6) break;
            }
            var lines = await ocr.TryReadAsync(f, 4, 3, end - 4 + 3, 15, 3, 8);
            var text = string.Join(" ", lines.Select(l => l.Text)).Trim();
            if (text.Length < 3) return (state, null);
            text = Regex.Replace(text, @"\s*[:;]\s*", " - ");
            text = Tidy(text);
            var room = RoomNumber.ReadCollapsed(f, out _);
            if (room != null) text = Regex.Replace(text, @"\s+\S{1,4}$", "") + " " + room; // swap the mangled "<1>" for the read number
            return (state, text);
        }
        return (state, null);
    }
}

/// <summary>
/// Reads the room number the minimap shows in angle brackets after a map name ("Free Market &lt;1&gt;").
/// OCR drops that part, so the digits are picked out of the picture and compared by shape with
/// digits drawn in ordinary fonts.
/// </summary>
public static class RoomNumber
{
    const int NW = 10, NH = 14;
    static List<(int digit, bool[] shape)>? _refs;
    static readonly object RefLock = new();

    public const int TextLeft = 46, TextTop = 24, TextBottom = 62;

    /// <summary>Right end of the minimap title text, or -1 when the title isn't showing: just inside the minimap's bright right border (its width varies by map).</summary>
    public static int TextRight(Frame f)
    {
        int y1 = Math.Min(f.H - 1, TextBottom), limit = Math.Min(f.W - 1, 300);
        for (int x = TextLeft; x <= limit; x++)
        {
            int n = 0;
            for (int y = TextTop; y <= y1; y++) if (Bright(f.At(x, y))) n++;
            if (n >= (y1 - TextTop + 1) * 9 / 10) return x - 2;
        }
        return -1; // no border: the minimap is collapsed, hidden or covered
    }

    static bool Bright(int p) => ((p >> 16) & 255) > 190 && ((p >> 8) & 255) > 190 && (p & 255) > 190;

    /// <summary>Returns the bracketed number on the last text line of the minimap title, or null if there is none.</summary>
    public static string? Read(Frame f, out string debug) =>
        ReadIn(f, TextLeft, TextRight(f), TextTop, Math.Min(f.H - 1, TextBottom), Bright, false, out debug);

    /// <summary>Same, for the collapsed minimap, whose single title line is dark text on a grey bar.</summary>
    public static string? ReadCollapsed(Frame f, out string debug) =>
        ReadIn(f, 4, Math.Min(f.W - 1, 300), 3, 17, Dark, true, out debug);

    static bool Dark(int p) => ((p >> 16) & 255) < 90 && ((p >> 8) & 255) < 90 && (p & 255) < 90;

    static string? ReadIn(Frame f, int x0, int x1, int y0, int y1, Func<int, bool> ink, bool stopAtWideGap, out string debug)
    {
        debug = "";
        if (x1 < x0 + 20) return null;

        // Text lines: bands of rows that contain ink. The frame line of the minimap is excluded by x1.
        var bands = new List<(int top, int bottom)>();
        int start = -1;
        for (int y = y0; y <= y1 + 1; y++)
        {
            bool any = false;
            if (y <= y1) for (int x = x0; x <= x1; x++) if (ink(f.At(x, y))) { any = true; break; }
            if (any && start < 0) start = y;
            if (!any && start >= 0) { if (y - start >= 6) bands.Add((start, y - 1)); start = -1; }
        }
        if (bands.Count == 0) return null;
        var (top, bottom) = bands[^1];

        // Glyphs: runs of columns with ink.
        var segs = new List<(int l, int r)>();
        start = -1;
        for (int x = x0; x <= x1 + 1; x++)
        {
            bool any = false;
            if (x <= x1) for (int y = top; y <= bottom; y++) if (ink(f.At(x, y))) { any = true; break; }
            if (any && start < 0) start = x;
            if (!any && start >= 0) { segs.Add((start, x - 1)); start = -1; }
        }
        // On the collapsed bar the buttons follow the text after a wide gap: drop everything from there on.
        if (stopAtWideGap)
            for (int i = 1; i < segs.Count; i++)
                if (segs[i].l - segs[i - 1].r - 1 >= 10) { segs = segs.Take(i).ToList(); break; }
        // Last word: glyphs after the last gap of 3+ columns.
        int first = 0;
        for (int i = 1; i < segs.Count; i++) if (segs[i].l - segs[i - 1].r - 1 >= 3) first = i;
        var word = segs.Skip(first).ToList();
        debug = $"line {top}-{bottom}, {segs.Count} glyphs, last word {word.Count}";
        if (word.Count < 3 || word.Count > 5) return null;

        // The word must open with '<' and close with its mirror image '>'.
        var open = Shape(f, word[0], top, bottom, true, ink);
        var close = Shape(f, word[^1], top, bottom, false, ink);
        double sym = Same(open, close);
        debug += $", bracket symmetry {sym:F2}";
        if (sym < 0.35 || word[0].r - word[0].l < 3) return null;

        lock (RefLock) _refs ??= BuildReferences();
        if (_refs.Count == 0) return null;
        var result = "";
        foreach (var seg in word.Skip(1).Take(word.Count - 2))
        {
            var shape = Shape(f, seg, top, bottom, false, ink);
            // The game draws these in a small pixel font that the reference fonts only roughly resemble. Each digit
            // gets the better of two comparisons: the 10x14 grid, and the grid pooled to 5x7, which smooths out the
            // pixel steps. Then the glyph's enclosed holes, counted at its real size, rule out digits of another
            // build (a 9 has one hole high up, a 0 one tall hole, an 8 two, 1/2/3/5/7 none): pixel-font outlines of
            // 9, 0 and 8 are close, their holes are not.
            var holes = Holes(f, seg, top, bottom, ink);
            debug += $", holes [{string.Join(" ", holes.Select(h => $"{h.top:F1}-{h.bottom:F1}"))}]";
            var best = Classify(shape, holes, _refs);
            if (best == null) return null;
            debug += $", {best.Value.digit}@{best.Value.score:F2}+{best.Value.lead:F2}";
            if (best.Value.score < 0.7 && !(best.Value.score >= 0.55 && best.Value.lead >= 0.1)) return null;
            result += best.Value.digit;
        }
        return result;
    }

    /// <summary>
    /// The digit a normalised glyph is most like, among the digits its holes allow, with its score (the better of the
    /// 10x14 and pooled 5x7 comparisons) and its lead over the next allowed digit. Null when no digit fits the holes.
    /// </summary>
    internal static (int digit, double score, double lead)? Classify(bool[] shape, IReadOnlyList<(double top, double bottom)> holes,
        IEnumerable<(int digit, bool[] shape)> refs)
    {
        var pooled = Pool(shape);
        var ranked = refs.GroupBy(r => r.digit)
            .Select(g => (digit: g.Key, score: Math.Max(g.Max(r => Same(shape, r.shape)), g.Max(r => Same(pooled, Pool(r.shape))))))
            .Where(r => HolesFit(r.digit, holes))
            .OrderByDescending(r => r.score).ToList();
        if (ranked.Count == 0) return null;
        return (ranked[0].digit, ranked[0].score, ranked[0].score - (ranked.Count > 1 ? ranked[1].score : 0));
    }

    /// <summary>The built-in reference digits: digit, its 10x14 shape, and which font style it came from (0-7).</summary>
    internal static IEnumerable<(int digit, bool[] shape, int font)> References =>
        Shapes.Select((s, i) => (i % 10, s.Select(c => c == '#').ToArray(), i / 10));

    /// <summary>
    /// The enclosed holes of a glyph at its real size: background not reachable from the glyph's edge (4-connected),
    /// as the fraction of the glyph's height each hole spans from its top (0) to its bottom (1).
    /// </summary>
    internal static List<(double top, double bottom)> Holes(Frame f, (int l, int r) seg, int top, int bottom, Func<int, bool> ink)
    {
        int t = bottom, b = top;
        for (int y = top; y <= bottom; y++)
            for (int x = seg.l; x <= seg.r; x++)
                if (ink(f.At(x, y))) { t = Math.Min(t, y); b = Math.Max(b, y); }
        int w = seg.r - seg.l + 1, h = b - t + 1;
        var holes = new List<(double, double)>();
        if (h < 3 || w < 3) return holes;
        var mask = new bool[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) mask[y * w + x] = ink(f.At(seg.l + x, t + y));
        return Holes(mask, w, h);
    }

    internal static List<(double top, double bottom)> Holes(bool[] mask, int w, int h)
    {
        var holes = new List<(double, double)>();
        var seen = new bool[w * h];
        var queue = new Queue<int>();
        // background reachable from the edge is outside
        for (int x = 0; x < w; x++) { Seed(x); Seed((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { Seed(y * w); Seed(y * w + w - 1); }
        Flood(_ => { });
        // what background is left forms the holes; tiny one-pixel gaps in a stroke don't count
        for (int i = 0; i < w * h; i++)
        {
            if (mask[i] || seen[i]) continue;
            int lo = h, hi = 0, size = 0;
            Seed(i);
            Flood(j => { size++; lo = Math.Min(lo, j / w); hi = Math.Max(hi, j / w); });
            if (size >= 2 || h <= 9) holes.Add(((double)lo / (h - 1), (double)hi / (h - 1)));
        }
        return holes;

        void Seed(int i) { if (!mask[i] && !seen[i]) { seen[i] = true; queue.Enqueue(i); } }
        void Flood(Action<int> visit)
        {
            while (queue.TryDequeue(out int i))
            {
                visit(i);
                int x = i % w, y = i / w;
                if (x > 0) Seed(i - 1);
                if (x < w - 1) Seed(i + 1);
                if (y > 0) Seed(i - w);
                if (y < h - 1) Seed(i + w);
            }
        }
    }

    /// <summary>Whether a digit can have these holes: 0 one tall hole; 6 one low; 9 one high; 8 two; 4 none or one; the rest none.</summary>
    internal static bool HolesFit(int digit, IReadOnlyList<(double top, double bottom)> holes) => digit switch
    {
        0 => holes.Count == 1 && holes[0].bottom - holes[0].top >= 0.45,
        6 => holes.Count == 1 && holes[0].top >= 0.35,
        9 => holes.Count == 1 && holes[0].bottom <= 0.65,
        8 => holes.Count == 2,
        4 => holes.Count <= 1,
        _ => holes.Count == 0,
    };

    /// <summary>Glyph normalised to a small grid: scaled to full height, centred horizontally.</summary>
    static bool[] Shape(Frame f, (int l, int r) seg, int top, int bottom, bool mirror, Func<int, bool> ink)
    {
        int t = bottom, b = top;
        for (int y = top; y <= bottom; y++)
            for (int x = seg.l; x <= seg.r; x++)
                if (ink(f.At(x, y))) { t = Math.Min(t, y); b = Math.Max(b, y); }
        int w = seg.r - seg.l + 1, h = Math.Max(1, b - t + 1);
        var mask = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                mask[y * w + x] = ink(f.At(seg.l + (mirror ? w - 1 - x : x), t + y));
        return Normalise(mask, w, h);
    }

    internal static bool[] Normalise(bool[] mask, int w, int h)
    {
        var grid = new bool[NW * NH];
        double scale = (double)h / NH;              // source pixels per grid cell
        double gridW = w / scale;                   // glyph width in grid cells
        double offset = (NW - gridW) / 2;
        for (int gy = 0; gy < NH; gy++)
            for (int gx = 0; gx < NW; gx++)
            {
                double sx = (gx + 0.5 - offset) * scale, sy = (gy + 0.5) * scale;
                int ix = (int)Math.Floor(sx), iy = (int)Math.Floor(sy);
                if (ix < 0 || ix >= w || iy < 0 || iy >= h) continue;
                grid[gy * NW + gx] = mask[iy * w + ix];
            }
        return grid;
    }

    /// <summary>The 10x14 grid pooled to 5x7: a cell is ink when at least two of its four pixels are.</summary>
    internal static bool[] Pool(bool[] g)
    {
        var o = new bool[5 * 7];
        for (int gy = 0; gy < 7; gy++)
            for (int gx = 0; gx < 5; gx++)
            {
                int n = 0;
                for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++) if (g[(gy * 2 + dy) * NW + gx * 2 + dx]) n++;
                o[gy * 5 + gx] = n >= 2;
            }
        return o;
    }

    static double Same(bool[] a, bool[] b)
    {
        int both = 0, either = 0;
        for (int i = 0; i < a.Length; i++) { if (a[i] && b[i]) both++; if (a[i] || b[i]) either++; }
        return either == 0 ? 0 : (double)both / either;
    }

    /// <summary>
    /// Digits 0-9 as drawn in common fonts (Arial, Tahoma, Verdana and Segoe UI, bold and regular), white on black, cut
    /// to their ink and normalised to the 10x14 grid. Rendered once and kept as data, so the reader needs no fonts and
    /// gives the same answer on every OS. Row by row, '#' is ink.
    /// </summary>
    static readonly string[] Shapes =
    {
        // Arial bold: digits 0-9
        "..#####.....######...###..###..###..###.###...###.###...#######...#######...#######...#######...###..###..###..###..###...######....#####...", "......##.......###......####....######....######....##.###.......###.......###.......###.......###.......###.......###.......###.......###..", "..######...########..####.###..##....###.......###.......##......####......###.....####......###......###......#########.###################", "..#####.....######...###.####.###...###.......##......####......####........###.......###.......#######...###..###.####...######....#####...", ".....####......####.....#####.....#####....##.###...###.###..###..###..###..###.###...###.####################......###.......###.......###.", ".########..########..########..###.......##........#######...########..###.####........##........######....##..###.####..#######....#####...", "...#####....######...####.###..###..###.####......###.###...#########.#########.####..########...###.###...###.####.###...#######....#####..", "#############################.......##.......###......###.......##........##.......###.......###......###.......###.......###.......###.....", "..######...#######...###..###.###...###..##...###..###.###....######...#######..####..###.###...#######...########..###..#######....######..", "..#####....#######...###.####..##...###.###....##.###...####.#########.#########...###.###.......##..##...###..###.####..#######....#####...",
        // Arial regular: digits 0-9
        "..#####.....######...##....##..##....##.##.....##.##......####......####......####......####.....##..##....##..##....##...######....#####...", "......##.......###......####.....#####....##..##........##........##........##........##........##........##........##........##........##..", "...#####....#######..##....##..##....##........##........##.......###......###......###......###......###......##........###################", "..#####.....######...##....##.##.....##........##.......##......###.......####.........##.........####......#####....##..#######....######..", "......##........##.......###......####.....##.##.....#..##....##..##...##...##..##....##..####################......##........##........##..", "..#######...#######...#........##........##.###....#######...##....##........##.........##........####.....##..##....##..#######....#####...", "...#####....#######..##....##..##....##..##........#.#####..####..###.###....##..##.....##.##.....##.##....##..##....##...#######....#####..", "####################......##........##.......##........##.......##........##.......##........##........##........##.......##........##......", "..#####.....######...##....##..##....##..##....##...##..##....######...###..###.##.....##.##......####......#####....##..#######....######..", "..#####.....######...##....##.##.....##.##......####.....###.##....###.###..####..#####.##........#.##.....##..##....##..#######....#####...",
        // Tahoma bold: digits 0-9
        "..######....#######..####.########...######....######....######....######....######....######....#######...###.####.####..#######...######..", "...####......####...#######...#######......####......####......####......####......####......####......####......####...####################", ".#######..########..#########.#.....###.......###.......###......####.....####......####.....####....#####.....###......####################", ".#######..#########.#########.#.....####......###......####....####......######.......####......#####.....#######################..#######..", ".....####.....#####....######....######...#######..###.####.###..####.###..####.##############################.....####......####......####.", ".#########.#########.###.......###.......###.......########..########.......####......####......######....#############.#########..#######..", "...######...#######..#####..#..###......####......#########.##############..#######....#######...#######...###.####.####.########....#####..", "####################......####......####.....####......####.....####......####.....####......####.....####......####.....####.....#####.....", "..######...########.#####.#######....#######...############...######...############...#######....######....########.####.########...######..", "..######...#######..####.########....######....######....########.##############..####.###......####......####.#..#####..#######...######...",
        // Tahoma regular: digits 0-9
        "..#####.....######...##...###..##....##.##.....##.##.....##.##.....#####.....#####.....##.##.....##..##....##..##...###...######....#####...", "....##........##.....#####......####........##........##........##........##........##........##........##........##.....#######...########.", ".######....#######...#.....##........##........##........##.......##.......###......###......###......###......###.......########..########.", ".#######...########..#.....##........##........##......###.....####........####........##........##........##........##..#######...######...", "......##.......###......####.....##.##....##..##...###..##...##...##..##....##..##########......##........##........##........##........##..", ".########..########..##........##........##........######....#######........###........##........##........##..#....###..#######...######...", "....####....######....##.......##........##.......##..###...#########.###....##.##.....#####.....###.##....###.##....##...######....######..", "####################.......##........##.......##........##.......##........##.......##........##.......##.......###.......##........##......", "...#####...########..##....##..##....##..##....##..####..##....####....##..####..##....##..##....###.##....###.##....##..########...######..", "...####....#######...##....##..##....##.###....##.###....###.##....###.########....###.##........##........##.......##...######....#####....",
        // Verdana bold: digits 0-9
        "..######...########.#####.#######....######....######....######....######....######....######....######....########.####.########...######..", "...####......####...#######...#######......####......####......####......####......####......####......####......####...####################", "########..#########.###########.....####......####......####.....####......####.....####....#####....####.....##############################", ".#######..#########.###########......###.......###...######....######....#######.......###.......####......######################.########..", ".....####.....#####....######...#######...##.####..###.####.###..####.##...####.##############################.....####......####......####.", "##################################......####......#########.##########......####.......###.......#####....#######################.########..", "...######...#######..#####..#.####......####......#########.##############..#######....######....######....#######..####.########...######..", "##############################......####......####.....####......####.....####.....####......####.....####......####.....####......####.....", "..######...#############..#######....######....#############.########..############...#######....######....#######..##############..######..", "..######...########.####..#######....######....######....########.##############.#########.......###......####.#..#####..#######...######...",
        // Verdana regular: digits 0-9
        "...####.....######...##....##..##....##.###....##.##.....#####.....#####.....#####.....######....##..##....##..##....##...######.....####...", "....##........##.....#####.....#####........##........##........##........##........##........##........##........##.....########..########.", ".#######...#######.........##........##........##........##.......##.......###......###......###......##.......###......####################", ".######....########..#.....##........##........##......###......###.........###........##........##........##........##..#######...######...", "......##.......###......####.....##.##.....##.##....##..##...##...##..##....##..####################......##........##........##........##..", ".########..########..##........##........##........######....########.......###........##........##........##........##..#######...######...", "....#####....######...##.......##........##.......###.###...#########.###....##.##......#####.....##.##.....##.##....##...#######....####...", "####################........##.......##.......##........##.......###.......##.......##........##.......###.......##.......##........##......", "..######...########..##....##.###....##..##....##..###...##...#####....##..####.###....##.##.....#####.....######....##..########...#####...", "..#####.....######...##....##.##......####......####......##.##....###.#########...####.##.......##........##.......##....#####.....####....",
        // Segoe UI bold: digits 0-9
        "...#####...########..###..####.###..########...#######...#######...#######...#######...#######...#######..####.###..###..#######....######..", ".....###.....#####....######....######....#..###.......###.......###.......###.......###.......###.......###.......###.......###.......###..", ".#######...########..##..####.......####......####......###......####.....####....#####....####......###......#########.####################", ".#######...########..#...####.......###.......###.....#####...######....#######......####.......###.......###.##...####.#########..######...", ".....####......####.....#####....######....######...#######..###.####.###..####.###..####.####################.....####......####......####.", ".########..########..###.......###.......###.......######....#######...########.......####......####......###.##...####.########..#######...", "...######....######..####...#..###......####......###.####..#########.####..########...######....#######...###.###..####..#######....#####..", "####################......####......###......####......###......###.......###......####......###.......###.......###......####......###.....", "..######...########.####..#######....######....###.########...######...########.####..#######....######....#######..####.########...######..", "..######...#######...###..###.####..#######....#######..########..####.#########..####.###......####......###..#...####..#######...######...",
        // Segoe UI regular: digits 0-9
        "...####.....######...##....##..##....##.###....#####......####......####......####......#####....###.##....##..##....##...######.....####...", "......#.......###......##.#......#..#.........#.........#.........#.........#.........#.........#.........#.........#.........#.........#...", "..#####....#######...#.....##........##........##........##.......##.......##.......##......##.......##........##.......#########.##########", "..#####....#######.........#.........#.........#........##....#####......#####.........##........##........##........##..#######...######...", "......##........##.......###......####......#.##.....#..##....##..##...##...##...#....##..####################......##........##........##..", "..#######...######....#.........#.........#........#####.....#######.........##........##........##........##........##..#######...######...", "....#####....######...##.......##........##........##.###...########..###....##.###....###.##.....##.##....###.##....##...#######....####...", "###################........##.......##........##.......##........##........#........##........#........##........##........##........##.....", "...####.....######...##....##..##....##..##....##...##..##.....####....##....##..##....##.###....######....######....###.########...######..", "...####.....######...##....##.###....##.##.....##.###....###.##....###..########...###.##........##........##........#...######....######...",
    };

    static List<(int digit, bool[] shape)> BuildReferences() =>
        Shapes.Select((s, i) => (i % 10, s.Select(c => c == '#').ToArray())).ToList();
}

/// <summary>Reads the character name from the status bar at the bottom of the game window.</summary>
public static class IgnReader
{
    public static async Task<string?> ReadAsync(IOcr ocr, Frame f)
    {
        // The status bar is 800 wide and centred; job and name sit right of the level box.
        int x = f.W / 2 - 400 + 78, y = f.H - 34;
        if (x < 0 || y < 0) return null;
        var lines = await ocr.TryReadAsync(f, x, y, 132, 32, 3, 6);
        if (lines.Count < 2) return null; // need both lines (job, then name) to trust it
        var name = lines.OrderBy(l => l.Cy()).Last().Text;
        name = new string(name.Where(char.IsLetterOrDigit).ToArray());
        return name.Length >= 3 ? name : null;
    }
}

/// <summary>
/// Reads the current channel from the game's Change Channel window, where the channel you are on is the
/// one blue button in a grid of grey ones (8 across, 5 down). Only works while that window is open.
/// </summary>
public static class ChannelReader
{
    const int PitchX = 70, PitchY = 22, Cols = 8, Rows = 5;

    static bool Is(int p, int r, int g, int b, int tol = 8) =>
        Math.Abs(((p >> 16) & 255) - r) <= tol && Math.Abs(((p >> 8) & 255) - g) <= tol && Math.Abs((p & 255) - b) <= tol;
    static bool Selected(int p) => Is(p, 54, 170, 221);
    static bool Plain(int p) => Is(p, 177, 177, 177);

    public static int? Read(Frame f)
    {
        for (int y = 40; y < f.H - 40; y++)
        {
            int run = 0;
            for (int x = 0; x < f.W; x++)
            {
                if (Selected(f.At(x, y))) { run++; continue; }
                if (run >= 58 && run <= 70)
                {
                    int left = x - run, probeX = left + run / 2;
                    // must be the top edge of a button: not blue just above
                    if (!Selected(f.At(probeX, y - 2)))
                    {
                        int col = 0, row = 0;
                        while (col < Cols - 1 && probeX - PitchX * (col + 1) >= 0 && Plain(f.At(probeX - PitchX * (col + 1), y + 1))) col++;
                        while (row < Rows - 1 && y + 1 - PitchY * (row + 1) >= 0 && Plain(f.At(probeX, y + 1 - PitchY * (row + 1)))) row++;
                        // confirm it sits in a grid of grey buttons: a grey neighbour beside it and above or below it
                        bool beside = col > 0 || (probeX + PitchX < f.W && Plain(f.At(probeX + PitchX, y + 1)));
                        bool vertical = row > 0 || (y + 1 + PitchY < f.H && Plain(f.At(probeX, y + 1 + PitchY)));
                        if (beside && vertical) return row * Cols + col + 1;
                    }
                }
                run = 0;
            }
        }
        return null;
    }
}
