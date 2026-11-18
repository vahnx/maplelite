namespace MapleLite.Core.Imaging;

/// <summary>Pure C# picture operations, so preprocessing gives the same pixels on every OS.</summary>
public static class ImageOps
{
    /// <summary>
    /// Enlarges a frame by a whole factor, with an optional margin of repeated edge pixels around it.
    /// Smooth (bilinear) suits the game's anti-aliased text; nearest keeps hard pixel fonts crisp.
    /// </summary>
    public static Frame Scale(Frame src, int scale, bool smooth, int pad = 0)
    {
        if (scale < 1) throw new ArgumentOutOfRangeException(nameof(scale));
        if (scale == 1 && pad == 0) return src;
        int w = src.W, h = src.H;
        int ow = (w + 2 * pad) * scale, oh = (h + 2 * pad) * scale;
        var dst = new Frame(ow, oh);
        var o = dst.Px;
        var s = src.Px;
        for (int y = 0; y < oh; y++)
        {
            for (int x = 0; x < ow; x++)
            {
                int value;
                if (!smooth || w < 2 || h < 2)
                {
                    int sx = Math.Clamp(x / scale - pad, 0, w - 1), sy = Math.Clamp(y / scale - pad, 0, h - 1);
                    value = s[sy * w + sx];
                }
                else
                {
                    double fx0 = Math.Clamp((x + 0.5) / scale - 0.5 - pad, 0, w - 1);
                    double fy0 = Math.Clamp((y + 0.5) / scale - 0.5 - pad, 0, h - 1);
                    int ix = Math.Min(w - 2, (int)fx0), iy = Math.Min(h - 2, (int)fy0);
                    double fx = fx0 - ix, fy = fy0 - iy;
                    int a = s[iy * w + ix], b = s[iy * w + ix + 1], c = s[(iy + 1) * w + ix], d = s[(iy + 1) * w + ix + 1];
                    int rgb = 0;
                    for (int sh = 0; sh < 24; sh += 8)
                    {
                        double v = ((a >> sh) & 255) * (1 - fx) * (1 - fy) + ((b >> sh) & 255) * fx * (1 - fy)
                                 + ((c >> sh) & 255) * (1 - fx) * fy + ((d >> sh) & 255) * fx * fy;
                        rgb |= (int)Math.Clamp(v + 0.5, 0, 255) << sh;
                    }
                    value = rgb;
                }
                o[y * ow + x] = value | Pixel.Black;
            }
        }
        return dst;
    }

