using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MapleLite.Core.Capture;
using MapleLite.Core.Imaging;
using MapleLite.Sdk;
using static MapleLite.Sdk.PanelKit;

namespace MapleLite.Plugins.SpawnTimer;

public sealed class SpawnSettings
{
    public bool ShowOnGame { get; set; } = true;
    /// <summary>Whether the panel's live view is open: null until you open or close it (then it's open only while overlays are off).</summary>
    public bool? ShowLiveView { get; set; }
    public double Cooldown { get; set; } = 5.0;
    public double Tick { get; set; } = 7.56;
}

/// <summary>
/// Respawn Timer: a countdown where each monster you kill will come back. Kills come from XP Tracker's EXP events;
/// where they happened comes from the damage numbers near your character (<see cref="SpawnTracker"/>). Timers show
/// over the game when overlays are on, and always in this plugin's own preview.
/// </summary>
public sealed class SpawnTimerPlugin : IPlugin
{
    const double FrameFps = 15; // the camera follow needs it to keep timers steady over a scrolling view (about 20 ms of work per picture)

    IPluginContext? _ctx;
    readonly SpawnTracker _tracker = new();
    PlayerFinder? _finder;
    SpawnSettings _settings = new();
    IDisposable? _frames, _exp;
    Timer? _draw;
    int _busy, _finding, _found, _notFound;
    DateTimeOffset _nextFind;
    volatile GameFrame? _latest;
    DateTimeOffset? _lastExpEvent, _lastPlayerSeen;

    public PluginManifest Manifest { get; } = new("spawntimer", "Respawn Timer", "0.1.0-alpha", "CheesyFrog Technologies",
        "Countdowns where the monsters you kill will respawn, over the game or in the panel.");

    public Task StartAsync(IPluginContext context)
    {
        _ctx = context;
        _settings = context.Storage.Load<SpawnSettings>("settings");
        ApplyRule();
        _finder = new PlayerFinder(context.Ocr);
        _frames = context.Frames.Subscribe(OnFrame, FrameFps);
        _exp = context.Events.Subscribe<ExpGainedEvent>(e =>
        {
            _lastExpEvent = e.Time;
            var spot = _tracker.Kill(e.Time, e.Exp);
            if (spot == null) context.Log("kill not placed: " + _tracker.LastKillNote);
            else context.Log($"kill timed at {(spot.AtSpawn ? "spawn point" : "death spot")} ({spot.MapX:0},{spot.MapY:0}), died {(e.Time - spot.KilledAt).TotalSeconds:0.0} s before the EXP; spawn points {_tracker.SpawnPointsKnown}, fade-ins {_tracker.FadesSeen}, refills {_tracker.Refills}, period {_tracker.TickPeriod:0.00} s{(_tracker.TickMeasured ? " (measured)" : "")}");
        });
        _draw = new Timer(_ => DrawOverlay(), null, 200, 33);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _draw?.Dispose(); _draw = null;
        _frames?.Dispose(); _exp?.Dispose();
        _ctx?.Overlay.Clear();
        return Task.CompletedTask;
    }

    void ApplyRule() => _tracker.Rule = new RespawnRule(_settings.Cooldown, _settings.Tick);
    void SaveSettings() { ApplyRule(); _ctx?.Storage.Save("settings", _settings); }

    // Runs on the capture thread: hand the work to the pool, one picture at a time (drop pictures while busy).
    void OnFrame(GameFrame gf)
    {
        _latest = gf;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        Task.Run(() =>
        {
            try { _tracker.AddFrame(gf.Frame, gf.Time); }
            catch (Exception e) { _ctx?.Log("frame failed: " + e.Message); }
            finally { Volatile.Write(ref _busy, 0); }
        });
        if (gf.Time >= _nextFind && Interlocked.Exchange(ref _finding, 1) == 0)
        {
            _nextFind = gf.Time.AddSeconds(1.5);
            _ = FindPlayerAsync(gf);
        }
    }

    async Task FindPlayerAsync(GameFrame gf)
    {
        try
        {
            if (_finder != null && await _finder.FindAsync(gf.Frame) is { } p)
            {
                _tracker.SetPlayer(p.X, p.Y, gf.Time);
                _lastPlayerSeen = gf.Time;
                _found++;
            }
            else _notFound++;
            if ((_found + _notFound) % 10 == 1) _ctx?.Log($"name tag: {_found} found, {_notFound} not (name \"{_finder?.Name}\")");
        }
        catch (Exception e) { _ctx?.Log("name tag search failed: " + e.Message); }
        finally { Volatile.Write(ref _finding, 0); }
    }

