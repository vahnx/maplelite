using MapleLite.Core.Imaging;

namespace MapleLite.Plugins.ChatHistory;

/// <summary>
/// Finds the chat panel from its timestamped lines ("[22:48] ..."), the most reliable chat marker.
/// Null when fewer than two timestamped lines are visible; the user can then select the area by hand.
/// Same rules as the MapleMemory app.
/// </summary>
internal static class ChatDetector
{
    public static async Task<Region?> DetectAsync(ChatReader reader, Frame f, CancellationToken ct = default)
    {
        // Chat sits in the lower part of the game window; skip the upper scene to keep OCR fast.
        int top = f.H * 2 / 5;
        var rows = await reader.ReadPlainAsync(f, new Region(0, top, f.W, f.H - top), hints: false, ct).ConfigureAwait(false);
        return FromRows(rows, f.W, f.H);
    }

    /// <summary>The chat area around timestamped rows (rows in frame coordinates).</summary>
    internal static Region? FromRows(IReadOnlyList<ChatRow> rows, int frameW, int frameH)
    {
        var stamped = rows.Where(r => MessageParser.Parse(r.Text).GameTime.Length > 0).ToList();
        if (stamped.Count < 2) return null;

        double lineHeight = Math.Max(10, stamped.Average(r => r.Bottom - r.Y));
        double left = stamped.Min(r => r.X), right = stamped.Max(r => r.Right);
        double top0 = stamped.Min(r => r.Y), bottom0 = stamped.Max(r => r.Bottom);

        // Pad for the panel's margins, and keep one extra line below for a wrapped final message.
        int x = (int)Math.Max(0, left - 12);
        int y = (int)Math.Max(0, top0 - lineHeight * 0.5);
        int width = (int)Math.Min(frameW - x, Math.Max(right - left + 40, frameW * 0.40));
        int height = (int)Math.Min(frameH - y, bottom0 + lineHeight * 1.5 - y);
        if (width < 120 || height < 40) return null;
        return new Region(x, y, width, height);
    }
}
