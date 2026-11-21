using MapleLite.Core.Capture;
using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;
using MapleLite.Core.Platform;
using MapleLite.Plugins.XpTracker;
using MapleLite.Sdk.Theming;
using Xunit;

namespace MapleLite.Tests;

sealed class FakePlatform : IPlatform, IGameWindowFinder, IWindowCapture, ICursor, IWindowList
{
    public GameWindow? Window = new(1, 42, "MapleStory", "MapleStory", new Region(0, 0, 800, 600), new Region(0, 0, 800, 600), WindowMode.Windowed, 1);
    public Func<Frame> Next = () => { var f = new Frame(800, 600); Array.Fill(f.Px, Pixel.Rgb(90, 140, 200)); return f; };
    public string Name => "fake";
    public IGameWindowFinder Finder => this;
    public IWindowCapture Capture => this;
    public ICursor Cursor => this;
    public IWindowList Windows => this;
    public IOcrEngine? Ocr => null;
    public IClipboardImage? Clipboard => null;
    public IReadOnlyList<string> Notes => [];
    public PermissionState ScreenCapturePermission() => PermissionState.NotRequired;
    public GameWindow? Find(GameTarget target) => Window;
    public GameWindow? Refresh(GameWindow window) => Window;
    public IReadOnlyList<WindowCandidate> ListCandidates() => [];
    public IReadOnlyList<WindowCandidate> TopLevelWindows() => [];
    public Task<Frame> CaptureClientAsync(GameWindow window, CancellationToken ct) => Task.FromResult(Next());
    public Task<Frame> CaptureWindowAsync(nint handle, CancellationToken ct) => Task.FromResult(Next());
    public (int X, int Y)? PositionIn(GameWindow window) => null;
    public void Dispose() { }
}

public class CaptureLoopTests
{
    [Fact]
    public async Task Capture_follows_subscriber_demand_and_throttles_status_notifications()
    {
        await using var loop = new CaptureLoop(new FakePlatform()) { TargetFps = 60 };
        int frames = 0, notifications = 0;
        using var sub = loop.Subscribe(_ => Interlocked.Increment(ref frames), 5);
        loop.StatusChanged += _ => Interlocked.Increment(ref notifications);
        loop.Start();
        await Task.Delay(1300);
        await loop.StopAsync();
        Assert.InRange(loop.Latest == null ? 0 : frames, 3, 9);
        Assert.InRange(notifications, 2, 4);
    }

    static async Task<CaptureStatus> WaitFor(CaptureLoop loop, Func<CaptureStatus, bool> done)
    {
        for (int i = 0; i < 100 && !done(loop.Status); i++) await Task.Delay(30);
        return loop.Status;
    }

    [Fact]
    public async Task Subscribers_get_frames_and_status_says_capturing()
    {
        var loop = new CaptureLoop(new FakePlatform());
        int got = 0;
        using var sub = loop.Subscribe(_ => Interlocked.Increment(ref got), 30);
        loop.Start();
        var s = await WaitFor(loop, x => x.State == CaptureState.Capturing && got >= 3);
        await loop.StopAsync();
        Assert.True(got >= 3);
        Assert.Equal(CaptureState.Capturing, s.State);
    }

    [Fact]
    public async Task Missing_window_reports_searching()
    {
        var loop = new CaptureLoop(new FakePlatform { Window = null });
        loop.Start();
        var s = await WaitFor(loop, x => x.State == CaptureState.Searching);
        await loop.StopAsync();
        Assert.Equal(CaptureState.Searching, s.State);
    }

    [Fact]
    public async Task Black_frames_in_exclusive_fullscreen_explain_what_to_do()
    {
        var p = new FakePlatform { Next = () => new Frame(800, 600) };
        p.Window = p.Window! with { Mode = WindowMode.ExclusiveFullscreen };
        var loop = new CaptureLoop(p);
        using var sub = loop.Subscribe(_ => { }, 30);
        loop.Start();
        var s = await WaitFor(loop, x => x.State == CaptureState.Blocked);
        await loop.StopAsync();
        Assert.Equal(CaptureState.Blocked, s.State);
        Assert.Contains("borderless", s.Message);
    }

