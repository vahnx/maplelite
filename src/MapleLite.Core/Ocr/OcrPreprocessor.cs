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
    /// The game's chat box: a translucent panel that dims the world behind it while the letters stay bright.
    /// Bright glyphs become black on white. Rows that look like megaphone banners (colored backgrounds
    /// with dark lettering) are left untouched, since thresholding would erase them.
    /// Ported from MapleMemory's PrepareChat.
    /// </summary>
    public static readonly OcrPreprocessor ChatGlyphs = new("chat-glyphs", f =>
    {
        var o = f.Clone();
        for (int y = 0; y < o.H; y++)
        {
            int brightBackground = 0;
            for (int x = 0; x < o.W; x++)
            {
                int p = o.At(x, y);
                if (Pixel.Max(p) > 180 && Pixel.Chroma(p) > 65) brightBackground++;
            }
            int bannerEdges = 0;
            foreach (int x in new[] { 0, 1, Math.Max(0, o.W - 2), o.W - 1 })
            {
                int p = o.At(x, y);
                if (Pixel.Max(p) > 180 && Pixel.Chroma(p) > 35) bannerEdges++;
            }
            if (brightBackground > o.W * .45 || bannerEdges >= 3) continue;
            for (int x = 0; x < o.W; x++)
                o.Px[y * o.W + x] = Pixel.Max(o.At(x, y)) >= 220 ? Pixel.Black : Pixel.White;
        }
        return o;
    });
}
