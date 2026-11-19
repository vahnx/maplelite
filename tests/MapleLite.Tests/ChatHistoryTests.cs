using System.Text.Json;
using MapleLite.Core.Capture;
using MapleLite.Core.Game;
using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;
using MapleLite.Plugins.ChatHistory;
using MapleLite.Plugins.Loot;
using Xunit;

namespace MapleLite.Tests;

/// <summary>
/// The MapleMemory app's --self-test checks, one for one, plus tests for what the port changed.
/// MessageParser.StripTrailingAt is a static switch, so tests that touch it share a collection (no parallel runs).
/// </summary>
[CollectionDefinition("ChatParser", DisableParallelization = true)]
public class ChatParserCollection { }

[Collection("ChatParser")]
public class ChatClassifierTests
{
    [Theory]
    [InlineData("[Buddy] Alice : hello", "Buddy")]
    [InlineData("[From Alice] hello", "Whisper")]
    [InlineData("Alice wants to trade with you.", "Trade")]
    [InlineData("Alice: want to trade apples?", "General")]           // trade conversation
    [InlineData("Alice: I lost fame yesterday", "General")]           // fame conversation
    [InlineData("[19:42] Alice: hello", "General")]                   // timestamp
    [InlineData("[1 9:42] Alice: hello", "General")]                  // OCR timestamp
    [InlineData("Trade request from 'DiPLOMaT'", "Trade")]            // trade dialog from a sample screenshot
    [InlineData("[22:48] Pomp>> Double it and give it to the next person", "Whisper")]
    [InlineData("[System] Alice requests a trade.", "Trade")]
    [InlineData("Alice has invited you to trade.", "Trade")]
    [InlineData("Alice sent you a trade request", "Trade")]
    [InlineData("The trade has been completed.", "Trade")]
    [InlineData("Trade request declined", "Trade")]
    [InlineData("Trade with Alice cancelled", "Trade")]
    [InlineData("tr ade request from Alice", "Trade")]                // OCR spacing
    [InlineData("Trade", "Trade")]                                    // dialog heading
    [InlineData("[20:05] Alice wants to trade with you.", "Trade")]
    [InlineData("[19:55] Alice : want trade request?", "General")]    // player chat stays chat
    [InlineData("[22:48] Alice : I gained an item (lol)", "General")] // player chat is never loot
    public void Channel(string text, string channel) => Assert.Equal(channel, Classifier.Parse(text).Channel);

    [Theory]
    [InlineData("TRADING LEATHERS FOR MANA POTS")]      // speech bubble
    [InlineData("Selling gloves 150k trade me")]        // ad mentioning trade
    public void Not_a_trade(string text) => Assert.NotEqual("Trade", Classifier.Parse(text).Channel);

    [Fact] public void Ad_fragment_is_not_a_whisper() => Assert.NotEqual("Whisper", Classifier.Parse("[Yellow Square] S>int+10 earning 2m").Channel);

    [Theory]
    [InlineData("Your fame has decreased.", "− fame · received")]
    [InlineData("You have increased Alice's fame.", "+ fame · given")]
    [InlineData("You have decreased Bob's fame.", "− fame · given")]
    [InlineData("Bob has increased your fame.", "+ fame · received")]
    public void Fame_direction(string text, string direction) => Assert.Equal(direction, Classifier.Parse(text).Direction);

    [Fact]
    public void Loot_channel_with_summary()
    {
        var mesos = Classifier.Parse("You have gained mesos (+5800)");
        Assert.Equal("Loot", mesos.Channel);
        Assert.Equal("+5,800 mesos", mesos.Direction);
        Assert.Equal("Snail Shell", Classifier.Parse("You have gained an item in the Etc tab (Snail Shell)").Direction);
    }
}

[Collection("ChatParser")]
public class ScrollTrackerTests
{
    [Fact]
    public void Basic_sequence()
    {
        var t = new ScrollTracker();
        Assert.Equal(2, t.Read(["Alice: hi", "Bob: hello"]).Count);           // first
        Assert.Empty(t.Read(["Alice: hi", "Bob: hello"]));                     // unchanged
        Assert.Single(t.Read(["Bob: hello", "Bob: hello"]));                   // repeated
        Assert.Empty(t.Read([]));                                              // empty
        Assert.Empty(t.Read(["Bob: hello", "Bob: hello"]));                    // empty recovery
    }

