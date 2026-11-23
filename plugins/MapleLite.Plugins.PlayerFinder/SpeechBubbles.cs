using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;

namespace MapleLite.Plugins.PlayerFinder;

internal sealed record SpeechBubble(string Name, Region Box, int TailX);

internal static class SpeechBubbles
{
    public static List<SpeechBubble> Find(Frame frame, IReadOnlyList<OcrLine> lines, Region chat, Region? minimap)
    {
        var bubbles = new List<SpeechBubble>();
        foreach (var line in lines)
        {
            var words = line.Words.OrderBy(word => word.Box.X).ToArray();
            for (int i = 0; i < words.Length; i++)
            {
                var word = words[i];
                string name = word.Text.TrimEnd(':', '：');
                if (!PlayerReader.IsName(name)) continue;
                bool colon = name.Length < word.Text.Length;
                if (!colon && (i + 1 >= words.Length || words[i + 1].Box.Left - word.Box.Right > 7
                    || Math.Abs(words[i + 1].Box.CenterY - word.Box.CenterY) > 3
                    || !(words[i + 1].Text.StartsWith(':') || words[i + 1].Text.StartsWith('：')))) continue;
                int x = (int)word.Box.CenterX, y = (int)word.Box.CenterY;
                if (chat.Contains(x, y) || minimap?.Contains(x, y) == true) continue;
                if (FindBox(frame, word.Box) is { } found && !bubbles.Any(b => b.Box == found.Box))
                    bubbles.Add(new SpeechBubble(name, found.Box, found.TailX));
            }
        }
        return bubbles;
    }

    static (Region Box, int TailX)? FindBox(Frame frame, TextBounds header)
    {
        // The standard bubble has a black frame around a flat light gray body.
        // Check two border sections and the break in the bottom border at the pointer.
        for (int top = Math.Max(1, (int)header.Top - 16); top <= (int)header.Top - 3; top++)
        {
            int x = (int)header.CenterX;
            if (x < 1 || x >= frame.W - 1 || Rgb(frame, x, top) != 0 || Rgb(frame, x, top + 1) != 0xffffff) continue;
            int left = x, right = x;
            while (left > 0 && x - left < 100 && Rgb(frame, left - 1, top) == 0) left--;
            while (right < frame.W - 1 && right - x < 120 && Rgb(frame, right + 1, top) == 0) right++;
            int width = right - left + 1;
            if (width is < 28 or > 140 || header.Left - left < -1 || header.Left - left > 51 || header.Right > right) continue;

            int middle = (left + right) / 2;
            if (Rgb(frame, middle, top + 3) is not (0xeeeeee or 0xffffff)) continue;
            for (int bottom = Math.Max(top + 21, (int)header.Bottom + 4);
                bottom < Math.Min(frame.H - 8, top + 177); bottom++)
            {
                int black = 0;
                for (int col = left + 2; col <= right - 2; col++)
                    if (Rgb(frame, col, bottom) == 0) black++;
                if (black < (width - 4) * .65) continue;

                for (int tail = middle - 9; tail <= middle + 9; tail++)
                {
                    if (Rgb(frame, tail, bottom - 2) != 0xeeeeee || Rgb(frame, tail, bottom) != 0xeeeeee) continue;
                    int start = tail, end = tail;
                    while (start > left && Rgb(frame, start - 1, bottom) != 0) start--;
                    while (end < right && Rgb(frame, end + 1, bottom) != 0) end++;
                    if (end - start is < 3 or > 16 || start <= left + 3 || end >= right - 3) continue;
                    var box = new Region(left - 3, top, width + 6, bottom - top + 7).Intersect(frame.Bounds);
                    return (box, (start + end) / 2);
                }
            }
        }
        return null;
    }

    static int Rgb(Frame frame, int x, int y) => frame.At(x, y) & 0xffffff;
}
