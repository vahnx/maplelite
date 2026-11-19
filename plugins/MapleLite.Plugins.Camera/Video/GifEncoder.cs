using MapleLite.Core.Imaging;

namespace MapleLite.Plugins.Camera.Video;

/// <summary>
/// Writes animated GIFs, with no outside tools. Each frame gets its own 255-color palette (median cut), and only the
/// part of the picture that changed is stored; pixels that didn't change are left transparent so the previous frame
/// shows through. Game scenes with a still background shrink a lot that way. GIF is limited to 256 colors per frame,
/// so smooth gradients band a little; light ordered dithering (on by default) hides most of it.
/// </summary>
public sealed class GifEncoder : IDisposable
{
    const int Transparent = 255; // palette slot kept for "unchanged"
    readonly Stream _out;
    readonly int _w, _h;
    readonly bool _dither;
    readonly int _tolerance;
    readonly int[] _shown; // the source color each pixel had when it was last drawn
    bool _first = true, _done;
    byte[]? _pending; int _pendingDelay;

    /// <param name="tolerance">How much a pixel may change (sum of R, G, B differences) and still count as unchanged.
    /// A little tolerance skips JPEG noise; 0 redraws every change.</param>
    public GifEncoder(Stream output, int width, int height, bool loop = true, bool dither = true, int tolerance = 9)
    {
        _out = output; _w = width; _h = height; _dither = dither; _tolerance = tolerance;
        _shown = new int[width * height];
        var head = new BinaryWriter(output, System.Text.Encoding.ASCII, leaveOpen: true);
        head.Write("GIF89a"u8);
        head.Write((ushort)width); head.Write((ushort)height);
        head.Write((byte)0); head.Write((byte)0); head.Write((byte)0); // no global palette
        if (loop) { head.Write([0x21, 0xFF, 0x0B]); head.Write("NETSCAPE2.0"u8); head.Write([0x03, 0x01, 0x00, 0x00, 0x00]); }
        head.Flush();
    }

    public int Frames { get; private set; }

    /// <summary>Adds a picture shown for <paramref name="delayMs"/> (GIF counts in hundredths of a second).</summary>
    public void Add(Frame f, int delayMs)
    {
        if (f.W != _w || f.H != _h) throw new ArgumentException($"Every frame must be {_w}x{_h}, not {f.W}x{f.H}.");
        // Which pixels changed since they were last drawn?
        int x0 = _w, y0 = _h, x1 = -1, y1 = -1;
        if (_first) { x0 = 0; y0 = 0; x1 = _w - 1; y1 = _h - 1; }
        else
        {
            for (int y = 0; y < _h; y++)
            {
                int row = y * _w;
                for (int x = 0; x < _w; x++)
                {
                    if (Same(f.Px[row + x], _shown[row + x])) continue;
                    if (x < x0) x0 = x; if (x > x1) x1 = x;
                    if (y < y0) y0 = y; if (y > y1) y1 = y;
                }
            }
        }
        if (x1 < 0) { _pendingDelay += delayMs; return; } // nothing changed: the previous frame just stays longer

        Flush();
        int rw = x1 - x0 + 1, rh = y1 - y0 + 1;
        var changed = new bool[rw * rh];
        int changedCount = 0;
        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                int i = (y0 + y) * _w + x0 + x;
                if (_first || !Same(f.Px[i], _shown[i])) { changed[y * rw + x] = true; changedCount++; }
            }