    [Fact]
    public void New_price_is_not_fuzzy_suppressed()
    {
        var t = new ScrollTracker();
        t.Read(["Seller: selling attack gloves for 15000000 mesos"]);
        Assert.Single(t.Read(["Seller: selling attack gloves for 16000000 mesos"]));
    }

    [Fact]
    public void Scroll_bursts_and_gaps()
    {
        var t = new ScrollTracker();
        t.Read(["SellerA: one", "SellerB: two", "SellerC: three", "SellerD: four"]);
        Assert.Equal(2, t.Read(["SellerC: three", "SellerD: four", "SellerE: five", "SellerF: six"]).Count);
        Assert.False(t.LastHadGap);
        Assert.Equal(2, t.Read(["SellerG: seven", "SellerH: eight"]).Count);
        Assert.True(t.LastHadGap);                                             // complete replacement reported
        Assert.Empty(t.Read(["SellerG: seven", "SellerH: eight"]));
        Assert.False(t.LastHadGap);                                            // stable frame clears the gap
    }

    [Fact]
    public void Repeated_advert_after_scroll_is_kept()
    {
        var t = new ScrollTracker();
        t.Read(["Alpha: sale", "Beta: sale"]);
        Assert.Single(t.Read(["Beta: sale", "Alpha: sale"]));
    }

    [Fact]
    public void Row_missed_by_OCR_keeps_alignment()
    {
        var t = new ScrollTracker();
        t.Read(["First: hello", "Second: hello", "Third: hello"]);
        Assert.Equal(["Fourth: hello"], t.Read(["First: hello", "Third: hello", "Fourth: hello"]));
    }

    [Fact]
    public void Timestamp_punctuation_jitter_is_not_a_new_line()
    {
        var t = new ScrollTracker();
        t.Read(["[1 9:531 ChasinDraqon : +7 scrolled"]);
        Assert.Empty(t.Read(["[1 9:53] ChasinDragon : +7 scrolled"]));
    }
}

/// <summary>The app's PrepareChat checks, now against Core's shared OcrPreprocessor.ChatGlyphs.</summary>
public class ChatGlyphTests
{
    [Fact]
    public void Background_suppressed_glyphs_kept_banners_preserved()
    {
        var image = new Frame(20, 2);
        Array.Fill(image.Px, unchecked((int)0xff888888));
        image.Px[3] = unchecked((int)0xffffffff);
        image.Px[4] = unchecked((int)0xffff9900);
        for (int x = 0; x < 20; x++) image.Px[20 + x] = unchecked((int)0xffffcc00);
        var prepared = OcrPreprocessor.ChatGlyphs.Apply(image);
        Assert.Equal(Pixel.White, prepared.Px[0]);                               // dim background suppressed
        Assert.Equal(Pixel.Black, prepared.Px[3]);                               // bright white glyph kept
        Assert.Equal(Pixel.Black, prepared.Px[4]);                               // bright colored glyph kept
        Assert.Equal(image.Px[20], prepared.Px[20]);                             // megaphone background preserved
        Assert.Equal(unchecked((int)0xff888888), image.Px[0]);                   // source unchanged
    }

    [Fact]
    public void Low_saturation_pink_banner_is_kept()
    {
        var pink = new Frame(20, 2);
        Array.Fill(pink.Px, unchecked((int)0xffe0adc8));
        Assert.Equal(pink.Px[0], OcrPreprocessor.ChatGlyphs.Apply(pink).Px[0]);
    }