    /// <summary>
    /// Enlarges with bicubic (Catmull-Rom) interpolation, plus an optional margin of repeated edge pixels.
    /// Sharper letter edges than bilinear; this matches the HighQualityBicubic resize the MapleMemory app
    /// used before OCR closely enough for the same reads.
    /// </summary>
    public static Frame ScaleBicubic(Frame src, int scale, int pad = 0)
    {
        if (scale < 1) throw new ArgumentOutOfRangeException(nameof(scale));
        if (scale == 1 && pad == 0) return src;
        int w = src.W, h = src.H, ow = (w + 2 * pad) * scale, oh = (h + 2 * pad) * scale;
        var s = src.Px;
        var dst = new Frame(ow, oh);
        // Weights depend only on the sub-pixel phase, which repeats every `scale` pixels.
        var wts = new double[scale, 4];
        for (int k = 0; k < scale; k++)
        {
            double t = (k + 0.5) / scale - 0.5;
            t -= Math.Floor(t);
            for (int j = 0; j < 4; j++) wts[k, j] = CatmullRom(j - 1 - t);
        }
        var row = new double[ow * 3]; // horizontal pass result for one source row, per output column
        var rows = new double[4][];
        for (int i = 0; i < 4; i++) rows[i] = new double[ow * 3];
        int cachedY = int.MinValue;
        double[] Horizontal(int sy, double[] into)
        {
            sy = Math.Clamp(sy, 0, h - 1);
            for (int x = 0; x < ow; x++)
            {
                double fx = (x + 0.5) / scale - 0.5 - pad;
                int ix = (int)Math.Floor(fx);
                int k = ((x % scale) + scale) % scale;
                double r = 0, g = 0, b = 0;
                for (int j = 0; j < 4; j++)
                {
                    int p = s[sy * w + Math.Clamp(ix - 1 + j, 0, w - 1)];
                    double wt = wts[k, j];
                    r += Pixel.R(p) * wt; g += Pixel.G(p) * wt; b += Pixel.B(p) * wt;
                }
                into[x * 3] = r; into[x * 3 + 1] = g; into[x * 3 + 2] = b;
            }
            return into;
        }
        for (int y = 0; y < oh; y++)
        {
            double fy = (y + 0.5) / scale - 0.5 - pad;
            int iy = (int)Math.Floor(fy);
            int k = ((y % scale) + scale) % scale;
            if (iy != cachedY)
            {
                for (int j = 0; j < 4; j++) Horizontal(iy - 1 + j, rows[j]);
                cachedY = iy;
            }
            for (int x = 0; x < ow; x++)
            {
                double r = 0, g = 0, b = 0;
                for (int j = 0; j < 4; j++) { double wt = wts[k, j]; r += rows[j][x * 3] * wt; g += rows[j][x * 3 + 1] * wt; b += rows[j][x * 3 + 2] * wt; }
                dst.Px[y * ow + x] = Pixel.Rgb(Clamp(r), Clamp(g), Clamp(b));
            }
        }
        _ = row;
        return dst;

        static int Clamp(double v) => (int)Math.Clamp(v + 0.5, 0, 255);
        static double CatmullRom(double x)
        {
            x = Math.Abs(x);
            return x < 1 ? 1.5 * x * x * x - 2.5 * x * x + 1 : x < 2 ? -0.5 * x * x * x + 2.5 * x * x - 4 * x + 2 : 0;
        }
    }

    /// <summary>A copy with every pixel passed through <paramref name="map"/>.</summary>
    public static Frame Map(Frame src, Func<int, int> map)
    {
        var dst = new Frame(src.W, src.H);
        for (int i = 0; i < src.Px.Length; i++) dst.Px[i] = map(src.Px[i]);
        return dst;
    }

    /// <summary>
    /// How many characters are drawn in a region of one text line: runs of columns holding ink. Lets a read be checked
    /// against the picture (an OCR result that dropped or invented a character has a different count). Characters that
    /// touch count as one, so compare against fonts with spacing, such as the game's.
    /// </summary>
    public static int CountInkRuns(Frame f, Region region, Func<int, bool> isInk)
    {
        var r = region.Intersect(f.Bounds);
        int count = 0; bool previous = false;
        for (int x = r.X; x < r.Right; x++)
        {
            bool ink = false;
            for (int y = r.Y; y < r.Bottom && !ink; y++) ink = isInk(f.At(x, y));
            if (ink && !previous) count++;
            previous = ink;
        }
        return count;
    }