    [Fact]
    public async Task A_throwing_subscriber_does_not_stop_others()
    {
        var loop = new CaptureLoop(new FakePlatform());
        int good = 0;
        using var bad = loop.Subscribe(_ => throw new Exception("plugin bug"), 30);
        using var ok = loop.Subscribe(_ => Interlocked.Increment(ref good), 30);
        loop.Start();
        await WaitFor(loop, _ => good >= 3);
        await loop.StopAsync();
        Assert.True(good >= 3);
    }
}

public class ThemeTests
{
    [Fact]
    public void Built_in_themes_define_every_token_with_valid_colors()
    {
        foreach (var t in BuiltInThemes.All)
            foreach (var token in ThemeTokens.All)
                Assert.Matches("^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", t.Colors[token]);
        Assert.Equal(BuiltInThemes.All.Count, BuiltInThemes.All.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void User_theme_inherits_missing_tokens_from_its_base()
    {
        var t = ThemeLoader.Parse("""{ "id": "mine", "name": "Mine", "base": "ocean", "colors": { "accent": "#123456" } }""", BuiltInThemes.All);
        Assert.Equal("#123456", t.Colors[ThemeTokens.Accent]);
        Assert.Equal(BuiltInThemes.Ocean.Colors[ThemeTokens.Background], t.Colors[ThemeTokens.Background]);
        Assert.True(t.Dark);
    }

    [Theory]
    [InlineData("""{ "name": "no id" }""", "id")]
    [InlineData("""{ "id": "x", "colors": { "Sparkle": "#ffffff" } }""", "Sparkle")]
    [InlineData("""{ "id": "x", "colors": { "Text": "red" } }""", "#RRGGBB")]
    [InlineData("""{ "id": "x", "base": "nope" }""", "nope")]
    [InlineData("not json", "JSON")]
    public void Bad_theme_files_give_readable_reasons(string json, string expected)
    {
        var e = Assert.Throws<FormatException>(() => ThemeLoader.Parse(json, BuiltInThemes.All));
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void Exported_theme_reads_back()
    {
        var json = ThemeLoader.ToJson(BuiltInThemes.Sakura, "sakura-custom");
        var t = ThemeLoader.Parse(json, BuiltInThemes.All);
        Assert.Equal("sakura-custom", t.Id);
        Assert.False(t.Dark);
        Assert.Equal(BuiltInThemes.Sakura.Colors[ThemeTokens.Accent], t.Colors[ThemeTokens.Accent]);
    }
}

public class XpTests
{
    [Theory]
    [InlineData("EXP 12345 [12.34%]", 12345, 12.34)]
    [InlineData("12345[12.34%]", 12345, 12.34)]
    [InlineData("EXP 12,345 / 50,000", 12345, 24.69)]
    [InlineData("1234O (5.5%)", 12340, 5.5)]
    [InlineData("EXP 98 [0.01%]", 98, 0.01)]
    public void Parses_status_bar_shapes(string text, long exp, double pct)
    {
        var r = ExpParser.Parse(text);
        Assert.NotNull(r);
        Assert.Equal(exp, r!.Value.Exp);
        Assert.Equal(pct, r.Value.Percent!.Value, 2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("HP MP")]
    public void Rejects_non_exp_text(string text) => Assert.Null(ExpParser.Parse(text));

    [Fact]
    public void Rate_and_time_to_level()
    {
        var x = new XpRate();
        var t0 = DateTimeOffset.Parse("2026-11-16T12:00:00Z");
        x.Add(t0, new ExpReading(1000, 10, null));
        x.Add(t0.AddMinutes(30), new ExpReading(6000, 15, null));
        Assert.Equal(10000, x.PerHour!.Value, 1);
        Assert.Equal(10, x.PercentPerHour!.Value, 3);
        Assert.Equal(8.5, x.TimeToLevel!.Value.TotalHours, 3); // 85% left at 10%/h
    }

    [Fact]
    public void Level_up_keeps_the_session_total()
    {
        var x = new XpRate();
        var t0 = DateTimeOffset.Parse("2026-11-16T12:00:00Z");
        x.Add(t0, new ExpReading(9000, 95, null));
        x.Add(t0.AddMinutes(5), new ExpReading(9800, 99, null));
        x.Add(t0.AddMinutes(10), new ExpReading(300, 1, null)); // levelled, counter restarts
        x.Add(t0.AddMinutes(15), new ExpReading(1300, 3, null));
        Assert.Equal(800 + 1000, x.Gained);
        Assert.Equal(1, x.Levels);
    }
}
