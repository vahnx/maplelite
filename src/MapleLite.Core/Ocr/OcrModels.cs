using MapleLite.Core.Imaging;

namespace MapleLite.Core.Ocr;

/// <summary>A box around recognized text, in frame pixels (fractions kept: upscaled reads are finer than a pixel).</summary>
public readonly record struct TextBounds(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;

    /// <summary>Maps a box from an enlarged, padded crop back to the frame it came from.</summary>
    public TextBounds FromScaled(int scale, int pad, int offsetX, int offsetY) =>
        new(X / scale - pad + offsetX, Y / scale - pad + offsetY, Width / scale, Height / scale);

    public static TextBounds Union(IEnumerable<TextBounds> boxes)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        foreach (var x in boxes) { l = Math.Min(l, x.Left); t = Math.Min(t, x.Top); r = Math.Max(r, x.Right); b = Math.Max(b, x.Bottom); }
        return l == double.MaxValue ? default : new TextBounds(l, t, r - l, b - t);
    }
}

public sealed record OcrWord(string Text, TextBounds Box);

public sealed record OcrLine(string Text, TextBounds Box, IReadOnlyList<OcrWord> Words)
{
    public static OcrLine FromWords(IEnumerable<OcrWord> words)
    {
        var ordered = words.OrderBy(w => w.Box.X).ToList();
        return new OcrLine(string.Join(" ", ordered.Select(w => w.Text)).Trim(), TextBounds.Union(ordered.Select(w => w.Box)), ordered);
    }
}

/// <summary>What a read found. Every box is in the coordinates of the frame that was read, not the crop.</summary>
public sealed record OcrResult(IReadOnlyList<OcrLine> Lines, Region Region, int Scale, TimeSpan Elapsed, string Engine, bool FromCache = false)
{
    public string Text => string.Join("\n", Lines.Select(l => l.Text));
    public static OcrResult Empty(Region region, string engine) => new(Array.Empty<OcrLine>(), region, 1, TimeSpan.Zero, engine);
}

/// <summary>Thrown when the OS has no usable text recognizer (for example, no English OCR language on Windows).</summary>
public sealed class OcrUnavailableException(string message) : Exception(message);