    /// <summary>
    /// The part of a picture left after trimming plain bands off its sides: from each side, columns (and with
    /// <paramref name="topBottom"/>, rows) that look like the outermost one, row by row, within <paramref name="tolerance"/>
    /// per channel for at least <paramref name="share"/> of their pixels. Comparing with the edge row by row, instead of
    /// asking for one flat colour, trims a sky that shades from top to bottom too. Never trims below a quarter of the size.
    /// </summary>
    public static Region TrimPlain(Frame f, int tolerance = 18, double share = 0.92, bool leftRight = true, bool topBottom = false)
    {
        bool Close(int a, int b) => Math.Abs(Pixel.R(a) - Pixel.R(b)) <= tolerance && Math.Abs(Pixel.G(a) - Pixel.G(b)) <= tolerance && Math.Abs(Pixel.B(a) - Pixel.B(b)) <= tolerance;
        bool ColumnLike(int x, int edge, int y0, int y1)
        {
            int same = 0;
            for (int y = y0; y < y1; y++) if (Close(f.At(x, y), f.At(edge, y))) same++;
            return same >= (y1 - y0) * share;
        }
        bool RowLike(int y, int edge, int x0, int x1)
        {
            int same = 0;
            for (int x = x0; x < x1; x++) if (Close(f.At(x, y), f.At(x, edge))) same++;
            return same >= (x1 - x0) * share;
        }
        // a band counts from 3 px: an edge that merely looks like itself is not a plain band
        const int MinBand = 3;
        int left = 0, right = f.W - 1, top = 0, bottom = f.H - 1;
        int minW = Math.Max(1, f.W / 4), minH = Math.Max(1, f.H / 4);
        if (topBottom)
        {
            int n = 0; while (f.H - n > minH && RowLike(n, 0, 0, f.W)) n++;
            if (n >= MinBand) top = n;
            n = 0; while (bottom - top + 1 - n > minH && RowLike(f.H - 1 - n, f.H - 1, 0, f.W)) n++;
            if (n >= MinBand) bottom = f.H - 1 - n;
        }
        if (leftRight)
        {
            int n = 0; while (f.W - n > minW && ColumnLike(n, 0, top, bottom + 1)) n++;
            if (n >= MinBand) left = n;
            n = 0; while (right - left + 1 - n > minW && ColumnLike(f.W - 1 - n, f.W - 1, top, bottom + 1)) n++;
            if (n >= MinBand) right = f.W - 1 - n;
        }
        return new Region(left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>
    /// The game's picture inside a window that is bigger than it: in borderless fullscreen on a monitor of another
    /// shape, the game draws black bars above and below (or left and right). Bars count only when both opposite
    /// edges have them, and the thinner side sets both, so a dark sky at the top of a map can't pass for a bar.
    /// Returns <see cref="Frame.Bounds"/> when there are none.
    /// </summary>
    public static Region Letterbox(Frame f, int minBar = 8)
    {
        if (f.W < 200 || f.H < 200 || LooksBlack(f)) return f.Bounds;
        bool Row(int y) { int o = y * f.W; for (int x = 0; x < f.W; x += 3) if (Pixel.Max(f.Px[o + x]) > 3) return false; return true; }
        bool Col(int x) { for (int y = 0; y < f.H; y += 3) if (Pixel.Max(f.Px[y * f.W + x]) > 3) return false; return true; }
        int top = 0, bottom = 0, left = 0, right = 0;
        while (top < f.H / 4 && Row(top)) top++;
        while (bottom < f.H / 4 && Row(f.H - 1 - bottom)) bottom++;
        while (left < f.W / 4 && Col(left)) left++;
        while (right < f.W / 4 && Col(f.W - 1 - right)) right++;
        int v = Math.Min(top, bottom), h = Math.Min(left, right);
        if (v < minBar) v = 0;
        if (h < minBar) h = 0;
        if (v == 0 && h == 0) return f.Bounds;
        // bars of odd total split unevenly (52 above, 53 below): a side one thicker than the other keeps its own size;
        // one much thicker has dark scenery past its bar, so it gets the thinner side's
        static int Side(int own, int thin) => thin == 0 ? 0 : own <= thin + 1 ? own : thin;
        int t = Side(top, v), b = Side(bottom, v), l = Side(left, h), r = Side(right, h);
        return new Region(l, t, f.W - l - r, f.H - t - b);
    }

    /// <summary>True when nearly every sampled pixel is black: what blocked or failed captures look like.</summary>
    public static bool LooksBlack(Frame f, int samples = 400)
    {
        int step = Math.Max(1, f.Px.Length / samples), dark = 0, seen = 0;
        for (int i = 0; i < f.Px.Length; i += step) { seen++; if (Pixel.Max(f.Px[i]) < 8) dark++; }
        return seen > 0 && dark >= seen * 0.995;
    }
}