    List<OverlayItem> Items(DateTimeOffset now, int w, int h)
    {
        var items = new List<OverlayItem>();
        foreach (var a in _tracker.Active(now))
        {
            if (a.ScreenX < 0 || a.ScreenY < 0 || a.ScreenX >= w || a.ScreenY >= h - 70) continue;
            items.Add(a.Phase switch
            {
                SpotPhase.Cooldown => new OverlayItem(a.ScreenX, a.ScreenY, $"{a.Seconds:0.0}", OverlayTone.Warning, a.Share),
                SpotPhase.Due => new OverlayItem(a.ScreenX, a.ScreenY, a.Exact ? "now" : "any moment", OverlayTone.Good, a.Share, 12),
                _ => new OverlayItem(a.ScreenX, a.ScreenY, "", OverlayTone.Muted, 1),
            });
        }
        return items;
    }

    void DrawOverlay()
    {
        try
        {
            if (_ctx == null) return;
            if (!_settings.ShowOnGame || _latest is not { } gf) { _ctx.Overlay.Clear(); return; }
            _ctx.Overlay.Draw(Items(DateTimeOffset.Now, gf.Frame.W, gf.Frame.H));
        }
        catch (Exception e) { _ctx?.Log("overlay failed: " + e.Message); }
    }

    // ---- panel ----

