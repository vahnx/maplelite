using MapleLite.Core.Game;
using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;
using MapleLite.Plugins.NpcHighlighter;
using Xunit;

namespace MapleLite.Tests;

public class NpcHighlighterTests
{
    const int Yellow = 0xffff00, White = 0xffffff;

    /// <summary>A mid-green scene with a dark tag at (x, y), lettered in <paramref name="ink"/>.</summary>
    static Frame Scene(params (int X, int Y, int W, int Ink)[] tags)
    {
        var f = new Frame(400, 300);
        for (int i = 0; i < f.Px.Length; i++) f.Px[i] = Pixel.Black | 0x88aa66;
        foreach (var (tx, ty, tw, ink) in tags)
            for (int y = ty; y < ty + 12; y++) for (int x = tx; x < tx + tw; x++)
                f.Px[y * f.W + x] = Pixel.Black | ((x + y) % 4 == 0 ? ink : 0x202020);
        return f;
    }

    static OcrLine Line(params (string Text, double X, double Y, double W)[] words) =>
        OcrLine.FromWords(words.Select(w => new OcrWord(w.Text, new TextBounds(w.X, w.Y, w.W, 12))));

    [Theory]
    [InlineData("Mr. Pickall", "mrplckall")]
    [InlineData("  Grendel the  Really Old ", "grendelthereallyold")]
    [InlineData("Silas Irons", "sllaslrons")]
    [InlineData("SiIas 1rons", "sllaslrons")]                 // i, I, l and 1 read alike
    public void Names_compact_to_letters_and_digits(string name, string compact) => Assert.Equal(compact, NpcTags.Compact(name));

    [Theory]
    [InlineData("Anvll", "Anvil", true)]
    [InlineData("Mr. Plckal", "Mr. Pickall", true)]          // one dropped letter
    [InlineData("Mr. Piekall", "Mr. Pickall", true)]         // one misread letter
    [InlineData("Shank", "Shanks", true)]
    [InlineData("Shan", "Shanks", false)]
    [InlineData("Ana", "Ann", false)]                        // three letters must match exactly
    [InlineData("Sllas Irns", "Silas Irons", true)]          // long names allow two
    [InlineData("", "Shanks", false)]
    public void Matching_allows_misread_letters(string ocr, string want, bool match) =>
        Assert.Equal(match, NpcTags.Matches(NpcTags.Compact(ocr), NpcTags.Compact(want)));

    [Fact]
    public void Yellow_tags_are_npcs_and_white_tags_are_players()
    {
        var f = Scene((80, 150, 60, Yellow), (220, 150, 60, White));
        Assert.True(NpcTags.LooksLikeNpcTag(f, new TextBounds(80, 150, 60, 12)));
        Assert.False(NpcTags.LooksLikeNpcTag(f, new TextBounds(220, 150, 60, 12)));  // player tag
        Assert.False(NpcTags.LooksLikeNpcTag(f, new TextBounds(10, 20, 60, 12)));    // plain scenery
        Assert.True(Nameplates.LooksLikeNameplate(f, new TextBounds(220, 150, 60, 12)));
    }

    [Fact]
    public void Multi_word_names_match_across_ocr_words_on_npc_tags_only()
    {
        var f = Scene((80, 150, 70, Yellow), (220, 150, 70, White));
        var lines = new[]
        {
            Line(("Mr.", 80, 150, 20), ("Pickall", 104, 150, 46)),
            Line(("Mr.", 220, 150, 20), ("Pickall", 244, 150, 46)),   // a player named the same: white tag
        };
        var found = NpcTags.Match(f, lines, ["Mr. Pickall", "Shanks"]);
        var npc = Assert.Single(found);
        Assert.Equal("Mr. Pickall", npc.Name);
        Assert.Equal(80, npc.Plate.X, 3);
        Assert.Equal(150, npc.Plate.Right, 3);
        Assert.True(npc.Body.Bottom > 150 && npc.Body.Height >= 80 && npc.Body.X < 80 && npc.Body.Right > 150);
    }

    [Fact]
    public void Matching_ignores_surrounding_words_but_not_far_apart_ones()
    {
        var f = Scene((80, 150, 140, Yellow));
        // OCR merged the tag with a neighbour; and "Pickall" stands too far from "Mr." to be one tag.
        var near = NpcTags.Match(f, [Line(("Shanks", 80, 150, 40), ("Mr.", 150, 150, 20), ("Pickall", 174, 150, 46))], ["Shanks", "Mr. Pickall"]);
        Assert.Equal(["Shanks", "Mr. Pickall"], near.Select(n => n.Name));
        var far = NpcTags.Match(f, [Line(("Mr.", 80, 150, 20), ("Pickall", 140, 150, 46))], ["Mr. Pickall"]);
        Assert.Empty(far);
    }