    // The game's own lines ("Bronze Ore x8 earned. (Etc)") are grey, about 190, over the dim chat panel.
    [Fact]
    public void Grey_system_text_is_kept_over_the_dim_panel()
    {
        var row = new Frame(20, 1);
        Array.Fill(row.Px, Pixel.Rgb(60, 60, 64));
        row.Px[5] = Pixel.Rgb(190, 190, 190);
        row.Px[6] = Pixel.Rgb(150, 150, 150); // a dim edge stays background
        var o = OcrPreprocessor.ChatGlyphs.Apply(row);
        Assert.Equal(Pixel.Black, o.Px[5]);
        Assert.Equal(Pixel.White, o.Px[6]);
        Assert.Equal(Pixel.White, o.Px[0]);
    }

    [Fact]
    public void Over_a_bright_scene_only_white_text_counts()
    {
        var row = new Frame(20, 1);
        Array.Fill(row.Px, Pixel.Rgb(185, 185, 190)); // snow behind the panel
        row.Px[5] = Pixel.Rgb(250, 250, 250);
        var o = OcrPreprocessor.ChatGlyphs.Apply(row);
        Assert.Equal(Pixel.Black, o.Px[5]);
        Assert.Equal(Pixel.White, o.Px[0]);
    }
}

[Collection("ChatParser")]
public class SpamFilterTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Now;
    static Entry Ad(string who, string body, int minutes = 0, string channel = "General") => new(Now.AddMinutes(minutes), channel, "", $"[19:55] {who} : {body}");

    [Fact]
    public void Same_sender_repeats()
    {
        var spam = new SpamFilter();
        var ad = Ad("Alice", "Selling gloves 150k pm me");
        Assert.False(spam.ShouldSuppress(ad));                                                     // first advert kept
        spam.Remember(ad);
        Assert.True(spam.ShouldSuppress(Ad("Alice", "Selling gloves 150k pm me")));                // exact repeat
        Assert.True(spam.ShouldSuppress(Ad("Alice", "Selling gloves 150k pm mee")));               // one character
        Assert.True(spam.ShouldSuppress(Ad("Alice", "SELLING gloves 150k pm me!!!! @@@@@")));      // punctuation
        Assert.False(spam.ShouldSuppress(Ad("Alicee", "Selling gloves 150k pm me")));              // similar names stay separate
        Assert.False(spam.ShouldSuppress(Ad("Bob", "Selling gloves 150k pm me")));                 // different sender
        Assert.False(spam.ShouldSuppress(Ad("Alice", "Selling gloves 160k pm me")));               // changed price
        Assert.False(spam.ShouldSuppress(Ad("Alice", "Buying gloves 150k pm me")));                // buy vs sell
        Assert.False(spam.ShouldSuppress(Ad("Alice", "Selling gloves 150k pm me", channel: "Whisper"))); // channels separate
        Assert.False(spam.ShouldSuppress(Ad("Alice", "Selling gloves 150k pm me", 11)));           // expired
    }

    [Fact]
    public void Events_are_never_spam_and_senders_are_never_guessed()
    {
        var events = new SpamFilter();
        var fame = new Entry(Now, "Fame", "", "Alice has increased your fame.");
        events.Remember(fame);
        Assert.False(events.ShouldSuppress(fame));
        var trade = new Entry(Now, "Trade", "", "Alice wants to trade with you.");
        events.Remember(trade);
        Assert.False(events.ShouldSuppress(trade));
        Assert.Equal("Alice", SpamFilter.ParseSender(new Entry(Now, "Whisper", "", "[From Alice] Hello"))?.Sender);
        Assert.Null(SpamFilter.ParseSender(new Entry(Now, "Unknown", "", "a wrapped continuation")));
    }

    [Fact]
    public void Real_log_repeat_with_spaced_sender()
    {
        var spaced = new SpamFilter();
        var e = new Entry(Now, "Unknown", "", "[1 9:57] kirnl 23 : S>THSword 130k S>overall INT");
        spaced.Remember(e);
        Assert.True(spaced.ShouldSuppress(e with { Time = Now.AddSeconds(2) }));
        Assert.True(spaced.ShouldSuppress(e with { Channel = "General", Time = Now.AddSeconds(3) }));            // Unknown/General jitter
        Assert.False(spaced.ShouldSuppress(e with { Text = "[19:57] kim123 : S>THSword 130k S>overall INT", Time = Now.AddSeconds(4) })); // different player
    }
}

