using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;
using MapleLite.Platform.Mac.Interop;
using static MapleLite.Platform.Mac.Interop.ObjC;

namespace MapleLite.Platform.Mac;

/// <summary>
/// Apple's built-in text recognition (Vision, VNRecognizeTextRequest; macOS 10.15+). Each call makes its own
/// request and handler, so parallel reads are safe. Language correction is off: game text is names, numbers
/// and item names, which a dictionary would "fix" into the wrong words.
/// </summary>
internal sealed unsafe class MacOcrEngine : IOcrEngine
{
    const string Language = "en-US";

    MacOcrEngine() { }

    public static MacOcrEngine? TryCreate()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(10, 15) || !TryLoad(ObjC.Vision)) return null;
        return Class("VNRecognizeTextRequest") != 0 ? new MacOcrEngine() : null;
    }

    public string Name => $"Apple Vision ({Language})";

    // Vision has no fixed limit; this keeps the shared service from enlarging crops into huge pictures.
    public int MaxImageDimension => 8192;

    public Task<IReadOnlyList<OcrLine>> RecognizeAsync(Frame image, CancellationToken ct) =>
        Task.Run(() => Pool(() => Recognize(image)), ct);

    static IReadOnlyList<OcrLine> Recognize(Frame f)
    {
        fixed (int* px = f.Px)
        {
            var cgImage = CG.ToImage(f, px);
            if (cgImage == 0) throw new InvalidOperationException("Couldn't hand the picture to Vision.");
            nint handler = 0, request = 0;
            try
            {
                handler = Send(Send(Class("VNImageRequestHandler"), "alloc"), "initWithCGImage:options:", cgImage, Send(Class("NSDictionary"), "dictionary"));
                request = New("VNRecognizeTextRequest");
                Send(request, "setRecognitionLevel:", 0); // VNRequestTextRecognitionLevelAccurate
                Send(request, "setUsesLanguageCorrection:", Bool(false));
                Send(request, "setRecognitionLanguages:", ArrayOf(Str(Language)));
                // The default skips text shorter than 1/32 of the picture, which drops chat lines in a full frame.
                SendFloat(request, "setMinimumTextHeight:", Math.Min(1f / 32, 6f / f.H));

                nint error = 0;
                if (!IsYes(Send(handler, "performRequests:error:", ArrayOf(request), (nint)(&error))))
                    throw new OcrUnavailableException("Vision couldn't read the picture: " + Describe(error));
                return Lines(Send(request, "results"), f.W, f.H);
            }
            finally
            {
                Release(request);
                Release(handler);
                CG.CGImageRelease(cgImage);
            }
        }
    }

    static List<OcrLine> Lines(nint results, int w, int h)
    {
        var lines = new List<OcrLine>();
        for (int i = 0, n = Count(results); i < n; i++)
        {
            var observation = At(results, i);
            var candidates = Send(observation, "topCandidates:", 1);
            if (Count(candidates) == 0) continue;
            var best = At(candidates, 0);
            var text = ToStr(Send(best, "string"))?.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            var lineBox = ToPixels(SendRect(observation, "boundingBox"), w, h);
            var words = Words(best, text, lineBox, w, h);
            if (words.Count == 0) continue;
            lines.Add(new OcrLine(string.Join(' ', words.Select(x => x.Text)), TextBounds.Union(words.Select(x => x.Box)), words));
        }
        // Vision doesn't promise an order; the shared service expects top to bottom, then left to right.
        lines.Sort((a, b) => a.Box.Y != b.Box.Y ? a.Box.Y.CompareTo(b.Box.Y) : a.Box.X.CompareTo(b.Box.X));
        return lines;
    }

    /// <summary>
    /// Vision reports lines; words come from asking for the box of each word's character range.
    /// Some systems answer with the whole line's box for every range; then words are spaced out by character count.
    /// </summary>
    static List<OcrWord> Words(nint recognized, string text, TextBounds line, int w, int h)
    {
        var spans = new List<(int start, int length)>();
        for (int i = 0; i < text.Length;)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            int s = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            if (i > s) spans.Add((s, i - s));
        }
        if (spans.Count == 0) return [];
        if (spans.Count == 1) return [new OcrWord(text, line)];

        var boxes = new List<TextBounds>(spans.Count);
        bool usable = true;
        foreach (var (start, length) in spans)
        {
            nint error = 0;
            var rect = SendRange(recognized, "boundingBoxForRange:error:", new NSRange(start, length), (nint)(&error));
            if (rect == 0) { usable = false; break; }
            var box = ToPixels(SendRect(rect, "boundingBox"), w, h);
            if (box.Width <= 0 || (box.Width >= line.Width * 0.9 && length < text.Length * 0.7)) { usable = false; break; }
            boxes.Add(box);
        }

        var words = new List<OcrWord>(spans.Count);
        for (int i = 0; i < spans.Count; i++)
        {
            var (start, length) = spans[i];
            var box = usable ? boxes[i]
                : new TextBounds(line.X + line.Width * start / text.Length, line.Y, line.Width * length / text.Length, line.Height);
            words.Add(new OcrWord(text.Substring(start, length), box));
        }
        return words;
    }

    /// <summary>Vision boxes are fractions of the picture with the origin at the bottom left.</summary>
    static TextBounds ToPixels(CGRect r, int w, int h) =>
        new(r.X * w, (1 - r.Y - r.Height) * h, r.Width * w, r.Height * h);
}
