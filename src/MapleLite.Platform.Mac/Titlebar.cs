using System.Collections.Concurrent;
using MapleLite.Core.Imaging;

namespace MapleLite.Platform.Mac;

/// <summary>
/// How tall the game window's title bar is, in pixels.
///
/// macOS reports another app's window frame including its title bar, and has no public way to ask for the content
/// area without Accessibility permission. So the capture looks at the picture: a title bar is a band of flat gray
/// across the top (sampled left and right of the centered title, past the traffic lights) that ends sharply
/// 20–40 points down. Once seen, the answer is kept for that window at that size. Until then a framed window is
/// assumed to have the standard 28-point bar, and a fullscreen one none.
/// </summary>
internal static class Titlebar
{
    const double StandardPoints = 28;
    static readonly ConcurrentDictionary<(uint id, int w, int h), int> Known = new();

    public static int Height(uint id, int w, int h, double scale, bool fullscreen)
    {
        if (fullscreen) return 0;
        return Known.TryGetValue((id, w, h), out var px) ? px : (int)Math.Round(StandardPoints * scale);
    }

    /// <summary>Checks a fresh capture of the whole window and returns the title bar height to cut off.</summary>
    public static int Resolve(uint id, Frame whole, double scale, bool fullscreen)
    {
        var key = (id, whole.W, whole.H);
        if (Known.TryGetValue(key, out var px)) return px;
        var found = Detect(whole, scale);
        if (found is { } f) { Known[key] = f; return f; }
        return Height(id, whole.W, whole.H, scale, fullscreen);
    }

    /// <summary>Null when the picture can't tell (for example the top of the game is a flat color too).</summary>
    internal static int? Detect(Frame f, double scale)
    {
        int maxBand = (int)Math.Ceiling(44 * scale);
        if (f.H < maxBand * 4 || f.W < 300 * scale) return null;

        var cols = new List<int>();
        void Span(int x0, int x1) { int step = Math.Max(1, (x1 - x0) / 10); for (int x = x0; x < x1; x += step) cols.Add(x); }
        Span((int)(90 * scale), (int)(f.W * 0.28));
        Span((int)(f.W * 0.72), f.W - (int)(14 * scale));
        if (cols.Count < 8) return null;

        int y0 = Math.Max(1, (int)(2 * scale)); // skip the 1px highlight along the very top edge
        var first = Uniform(f, y0, cols);
        if (first is not { } prev || !Grayish(prev)) return 0; // busy or colorful top row: the game draws there

        for (int y = y0 + 1; y < maxBand; y++)
        {
            var row = Uniform(f, y, cols);
            if (row is { } c && Grayish(c) && Close(c, prev, 10)) { prev = c; continue; }
            // A flat 1px separator line can sit under the bar: count it as bar if the row after it is different.
            int band = y;
            if (row is { } sep && Grayish(sep) && !(Uniform(f, y + 1, cols) is { } next && Close(next, sep, 10))) band = y + 1;
            double points = band / scale;
            if (points >= 18 && points <= 40) return band;
            return points < 18 ? 0 : null;
        }
        return null; // flat all the way down: a loading screen or a flat game area, decide later
    }

    /// <summary>The row's color when every sample is within a small range of the first, else null.</summary>
    static (int r, int g, int b)? Uniform(Frame f, int y, List<int> cols)
    {
        if (y >= f.H) return null;
        int p0 = f.At(cols[0], y);
        long r = 0, g = 0, b = 0;
        foreach (var x in cols)
        {
            int p = f.At(x, y);
            if (Math.Abs(Pixel.R(p) - Pixel.R(p0)) > 14 || Math.Abs(Pixel.G(p) - Pixel.G(p0)) > 14 || Math.Abs(Pixel.B(p) - Pixel.B(p0)) > 14) return null;
            r += Pixel.R(p); g += Pixel.G(p); b += Pixel.B(p);
        }
        return ((int)(r / cols.Count), (int)(g / cols.Count), (int)(b / cols.Count));
    }

    static bool Grayish((int r, int g, int b) c) =>
        Math.Max(c.r, Math.Max(c.g, c.b)) - Math.Min(c.r, Math.Min(c.g, c.b)) <= 20;

    static bool Close((int r, int g, int b) a, (int r, int g, int b) b, int tol) =>
        Math.Abs(a.r - b.r) <= tol && Math.Abs(a.g - b.g) <= tol && Math.Abs(a.b - b.b) <= tol;
}