[Collection("ChatParser")]
public class MessageParserTests
{
    [Fact]
    public void Real_log_line_splits_sender_and_time()
    {
        var p = MessageParser.Parse("[1 9:57] kirnl 23 : S>THSword 130k S>overall INT");
        Assert.Equal(("19:57", "kirnl23", "S>THSword 130k S>overall INT"), (p.GameTime, p.Username, p.Body));
    }

    [Fact]
    public void Senders_are_not_invented()
    {
        Assert.Equal("", MessageParser.Parse("[19:58] : B>GFA 1.6M").Username);
        Assert.Equal("", MessageParser.Parse("[19:58] Helaena NX 1:17").Username);
    }

    [Fact]
    public void Display_fields_derive_from_text_and_are_not_stored()
    {
        var now = DateTimeOffset.Now;
        Assert.Equal("hello", new Entry(now, "General", "", "[19:57] Alice : hello").Message);
        Assert.DoesNotContain("GameTime", JsonSerializer.Serialize(new Entry(now, "Unknown", "", "[1 9:57] kirnl 23 : S>THSword 130k")));
    }

    [Fact]
    public void Green_whisper_sender()
    {
        Assert.Equal("Pomp", MessageParser.Parse("[22:48] Pomp>> Double it and give it to the next person").Username);
    }

    [Fact]
    public void Items_and_names_in_brackets()
    {
        var wg = MessageParser.Parse("[22:56] : [Work Gloves] S>II att wg dropped price am 61m");
        Assert.Equal(("Work Gloves", "Selling", ""), (wg.Item, wg.Side, wg.Username));
        var brk = MessageParser.Parse("[Seller1] [Scroll 60%] S>selling 50k");
        Assert.Equal(("Seller1", "Scroll 60%", "Selling"), (brk.Username, brk.Item, brk.Side));
        Assert.Equal("Seller1", MessageParser.Parse("[22:56] [Seller1] : [Gloves] selling").Username);
        Assert.Equal("Alice", MessageParser.Parse("[Buddy] Alice : hi").Username);
        var beanie = MessageParser.Parse("[00:17] Quadwoofer : [Black Cat Beaniel B< nx trash 800k (for 20)");
        Assert.Equal(("Quadwoofer", "Black Cat Beanie", "Buying"), (beanie.Username, beanie.Item, beanie.Side));
        var scepter = MessageParser.Parse("[00:17] Quadwoofer : [Starlight Heart Scepter) S< c/0 2.3");
        Assert.Equal(("Starlight Heart Scepter", "Selling"), (scepter.Item, scepter.Side));
        Assert.Equal("Steel Shield", MessageParser.Parse("[00:17] Alice : [Steel Shield] hello").Item);
        Assert.Equal("", MessageParser.Parse("[00:17] Alice : hello all").Item);
    }

    [Theory]
    [InlineData("[22:56] Alice : s 50k gloves", "Selling")]
    [InlineData("[22:56] Alice : S < gloves", "Selling")]
    [InlineData("[22:56] Alice : B > gloves", "Buying")]
    [InlineData("[22:56] Alice : b<50k", "Buying")]
    [InlineData("[22:56] Alice : buying gloves", "")]
    public void Buy_sell_side(string text, string side) => Assert.Equal(side, MessageParser.Parse(text).Side);

    [Fact]
    public void Filler_at_signs()
    {
        try
        {
            MessageParser.StripTrailingAt = false;
            Assert.Equal("@@@@", MessageParser.Parse("[22:56] Alice : aaaa").Body);
            Assert.Equal("S>gloves 50k @@@@@@", MessageParser.Parse("[22:56] Alice : S>gloves 50k @@@@@@").Body);
            MessageParser.StripTrailingAt = true;
            Assert.Equal("S>gloves 50k", MessageParser.Parse("[22:56] Alice : S>gloves 50k @@@@ aaaa").Body);
            Assert.Equal("S>gloves\n50k", MessageParser.Parse("[22:56] Alice : S>gloves @@@\n50k @@@@").Body);
            Assert.Equal("mail me @ home", MessageParser.Parse("[22:56] Alice : mail me @ home").Body);
        }
        finally { MessageParser.StripTrailingAt = true; }
    }