    [Fact]
    public void Overlay_items_use_each_entrys_color_and_skip_turned_off_ones()
    {
        var found = new[]
        {
            new TagSighting("Shanks", new TextBounds(80, 150, 40, 12), new Region(60, 75, 80, 89)),
            new TagSighting("Mr. Pickall", new TextBounds(200, 150, 70, 12), new Region(190, 75, 90, 89)),
        };
        var items = NpcHighlighterPlugin.Items(found,
        [
            new NpcHighlighterPlugin.NpcEntry("shanks", "#00FF80"),
            new NpcHighlighterPlugin.NpcEntry("Mr. Pickall", "#123456", On: false),
        ]);
        var item = Assert.Single(items);
        Assert.Equal("Shanks", item.Text);
        Assert.Equal(0x00FF80u, item.ColorRgb);
        Assert.Equal(new Region(60, 75, 80, 89), item.Rectangle);
        Assert.Equal(0x123456u, NpcHighlighterPlugin.ParseRgb("#FF123456"));
        Assert.Equal(0xFF55CCu, NpcHighlighterPlugin.ParseRgb("not a colour"));
    }

    // ---- Following tags between OCR reads -------------------------------------------------------------------

    sealed record Tag(int X, int Y, int W, int Seed, bool Npc = true);

    /// <summary>
    /// The scene seen with the camera at (camX, camY): a tan, rocky world (colors from Perion) with name tags at fixed
    /// world spots. NPC tags have scattered yellow lettering, player tags (<paramref name="covers"/>, drawn on top) white.
    /// </summary>
    static Frame World(int camX, int camY, Tag[] tags, params Tag[] covers)
    {
        int[] rock = [0xcc8844, 0xcc9955, 0x5f4c33, 0xbb7744, 0x463320];
        var f = new Frame(400, 300);
        for (int y = 0; y < f.H; y++)
            for (int x = 0; x < f.W; x++)
                f.Px[y * f.W + x] = Pixel.Black | rock[Hash(0, x + camX, (y + camY) / 3) % rock.Length];
        foreach (var t in tags.Concat(covers))
            for (int y = 0; y < 14; y++)
                for (int x = 0; x < t.W; x++)
                {
                    int sx = t.X + x - camX, sy = t.Y + y - camY;
                    if (sx < 0 || sy < 0 || sx >= f.W || sy >= f.H) continue;
                    bool letter = x >= 2 && x < t.W - 2 && y >= 2 && y < 12 && Hash(t.Seed, x, y) % 3 == 0;
                    f.Px[sy * f.W + sx] = Pixel.Black | (letter ? (t.Npc ? Yellow : White) : 0x2a2016);
                }
        return f;
    }

    static int Hash(int seed, int x, int y) => (int)((uint)(x * 73856093 ^ y * 19349663 ^ seed * 83492791) % 1000003);

    static TagSighting Seen(string name, Tag t) => new(name, new TextBounds(t.X + 2, t.Y + 2, t.W - 4, 10), default);

    static TextBounds PlateOf(NameTagTracker tracker, Frame frame, string name) => tracker.Current(frame).Single(s => s.Name == name).Plate;

    [Fact]
    public void Tags_follow_the_camera_without_ocr()
    {
        var anvil = new Tag(120, 150, 40, 1);
        var tracker = NameTagTracker.ForNpcs();
        var t0 = DateTimeOffset.UnixEpoch;
        tracker.Confirm(World(0, 0, [anvil]), [Seen("Anvil", anvil)], t0);
        int[] cams = [0, 12, 30, 52, 40, 20];
        for (int i = 0; i < cams.Length; i++)
        {
            var frame = World(cams[i], i % 2, [anvil]);
            tracker.Follow(frame, t0.AddMilliseconds(70 * i));
            var plate = PlateOf(tracker, frame, "Anvil");
            Assert.Equal(122 - cams[i], plate.X);
            Assert.Equal(152 - i % 2, plate.Y);
        }
    }

    [Fact]
    public void A_tag_hidden_under_a_player_tag_moves_with_the_others()
    {
        var anvil = new Tag(100, 150, 40, 1);
        var silas = new Tag(160, 150, 64, 2);
        var me = new Tag(150, 150, 84, 9, Npc: false);   // the user's own tag, right over Silas's
        var tracker = NameTagTracker.ForNpcs();
        var t0 = DateTimeOffset.UnixEpoch;
        tracker.Confirm(World(0, 0, [anvil, silas]), [Seen("Anvil", anvil), Seen("Silas Irons", silas)], t0);

        var covered = World(20, 0, [anvil, silas], me with { X = me.X + 20 });
        tracker.Follow(covered, t0.AddMilliseconds(70));
        Assert.Equal(162 - 20, PlateOf(tracker, covered, "Silas Irons").X);

        var clear = World(45, 0, [anvil, silas]);
        tracker.Follow(clear, t0.AddMilliseconds(140));
        Assert.Equal(162 - 45, PlateOf(tracker, clear, "Silas Irons").X);
        Assert.Equal(102 - 45, PlateOf(tracker, clear, "Anvil").X);
    }