        var palette = Quantizer.Palette(f, x0, y0, rw, rh, changed, 255);
        var map = new Quantizer.Mapper(palette);
        var indices = new byte[rw * rh];
        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                int k = y * rw + x, i = (y0 + y) * _w + x0 + x;
                if (!changed[k]) { indices[k] = Transparent; continue; }
                int p = f.Px[i];
                int d = _dither ? Bayer[(y & 3) * 4 + (x & 3)] : 0;
                indices[k] = (byte)map.Nearest(Clamp(Pixel.R(p) + d), Clamp(Pixel.G(p) + d), Clamp(Pixel.B(p) + d));
                _shown[i] = p;
            }

        var ms = new MemoryStream();
        // Graphic control: "leave in place" so the next frame draws over this one; transparency for unchanged pixels.
        // The delay is filled in when the next frame comes (it may stay up longer if nothing changes).
        ms.Write([0x21, 0xF9, 0x04, (1 << 2) | 1, 0, 0, Transparent, 0]);
        ms.WriteByte(0x2C);
        WriteU16(ms, x0); WriteU16(ms, y0); WriteU16(ms, rw); WriteU16(ms, rh);
        ms.WriteByte(0x80 | 7); // local palette, 256 entries
        for (int i = 0; i < 256; i++)
        {
            int c = i < palette.Length ? palette[i] : 0;
            ms.WriteByte((byte)Pixel.R(c)); ms.WriteByte((byte)Pixel.G(c)); ms.WriteByte((byte)Pixel.B(c));
        }
        Lzw.Encode(ms, indices, 8);
        _pending = ms.ToArray();
        _pendingDelay = delayMs;
        _first = false;
        Frames++;
    }

    void Flush()
    {
        if (_pending == null) return;
        int cs = Math.Clamp((int)Math.Round(_pendingDelay / 10.0), 2, 65535); // under 2/100 s most viewers slow down to 10/100 s
        _pending[4] = (byte)(cs & 255); _pending[5] = (byte)(cs >> 8);
        _out.Write(_pending);
        _pending = null;
    }

    bool Same(int a, int b) =>
        Math.Abs(Pixel.R(a) - Pixel.R(b)) + Math.Abs(Pixel.G(a) - Pixel.G(b)) + Math.Abs(Pixel.B(a) - Pixel.B(b)) <= _tolerance;

    static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
    static void WriteU16(Stream s, int v) { s.WriteByte((byte)(v & 255)); s.WriteByte((byte)(v >> 8)); }

    // A 4x4 ordered-dither matrix, spread over about ±4 levels (one 5-bit palette step).
    static readonly int[] Bayer = [-4, 0, -3, 1, 2, -2, 3, -1, -3, 1, -4, 0, 3, -1, 2, -2];

    public void Dispose()
    {
        if (_done) return;
        _done = true;
        Flush();
        _out.WriteByte(0x3B);
        _out.Flush();
    }
}

/// <summary>Median-cut color reduction over a 5-bit-per-channel histogram.</summary>
internal static class Quantizer
{
    public static int[] Palette(Frame f, int x0, int y0, int w, int h, bool[]? use, int max)
    {
        var count = new int[32768];
        var sumR = new long[32768]; var sumG = new long[32768]; var sumB = new long[32768];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (use != null && !use[y * w + x]) continue;
                int p = f.Px[(y0 + y) * f.W + x0 + x];
                int r = Pixel.R(p), g = Pixel.G(p), b = Pixel.B(p);
                int k = (r >> 3) << 10 | (g >> 3) << 5 | b >> 3;
                count[k]++; sumR[k] += r; sumG[k] += g; sumB[k] += b;
            }
        var keys = new List<int>();
        for (int k = 0; k < 32768; k++) if (count[k] > 0) keys.Add(k);
        if (keys.Count == 0) return [Pixel.Black];
        var arr = keys.ToArray();