    [Fact]
    public void OCR_letters_in_timestamps()
    {
        var p = MessageParser.Parse("[2O:O5] Alice : hi");
        Assert.Equal(("20:05", "Alice"), (p.GameTime, p.Username));
        Assert.Equal("Alice : hi", MessageParser.StripStamp("[19:57] Alice : hi"));
        Assert.Equal("Sezuzi", MessageParser.Parse("[20:05] Sezuzi • Selling station\nother station").Username);
    }
}

[Collection("ChatParser")]
public class ChatLayoutTests
{
    [Fact]
    public void Wrapped_lines_join_their_message()
    {
        var wraps = ChatLayout.Assemble([new ChatRow("[20:05] Alice : Selling wea", 1, 3, 535, 15), new ChatRow("pon station", 22, 19, 120, 31), new ChatRow("[20:05] Bob : buying gloves", 1, 35, 220, 47)], 540, 64);
        Assert.Equal(2, wraps.Count);
        Assert.Contains("\npon station", wraps[0].Text);
        Assert.Equal("Selling wea\npon station", MessageParser.Parse(wraps[0].Text).Body);
    }

    [Fact]
    public void Orphans_and_clipped_lines_are_marked_partial()
    {
        var orphan = ChatLayout.Assemble([new ChatRow("orphan fragment", 22, 0, 200, 11), new ChatRow("[20:05] Bob : hello", 1, 15, 220, 27)], 540, 64);
        Assert.Equal(2, orphan.Count);
        Assert.True(orphan[0].Partial);
        Assert.True(ChatLayout.Assemble([new ChatRow("[20:05] Bob : hello", 1, 52, 220, 64)], 540, 64)[0].Partial);
    }

    [Fact]
    public void Lines_starting_with_bracket_start_new_messages()
    {
        var ambiguous = ChatLayout.Assemble([new ChatRow("[20:05] Alice : hello", 1, 3, 300, 15), new ChatRow("C CH3S", 90, 19, 130, 31)], 540, 64);
        Assert.Single(ambiguous);
        Assert.Contains("\nC CH3S", ambiguous[0].Text);
        Assert.Equal(2, ChatLayout.Assemble([new ChatRow("[20:05] Alice : hello", 1, 3, 300, 15), new ChatRow("[20:06] Bob : hi", 1, 19, 300, 31)], 540, 64).Count);
    }

    [Fact]
    public void Settler_waits_for_wraps_and_keeps_the_first_time()
    {
        var now = DateTimeOffset.Now;
        var settle = new MessageSettler();
        var shortMsg = new Entry(now, "General", "", "[20:05] Alice : Selling weapon");
        Assert.Empty(settle.Push([shortMsg], now));
        var longMsg = shortMsg with { Text = shortMsg.Text + "\nstation", Time = now.AddMilliseconds(100) };
        Assert.Empty(settle.Push([longMsg], now.AddMilliseconds(100)));
        var settled = settle.Push([], now.AddMilliseconds(450));
        Assert.Single(settled);
        Assert.EndsWith("station", settled[0].Text);
        Assert.Equal(now, settled[0].Time);
        settle.Push([shortMsg], now);
        Assert.Single(settle.Flush());
    }
}

