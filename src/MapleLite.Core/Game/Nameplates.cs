using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;

namespace MapleLite.Core.Game;

/// <summary>A character found in the game world by its name tag: the tag, and the box the character stands in above it.</summary>
public sealed record Nameplate(string Name, TextBounds Plate, Region Character);

/// <summary>
/// Finding characters by the dark name tag under their feet. Shared by Player Finder (who is nearby) and Shop Card
/// (taking your own portrait from the game world).
/// </summary>
public static class Nameplates
{
    /// <summary>The part of the picture that is game world: everything above the status bar.</summary>
    public static Region Scene(Frame frame) => new(0, 0, frame.W, Math.Max(1, frame.H - 65));

    /// <summary>True for a word that could be a character name (2–16 letters, digits or underscores).</summary>
    public static bool IsName(string name) => name.Length is >= 2 and <= 16
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>
    /// True when the word sits on a name tag: the tag darkens the background to at most 95, with white or yellow
    /// lettering. Requires both glyphs and the dark backing across the word, rather than any scene text.
    /// </summary>
    public static bool LooksLikeNameplate(Frame frame, TextBounds box)
    {
        var bounds = new Region((int)Math.Floor(box.Left), (int)Math.Floor(box.Top),
            (int)Math.Ceiling(box.Width), (int)Math.Ceiling(box.Height)).Intersect(frame.Bounds);
        if (bounds.IsEmpty) return false;
        int dark = 0, backing = 0, ink = 0;
        for (int y = bounds.Y; y < bounds.Bottom; y++)
            for (int x = bounds.X; x < bounds.Right; x++)
            {
                int p = frame.At(x, y);
                if (Pixel.Max(p) <= 95) dark++;
                if (Pixel.Max(p) is > 0 and <= 95) backing++;
                if ((p & 0xffffff) is 0xffffff or 0xffff00) ink++;
            }
        int total = bounds.Width * bounds.Height;
        return dark >= total * .62 && backing >= total * .30 && ink >= Math.Max(6, total * .08);
    }

    /// <summary>Where the character stands: centred on the tag, from just under it up to a normal character's height.</summary>
    public static Region CharacterAbove(TextBounds plate, Region scene)
    {
        int center = (int)Math.Round(plate.CenterX);
        int bottom = (int)Math.Ceiling(plate.Bottom) + 3;
        int half = Math.Max(30, (int)Math.Ceiling(plate.Width / 2) + 6);
        return new Region(center - half, bottom - 89, half * 2, 89).Intersect(scene);
    }

    /// <summary>
    /// Every name tag in the game world, with the character above it. One OCR pass, then one with bright text
    /// pulled out if <paramref name="want"/> wasn't found (tags on light backgrounds read badly otherwise).
    /// </summary>
    public static async Task<List<Nameplate>> FindAsync(IOcr ocr, Frame frame, string? want = null, CancellationToken ct = default)
    {
        var scene = Scene(frame);
        var request = new OcrRequest(scene) { Scale = 2, Scaling = OcrScaling.Bicubic };
        var found = new Dictionary<string, TextBounds>(StringComparer.OrdinalIgnoreCase);
        Collect(frame, await ocr.TryReadAsync(frame, request, ct).ConfigureAwait(false), found);
        if (!string.IsNullOrWhiteSpace(want) && !found.ContainsKey(want.Trim()))
            Collect(frame, await ocr.TryReadAsync(frame, request with { Preprocess = OcrPreprocessor.BrightText(235) }, ct).ConfigureAwait(false), found);
        return found.Select(kv => new Nameplate(kv.Key, kv.Value, CharacterAbove(kv.Value, scene)))
            .Where(p => !p.Character.IsEmpty).OrderBy(p => p.Plate.X).ToList();
    }

    static void Collect(Frame frame, IReadOnlyList<OcrLine> lines, Dictionary<string, TextBounds> found)
    {
        foreach (var line in lines)
        {
            var words = line.Words.Count == 0 ? [new OcrWord(line.Text, line.Box)] : line.Words.OrderBy(w => w.Box.X).ToArray();
            for (int i = 0; i < words.Length; i++)
            {
                Take(words[i].Text, words[i].Box);
                // Narrow gaps can split one name into two OCR words.
                if (i + 1 < words.Length && words[i + 1].Box.Left - words[i].Box.Right is >= 0 and <= 2.5
                    && Math.Abs(words[i + 1].Box.CenterY - words[i].Box.CenterY) <= 2)
                    Take(words[i].Text + words[i + 1].Text, TextBounds.Union([words[i].Box, words[i + 1].Box]));
            }
        }

        void Take(string text, TextBounds box)
        {
            text = text.Trim('•', '.', ':', '\'', '"', ',', '|');
            if (box.Height is < 5 or > 18 || !IsName(text) || !LooksLikeNameplate(frame, box)) return;
            found.TryAdd(text, box);
        }
    }
}
