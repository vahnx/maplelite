using MapleLite.Core.Imaging;
using MapleLite.Plugins.SpawnTimer;
using Xunit;

namespace MapleLite.Tests;

public class SpawnWatcherTests
{
    // A still, textured, bright background (like grass and bushes, which the watcher needs to see steady).
    static Frame Background()
    {
        var f = new Frame(1366, 768);
        for (int y = 0; y < f.H; y++)
            for (int x = 0; x < f.W; x++)
            {
                int v = 120 + ((x * 7 + y * 13) % 5) * 15;
                f.Px[y * f.W + x] = Pixel.Rgb(v - 30, v, v - 50);
            }
        return f;
    }

    // A monster: an orange body with a dark outline, blended in at `alpha` (they fade in when they spawn).
    static Frame WithMonster(double alpha, int left = 600, int top = 400, int size = 60)
    {
        var f = Background();
        for (int y = top; y < top + size; y++)
            for (int x = left; x < left + size; x++)
            {
                bool edge = x < left + 4 || y < top + 4 || x >= left + size - 4 || y >= top + size - 4;
                int c = edge ? Pixel.Rgb(20, 20, 20) : Pixel.Rgb(230, 120, 60), b = f.Px[y * f.W + x];
                int Mix(int s) => (int)(((b >> s) & 255) * (1 - alpha) + ((c >> s) & 255) * alpha);
                f.Px[y * f.W + x] = Pixel.Rgb(Mix(16), Mix(8), Mix(0));
            }
        return f;
    }

    [Fact]
    public void A_monster_fading_in_is_found_where_it_stands()
    {
        var w = new SpawnWatcher();
        double[] alpha = [0, 0, 0, 0, 0, 0, 0, 0, 0.33, 0.66, 1, 1, 1, 1, 1, 1];
        var found = new List<FadeIn>();
        for (int i = 0; i < alpha.Length; i++) found.AddRange(w.Add(WithMonster(alpha[i]), i * 0.2, 0, 0));
        var fade = Assert.Single(found);
        Assert.InRange(fade.MapX, 615, 645);  // the middle of the monster
        Assert.InRange(fade.MapY, 445, 465);  // its feet
    }

    [Fact]
    public void A_monster_walking_in_or_standing_still_is_not_a_spawn()
    {
        var w = new SpawnWatcher();
        var found = new List<FadeIn>();
        for (int i = 0; i < 16; i++) found.AddRange(w.Add(WithMonster(1, left: 300 + i * 25), i * 0.2, 0, 0)); // walking
        for (int i = 16; i < 32; i++) found.AddRange(w.Add(WithMonster(1), i * 0.2, 0, 0));                    // appears at once, then stands
        Assert.Empty(found);
    }

    [Fact]
    public void A_server_refill_makes_countdowns_exact_and_kills_are_timed_at_the_spawn_point()
    {
        var t0 = DateTimeOffset.UnixEpoch.AddDays(1);
        var tr = new SpawnTracker { Rule = new RespawnRule(5, 7.56) };
        var sky = new Frame(800, 500);
        Array.Fill(sky.Px, Pixel.Rgb(60, 110, 220));
        tr.AddFrame(sky, t0);
        // two spawn points fill at once, 0.3 s in: a refill
        tr.OnFadeIn(new FadeIn(0.3, 400, 312));
        tr.OnFadeIn(new FadeIn(0.4, 900, 100));
        Assert.Equal(1, tr.Refills);
        Assert.Equal(2, tr.SpawnPointsKnown);

        // a kill near the first spawn point (its damage number around x 310)
        tr.AddFrame(SpawnTimerTests.WithDigits((300, 200)), t0.AddSeconds(0.5));
        var spot = tr.Kill(t0.AddSeconds(1.5), 100);
        Assert.NotNull(spot);
        Assert.True(spot!.AtSpawn);
        Assert.Equal(400, spot.MapX, 0.5);

        // ready 5 s after it died (at 0.5 s); the first refill after that is 0.3 + 7.56 = 7.86 s
        var a = Assert.Single(tr.Active(t0.AddSeconds(3)));
        Assert.True(a.Exact);
        Assert.Equal(SpotPhase.Cooldown, a.Phase);
        Assert.Equal(4.86, a.Seconds, 0.05);
        Assert.Equal(SpotPhase.Due, Assert.Single(tr.Active(t0.AddSeconds(8))).Phase);
        Assert.Empty(tr.Active(t0.AddSeconds(10.5)));
    }
}