public class LootTests
{
    [Fact]
    public void Pickups()
    {
        var mesos = LootParser.Parse("You have gained mesos (+5800)");
        Assert.Equal(("Mesos", 5800L), (mesos!.Kind, mesos.Quantity));
        Assert.Equal(5800, LootParser.Parse("You have gained mesos (+5,800)")!.Quantity);
        Assert.Equal("MesosLost", LootParser.Parse("You have lost mesos (-50)")!.Kind);
        var shell = LootParser.Parse("You have gained an item in the Etc tab (Snail Shell)");
        Assert.Equal(("Item", "Snail Shell", 1L), (shell!.Kind, shell.Name, shell.Quantity));
        var potions = LootParser.Parse("You have gained items in the Use tab (Red Potion 5)");
        Assert.Equal(("Red Potion", 5L), (potions!.Name, potions.Quantity));
        Assert.Equal("Scroll for Gloves for ATT 60%", LootParser.Parse("You have gained an item (Scroll for Gloves for ATT 60%)")!.Name);
        Assert.Null(LootParser.Parse("You have gained experience (+15)"));
        Assert.Null(LootParser.Parse("You have gained fame (+1)"));
        Assert.NotNull(LootParser.Parse("[22:48] Alice : I gained an item (lol)"));   // parser matches; Classifier keeps it as chat
        var liquid = LootParser.Parse("You have gained iterns in the Etc tab (Squishy Liquid 2)");
        Assert.Equal(("Squishy Liquid", 2L), (liquid!.Name, liquid.Quantity));
        Assert.NotNull(LootParser.Parse("You have gained ltems (Squishy Liquid 2)"));
    }
}

[Collection("ChatParser")]
public class PartialRepeatTests
{
    [Fact]
    public void Cut_off_copies()
    {
        var now = DateTimeOffset.Now;
        var full = new Entry(now, "Megaphone", "", "[Gloves Atta'! croll: Intermediatel TRADING FOR 2 HA");
        var cut = new Entry(now.AddSeconds(5), "Megaphone", "", "Scroll: Intermediatel TRADING FOR 2 HA");
        var f = new PartialRepeatFilter();
        f.Remember(full);
        Assert.True(f.ShouldSkip(cut));                                                   // cut-off copy skipped
        Assert.False(new PartialRepeatFilter().ShouldSkip(cut));                          // first copy kept
        var later = new PartialRepeatFilter();
        later.Remember(cut);
        Assert.False(later.ShouldSkip(full));                                             // longer copy kept
        Assert.False(f.ShouldSkip(new Entry(now.AddSeconds(5), "Megaphone", "", "Selling gloves 150k pm me")));
        var buy = new PartialRepeatFilter();
        buy.Remember(new Entry(now, "Megaphone", "", "Selling gloves 150k pm me"));
        Assert.False(buy.ShouldSkip(new Entry(now, "Megaphone", "", "Buying gloves 150k pm me")));
    }
}

public class TimeWindowTests
{
    static readonly DateTime Day = new(2026, 11, 15);
    static readonly DateTimeOffset At = new(Day.AddHours(14.75));

    [Fact]
    public void Dated_windows()
    {
        var dated = TimeWindow.Build(Day, "14:30", Day, "15:00");
        Assert.True(dated.Matches(At));
        Assert.False(dated.Matches(new DateTimeOffset(Day.AddHours(15.1))));
        Assert.False(dated.Matches(new DateTimeOffset(Day.AddHours(14.4))));
        Assert.True(TimeWindow.Build(Day, "15:00", Day, "15:00").Matches(new DateTimeOffset(Day.AddHours(15).AddSeconds(59))));
        var after = TimeWindow.Build(Day, "14:30", null, "");
        Assert.True(after.Matches(At));
        Assert.False(after.Matches(new DateTimeOffset(Day.AddHours(14))));
    }

    [Fact]
    public void Clock_only_windows_apply_every_day()
    {
        var from = TimeWindow.Build(null, "19:00", null, "");
        Assert.True(from.Matches(new DateTimeOffset(Day.AddHours(20))));
        Assert.False(from.Matches(new DateTimeOffset(Day.AddHours(18))));
        Assert.True(from.Matches(new DateTimeOffset(Day.AddDays(-3).AddHours(20))));
        var between = TimeWindow.Build(null, "09:00", null, "12:00");
        Assert.True(between.Matches(new DateTimeOffset(Day.AddHours(11))));
        Assert.False(between.Matches(new DateTimeOffset(Day.AddHours(13))));
        Assert.True(TimeFilter.Open.Matches(At));
    }

    [Fact]
    public void Bad_input_is_rejected()
    {
        Assert.Throws<FormatException>(() => TimeWindow.Build(Day, "25:00", null, ""));
        Assert.Throws<FormatException>(() => TimeWindow.Build(null, "15:00", null, "14:00"));
    }

