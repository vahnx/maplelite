using MapleLite.Core.Game;
using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;
using Xunit;

namespace MapleLite.Tests;

public class RoomNumberTests
{
    // Drops line breaks of either kind: the file may be checked out with Windows (\r\n) or Unix (\n) line endings.
    static bool[] Grid(string rows) => rows.Replace("\r", "").Replace("\n", "").Select(c => c == '#').ToArray();

    [Fact]
    public void Every_reference_digit_is_recognised_from_the_other_fonts()
    {
        // Leave one font style out at a time: its digits must be read correctly using only the others, with the hole
        // check on. Shows the pooled comparison and the hole rules don't make any digit unreadable.
        var refs = RoomNumber.References.ToList();
        var wrong = new List<string>();
        foreach (var (digit, shape, font) in refs)
        {
            var holes = RoomNumber.Holes(shape, 10, 14);
            var got = RoomNumber.Classify(shape, holes, refs.Where(r => r.font != font).Select(r => (r.digit, r.shape)));
            if (got?.digit != digit) wrong.Add($"font {font} digit {digit} read as {got?.digit.ToString() ?? "nothing"} (holes {holes.Count})");
        }
        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }

    [Fact]
    public void The_games_pixel_font_nine_reads_as_nine()
    {
        // "Free Market <9>" as the full-size minimap draws it (6x9 px), from a saved capture. Its outline is close to
        // the 0 and 8 of ordinary fonts; its single high hole is not.
        var nine = Grid("""
            ..##..
            .#..#.
            .##.#.
            ##..##
            ##..##
            ..####
            ....#.
            ##..#.
            .###..
            """);
        var holes = RoomNumber.Holes(nine, 6, 9);
        Assert.Single(holes);
        Assert.True(RoomNumber.HolesFit(9, holes));
        Assert.False(RoomNumber.HolesFit(0, holes));
        Assert.False(RoomNumber.HolesFit(8, holes));
        var got = RoomNumber.Classify(RoomNumber.Normalise(nine, 6, 9), holes, RoomNumber.References.Select(r => (r.digit, r.shape)));
        Assert.NotNull(got);
        Assert.Equal(9, got!.Value.digit);
        Assert.True(got.Value.score >= 0.7 || (got.Value.score >= 0.55 && got.Value.lead >= 0.1), $"score {got.Value.score:F2} lead {got.Value.lead:F2}");
    }

    [Fact]
    public void Your_yellow_dot_is_found_on_the_minimap()
    {
        // a 60x30 map: tan terrain everywhere (the yellow-brown that isn't you) and your 6x6 ring at 40..45, 10..15
        int w = 60, h = 30;
        var px = Enumerable.Repeat(Pixel.Rgb(0xCC, 0x99, 0x66), w * h).ToArray();
        foreach (var (x, y) in new[] { (42, 10), (43, 10), (41, 11), (44, 11), (40, 12), (45, 12), (40, 13), (45, 13), (41, 14), (44, 14), (42, 15), (43, 15) })
            px[y * w + x] = Pixel.Rgb(0xFF, 0xEE, 0x22);
        foreach (var (x, y) in new[] { (42, 11), (43, 11), (41, 12), (44, 12), (41, 13), (44, 13), (42, 14), (43, 14) })
            px[y * w + x] = Pixel.Rgb(0xFF, 0xDD, 0x44);
        var dot = MapReader.PlayerDot(px, w, h);
        Assert.NotNull(dot);
        Assert.InRange(dot!.Value.x, 42, 43);
        Assert.InRange(dot.Value.y, 12, 13);
        // no ring: nothing (terrain alone is never taken for you)
        Assert.Null(MapReader.PlayerDot(Enumerable.Repeat(Pixel.Rgb(0xCC, 0x99, 0x66), w * h).ToArray(), w, h));
    }

    [Fact]
    public void Holes_are_counted_and_placed()
    {
        var eight = Grid("""
            .###.
            #...#
            .###.
            #...#
            .###.
            """);
        Assert.Equal(2, RoomNumber.Holes(eight, 5, 5).Count);
        var seven = Grid("""
            #####
            ....#
            ...#.
            ..#..
            ..#..
            """);
        Assert.Empty(RoomNumber.Holes(seven, 5, 5));
    }
}

public class OcrBlockTests
{
    static OcrLine Line(string text, double x, double y, double w, double h = 12) =>
        new(text, new TextBounds(x, y, w, h), [new OcrWord(text, new TextBounds(x, y, w, h))]);

    [Fact]
    public void Split_words_on_one_row_are_joined_but_far_ones_are_not()
    {
        var lines = new[] { Line("Dark", 900, 110, 39), Line("Guise", 945, 110, 39), Line("Dark Burgler", 1138, 110, 97) };
        var all = OcrBlocks.WithJoinedNeighbours(lines);
        Assert.Contains(all, l => l.Text == "Dark Guise");
        Assert.DoesNotContain(all, l => l.Text.Contains("Guise Dark"));
        Assert.Equal(3, all.Take(3).Count());   // the lines as read come first, unchanged
    }

