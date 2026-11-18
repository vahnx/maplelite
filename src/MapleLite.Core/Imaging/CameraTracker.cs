namespace MapleLite.Core.Imaging;

/// <summary>
/// Follows the game camera from picture to picture, so a spot on the map can be found again after the view scrolls.
/// <see cref="X"/>,<see cref="Y"/> is how far the camera has moved since tracking began, in game pixels: a map point
/// seen at screen (sx, sy) is at map (sx + X, sy + Y), and is drawn at screen (mx − X, my − Y) later.
/// <para>How: many small textured patches are matched against a keyframe (at quarter size, then refined at full size);
/// the most common shift wins. The ground and platforms move with the camera and carry most of the texture, so the
/// vote follows them, not the slower background layers, the UI or moving characters. Matching against a keyframe
/// rather than the previous picture keeps small errors from adding up; a new keyframe is taken once the view has
/// moved about a quarter of the screen.</para>
/// </summary>
public sealed class CameraTracker
{
    const int S = 4;          // quarter size for the coarse search
    const int Patch = 12;     // patch size at quarter size (48 px)
    const int Range = 10;     // wide search ± around the last shift, at quarter size (± 40 px)
    const int NearRange = 3;       // first, a narrow search ± around where the motion so far predicts (± 12 px): much cheaper
    const int Fine = 3;       // full-size refinement ± px

    byte[]? _key;
    int[]? _keyFull;
    int _w, _h, _fw, _fh;
    double _keyX, _keyY;      // camera position when the keyframe was taken
    int _offX, _offY;         // last shift from the keyframe, full pixels
    int _velX, _velY;         // how far the last picture moved from the one before, full pixels

    public double X { get; private set; }
    public double Y { get; private set; }
    /// <summary>False after a picture where no shift could be agreed on (a loading screen, a map change, a big jump).</summary>
    public bool Locked { get; private set; }
    /// <summary>How many times tracking was lost after being locked. Map positions from before a break don't carry over.</summary>
    public int Breaks { get; private set; }

    /// <summary>Areas never used for matching (UI that stays put): the minimap corner and the bottom bars.</summary>
    public int IgnoreBottom { get; set; } = 70;
    public (int W, int H) IgnoreTopLeft { get; set; } = (210, 205);

    /// <summary>How much fine detail a patch needs to be used: the average brightness step between neighbouring quarter-size pixels.</summary>
    public int Sharpness { get; set; } = 12;

    public void Reset() { _key = null; _keyFull = null; X = Y = 0; _keyX = _keyY = 0; _offX = _offY = 0; _velX = _velY = 0; Locked = false; }

    /// <summary>Adds the next picture. Returns false when it couldn't tell how far the camera moved.</summary>
    public bool Add(Frame f)
    {
        var g = Gray(f, out int w, out int h);
        if (_key == null || w != _w || h != _h || f.W != _fw || f.H != _fh)
        {
            TakeKey(g, f, w, h);
            return false;
        }
        var guess = (_offX + _velX, _offY + _velY);
        var near = Locked ? Estimate(g, f, guess, NearRange) : null; // usually enough, and about 4× cheaper than the wide search
        if ((near ?? Estimate(g, f, (_offX, _offY), Range)) is { } off)
        {
            _velX = off.Item1 - _offX; _velY = off.Item2 - _offY;
            _offX = off.Item1; _offY = off.Item2;
            X = _keyX + _offX; Y = _keyY + _offY;
            Locked = true;
            if (Math.Abs(_offX) > f.W / 4 || Math.Abs(_offY) > f.H / 4) TakeKey(g, f, w, h);
            return true;
        }
        if (Locked) Breaks++;
        Locked = false; _velX = _velY = 0;
        TakeKey(g, f, w, h); // start over from here; the position keeps counting from where it was
        return false;
    }

    void TakeKey(byte[] g, Frame f, int w, int h)
    {
        _key = g; _keyFull = f.Px; _w = w; _h = h; _fw = f.W; _fh = f.H;
        _keyX = X; _keyY = Y; _offX = _offY = 0;
    }