    [Fact]
    public void Zones()
    {
        Assert.Equal("Eastern Standard Time", ClockZone.Find("Eastern Standard Time").Id);
        Assert.NotNull(ClockZone.Find("not a zone"));
    }
}

/// <summary>What the port added: color hints on shared OCR, chat finding from rows, import, CSV, the pipeline.</summary>
public class ChatHistoryPortTests
{
    [Fact]
    public void Green_row_is_a_whisper_hint_and_full_color_is_a_megaphone()
    {
        var f = new Frame(100, 20);
        Array.Fill(f.Px, Pixel.Rgb(30, 30, 30));
        // green glyph strokes covering ~20% of the row band (text is sparse; >48% would read as a banner)
        for (int x = 10; x < 60; x += 5) for (int y = 4; y < 14; y++) { f.Px[y * 100 + x] = Pixel.Rgb(60, 230, 60); f.Px[y * 100 + x + 1] = Pixel.Rgb(60, 230, 60); }
        Assert.Equal("Whisper", ChatReader.Hint(f, new TextBounds(10, 4, 50, 10)));
        var banner = new Frame(100, 20);
        Array.Fill(banner.Px, Pixel.Rgb(250, 180, 40));
        Assert.Equal("Megaphone", ChatReader.Hint(banner, new TextBounds(0, 0, 100, 20)));
        var plain = new Frame(100, 20);
        Array.Fill(plain.Px, Pixel.Rgb(220, 220, 220));
        Assert.Null(ChatReader.Hint(plain, new TextBounds(0, 0, 100, 20)));
    }

    [Fact]
    public void Chat_area_from_timestamped_rows()
    {
        var rows = new List<ChatRow> { new("[20:05] Alice : hi", 290, 640, 500, 652), new("[20:06] Bob : hey", 290, 656, 480, 668), new("Some scenery text", 900, 300, 1000, 312) };
        var area = ChatDetector.FromRows(rows, 1366, 768);
        Assert.NotNull(area);
        Assert.Equal(278, area!.Value.X);
        Assert.True(area.Value.Y <= 640 && area.Value.Bottom >= 668);
        Assert.Null(ChatDetector.FromRows([new("[20:05] only one", 0, 0, 100, 12)], 1366, 768));
    }