    [Fact]
    public void A_mostly_covered_tag_holds_its_place_instead_of_jumping_to_the_title_below()
    {
        var silas = new Tag(100, 150, 64, 2);
        var title = new Tag(100, 167, 64, 3);              // "Blacksmith" under "Silas Irons"
        var tracker = NameTagTracker.ForNpcs();
        var t0 = DateTimeOffset.UnixEpoch;
        tracker.Confirm(World(0, 0, [silas, title]), [Seen("Silas Irons", silas)], t0);
        var frame = World(0, 0, [silas, title], new Tag(96, 150, 56, 9, Npc: false));
        for (int i = 1; i <= 5; i++) tracker.Follow(frame, t0.AddMilliseconds(70 * i));
        Assert.Equal(new TextBounds(102, 152, 60, 10), PlateOf(tracker, frame, "Silas Irons"));
    }

    [Fact]
    public void Tags_are_dropped_when_they_leave_and_held_only_so_long_while_covered()
    {
        var anvil = new Tag(100, 150, 40, 1);
        var t0 = DateTimeOffset.UnixEpoch;
        var tracker = NameTagTracker.ForNpcs();
        tracker.Confirm(World(0, 0, [anvil]), [Seen("Anvil", anvil)], t0);
        var sky = new Frame(400, 300);
        Array.Fill(sky.Px, Pixel.Black | 0xaaccee);                // another map: bright sky where the tag was
        tracker.Follow(sky, t0.AddMilliseconds(100));
        Assert.Equal(1, tracker.Count);                        // a moment's miss is held
        tracker.Follow(sky, t0.AddMilliseconds(2500));
        Assert.Equal(1, tracker.Count);                        // could be covered: held a while
        tracker.Follow(sky, t0.AddMilliseconds(3500));
        Assert.Equal(0, tracker.Count);                        // then let go

        // Covered by a player's tag with no other tag to show the camera: held a while, in place.
        tracker.Confirm(World(0, 0, [anvil]), [Seen("Anvil", anvil)], t0);
        var covered = World(0, 0, [anvil], new Tag(96, 150, 48, 9, Npc: false));
        tracker.Follow(covered, t0.AddSeconds(5));
        Assert.Equal(1, tracker.Count);
        tracker.Follow(covered, t0.AddSeconds(6.5));
        Assert.Equal(0, tracker.Count);

        tracker.Confirm(World(0, 0, [anvil]), [Seen("Anvil", anvil)], t0);
        tracker.Keep(["Silas Irons"]);
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void A_covered_tag_moves_with_the_camera_for_long_while_another_tag_shows_it()
    {
        var anvil = new Tag(100, 150, 40, 1);
        var silas = new Tag(250, 150, 64, 2);
        var t0 = DateTimeOffset.UnixEpoch;
        var tracker = NameTagTracker.ForNpcs();
        tracker.Confirm(World(0, 0, [anvil, silas]), [Seen("Anvil", anvil), Seen("Silas Irons", silas)], t0);
        Frame frame = null!;
        for (int i = 1; i <= 90; i++)   // 9 s with Anvil covered by a character (bright, not a tag), camera drifting
        {
            int cam = i % 20 < 10 ? i % 10 : 10 - i % 10;
            frame = World(cam, 0, [anvil, silas]);
            for (int y = 140; y < 170; y++) for (int x = 95 - cam; x < 150 - cam; x++) frame.Px[y * frame.W + x] = Pixel.Black | 0xddccbb;
            tracker.Follow(frame, t0.AddMilliseconds(100 * i));
        }
        Assert.Equal(2, tracker.Count);
        Assert.Equal(102 - 10, PlateOf(tracker, frame, "Anvil").X);
    }

    [Fact]
    public void A_tag_half_off_the_screen_edge_is_still_followed()
    {
        var anvil = new Tag(20, 150, 40, 1);
        var t0 = DateTimeOffset.UnixEpoch;
        var tracker = NameTagTracker.ForNpcs();
        tracker.Confirm(World(0, 0, [anvil]), [Seen("Anvil", anvil)], t0);
        foreach (var (cam, i) in new[] { 8, 16, 24, 30 }.Select((c, i) => (c, i)))
        {
            var frame = World(cam, 0, [anvil]);
            tracker.Follow(frame, t0.AddMilliseconds(70 * (i + 1)));
            Assert.Equal(22 - cam, PlateOf(tracker, frame, "Anvil").X);
        }
        tracker.Follow(World(60, 0, [anvil]), t0.AddMilliseconds(400));
        tracker.Follow(World(60, 0, [anvil]), t0.AddMilliseconds(650));
        Assert.Equal(0, tracker.Count);                        // gone off the left edge
    }

    [Fact]
    public void Yellow_only_reading_drops_white_player_lettering()
    {
        var f = World(0, 0, [new Tag(100, 150, 40, 1)], new Tag(60, 150, 40, 9, Npc: false));
        var clean = OcrPreprocessor.YellowText.Apply(f);
        int black = clean.Px.Count(p => p == Pixel.Black);
        Assert.Equal(f.Px.Count(p => (p & 0xffffff) == Yellow), black);
        Assert.True(black > 50);
    }

    [Fact]
    public void Npcs_in_view_leave_out_titles_player_tags_chat_and_notices()
    {
        var f = Scene((80, 150, 70, Yellow), (80, 167, 70, Yellow), (220, 150, 60, White), (220, 40, 60, Yellow), (20, 250, 120, Yellow));
        var lines = new[]
        {
            Line(("Silas", 80, 150, 30), ("Irons", 114, 150, 36)),
            Line(("Blacksmith", 80, 167, 70)),                        // title right under the name
            Line(("SageTheC", 220, 150, 60)),                          // white: a player
            Line(("Manji", 220, 40, 60)),
            Line(("[Welcome]", 20, 250, 60), ("Hi", 84, 250, 20)),    // a notice
        };
        Assert.Equal(["Silas Irons", "Manji"], NpcTags.InView(f, lines, chat: null));
        Assert.Equal(["Silas Irons"], NpcTags.InView(f, lines, chat: new Region(200, 30, 120, 40)));
    }

    [Fact]
    public void Nearby_npcs_join_after_three_reads_and_fragments_count_for_known_names()
    {
        var roster = new NpcRoster();
        var t = DateTimeOffset.UnixEpoch;
        Assert.Empty(roster.Update(t, ["Silas Irons", "Sil"], t));
        Assert.Empty(roster.Update(t.AddSeconds(0.5), ["Silas Irons"], t.AddSeconds(0.5)));
        Assert.Equal(["Silas Irons"], roster.Update(t.AddSeconds(1), ["Silas Irons", "Blacksmith"], t.AddSeconds(1)));
        // Covered: only fragments and misreads, which keep Silas listed and never add up to new names.
        for (int i = 3; i < 10; i++)
            Assert.Equal(["Silas Irons"], roster.Update(t.AddSeconds(i * 0.5), [i % 2 == 0 ? "Irons" : "SiIas lrons"], t.AddSeconds(i * 0.5)));
        Assert.Equal(["Silas Irons"], roster.Update(t.AddSeconds(8), ["Blacksmith"], t.AddSeconds(8)));   // stray votes expired
    }

    [Fact]
    public void Player_tags_are_followed_each_on_their_own_as_players_walk()
    {
        var walker = new Tag(100, 150, 48, 4, Npc: false);
        var idler = new Tag(260, 150, 48, 5, Npc: false);
        var tracker = NameTagTracker.ForPlayers();
        var t0 = DateTimeOffset.UnixEpoch;
        tracker.Confirm(World(0, 0, [], walker, idler), [Seen("Skeen", walker), Seen("mokpo", idler)], t0);
        for (int i = 1; i <= 8; i++)
        {
            var frame = World(0, 0, [], walker with { X = walker.X + 9 * i }, idler);
            tracker.Follow(frame, t0.AddMilliseconds(70 * i));
            Assert.Equal(102 + 9 * i, PlateOf(tracker, frame, "Skeen").X);
            Assert.Equal(262, PlateOf(tracker, frame, "mokpo").X);
        }
    }

    [Fact]
    public void A_tag_found_on_an_older_frame_is_found_again_further_away()
    {
        var walker = new Tag(100, 150, 48, 4, Npc: false);
        var tracker = NameTagTracker.ForPlayers();
        var t0 = DateTimeOffset.UnixEpoch;
        tracker.Confirm(World(0, 0, [], walker), [Seen("Skeen", walker)], t0);   // the scan's picture, 2 s ago
        var now = World(0, 0, [], walker with { X = 260 });
        tracker.Follow(now, t0.AddSeconds(2));
        Assert.Equal(262, PlateOf(tracker, now, "Skeen").X);
    }
}