    (int, int)? Estimate(byte[] g, Frame f, (int X, int Y) around, int range)
    {
        int w = _w, h = _h;
        var key = _key!;
        int pdx = (int)Math.Round(around.X / (double)S), pdy = (int)Math.Round(around.Y / (double)S);
        var votes = new Dictionary<(int, int), int>();
        var patches = new List<(int px, int py, int dx, int dy)>();
        int bottom = h - IgnoreBottom / S, ilw = IgnoreTopLeft.W / S, ilh = IgnoreTopLeft.H / S;
        for (int py = 2; py + Patch + 2 < bottom; py += Patch + 3)
            for (int px = 2; px + Patch + 2 < w; px += Patch + 3)
            {
                if (px < ilw && py < ilh) continue;
                // Only sharp detail (grass, tiles, outlines). Sky matches anywhere, and soft drifting clouds would pull
                // the vote along with them while the camera stands still.
                int edges = 0;
                for (int j = 0; j < Patch - 1; j++)
                    for (int i = 0; i < Patch - 1; i++)
                    {
                        int a = (py + j) * w + px + i;
                        edges += Math.Abs(g[a] - g[a + 1]) + Math.Abs(g[a] - g[a + w]);
                    }
                if (edges < Sharpness * (Patch - 1) * (Patch - 1)) continue;
                int best = int.MaxValue, bx = 0, by = 0;
                for (int dy = pdy - range; dy <= pdy + range; dy++)
                {
                    if (py + dy < 0 || py + dy + Patch > bottom) continue;
                    for (int dx = pdx - range; dx <= pdx + range; dx++)
                    {
                        if (px + dx < 0 || px + dx + Patch > w) continue;
                        int sad = 0;
                        for (int j = 0; j < Patch && sad < best; j++)
                        {
                            int a = (py + j) * w + px, b = (py + j + dy) * w + px + dx;
                            for (int i = 0; i < Patch; i++) sad += Math.Abs(g[a + i] - key[b + i]);
                        }
                        if (sad < best) { best = sad; bx = dx; by = dy; }
                    }
                }
                if (best > 12 * Patch * Patch) continue; // nothing in the keyframe looks like it
                votes[(bx, by)] = votes.GetValueOrDefault((bx, by)) + 1;
                patches.Add((px, py, bx, by));
            }
        if (votes.Count == 0) return null;
        // neighbours of the winning shift vote with it (a true shift between two quarter-pixels splits its votes)
        int Near((int, int) k) => votes.GetValueOrDefault(k) + votes.GetValueOrDefault((k.Item1 + 1, k.Item2)) + votes.GetValueOrDefault((k.Item1 - 1, k.Item2))
            + votes.GetValueOrDefault((k.Item1, k.Item2 + 1)) + votes.GetValueOrDefault((k.Item1, k.Item2 - 1));
        var win = votes.Keys.MaxBy(Near);
        var agree = patches.Where(p => Math.Abs(p.dx - win.Item1) <= 1 && Math.Abs(p.dy - win.Item2) <= 1).ToList();
        if (agree.Count < 4 || agree.Count < patches.Count / 4) return null;
        return Refine(f, agree, win.Item1 * S, win.Item2 * S);
    }

    // The coarse shift is a multiple of 4: settle the last few pixels at full size on the patches that agreed.
    (int, int) Refine(Frame f, List<(int px, int py, int dx, int dy)> agree, int cx, int cy)
    {
        var key = _keyFull!;
        int W = f.W, H = f.H, size = Patch * S / 2, n = 2 * (Fine + S) + 1;
        var score = new long[n * n];
        int reach = Fine + S;
        foreach (var p in agree.Take(24))
        {
            int x0 = p.px * S + size / 2, y0 = p.py * S + size / 2;
            for (int dy = -reach; dy <= reach; dy++)
                for (int dx = -reach; dx <= reach; dx++)
                {
                    int sx = cx + dx, sy = cy + dy, k = (dy + reach) * n + dx + reach;
                    if (x0 + sx < 0 || y0 + sy < 0 || x0 + size + sx > W || y0 + size + sy > H) { score[k] += 1L << 40; continue; }
                    long sad = 0;
                    for (int j = 0; j < size; j += 2)
                    {
                        int a = (y0 + j) * W + x0, b = (y0 + j + sy) * W + x0 + sx;
                        for (int i = 0; i < size; i += 2) { int d = Luma(f.Px[a + i]) - Luma(key[b + i]); sad += d < 0 ? -d : d; }
                    }
                    score[k] += sad;
                }
        }
        int best = Array.IndexOf(score, score.Min());
        return (cx + best % n - reach, cy + best / n - reach);
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
