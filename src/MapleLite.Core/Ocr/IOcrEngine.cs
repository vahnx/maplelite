using MapleLite.Core.Imaging;

namespace MapleLite.Core.Ocr;

/// <summary>
/// The raw text recognizer an OS provides: Windows.Media.Ocr, Apple Vision, or Tesseract on Linux.
/// Platforms implement only this. Cropping, cleanup, enlarging, caching and layout happen once,
/// in <see cref="OcrService"/>, so every plugin gets the same behavior on every OS.
/// </summary>
public interface IOcrEngine
{
    /// <summary>A short name shown in status, such as "Windows OCR (en-US)".</summary>
    string Name { get; }

    /// <summary>The largest width or height the engine accepts.</summary>
    int MaxImageDimension { get; }

    /// <summary>
    /// Reads all text in <paramref name="image"/>. Boxes are in the image's own pixels.
    /// Must be safe to call from several threads at once (pool engines if the OS API isn't).
    /// </summary>
    Task<IReadOnlyList<OcrLine>> RecognizeAsync(Frame image, CancellationToken ct);
}
