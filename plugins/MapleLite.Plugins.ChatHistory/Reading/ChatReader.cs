using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;

namespace MapleLite.Plugins.ChatHistory;

/// <summary>
/// Reads chat with the host's shared OCR. Replaces the MapleMemory app's Reader: the OCR engine,
/// glyph cleanup (OcrPreprocessor.ChatGlyphs) and row grouping (OcrGrouping.Rows) now come from MapleLite
/// Core. What stays here is chat-specific: the channel color estimate per row.
/// </summary>
internal sealed class ChatReader(IOcr ocr)
{
    /// <summary>Rows of the chat crop, in crop coordinates, with a channel hint where the text color suggests one.</summary>
    public async Task<List<ChatRow>> ReadChatAsync(Frame crop, CancellationToken ct = default)
    {
        var result = await ocr.ReadAsync(crop, new OcrRequest(crop.Bounds)
        {
            Preprocess = OcrPreprocessor.ChatGlyphs,
            Grouping = OcrGrouping.Rows,
            Scaling = OcrScaling.Bicubic, // matches the app's HighQualityBicubic resize; best reads on chat in A/B tests
            Cache = false, // each chat crop is private to Chat History; nothing to share
        }, ct).ConfigureAwait(false);
        return Rows(crop, result.Lines, 0, 0);
    }

    /// <summary>A read of a region of a frame (no cleanup unless given), as rows in frame coordinates.</summary>
    public async Task<List<ChatRow>> ReadPlainAsync(Frame frame, Region region, bool hints, CancellationToken ct = default, OcrPreprocessor? preprocess = null)
    {
        var result = await ocr.ReadAsync(frame, new OcrRequest(region) { Grouping = OcrGrouping.Rows, Scaling = OcrScaling.Bicubic, Preprocess = preprocess ?? OcrPreprocessor.None }, ct).ConfigureAwait(false);
        return hints ? Rows(frame, result.Lines, 0, 0) : result.Lines.Where(l => l.Text.Trim().Length > 1)
            .Select(l => new ChatRow(l.Text.Trim(), l.Box.X, l.Box.Y, l.Box.Right, l.Box.Bottom)).OrderBy(r => r.Y).ToList();
    }

    static List<ChatRow> Rows(Frame f, IReadOnlyList<OcrLine> lines, int ox, int oy) =>
        lines.Where(l => l.Text.Trim().Length > 1)
             .Select(l => new ChatRow(l.Text.Trim(), l.Box.X - ox, l.Box.Y - oy, l.Box.Right - ox, l.Box.Bottom - oy, Hint(f, l.Box)))
             .OrderBy(r => r.Y).ToList();

    /// <summary>
    /// Classic chat colors: green whisper, orange buddy, pink party, purple guild; a row mostly covered by
    /// color is a megaphone banner. Votes come from the original (not cleaned) pixels of the row's band.
    /// Same thresholds as the MapleMemory app.
    /// </summary>
    internal static string? Hint(Frame f, TextBounds box)
    {
        int y0 = Math.Clamp((int)box.Top, 0, f.H - 1), y1 = Math.Clamp((int)box.Bottom, y0 + 1, f.H);
        var votes = new Dictionary<string, int>();
        int total = 0, colored = 0;
        for (int y = y0; y < y1; y++)
            for (int x = 0; x < f.W; x++)
            {
                int p = f.At(x, y), rr = Pixel.R(p), gg = Pixel.G(p), bb = Pixel.B(p);
                total++;
                if (Pixel.Chroma(p) < 65 || Pixel.Max(p) < 150) continue;
                colored++;
                string c = gg > rr * 1.25 && gg > bb * 1.25 ? "Whisper"
                    : rr > 200 && gg > 80 && gg < 190 && bb < 100 ? "Buddy"
                    : rr > 210 && bb > 150 && gg > 100 && gg < 210 ? "Party"
                    : bb > 150 && rr > 100 && gg < rr * .8 ? "Guild" : "";
                if (c != "") votes[c] = votes.GetValueOrDefault(c) + 1;
            }
        if (colored > total * .48) return "Megaphone";
        if (votes.Count == 0) return null;
        var v = votes.MaxBy(k => k.Value);
        return v.Value > 20 && v.Value > colored * .6 ? v.Key : null;
    }
}
