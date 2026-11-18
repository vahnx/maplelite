using MapleLite.Core.Imaging;

namespace MapleLite.Plugins.SpawnTimer;

/// <summary>A monster fading in: where its feet are (map pixels) and when.</summary>
public readonly record struct FadeIn(double T, double MapX, double MapY);

/// <summary>
/// Spots monsters as they spawn. A new monster fades in where it stands over about half a second, so the same place
/// on the map goes from steady background, through in-between pictures, to something with dark outlines. Drifting
/// clouds also change gradually but are soft and bright; monsters walking in change abruptly. Only looked for while
/// the view is still (moving background layers look like gradual change everywhere). Works at quarter size on a few
/// pictures a second.
/// </summary>
public sealed class SpawnWatcher
{
    const int S = 4;
    // in pictures (one every ~0.2 s): before = 0.8 s ahead of the middle, after = 0.4 s past it, steady for 1 s before
    const int Before = 4, Mid = 3, After = 2, Steady = 5;
    const double Spacing = 0.18;

    sealed record Snap(double T, byte[] G, double CamX, double CamY);
    readonly List<Snap> _snaps = new();
    readonly List<FadeIn> _recent = new();
    int _w, _h;

    public int MinChange { get; set; } = 30;      // brightness change from before to after
    public int MinArea { get; set; } = 20;        // quarter-size pixels (about 320 full-size)
    public int MaxSize { get; set; } = 30;        // quarter-size pixels across (120 full-size)
    public int DarkLevel { get; set; } = 70;      // outline pixels are darker than this
    public int MinDark { get; set; } = 4;
    public int IgnoreBottom { get; set; } = 70;
    public (int W, int H) IgnoreTopLeft { get; set; } = (210, 205);

    public void Reset() { _snaps.Clear(); _recent.Clear(); }

    /// <summary>Adds a picture (taken at <paramref name="t"/> seconds, with the camera at camX, camY). Returns monsters that just finished fading in.</summary>
    public List<FadeIn> Add(Frame f, double t, double camX, double camY)
    {
        var found = new List<FadeIn>();
        if (_snaps.Count > 0 && t - _snaps[^1].T < Spacing) return found;
        var g = Gray(f, out int w, out int h);
        if (w != _w || h != _h) { _snaps.Clear(); _w = w; _h = h; }
        _snaps.Add(new Snap(t, g, camX, camY));
        int need = Steady + Before + After + 1;
        if (_snaps.Count > need) _snaps.RemoveAt(0);
        if (_snaps.Count < need) return found;
        int i = _snaps.Count - 1 - After;
        var mid = _snaps[i];
        // only while the view holds still across the whole stretch
        foreach (var s in _snaps)
            if (Math.Abs(s.CamX - mid.CamX) > 2 || Math.Abs(s.CamY - mid.CamY) > 2) return found;
        _recent.RemoveAll(e => t - e.T > 2);

        int At(int k, int x, int y)
        {
            var s = _snaps[k];
            int xx = x + (int)Math.Round((mid.CamX - s.CamX) / S), yy = y + (int)Math.Round((mid.CamY - s.CamY) / S);
            return xx < 0 || yy < 0 || xx >= w || yy >= h ? -1 : s.G[yy * w + xx];
        }

        int b0 = i - Before, a0 = i + After, bottom = h - IgnoreBottom / S - 2, ilw = IgnoreTopLeft.W / S, ilh = IgnoreTopLeft.H / S;
        var flag = new bool[w * h];
        for (int y = 2; y < bottom; y++)
            for (int x = 2; x < w - 2; x++)
            {
                if (x < ilw && y < ilh) continue;
                int b = At(b0, x, y), b2 = At(b0 - 1, x, y), b3 = At(0, x, y), a = At(a0, x, y);
                if (b < 0 || b2 < 0 || b3 < 0 || a < 0) continue;
                int d = a - b;
                if (Math.Abs(d) < MinChange || Math.Abs(b - b2) > 8 || Math.Abs(b - b3) > 8) continue;
                bool between = false;
                for (int k = i - Mid; k <= i && !between; k++)
                {
                    int m = At(k, x, y);
                    if (m < 0) continue;
                    double frac = (m - b) / (double)d;
                    between = frac > 0.2 && frac < 0.8;
                }
                int a1 = At(a0 - 1, x, y);
                if (between && a1 >= 0 && Math.Abs(a1 - a) < Math.Abs(d) / 3) flag[y * w + x] = true;
            }

        var seen = new bool[w * h];
        var stack = new Stack<int>();
        for (int p = 0; p < flag.Length; p++)
        {
            if (!flag[p] || seen[p]) continue;
            int area = 0, x0 = w, x1 = 0, y0 = h, y1 = 0;
            stack.Push(p); seen[p] = true;
            while (stack.Count > 0)
            {
                int q = stack.Pop(), qx = q % w, qy = q / w;
                area++;
                x0 = Math.Min(x0, qx); x1 = Math.Max(x1, qx); y0 = Math.Min(y0, qy); y1 = Math.Max(y1, qy);
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int rx = qx + dx, ry = qy + dy, r = ry * w + rx;
                        if (rx < 0 || ry < 0 || rx >= w || ry >= h || seen[r] || !flag[r]) continue;
                        seen[r] = true; stack.Push(r);
                    }
            }
            if (area < MinArea || x1 - x0 + 1 > MaxSize || y1 - y0 + 1 > MaxSize) continue;
            int dark = 0;
            for (int yy = y0; yy <= y1; yy++)
                for (int xx = x0; xx <= x1; xx++)
                {
                    int av = At(a0, xx, yy), bv = At(b0, xx, yy);
                    if (av >= 0 && av < DarkLevel && bv >= DarkLevel + 15) dark++;
                }
            if (dark < MinDark) continue;
            double mx = (x0 + x1 + 1) / 2.0 * S + mid.CamX, my = (y1 + 1) * S + mid.CamY;
            if (_recent.Any(e => Math.Abs(e.MapX - mx) < 40 && Math.Abs(e.MapY - my) < 40)) continue;
            var fade = new FadeIn(mid.T, mx, my);
            _recent.Add(fade);
            found.Add(fade);
        }
        return found;
    }

    static int Luma(int p) => (((p >> 16) & 255) * 3 + ((p >> 8) & 255) * 6 + (p & 255)) / 10;

    static byte[] Gray(Frame f, out int w, out int h)
    {
        w = f.W / S; h = f.H / S;
        var g = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int s = 0;
                for (int j = 0; j < S; j += 2)
                    for (int i = 0; i < S; i += 2) s += Luma(f.Px[(y * S + j) * f.W + x * S + i]);
                g[y * w + x] = (byte)(s / 4);
            }
        return g;
    }
}
