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

    /// <summary>A copy with every pixel passed through <paramref name="map"/>.</summary>
    public static Frame Map(Frame src, Func<int, int> map)
    {
        var dst = new Frame(src.W, src.H);
        for (int i = 0; i < src.Px.Length; i++) dst.Px[i] = map(src.Px[i]);
        return dst;
    }

    /// <summary>True when nearly every sampled pixel is black: what blocked or failed captures look like.</summary>
    public static bool LooksBlack(Frame f, int samples = 400)
    {
        int step = Math.Max(1, f.Px.Length / samples), dark = 0, seen = 0;
        for (int i = 0; i < f.Px.Length; i += step) { seen++; if (Pixel.Max(f.Px[i]) < 8) dark++; }
        return seen > 0 && dark >= seen * 0.995;
    }
}
