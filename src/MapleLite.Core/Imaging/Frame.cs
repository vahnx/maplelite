using System.Runtime.InteropServices;

namespace MapleLite.Core.Imaging;

/// <summary>A rectangle in whole pixels.</summary>
public readonly record struct Region(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;

    public Region Intersect(Region other)
    {
        int x = Math.Max(X, other.X), y = Math.Max(Y, other.Y);
        int r = Math.Min(Right, other.Right), b = Math.Min(Bottom, other.Bottom);
        return r <= x || b <= y ? default : new Region(x, y, r - x, b - y);
    }

    public Region Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };
    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}

/// <summary>
/// A 32-bit picture. Each int is 0xAARRGGBB, which in memory is the byte order B,G,R,A,
/// so the buffer can go straight to OCR engines and bitmaps that take BGRA.
/// Frames handed out by the capture loop are shared by every plugin: treat them as read-only.
/// </summary>
public sealed class Frame
{
    static long _nextId;

    public readonly int W, H;
    public readonly int[] Px;

    /// <summary>Unique for each frame made in this process. The shared OCR service caches results by it.</summary>
    public long Id { get; }

    public Frame(int w, int h)
    {
        if (w <= 0 || h <= 0) throw new ArgumentOutOfRangeException(nameof(w), $"A frame needs a positive size, not {w}x{h}.");
        W = w; H = h; Px = new int[w * h];
        Id = Interlocked.Increment(ref _nextId);
    }

    public Frame(int w, int h, int[] px)
    {
        if (w <= 0 || h <= 0) throw new ArgumentOutOfRangeException(nameof(w), $"A frame needs a positive size, not {w}x{h}.");
        if (px.Length != w * h) throw new ArgumentException($"{px.Length} pixels don't fill {w}x{h}.", nameof(px));
        W = w; H = h; Px = px;
        Id = Interlocked.Increment(ref _nextId);
    }

    public Region Bounds => new(0, 0, W, H);
    public int At(int x, int y) => Px[y * W + x];

    /// <summary>The pixels as B,G,R,A bytes, without copying.</summary>
    public Span<byte> Bgra => MemoryMarshal.AsBytes(Px.AsSpan());

    public Frame Crop(Region r)
    {
        if (r.IsEmpty || r.X < 0 || r.Y < 0 || r.Right > W || r.Bottom > H)
            throw new ArgumentOutOfRangeException(nameof(r), $"Region {r} is outside the {W}x{H} frame.");
        var c = new Frame(r.Width, r.Height);
        for (int y = 0; y < r.Height; y++) Array.Copy(Px, (r.Y + y) * W + r.X, c.Px, y * r.Width, r.Width);
        return c;
    }

    public Frame Clone() => new(W, H, (int[])Px.Clone());

    public static Frame FromBgra(int w, int h, ReadOnlySpan<byte> bgra, int stride)
    {
        var f = new Frame(w, h);
        var dst = f.Bgra;
        for (int y = 0; y < h; y++) bgra.Slice(y * stride, w * 4).CopyTo(dst.Slice(y * w * 4, w * 4));
        return f;
    }
}

/// <summary>Small helpers for reading pixel colors.</summary>
public static class Pixel
{
    public static int R(int p) => (p >> 16) & 255;
    public static int G(int p) => (p >> 8) & 255;
    public static int B(int p) => p & 255;
    public static int Max(int p) => Math.Max(R(p), Math.Max(G(p), B(p)));
    public static int Min(int p) => Math.Min(R(p), Math.Min(G(p), B(p)));
    public static int Chroma(int p) => Max(p) - Min(p);
    public static int Rgb(int r, int g, int b) => unchecked((int)0xff000000) | (r << 16) | (g << 8) | b;
    public const int Black = unchecked((int)0xff000000);
    public const int White = unchecked((int)0xffffffff);
}