    [Fact]
    public void Import_copies_new_lines_once_and_leaves_the_app_files_alone()
    {
        var root = Path.Combine(Path.GetTempPath(), "ml-import-" + Guid.NewGuid().ToString("N"));
        var app = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
        var mine = Path.Combine(root, "mine");
        try
        {
            var a = JsonSerializer.Serialize(new Entry(DateTimeOffset.Parse("2026-11-15T10:00:00Z"), "General", "", "[10:00] Alice : hi"));
            var b = JsonSerializer.Serialize(new Entry(DateTimeOffset.Parse("2026-11-15T10:01:00Z"), "Whisper", "Received", "[From Bob] yo"));
            File.WriteAllLines(Path.Combine(app, "messages-2026-11.jsonl"), [a, b]);
            var before = File.ReadAllText(Path.Combine(app, "messages-2026-11.jsonl"));
            Assert.Equal((1, 2), Journal.Import(app, mine));
            Assert.Equal((0, 0), Journal.Import(app, mine));        // second import adds nothing
            File.AppendAllLines(Path.Combine(app, "messages-2026-11.jsonl"), [JsonSerializer.Serialize(new Entry(DateTimeOffset.Parse("2026-11-15T10:02:00Z"), "General", "", "[10:02] Cy : new"))]);
            Assert.Equal((1, 1), Journal.Import(app, mine));        // only the new line
            var journal = new Journal(mine);
            Assert.Equal(3, journal.Entries.Count);
            Assert.Equal("Whisper", journal.Entries[1].Channel);
            Assert.StartsWith(before, File.ReadAllText(Path.Combine(app, "messages-2026-11.jsonl")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void App_settings_read_including_the_chat_area()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ml-set-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), """{"SuppressSpam":true,"ManualChat":false,"Chat":{"X":280,"Y":630,"Width":546,"Height":74},"FrameWidth":1366,"FrameHeight":768}""");
            var s = Journal.AppSettings(dir)!;
            Assert.Equal(new ChatArea(280, 630, 546, 74), s.Chat);
            Assert.Equal(1366, s.FrameWidth);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Csv_neutralizes_formulas_and_quotes()
    {
        var lines = Csv.Lines([new Entry(DateTimeOffset.Parse("2026-11-15T10:00:00Z"), "General", "", "[10:00] Alice : =HYPERLINK(\"x\")")]).ToList();
        Assert.Equal(Csv.Header, lines[0]);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", lines[1]);
    }

    /// <summary>An engine that "sees" two chat lines in any image, at fixed spots (in enlarged-image pixels).</summary>
    sealed class ChatEngine : IOcrEngine
    {
        public string Name => "chat-fake";
        public int MaxImageDimension => 4000;
        public Task<IReadOnlyList<OcrLine>> RecognizeAsync(Frame image, CancellationToken ct)
        {
            OcrLine L(string t, double y) { var w = new OcrWord(t, new TextBounds(9, y, 600, 30)); return new OcrLine(t, w.Box, [w]); }
            // a chat crop is 546x74 → 3x = 1638x222; rows at crop y≈8 and y≈30
            IReadOnlyList<OcrLine> lines = image.W >= 1600 && image.H < 300
                ? [L("[20:05] Alice : selling gloves 150k", 24), L("[20:06] Bob : hello there friend", 90)]
                : [];
            return Task.FromResult(lines);
        }
    }

    [Fact]
    public async Task Pipeline_turns_host_frames_into_journal_entries()
    {
        var platform = new FakePlatform { Next = () => { var f = new Frame(1366, 768); Array.Fill(f.Px, Pixel.Rgb(40, 40, 50)); return f; } };
        await using var loop = new CaptureLoop(platform);
        var ocr = new OcrService(new ChatEngine());
        var pipeline = new ChatPipeline(loop, new ChatReader(ocr), new Region(280, 630, 546, 74), 1366, 768);
        loop.Start();
        pipeline.Start();
        var got = new List<Entry>();
        for (int i = 0; i < 100 && got.Count < 2; i++) { await Task.Delay(30); while (pipeline.TryTake(out var e)) got.Add(e!); }
        await pipeline.StopAsync();
        while (pipeline.TryTake(out var e)) got.Add(e!);
        Assert.Equal(2, got.Count);                    // same picture every frame: each line saved once
        Assert.Equal("Alice", got[0].Username);
        Assert.Equal("General", got[0].Channel);
        Assert.StartsWith("OCR", got[0].Source);
        Assert.Null(pipeline.Error);
    }

    [Fact]
    public async Task Pipeline_flags_a_resolution_change()
    {
        var platform = new FakePlatform();   // 800x600 frames
        await using var loop = new CaptureLoop(platform);
        var pipeline = new ChatPipeline(loop, new ChatReader(new OcrService(new ChatEngine())), new Region(280, 630, 546, 74), 1366, 768);
        loop.Start();
        pipeline.Start();
        for (int i = 0; i < 100 && !pipeline.AreaInvalid; i++) await Task.Delay(30);
        await pipeline.StopAsync();
        Assert.True(pipeline.AreaInvalid);
        Assert.Contains("size changed", pipeline.Error);
    }
}

[Collection("ChatParser")]
public class MentionsTests
{
    static Entry E(string text) => new(DateTimeOffset.Now, "General", "", text);

    [Fact]
    public void Matches_other_players_mentioning_my_name()
    {
        var me = Mentions.Split("Froggy, AltChar");
        Assert.True(Mentions.Of(E("Bob: hey froggy want to party?"), me));
        Assert.True(Mentions.Of(E("Bob: buying from AltChar"), me));
        Assert.False(Mentions.Of(E("Froggy: hello everyone"), me));
        Assert.False(Mentions.Of(E("Bob: Froggyland is great"), me));
        Assert.False(Mentions.Of(E("Bob: hi"), Mentions.Split("")));
    }
}
