using MapleLite.Core.Game;
using MapleLite.Core.Imaging;
using MapleLite.Plugins.XpTracker;
using Xunit;

namespace MapleLite.Tests;

public class StatusBarTests
{
    static readonly int Bg = Pixel.Rgb(70, 72, 80), Ink = Pixel.Rgb(255, 255, 255), Bracket = Pixel.Rgb(130, 130, 130);

    // The status bar font's rows, as in StatusBarReader.Font, for drawing test pictures.
    static readonly Dictionary<char, string[]> Shapes = new()
    {
        ['0'] = [".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."],
        ['1'] = [".#...", "##...", ".#...", ".#...", ".#...", ".#...", ".#..."],
        ['2'] = [".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####"],
        ['3'] = [".###.", "#...#", "....#", "..##.", "....#", "#...#", ".###."],
        ['4'] = ["...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#."],
        ['5'] = ["#####", "#....", "#....", ".###.", "....#", "#...#", ".###."],
        ['6'] = [".###.", "#...#", "#....", "####.", "#...#", "#...#", ".###."],
        ['7'] = ["#####", "....#", "....#", "....#", "...#.", "..#..", "..#.."],
        ['8'] = [".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###."],
        ['9'] = [".###.", "#...#", "#...#", ".####", "....#", "#...#", ".###."],
        [','] = ["..", "..", "..", "..", "..", ".#", "#."],
        ['.'] = [".", ".", ".", ".", ".", ".", "#"],
        ['/'] = ["...", "..#", "..#", ".#.", ".#.", "#..", "#.."],
        ['%'] = [".#....#", "#.#..#.", ".#..#..", "...#...", "..#..#.", ".#..#.#", "#....#."],
        ['['] = ["##", "#.", "#.", "#.", "#.", "#.", "##"],
        [']'] = ["##", ".#", ".#", ".#", ".#", ".#", "##"],
    };

    /// <summary>A picture like the bottom of the game, with <paramref name="text"/> drawn at row <paramref name="top"/>.</summary>
    static Frame Draw(string text, int w = 900, int h = 60, int top = 40)
    {
        var f = new Frame(w, h);
        Array.Fill(f.Px, Bg);
        int x = 20;
        foreach (var c in text)
        {
            if (c == ' ') { x += 4; continue; }
            var rows = Shapes[c];
            for (int y = 0; y < rows.Length; y++)
                for (int i = 0; i < rows[y].Length; i++)
                    if (rows[y][i] == '#') f.Px[(top + y) * w + x + i] = c is '[' or ']' ? Bracket : Ink;
            x += rows[0].Length + 1;
        }
        return f;
    }

    [Fact]
    public void Reads_the_status_bar_numbers_exactly()
    {
        var f = Draw("[1320/1320]   [677/1089]   171,372 [71.26%]");
        var r = new StatusBarReader().Read(f);
        Assert.NotNull(r);
        Assert.Equal(171_372, r!.Exp);
        Assert.Equal(71.26, r.ExpPercent);
        Assert.Equal((1320, 1320, 677, 1089), (r.Hp!.Value, r.HpMax!.Value, r.Mp!.Value, r.MpMax!.Value));
        Assert.Equal(40, r.Top);
    }

    [Fact]
    public void Finds_the_bar_again_when_the_resolution_moves_it()
    {
        var reader = new StatusBarReader();
        Assert.Equal(40, reader.Read(Draw("4,567 [8.90%]"))!.Top);
        var moved = reader.Read(Draw("4,580 [8.91%]", h: 80, top: 55));
        Assert.Equal((55, 4_580L), (moved!.Top, moved.Exp));
    }

    [Fact]
    public void No_reading_without_the_exp_text() => Assert.Null(new StatusBarReader().Read(Draw("[1320/1320]   [677/1089]")));

    [Theory]
    [InlineData("?? 1320/1320 ?? 677/1089 ??171,660 71.38%", 171_660, 71.38)]
    [InlineData("?? 12 0.01%", 12, 0.01)]
    [InlineData("1,234,567 100.00%", 1_234_567, 100.0)]
    public void Parses_lines(string line, long exp, double pct)
    {
        var r = StatusBarReader.Parse(line);
        Assert.Equal((exp, pct), (r!.Exp, r.ExpPercent));
    }

    [Theory]
    [InlineData("171,66 71.38%")]   // a broken thousands group
    [InlineData("171,660 171.38%")] // over 100%
    [InlineData("171,660 ?1.38%")]
    public void Rejects_misreads(string line) => Assert.Null(StatusBarReader.Parse(line));

    [Fact]
    public void Session_counts_rises_and_ignores_a_single_bad_drop()
    {
        var s = new XpSession();
        var t = DateTimeOffset.Now;
        Assert.Equal(0, s.Add(t, 171_064, 71.13));
        Assert.Equal(8, s.Add(t.AddSeconds(7), 171_072, 71.13));
        Assert.Equal(0, s.Add(t.AddSeconds(8), 1_072, 0.44));   // one odd reading: not believed
        Assert.Equal(12, s.Add(t.AddSeconds(9), 171_084, 71.14));
        Assert.Equal((20L, 2), (s.Gained, s.Gains));
        Assert.Equal(240_502, s.Needed!.Value, 600.0);
    }

    [Fact]
    public void Session_believes_a_level_up_seen_twice()
    {
        var s = new XpSession();
        var t = DateTimeOffset.Now;
        s.Add(t, 240_000, 99.79);
        s.Add(t.AddSeconds(1), 300, 0.11);
        s.Add(t.AddSeconds(2), 300, 0.11);
        s.Add(t.AddSeconds(3), 312, 0.12);
        Assert.Equal(1, s.Levels);
        Assert.Equal(12, s.Gained); // the 240,000 before levelling was the starting point
    }
}