    public Control? CreatePanel()
    {
        var showOnGame = new CheckBox { Content = "Show timers over the game", IsChecked = _settings.ShowOnGame };
        showOnGame.IsCheckedChanged += (_, _) => { _settings.ShowOnGame = showOnGame.IsChecked == true; SaveSettings(); };
        var cooldown = new NumericUpDown { Minimum = 0, Maximum = 600, Increment = 0.5m, Value = (decimal)_settings.Cooldown, FormatString = "0.0#", Width = 130 };
        cooldown.ValueChanged += (_, _) => { _settings.Cooldown = (double)(cooldown.Value ?? 5); SaveSettings(); };
        var tick = new NumericUpDown { Minimum = 0.5m, Maximum = 600, Increment = 0.01m, Value = (decimal)_settings.Tick, FormatString = "0.0#", Width = 130 };
        tick.ValueChanged += (_, _) => { _settings.Tick = (double)(tick.Value ?? 7.56m); SaveSettings(); };

        var active = Big("0");
        var status = Stack(4);
        var preview = new Image { Stretch = Stretch.Uniform, MaxHeight = 460 };
        Avalonia.Media.Imaging.WriteableBitmap? bmp = null;
        // With timers drawn over the game the live view repeats them, so it starts closed then; it's how you see them otherwise.
        var live = new Expander
        {
            Header = Title("Live view"),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            IsExpanded = _settings.ShowLiveView ?? _ctx?.Overlay.Enabled != true,
            Content = Stack(8, Muted("The game picture with the timers MapleLite is keeping, and the spawn points it has learned (cyan). Useful when overlays are off, or to check what it sees."),
                new Border { Child = preview, MinHeight = 120 }),
        };
        live.PropertyChanged += (_, e) =>
        {
            if (e.Property != Expander.IsExpandedProperty) return;
            _settings.ShowLiveView = live.IsExpanded;
            SaveSettings();
            if (!live.IsExpanded) { preview.Source = null; bmp = null; } // free the picture while closed
        };

        void Refresh()
        {
            var now = DateTimeOffset.Now;
            var list = _tracker.Active(now);
            active.Text = list.Count == 1 ? "1 timer running" : $"{list.Count} timers running";
            status.Children.Clear();
            void Line(string s, bool warn = false) => status.Children.Add(warn ? Text(s, Sdk.Theming.ThemeTokens.Warning) : Muted(s));
            Line(_lastExpEvent is { } le
                ? $"Kills placed: {_tracker.KillsMarked} · not placed: {_tracker.KillsMissed} · last EXP {(now - le).TotalSeconds:0} s ago"
                : "Waiting for kills. Spot timers need XP Tracker turned on (it reports each EXP gain).", _lastExpEvent == null);
            Line(_finder?.Name is { } name
                ? (_lastPlayerSeen is { } ps && now - ps < TimeSpan.FromSeconds(5) ? $"Following {name} by name tag." : $"Looking for {name}'s name tag; kills by other players may be picked up meanwhile.")
                : "Reading your name from the status bar…");
            Line(_tracker.CameraLocked ? "Following the map as the view scrolls." : "Waiting for the view to settle (a map change resets the timers).");
            Line(_tracker.SpawnPointsKnown is var sp and > 0
                ? $"Learned {sp} spawn point{(sp == 1 ? "" : "s")} from monsters fading in; kills near one are timed there."
                : "Learning spawn points: stand still for a bit and MapleLite watches where monsters fade in. Until then, timers sit where monsters died.");
            Line(_tracker.SinceRefill(now) is { } sr && sr < 4 * _tracker.TickPeriod
                ? $"Server refill seen {sr:0} s ago; refills every {_tracker.TickPeriod:0.00} s ({(_tracker.TickMeasured ? "measured" : "setting")}), so countdowns are exact."
                : "No server refill seen lately: countdowns show the earliest return, then a window.");
            if (_ctx?.Overlay is { } ov) Line(ov.Visible ? "Drawing over the game." : "Over the game: " + (ov.Status ?? "nothing to show right now."));

            if (live.IsExpanded && _latest is { } gf) // no picture work while the live view is closed
            {
                var small = Half(gf.Frame);
                foreach (var p in _tracker.SpawnPoints(now)) Dot(small, p.ScreenX / 2, p.ScreenY / 2);
                foreach (var it in Items(now, gf.Frame.W, gf.Frame.H)) Mark(small, it.X / 2, it.Y / 2, it.Tone == OverlayTone.Good, it.Ring ?? 0);
                bmp = ToBitmap(small, bmp);
                preview.Source = bmp;
                preview.InvalidateVisual();
            }
        }

        var panel = Stack(14,
            Card(Stack(10, Title("Respawn timers"), active, status,
                Muted("Each of your kills gets a timer where its monster will come back: amber while its spawn point cools down, green when it is due. "
                    + "Monsters come back at their spawn point, not where they died, so MapleLite learns spawn points by watching monsters fade in (cyan in the live view) and times each kill at the nearest one."))),
            Card(live),
            Card(Stack(10, Title("Settings"), showOnGame,
                Wrap(Stack(4, Muted("Spawn point cooldown (s)"), cooldown), Stack(4, Muted("Server respawn tick (s)"), tick)),
                Muted("Defaults from meowdb's Classic World notes: a spawn point waits 5 s after its monster dies, and the server refills maps every 7.56 s. Once refills are seen, the period is measured instead. Bosses have longer timers."),
                Wrap(Button("Clear timers", _tracker.ClearSpots), Button("Forget spawn points", _tracker.ClearAll)))));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) => Refresh();
        panel.AttachedToVisualTree += (_, _) => { Refresh(); timer.Start(); };
        panel.DetachedFromVisualTree += (_, _) => timer.Stop();
        return panel;
    }

    static Frame Half(Frame f)
    {
        var h = new Frame(f.W / 2, f.H / 2);
        for (int y = 0; y < h.H; y++)
            for (int x = 0; x < h.W; x++) h.Px[y * h.W + x] = f.Px[y * 2 * f.W + x * 2];
        return h;
    }

    // A small cyan diamond where a spawn point is (at the monster's feet).
    static void Dot(Frame f, double cx, double cy)
    {
        int col = Pixel.Rgb(80, 220, 255);
        for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4 + Math.Abs(dy); dx <= 4 - Math.Abs(dy); dx++)
            {
                int x = (int)cx + dx, y = (int)cy + dy;
                if (x >= 0 && y >= 0 && x < f.W && y < f.H) f.Px[y * f.W + x] = col;
            }
    }

    // A ring (filled to `share`) around the spot in the preview.
    static void Mark(Frame f, double cx, double cy, bool due, double share)
    {
        int on = due ? Pixel.Rgb(110, 220, 120) : Pixel.Rgb(255, 200, 60), off = Pixel.Rgb(30, 30, 40);
        for (int a = 0; a < 360; a += 2)
        {
            double rad = a * Math.PI / 180;
            int col = a / 360.0 <= share ? on : off;
            for (double r = 7; r <= 9; r += 0.5)
            {
                int x = (int)(cx + r * Math.Sin(rad)), y = (int)(cy - r * Math.Cos(rad));
                if (x >= 0 && y >= 0 && x < f.W && y < f.H) f.Px[y * f.W + x] = col;
            }
        }
    }
}
