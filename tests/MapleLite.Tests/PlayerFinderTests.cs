using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;
using MapleLite.Plugins.PlayerFinder;
using Xunit;

namespace MapleLite.Tests;

public class PlayerFinderTests
{
    [Fact]
    public void Nearby_roster_survives_missed_reads_and_still_expires_departed_players()
    {
        var roster = new NearbyRoster();
        var start = DateTimeOffset.UnixEpoch;
        Assert.Equal(["iMarket", "Meekrob"], roster.Update(start, ["Meekrob", "iMarket"], start));
        Assert.Equal(["iMarket", "Meekrob"], roster.Update(start.AddSeconds(4)));
        Assert.Equal(["iMarket", "Meekrob"], roster.Update(start.AddSeconds(6), [], start.AddSeconds(5)));
        Assert.Equal(["iMarket", "Meekrob"], roster.Update(start.AddSeconds(8), ["IMARKET"], start.AddSeconds(6)));
        Assert.Equal(["iMarket"], roster.Update(start.AddSeconds(13), [], start.AddSeconds(12)));
        Assert.Empty(roster.Update(start.AddSeconds(19), [], start.AddSeconds(18)));
    }

    [Fact]
    public void Nearby_roster_accepts_slow_reads_and_clears_after_a_long_capture_gap()
    {
        var roster = new NearbyRoster();
        var start = DateTimeOffset.UnixEpoch;
        Assert.Equal(["iMarket"], roster.Update(start.AddSeconds(5), ["iMarket"], start));
        Assert.Equal(["iMarket"], roster.Update(start.AddSeconds(20)));
        Assert.Empty(roster.Update(start.AddSeconds(25)));
    }

    [Fact]
    public void Chat_cutout_follows_expansion_resize_and_collapse()
    {
        var frame = new Frame(1366, 768);
        Set(823, 716, 0xddffff);
        Set(821, 716, 0xddffff);
        foreach (int arrow in new[] { 615, 500 })
        {
            Set(837, arrow, 0x8899aa);
            Set(845, arrow, 0x8899aa);
            Set(836, arrow + 2, 0xeeeeff);
            Set(846, arrow + 2, 0xeeeeff);
            Assert.Equal(new Region(282, arrow - 17, 568, 748 - arrow), ChatPanel.Find(frame));
            Set(837, arrow, 0);
        }

        Set(821, 716, 0x44aadd);
        Assert.Equal(new Region(282, 700, 568, 31), ChatPanel.Find(frame));
        Set(823, 716, 0);
        Assert.Null(ChatPanel.Find(frame));

        void Set(int x, int y, int color) => frame.Px[y * frame.W + x] = color;
    }

    [Fact]
    public void Speech_bubble_points_below_its_sender_but_chat_panel_text_cannot_supply_a_position()
    {
        var frame = new Frame(200, 200);
        Array.Fill(frame.Px, 0x336699);
        Fill(45, 40, 86, 1, 0);
        Fill(45, 41, 86, 1, 0xffffff);
        Fill(45, 42, 86, 49, 0xeeeeee);
        Fill(45, 91, 38, 1, 0);
        Fill(83, 91, 10, 1, 0xeeeeee);
        Fill(93, 91, 38, 1, 0);
        var sender = OcrLine.FromWords([new("iMarket", new(58, 50, 37, 10)), new(":", new(98, 51, 2, 8))]);

        var bubble = Assert.Single(SpeechBubbles.Find(frame, [sender], default, null));
        Assert.Equal("iMarket", bubble.Name);
        Assert.Equal(87, bubble.TailX);
        Assert.Empty(SpeechBubbles.Find(frame, [sender], new Region(40, 40, 100, 60), null));

        void Fill(int x, int y, int width, int height, int color)
        {
            for (int row = y; row < y + height; row++)
                Array.Fill(frame.Px, color, row * frame.W + x, width);
        }
    }
}