    [Fact]
    public void Ink_runs_count_the_characters_drawn()
    {
        // "1 2 3": three strokes with gaps, in black on white
        var f = new Frame(20, 5);
        Array.Fill(f.Px, Pixel.White);
        foreach (int x in new[] { 2, 3, 8, 14, 15, 16 }) for (int y = 1; y < 4; y++) f.Px[y * 20 + x] = Pixel.Black;
        Assert.Equal(3, ImageOps.CountInkRuns(f, f.Bounds, p => Pixel.Max(p) < 100));
    }

    [Fact]
    public void Plain_sides_are_trimmed_even_when_shaded()
    {
        // 40x20: a sky band 8 px wide each side that shades from top to bottom, and a busy middle
        var f = new Frame(40, 20);
        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 40; x++)
                f.Px[y * 40 + x] = x < 8 || x >= 32 ? Pixel.Rgb(150, 190, 230 - y * 3) : Pixel.Rgb((x * 37 + y * 11) % 255, (x * 13) % 255, (y * 29) % 255);
        Assert.Equal(new Region(8, 0, 24, 20), ImageOps.TrimPlain(f));
        // nothing plain at the edges: nothing is cut
        var busy = new Frame(40, 20);
        for (int i = 0; i < busy.Px.Length; i++) busy.Px[i] = Pixel.Rgb(i * 37 % 255, i * 11 % 255, i * 5 % 255);
        Assert.Equal(busy.Bounds, ImageOps.TrimPlain(busy));
    }

    [Fact]
    public void Dark_text_cleanup_keeps_grey_lettering_only()
    {
        var f = new Frame(3, 1, [Pixel.Rgb(0xBB, 0xBB, 0xBB), Pixel.Rgb(0xF2, 0xF5, 0xF8), Pixel.White]);
        var o = OcrPreprocessor.DarkText(200).Apply(f);
        Assert.Equal([Pixel.Black, Pixel.White, Pixel.White], o.Px);
    }
}

public class TooltipTextTests
{
    [Fact]
    public void Scroll_description_becomes_its_success_rate_and_effect()
    {
        Assert.Equal("Success rate: 60%\nWeapon attack +2", ScrollReader.Stats("Success rate: 60%, weapon attack +2. The success rate of this scroll can be enhanced."));
        Assert.Equal("Success rate:60%\nWeapon att. +2", ScrollReader.Stats("Improves weapon attack on gloves. Success rate:60 %, weapon att. + 2"));
    }

    [Fact]
    public void Tooltip_titles_carry_gender_and_upgrade_marks()
    {
        Assert.Equal(("Blue pao Bottoms", 0), EquipmentReader.TitleParts("Blue pao Bottoms (M)"));
        Assert.True(EquipmentReader.IsTitleOf(" Blue pao Bottoms (M)", "Blue Pao Bottoms"));
        Assert.True(EquipmentReader.IsTitleOf("Meba (+2)", "Meba"));
        Assert.Equal(2, EquipmentReader.TitleParts("Pink Skirt (F) (+2)").upgrades);
        Assert.False(EquipmentReader.IsTitleOf("Blue Pao Bottoms Long", "Blue Pao Bottoms"));
    }

    [Fact]
    public void A_garbled_last_line_still_gives_the_slots()
    {
        // as read from saved captures: a letter split off, and chat running past the tooltip
        Assert.True(EquipmentReader.IsLastLine("Remaining Enhanceme ts: 7"));
        Assert.True(EquipmentReader.IsLastLine("Remaining Enhancemen s:"));
        Assert.False(EquipmentReader.IsLastLine("Type: Bottom"));
        var d = EquipmentReader.Parse(["Type: Bottom", "DEX: +2", "Weapon Def.: +25 (23+2)", "Remaining Enhanceme ts: 7 [16:11] Etrey"]);
        Assert.NotNull(d);
        Assert.Equal("7", d!.Slots);
        Assert.Equal("DEX: +2\nWeapon Def: +25 (23+2)", d.Stats);
        // the number itself missing: no reading rather than a wrong one
        Assert.Null(EquipmentReader.Parse(["Type: Bottom", "DEX: +2", "Remaining Enhancemen s:"]));
    }

    [Fact]
    public void Equipment_tooltip_lines_parse_into_stats_and_slots()
    {
        // the Sapphire Earrings tooltip as read row by row from a saved capture
        var d = EquipmentReader.Parse(["Sapphire Earrings", "BEGINNER I-URRRIOR MRGICIRN BOWMRN THIEF", "Type: Earrings", "MaxMP: +5",
            "Magic Def. +32 (30+2)", "Evasion: +2", "Remaining Enhancements: 5"]);
        Assert.NotNull(d);
        Assert.Equal("Max MP: +5\nMagic Def: +32 (30+2)\nEvasion: +2", d!.Stats);
        Assert.Equal("5", d.Slots);
    }
}
