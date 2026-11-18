using MapleLite.Core.Game;
using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;
using MapleLite.Plugins.SpawnTimer;
using Xunit;

namespace MapleLite.Tests;

public class SpawnTimerTests
{
    static readonly int Sky = Pixel.Rgb(60, 110, 220);

    /// <summary>A plain picture with damage-number-like digits (orange, white ring, black ring) at the given spots.</summary>
    internal static Frame WithDigits(params (int x, int y)[] digits)
    {
        var f = new Frame(800, 500);
        Array.Fill(f.Px, Sky);
        foreach (var (x, y) in digits)
            for (int j = -4; j < 32; j++)
                for (int i = -4; i < 24; i++)
                {
                    bool inner = i >= 0 && i < 20 && j >= 0 && j < 28, white = i >= -2 && i < 22 && j >= -2 && j < 30;
                    f.Px[(y + j) * f.W + x + i] = inner ? Pixel.Rgb(255, 120 + j * 4, 30) : white ? Pixel.Rgb(255, 255, 255) : Pixel.Rgb(0, 0, 0);
                }
        return f;
    }

    [Fact]
    public void Finds_damage_numbers_by_their_white_ring_and_groups_digits()
    {
        var found = DamageNumbers.Find(WithDigits((100, 100), (126, 100), (400, 300)));
        Assert.Equal(2, found.Count); // the two side-by-side digits are one number
        Assert.Contains(found, r => r.X == 100 && r.Width == 46);
    }

    [Fact]
    public void Orange_without_the_white_ring_is_not_a_damage_number()
    {
        var f = new Frame(400, 300);
        Array.Fill(f.Px, Sky);
        for (int y = 100; y < 140; y++) for (int x = 100; x < 140; x++) f.Px[y * f.W + x] = Pixel.Rgb(250, 140, 30); // an orange mushroom cap
        Assert.Empty(DamageNumbers.Find(f));
    }

    [Fact]
    public void A_kill_marks_the_spot_whose_hits_stopped_and_the_timer_runs_cooldown_then_due()
    {
        var tr = new SpawnTracker { Rule = new RespawnRule(5, 7.56) };
        var t0 = DateTimeOffset.Now;
        tr.AddFrame(WithDigits((300, 200)), t0);                   // hit on monster A
        tr.AddFrame(WithDigits((600, 200)), t0.AddSeconds(1.0));   // you moved on to monster B
        tr.SetPlayer(450, 330, t0.AddSeconds(1.0));
        var spot = tr.Kill(t0.AddSeconds(1.2), 1000);               // EXP for A arrives late
        Assert.NotNull(spot);
        Assert.InRange(spot!.MapX, 300, 320);
        Assert.Equal(t0, spot.KilledAt);                            // the timer starts at the death, not the EXP

        Assert.Equal(SpotPhase.Cooldown, tr.Active(t0.AddSeconds(2)).Single().Phase);
        Assert.Equal(SpotPhase.Due, tr.Active(t0.AddSeconds(6)).Single().Phase);
        Assert.Empty(tr.Active(t0.AddSeconds(20)));
    }

    [Fact]
    public void Hits_far_from_you_are_not_your_kills()
    {
        var tr = new SpawnTracker();
        var t0 = DateTimeOffset.Now;
        tr.AddFrame(WithDigits((300, 40)), t0);    // someone else's fight, two floors up
        tr.SetPlayer(300, 470, t0);
        Assert.Null(tr.Kill(t0.AddSeconds(1), 10));
    }

    static OcrLine Line(params (string text, double x)[] words) =>
        OcrLine.FromWords(words.Select(w => new OcrWord(w.text, new TextBounds(w.x, 700, w.text.Length * 7, 12))));

    [Fact]
    public void Finds_your_name_tag_even_glued_to_a_neighbour_or_misread()
    {
        var lines = new[] { Line(("Snail", 600), ("NPink", 650), ("Bunny", 700)), Line(("Snai", 905), ("Maple", 940), ("layer", 978)) };
        var p = PlayerFinder.Locate(lines, "MaplePlayer");
        Assert.NotNull(p);
        Assert.InRange(p!.Value.X, 940, 1010);
        Assert.Null(PlayerFinder.Locate([Line(("Nihan", 500))], "MaplePlayer"));
    }
}
