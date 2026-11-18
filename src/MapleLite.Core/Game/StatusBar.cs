using System.Globalization;
using System.Text.RegularExpressions;
using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;

namespace MapleLite.Core.Game;

/// <summary>What the status bar at the bottom of the game shows: HP, MP and EXP.</summary>
public sealed record StatusBarReading(int? Hp, int? HpMax, int? Mp, int? MpMax, long Exp, double ExpPercent, int Top, string Text);

/// <summary>
/// Reads the status bar's numbers ("HP [1320/1320]  MP [677/1089]  EXP 171,372 [71.26%]") with the game's own
/// pixel font, so every digit is exact. The bar is found by trying each row near the bottom of the picture
/// (it moves with the game's resolution); the row that worked is tried first next time.
/// Learned from recordings of the Classic World client at 1680x944, where the UI is drawn unscaled.
/// </summary>
public sealed partial class StatusBarReader
{
    /// <summary>The status bar's digits: white, 1-pixel strokes, 7 rows. The brackets are dimmer and read as gaps.</summary>
    public static readonly PixelFont Font = new(7,
    [
        ('0', [".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."]),
        ('1', [".#", "##", ".#", ".#", ".#", ".#", ".#"]),
        ('2', [".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####"]),
        ('3', [".###.", "#...#", "....#", "..##.", "....#", "#...#", ".###."]),
        ('4', ["...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#."]),
        ('5', ["#####", "#....", "#....", ".###.", "....#", "#...#", ".###."]),
        ('6', [".###.", "#...#", "#....", "####.", "#...#", "#...#", ".###."]),
        ('7', ["#####", "....#", "....#", "....#", "...#.", "..#..", "..#.."]),
        ('8', [".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###."]),
        ('9', [".###.", "#...#", "#...#", ".####", "....#", "#...#", ".###."]),
        (',', ["..", "..", "..", "..", "..", ".#", "#."]),
        ('.', [".", ".", ".", ".", ".", ".", "#"]),
        ('/', ["...", "..#", "..#", ".#.", ".#.", "#..", "#.."]),
        ('%', [".#....#", "#.#..#.", ".#..#..", "...#...", "..#..#.", ".#..#.#", "#....#."]),
    ]);

    /// <summary>How far up from the bottom of the picture to look for the bar's text.</summary>
    public const int SearchRows = 90;

    int? _lastTop;

    static bool Ink(int p) => Pixel.R(p) + Pixel.G(p) + Pixel.B(p) >= 510;

    [GeneratedRegex(@"(?<![\d,])(\d{1,3}(?:,\d{3})*|\d+) (\d{1,3}\.\d{1,3})%")]
    private static partial Regex ExpShape();

    [GeneratedRegex(@"(\d+)/(\d+)")]
    private static partial Regex Pair();

    /// <summary>The bar's numbers, or null when it isn't on screen (or something covers the EXP text).</summary>
    public StatusBarReading? Read(Frame f)
    {
        if (_lastTop is { } last && ReadAt(f, last) is { } again) return again;
        for (int top = f.H - Font.Height; top >= Math.Max(0, f.H - SearchRows); top--)
        {
            if (top == _lastTop) continue;
            if (ReadAt(f, top) is { } r) { _lastTop = top; return r; }
        }
        return null;
    }

    /// <summary>Reads the line whose glyph tops are on row <paramref name="top"/>, if it holds the EXP text.</summary>
    public static StatusBarReading? ReadAt(Frame f, int top)
    {
        var text = Font.ReadLine(f, top, 0, f.W, Ink);
        return Parse(text, top);
    }

    /// <summary>Picks the numbers out of a line read with <see cref="Font"/>, such as "?? 1320/1320 ?? 677/1089 ??? 171,372 71.26%".</summary>
    public static StatusBarReading? Parse(string text, int top = 0)
    {
        var m = ExpShape().Match(text);
        if (!m.Success) return null;
        if (!long.TryParse(m.Groups[1].Value.Replace(",", ""), out var exp)) return null;
        if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) || pct > 100) return null;
        int? hp = null, hpMax = null, mp = null, mpMax = null;
        var pairs = Pair().Matches(text[..m.Index]);
        if (pairs.Count >= 1 && int.TryParse(pairs[0].Groups[1].Value, out var a) && int.TryParse(pairs[0].Groups[2].Value, out var b)) { hp = a; hpMax = b; }
        if (pairs.Count >= 2 && int.TryParse(pairs[1].Groups[1].Value, out var c) && int.TryParse(pairs[1].Groups[2].Value, out var d)) { mp = c; mpMax = d; }
        return new StatusBarReading(hp, hpMax, mp, mpMax, exp, pct, top, text);
    }
}
