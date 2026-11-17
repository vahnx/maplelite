namespace MapleLite.Core.Ocr;

/// <summary>Layout helpers over OCR output that more than one plugin needs.</summary>
public static class OcrLayout
{
    /// <summary>
    /// Regroups words into rows by vertical center: a word joins the latest row whose center
    /// is within 55% of the typical word height. From MapleMemory's chat reader.
    /// </summary>
    public static IReadOnlyList<OcrLine> GroupRows(IEnumerable<OcrLine> lines)
    {
        var words = lines.SelectMany(l => l.Words).ToList();
        if (words.Count == 0) return Array.Empty<OcrLine>();
        double typical = words.Select(w => w.Box.Height).Order().ElementAt(words.Count / 2);
        var rows = new List<List<OcrWord>>();
        foreach (var word in words.OrderBy(w => w.Box.CenterY))
        {
            var row = rows.LastOrDefault(r => Math.Abs(r.Average(w => w.Box.CenterY) - word.Box.CenterY) < typical * .55);
            if (row == null) rows.Add([word]); else row.Add(word);
        }
        return rows.Select(OcrLine.FromWords).Where(l => l.Text.Length > 0).OrderBy(l => l.Box.Y).ToList();
    }

    /// <summary>The horizontal span of the first word containing <paramref name="word"/>, ignoring case.</summary>
    public static (double Left, double Right)? Span(OcrLine line, string word)
    {
        var w = line.Words.FirstOrDefault(x => x.Text.Contains(word, StringComparison.OrdinalIgnoreCase));
        return w == null ? null : (w.Box.Left, w.Box.Right);
    }
}