        var boxes = new List<(int Start, int End)> { (0, arr.Length) };
        while (boxes.Count < max)
        {
            // split the box with the most pixels times its widest spread
            int best = -1; long bestScore = 0; int bestAxis = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                var (s, e) = boxes[i];
                if (e - s < 2) continue;
                int axis = WidestAxis(arr, s, e, out int range);
                long pixels = 0;
                for (int j = s; j < e; j++) pixels += count[arr[j]];
                long score = pixels * range;
                if (score > bestScore) { bestScore = score; best = i; bestAxis = axis; }
            }
            if (best < 0) break;
            var (bs, be) = boxes[best];
            int shift = bestAxis == 0 ? 10 : bestAxis == 1 ? 5 : 0;
            Array.Sort(arr, bs, be - bs, Comparer<int>.Create((a, b) => ((a >> shift) & 31).CompareTo((b >> shift) & 31)));
            long total = 0, half = 0;
            for (int j = bs; j < be; j++) total += count[arr[j]];
            int cut = bs + 1;
            for (int j = bs; j < be - 1; j++) { half += count[arr[j]]; if (half * 2 >= total) { cut = j + 1; break; } cut = j + 1; }
            boxes[best] = (bs, cut);
            boxes.Add((cut, be));
        }

        var palette = new int[boxes.Count];
        for (int i = 0; i < boxes.Count; i++)
        {
            long n = 0, r = 0, g = 0, b = 0;
            for (int j = boxes[i].Start; j < boxes[i].End; j++) { int k = arr[j]; n += count[k]; r += sumR[k]; g += sumG[k]; b += sumB[k]; }
            palette[i] = n == 0 ? Pixel.Black : Pixel.Rgb((int)(r / n), (int)(g / n), (int)(b / n));
        }
        return palette;
    }

    static int WidestAxis(int[] arr, int s, int e, out int range)
    {
        int rMin = 31, rMax = 0, gMin = 31, gMax = 0, bMin = 31, bMax = 0;
        for (int j = s; j < e; j++)
        {
            int k = arr[j], r = k >> 10, g = (k >> 5) & 31, b = k & 31;
            if (r < rMin) rMin = r; if (r > rMax) rMax = r;
            if (g < gMin) gMin = g; if (g > gMax) gMax = g;
            if (b < bMin) bMin = b; if (b > bMax) bMax = b;
        }
        int rr = rMax - rMin, gr = gMax - gMin, br = bMax - bMin;
        // green counts a little more, as eyes see its steps most
        if (gr * 1.2 >= rr && gr * 1.2 >= br) { range = gr; return 1; }
        if (rr >= br) { range = rr; return 0; }
        range = br; return 2;
    }

    /// <summary>Nearest palette entry for a color, remembered per 5-bit cell.</summary>
    public sealed class Mapper(int[] palette)
    {
        readonly short[] _cache = Array.ConvertAll(new short[32768], _ => (short)-1);

        public int Nearest(int r, int g, int b)
        {
            int k = (r >> 3) << 10 | (g >> 3) << 5 | b >> 3;
            int c = _cache[k];
            if (c >= 0) return c;
            int cr = (r & ~7) + 4, cg = (g & ~7) + 4, cb = (b & ~7) + 4;
            int best = 0, bestD = int.MaxValue;
            for (int i = 0; i < palette.Length; i++)
            {
                int p = palette[i];
                int dr = Pixel.R(p) - cr, dg = Pixel.G(p) - cg, db = Pixel.B(p) - cb;
                int d = 3 * dr * dr + 4 * dg * dg + 2 * db * db;
                if (d < bestD) { bestD = d; best = i; }
            }
            _cache[k] = (short)best;
            return best;
        }
    }
}

/// <summary>GIF's variable-width LZW, written in 255-byte sub-blocks.</summary>
internal static class Lzw
{
    public static void Encode(Stream s, byte[] data, int minCodeSize)
    {
        s.WriteByte((byte)minCodeSize);
        var bits = new BitWriter(s);
        int clear = 1 << minCodeSize, eoi = clear + 1;
        int next = eoi + 1, size = minCodeSize + 1;
        var dict = new Dictionary<int, int>(4096);

        void Output(int code)
        {
            bits.Write(code, size);
            if (code == clear) size = minCodeSize + 1;
            else if (next > (1 << size) - 1 && size < 12) size++;
        }

        Output(clear);
        if (data.Length == 0) { Output(eoi); bits.Finish(); return; }
        int prefix = data[0];
        for (int i = 1; i < data.Length; i++)
        {
            int k = data[i], key = prefix << 8 | k;
            if (dict.TryGetValue(key, out int code)) { prefix = code; continue; }
            Output(prefix);
            if (next < 4096) dict[key] = next++;
            else { dict.Clear(); next = eoi + 1; Output(clear); }
            prefix = k;
        }
        Output(prefix);
        Output(eoi);
        bits.Finish();
    }

    sealed class BitWriter(Stream s)
    {
        readonly byte[] _block = new byte[255];
        int _n, _acc, _count;

        public void Write(int code, int size)
        {
            _acc |= code << _count;
            _count += size;
            while (_count >= 8) { Put((byte)(_acc & 255)); _acc >>= 8; _count -= 8; }
        }

        void Put(byte b)
        {
            _block[_n++] = b;
            if (_n == 255) { s.WriteByte(255); s.Write(_block, 0, 255); _n = 0; }
        }

        public void Finish()
        {
            if (_count > 0) { Put((byte)(_acc & 255)); _acc = 0; _count = 0; }
            if (_n > 0) { s.WriteByte((byte)_n); s.Write(_block, 0, _n); _n = 0; }
            s.WriteByte(0);
        }
    }
}
