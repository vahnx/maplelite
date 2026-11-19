using MapleLite.Core.Imaging;

namespace MapleLite.Core.Ocr;

/// <summary>
/// A named cleanup step applied to a crop before OCR. The key identifies it in the shared cache,
/// so two plugins asking for the same cleanup of the same region share one read.
/// Plugins can make their own; give each a unique key (prefix it with the plugin id).
/// </summary>
public sealed class OcrPreprocessor(string key, Func<Frame, Frame> apply)
{
    public string Key { get; } = key;
    public Frame Apply(Frame f) => apply(f);

    public static readonly OcrPreprocessor None = new("none", f => f);

    /// <summary>
    /// Thin dark lettering (the game's pickup notices over a light scene draw as 1-pixel dark strokes): pixels darker
    /// than <paramref name="level"/> become black on white, then strokes are made <paramref name="bold"/> pixels wider
    /// to the right, which OCR reads far better than hairlines.
    /// </summary>
    public static OcrPreprocessor DarkStrokes(int level, int bold = 1) => new($"strokes:{level}:{bold}", f =>
    {
        int w = f.W, h = f.H;
        var o = new Frame(w, h);
        Array.Fill(o.Px, Pixel.White);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (Pixel.Max(f.Px[y * w + x]) < level)
                    for (int k = 0; k <= bold && x + k < w; k++) o.Px[y * w + x + k] = Pixel.Black;
        return o;
    });

    /// <summary>
    /// Outlined lettering (white letters with a black outline, like the game's pickup notices) picked out of any
    /// scene: near-white pixels (every channel at least <paramref name="light"/>) with a near-black pixel (brightest
    /// channel under <paramref name="dark"/>) within <paramref name="reach"/> pixels become black on white. Scenery
    /// rarely puts pure white right beside pure black, so busy backgrounds drop out. Over a scene as light as the
    /// letters the fill can't be told apart; <see cref="DarkStrokes"/> reads the outline there instead.
    /// </summary>
    public static OcrPreprocessor OutlinedFill(int dark = 70, int light = 200, int reach = 2) => new($"fill:{dark}:{light}:{reach}", f =>
    {
        int w = f.W, h = f.H;
        var isDark = new bool[w * h];
        for (int i = 0; i < isDark.Length; i++) isDark[i] = Pixel.Max(f.Px[i]) < dark;
        var o = new Frame(w, h);
        Array.Fill(o.Px, Pixel.White);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = f.Px[y * w + x];
                if (Math.Min((p >> 16) & 255, Math.Min((p >> 8) & 255, p & 255)) < light) continue;
                bool near = false;
                for (int yy = Math.Max(0, y - reach); yy <= Math.Min(h - 1, y + reach) && !near; yy++)
                    for (int xx = Math.Max(0, x - reach); xx <= Math.Min(w - 1, x + reach) && !near; xx++)
                        near = isDark[yy * w + xx];
                if (near) o.Px[y * w + x] = Pixel.Black;
            }
        return o;
    });

    /// <summary>Dark text on light, from light text on dark.</summary>
    public static readonly OcrPreprocessor Invert = new("invert", f => ImageOps.Map(f, p => (~p & 0x00ffffff) | Pixel.Black));

    public static readonly OcrPreprocessor Grayscale = new("gray", f => ImageOps.Map(f, p =>
    {
        int v = (Pixel.R(p) * 299 + Pixel.G(p) * 587 + Pixel.B(p) * 114) / 1000;
        return Pixel.Rgb(v, v, v);
    }));

    /// <summary>Pixels whose brightest channel reaches <paramref name="level"/> become black text on white.</summary>
    public static OcrPreprocessor BrightText(int level) => new($"bright:{level}", f => ImageOps.Map(f, p => Pixel.Max(p) >= level ? Pixel.Black : Pixel.White));

    /// <summary>
    /// The opposite: pixels whose brightest channel stays below <paramref name="level"/> become black on white.
    /// For faint grey lettering on a pale panel (greyed-out, disabled or sold entries), which reads poorly as is.
    /// </summary>
    public static OcrPreprocessor DarkText(int level) => new($"dark:{level}", f => ImageOps.Map(f, p => Pixel.Max(p) < level ? Pixel.Black : Pixel.White));

    /// <summary>
    /// The game's chat box: a translucent panel that dims the world behind it while the letters stay bright.
    /// Bright glyphs become black on white. Rows that look like megaphone banners (colored backgrounds
    /// with dark lettering) are left untouched, since thresholding would erase them.
    /// Ported from MapleMemory's PrepareChat. The game's own lines ("Red Potion x5 earned.", "... has been lost.")
    /// are grey, about 190 bright, so over the usual dim panel anything well above the row's background counts
    /// as a glyph (from 170). Over a bright scene the cutoff stays at 220, where it always was.
    /// </summary>
    public static readonly OcrPreprocessor ChatGlyphs = new("chat-glyphs", f =>
    {
        var o = f.Clone();
        Span<int> hist = stackalloc int[256];
        for (int y = 0; y < o.H; y++)
        {
            int brightBackground = 0;
            hist.Clear();
            for (int x = 0; x < o.W; x++)
            {
                int p = o.At(x, y);
                hist[Pixel.Max(p)]++;
                if (Pixel.Max(p) > 180 && Pixel.Chroma(p) > 65) brightBackground++;
            }
            int cutoff = ChatCutoff(hist, o.W);
            int bannerEdges = 0;
            foreach (int x in new[] { 0, 1, Math.Max(0, o.W - 2), o.W - 1 })
            {
                int p = o.At(x, y);
                if (Pixel.Max(p) > 180 && Pixel.Chroma(p) > 35) bannerEdges++;
            }
            if (brightBackground > o.W * .45 || bannerEdges >= 3) continue;
            for (int x = 0; x < o.W; x++)
                o.Px[y * o.W + x] = Pixel.Max(o.At(x, y)) >= cutoff ? Pixel.Black : Pixel.White;
        }
        return o;
    });

    /// <summary>
    /// How bright a chat pixel must be to count as a glyph: 60 above the row's background (its median, since letters
    /// cover little of a row), at least 170 and at most 220.
    /// </summary>
    internal static int ChatCutoff(ReadOnlySpan<int> hist, int count)
    {
        int median = 0;
        for (int seen = 0; median < 255; median++)
            if ((seen += hist[median]) * 2 >= count) break;
        return Math.Clamp(median + 60, 170, 220);
    }
}
