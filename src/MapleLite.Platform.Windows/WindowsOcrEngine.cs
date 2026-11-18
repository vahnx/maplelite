using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;
using Windows.Graphics.Imaging;
using WinOcr = Windows.Media.Ocr;

namespace MapleLite.Platform.Windows;

/// <summary>Windows' built-in OCR (Windows.Media.Ocr). Engines are pooled so parallel reads don't share one.</summary>
internal sealed class WindowsOcrEngine : IOcrEngine
{
    readonly ConcurrentBag<WinOcr.OcrEngine> _pool = new();

    WindowsOcrEngine(WinOcr.OcrEngine first)
    {
        _pool.Add(first);
        Name = $"Windows OCR ({first.RecognizerLanguage.LanguageTag})";
    }

    public static WindowsOcrEngine? TryCreate() => Create() is { } e ? new WindowsOcrEngine(e) : null;

    static WinOcr.OcrEngine? Create() =>
        WinOcr.OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language("en-US"))
        ?? WinOcr.OcrEngine.TryCreateFromUserProfileLanguages();

    public string Name { get; }
    public int MaxImageDimension => (int)WinOcr.OcrEngine.MaxImageDimension;

    public async Task<IReadOnlyList<OcrLine>> RecognizeAsync(Frame image, CancellationToken ct)
    {
        if (!_pool.TryTake(out var engine)) engine = Create() ?? throw new OcrUnavailableException("Install an English OCR language in Windows Settings > Time & language.");
        try
        {
            var bytes = image.Bgra.ToArray();
            using var bmp = SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, image.W, image.H, BitmapAlphaMode.Ignore);
            var result = await engine.RecognizeAsync(bmp).AsTask(ct).ConfigureAwait(false);
            // When Windows decides the text is tilted (it does on some large pictures, even of level text), it reports
            // every box as if the picture had been turned level about its centre. Turn the boxes back, or they drift
            // further off the further they are from the centre (2° moves a box 25px at 700px out).
            double angle = (result.TextAngle ?? 0) * Math.PI / 180, cos = Math.Cos(angle), sin = Math.Sin(angle);
            double cx = image.W / 2.0, cy = image.H / 2.0;
            TextBounds Box(global::Windows.Foundation.Rect r)
            {
                if (angle == 0) return new TextBounds(r.X, r.Y, r.Width, r.Height);
                double qx = r.X + r.Width / 2 - cx, qy = r.Y + r.Height / 2 - cy;
                double px = qx * cos - qy * sin + cx, py = qx * sin + qy * cos + cy;
                return new TextBounds(px - r.Width / 2, py - r.Height / 2, r.Width, r.Height);
            }
            var lines = new List<OcrLine>(result.Lines.Count);
            foreach (var line in result.Lines)
            {
                if (line.Words.Count == 0) continue;
                var words = line.Words.Select(w => new OcrWord(w.Text, Box(w.BoundingRect))).ToList();
                lines.Add(new OcrLine(line.Text, TextBounds.Union(words.Select(w => w.Box)), words));
            }
            return lines;
        }
        finally { _pool.Add(engine); }
    }
}
