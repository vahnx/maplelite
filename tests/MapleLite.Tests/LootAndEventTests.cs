using System.Text.Json;
using MapleLite.Core.Capture;
using MapleLite.Core.Game;
using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;
using MapleLite.Plugins.Loot;
using MapleLite.Sdk;
using Xunit;

namespace MapleLite.Tests;

public class EventBusTests
{
    [Fact]
    public void Subscribers_get_events_until_disposed_and_last_is_remembered()
    {
        var bus = new EventBus();
        var got = new List<string>();
        var sub = bus.Subscribe<ChatMessageEvent>(e => got.Add(e.Text));
        Assert.Null(bus.Last<ChatAreaEvent>());
        bus.Publish(new ChatMessageEvent(DateTimeOffset.Now, "General", "one", "OCR", "test"));
        sub.Dispose();
        bus.Publish(new ChatMessageEvent(DateTimeOffset.Now, "General", "two", "OCR", "test"));
        Assert.Equal(["one"], got);
        Assert.Equal("two", bus.Last<ChatMessageEvent>()!.Text);
    }

    [Fact]
    public void A_throwing_handler_does_not_stop_others()
    {
        var bus = new EventBus();
        Exception? reported = null;
        bus.OnHandlerError = e => reported = e;
        int ok = 0;
        bus.Subscribe<ChatAreaEvent>(_ => throw new InvalidOperationException("plugin bug"));
        bus.Subscribe<ChatAreaEvent>(_ => ok++);
        bus.Publish(new ChatAreaEvent(new Region(0, 0, 10, 10), 100, 100, "test"));
        Assert.Equal(1, ok);
        Assert.IsType<InvalidOperationException>(reported);
    }
}

public class GameTextTests
{
    [Fact]
    public void Pickups_but_not_players_quoting_them()
    {
        Assert.Equal(5800, GameText.Pickup("You have gained mesos (+5800)")!.Quantity);
        Assert.Equal(5800, GameText.Pickup("[20:05] You have gained mesos (+5800)")!.Quantity);
        Assert.Null(GameText.Pickup("[22:48] Alice : I gained an item (lol)"));
        Assert.Equal("Alice : hi", GameText.StripStamp("[19:57] Alice : hi"));
    }
}

public class LootPluginTests
{
    static string TempDir() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ml-loot-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void Imports_pickups_from_MapleMemory_journals_once()
    {
        var app = TempDir(); var mine = TempDir();
        try
        {
            string Line(string channel, string text, string source, int min) =>
                $"{{\"Time\":\"2026-11-15T10:{min:00}:00-05:00\",\"Channel\":\"{channel}\",\"Direction\":\"\",\"Text\":\"{text}\",\"Source\":\"{source}\"}}";
            File.WriteAllLines(Path.Combine(app, "messages-2026-11.jsonl"),
            [
                Line("Loot", "You have gained mesos (+5800)", "OCR · loot", 1),
                Line("Loot", "You have gained items in the Use tab (Red Potion 5)", "OCR · assembled", 2),
                Line("General", "[10:03] Alice : hi", "OCR · assembled", 3),
                Line("Loot", "not a pickup at all", "OCR · loot", 4),
            ]);
            var log = new LootLog(mine);
            Assert.Equal(2, log.ImportJournals(app));
            Assert.Equal(0, log.ImportJournals(app));                        // nothing twice
            var again = new LootLog(mine);                                     // reloads from disk
            Assert.Equal(2, again.Records.Count);
            Assert.Equal(("Mesos", 5800L, "OCR · notice"), (again.Records[0].Kind, again.Records[0].Quantity, again.Records[0].Source));
            Assert.Equal(("Red Potion", 5L, "chat"), (again.Records[1].Name, again.Records[1].Quantity, again.Records[1].Source));
        }
        finally { Directory.Delete(app, true); Directory.Delete(mine, true); }
    }

    [Fact]
    public void Notice_crop_blanks_the_chat_box()
    {
        var f = new Frame(1000, 800);
        Array.Fill(f.Px, Pixel.White);
        var chat = new Region(300, 600, 400, 100); // overlaps the crop's left part (crop starts at x=450, y=360)
        var crop = LootReader.Crop(f, chat);
        Assert.Equal(new Region(0, 0, 550, 384), crop.Bounds);
        Assert.Equal(Pixel.Black, crop.At(0, 600 - 360));                    // inside the chat box
        Assert.Equal(Pixel.Black, crop.At(249, 699 - 360));
        Assert.Equal(Pixel.White, crop.At(250, 600 - 360));                  // right of the chat box
        Assert.Equal(Pixel.White, crop.At(0, 0));
        Assert.Equal(Pixel.White, LootReader.Crop(f, null).At(0, 600 - 360)); // no chat box known: nothing blanked
    }

    [Fact]
    public void Item_totals_group_by_name()
    {
        var t = DateTimeOffset.Parse("2026-11-15T10:00:00Z");
        var totals = LootView.Totals(
        [
            new LootRecord(t, "Item", "Snail Shell", 1, "", "chat"),
            new LootRecord(t.AddMinutes(1), "Item", "snail shell", 3, "", "chat"),
            new LootRecord(t.AddMinutes(2), "Mesos", "Mesos", 500, "", "chat"),
            new LootRecord(t.AddMinutes(3), "Item", "Red Potion", 5, "", "chat"),
        ]);
        Assert.Equal(2, totals.Count);
        Assert.Equal(("Red Potion", 5L), (totals[0].Name, totals[0].Quantity));   // most recent first
        Assert.Equal((4L, 2), (totals[1].Quantity, totals[1].Pickups));
    }

    [Theory]
    [InlineData(5_000, "5,000")]
    [InlineData(250_000, "250K")]
    [InlineData(12_000_000, "12M")]
    public void Stack_labels(long q, string text) => Assert.Equal(text, LootView.StackLabel(q).Text);

    sealed class NoticeEngine : IOcrEngine
    {
        public string Name => "notice-fake";
        public int MaxImageDimension => 4000;
        public Task<IReadOnlyList<OcrLine>> RecognizeAsync(Frame image, CancellationToken ct)
        {
            var w = new OcrWord("You have gained mesos (+5800)", new TextBounds(30, 60, 600, 30));
            var v = new OcrWord("Some scenery text", new TextBounds(30, 160, 300, 30));
            return Task.FromResult<IReadOnlyList<OcrLine>>([new OcrLine(w.Text, w.Box, [w]), new OcrLine(v.Text, v.Box, [v])]);
        }
    }

    [Fact]
    public async Task Reader_reports_each_visible_notice_once()
    {
        var platform = new FakePlatform();
        await using var loop = new CaptureLoop(platform);
        var got = new List<LootDrop>();
        var reader = new LootReader(loop, new OcrService(new NoticeEngine()), new EventBus(), (_, d, _) => { lock (got) got.Add(d); });
        loop.Start();
        reader.Start();
        for (int i = 0; i < 100 && reader.Reads < 4; i++) await Task.Delay(50);
        await reader.StopAsync();
        Assert.True(reader.Reads >= 4);
        var drop = Assert.Single(got);                      // still on screen in later reads: counted once
        Assert.Equal(("Mesos", 5800L), (drop.Kind, drop.Quantity));
        Assert.Null(reader.Error);
    }
}
