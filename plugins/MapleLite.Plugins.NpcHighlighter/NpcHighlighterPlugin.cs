using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MapleLite.Core.Capture;
using MapleLite.Core.Game;
using MapleLite.Sdk;

namespace MapleLite.Plugins.NpcHighlighter;

public sealed class NpcHighlighterPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = new("npc-highlighter", "NPC Highlighter", "0.1.0", "CheesyFrog Technologies",
        "Highlight specific NPCs with any color.");

    internal sealed record NpcEntry(string Name, string Color = DefaultColor, bool On = true);

    sealed record Settings
    {
        public NpcEntry[] Npcs { get; init; } = [];
        /// <summary>Offer the NPCs in view in a list to pick from.</summary>
        public bool Nearby { get; init; } = true;
    }

    const string DefaultColor = "#FF55CC";
    readonly object _gate = new();
    readonly CancellationTokenSource _stop = new();
    readonly NameTagTracker _tracker = NameTagTracker.ForNpcs();
    IPluginContext _context = null!;
    volatile Settings _settings = new();
    IDisposable? _frames;
    Task? _worker;
    /// <summary>The last frame the tags were followed on.</summary>
    GameFrame? _followed;
    List<OverlayItem> _drawn = [];

    readonly NpcRoster _roster = new();

    StackPanel? _rows;
    TextBlock? _status;
    ComboBox? _nearby;
    Button? _addNearby;
    bool _syncPicker;

    public Task StartAsync(IPluginContext context)
    {
        _context = context;
        _settings = context.Storage.Load<Settings>("settings");
        if (Environment.GetEnvironmentVariable("MAPLELITE_OVERLAY_TRACE") == "1")
            _tracker.Trace = message => context.Log($"npc trace {DateTimeOffset.Now:HH:mm:ss.fff}: {message}");
        // Following a tag costs no OCR, so it runs on every frame to keep outlines on the NPCs while the camera scrolls.
        _frames = context.Frames.Subscribe(OnFrame, 20);
        _worker = Task.Run(() => RunAsync(_stop.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _frames?.Dispose();
        _stop.Cancel();
        try { if (_worker != null) await _worker.ConfigureAwait(false); }
        finally
        {
            _context.Overlay.Clear();
            _stop.Dispose();
        }
    }

    public Control CreatePanel()
    {
        var input = new TextBox { Watermark = "NPC name, e.g. Mr. Pickall", MinWidth = 230 };
        AutomationProperties.SetName(input, "NPC name to highlight");
        var error = PanelKit.Muted("");
        error.IsVisible = false;
        _rows = new StackPanel { Spacing = 6 };
        _status = PanelKit.Muted("Waiting for a scan…");
        Populate();

        void AddTyped()
        {
            var name = string.Join(' ', (input.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            error.IsVisible = name.Length > 0 && NpcTags.Compact(name).Length < 2;
            if (error.IsVisible) error.Text = "Type at least two letters of the NPC's name.";
            if (name.Length == 0 || error.IsVisible) return;
            Add(name);
            input.Text = "";
        }

        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            AddTyped();
            e.Handled = true;
        };

        _nearby = new ComboBox { PlaceholderText = "Choose a nearby NPC", MinWidth = 230, MaxDropDownHeight = 320 };
        AutomationProperties.SetName(_nearby, "Nearby NPCs");
        _addNearby = PanelKit.Button("+ Highlight", () =>
        {
            if (_nearby?.SelectedItem is string name) Add(name);
        });
        _nearby.SelectionChanged += (_, _) => { if (!_syncPicker) UpdatePicker(); };
        UpdatePicker();
        var nearbyControls = PanelKit.Wrap(_nearby, _addNearby);
        nearbyControls.IsVisible = _settings.Nearby;
        var listNearby = new CheckBox { Content = "List nearby NPCs", IsChecked = _settings.Nearby };
        listNearby.IsCheckedChanged += (_, _) =>
        {
            nearbyControls.IsVisible = listNearby.IsChecked == true;
            Change(_settings with { Nearby = listNearby.IsChecked == true });
        };

        return PanelKit.Stack(12,
            PanelKit.Card(PanelKit.Stack(8, PanelKit.Title("Nearby NPCs"), listNearby, nearbyControls)),
            PanelKit.Card(PanelKit.Stack(8, PanelKit.Title("NPCs to highlight"),
                PanelKit.Wrap(input, PanelKit.Button("+ Add NPC", AddTyped, accent: true)),
                error, _rows,
                PanelKit.Muted("Names are matched without regard to case, spaces or punctuation."))),
            PanelKit.Card(PanelKit.Stack(6, _status,
                PanelKit.Muted("For in-game outlines, turn on Settings → Overlay and use windowed or borderless mode."))));
    }

    void Add(string name)
    {
        if (!_settings.Npcs.Any(n => Same(n.Name, name)))
        {
            Change(_settings with { Npcs = [.. _settings.Npcs, new NpcEntry(name)] });
            Populate();
        }
        UpdatePicker();
    }

    /// <summary>"+ Highlight" works for a picked NPC that isn't listed yet.</summary>
    void UpdatePicker()
    {
        if (_addNearby != null)
            _addNearby.IsEnabled = _nearby?.SelectedItem is string name && !_settings.Npcs.Any(n => Same(n.Name, name));
    }

    void ShowNearby(string[] nearby)
    {
        if (_nearby is not { IsDropDownOpen: false } || _nearby.ItemsSource is string[] old && old.SequenceEqual(nearby)) return;
        _syncPicker = true;
        var picked = _nearby.SelectedItem as string;
        _nearby.ItemsSource = nearby;
        _nearby.SelectedItem = nearby.FirstOrDefault(n => Same(n, picked));
        _nearby.PlaceholderText = nearby.Length == 0 ? "No NPC names in view" : "Choose a nearby NPC";
        _syncPicker = false;
        UpdatePicker();
    }

    void Populate()
    {
        if (_rows == null) return;
        _rows.Children.Clear();
        foreach (var entry in _settings.Npcs)
        {
            string name = entry.Name;
            var on = new CheckBox { Content = name, IsChecked = entry.On, VerticalAlignment = VerticalAlignment.Center, MinWidth = 160 };
            AutomationProperties.SetName(on, $"Highlight {name}");
            on.IsCheckedChanged += (_, _) => Update(name, e => e with { On = on.IsChecked == true });
            var picker = new ColorPicker
            {
                Color = Color.TryParse(entry.Color, out var c) ? c : Color.Parse(DefaultColor),
                IsAlphaEnabled = false, VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(picker, $"Highlight color for {name}");
            picker.ColorChanged += (_, e) =>
                Update(name, x => x with { Color = $"#{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}" });
            var remove = PanelKit.Button("Remove", () =>
            {
                Change(_settings with { Npcs = _settings.Npcs.Where(n => !Same(n.Name, name)).ToArray() });
                Populate();
            });
            AutomationProperties.SetName(remove, $"Remove {name}");
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(on);
            row.Children.Add(picker);
            row.Children.Add(remove);
            _rows.Children.Add(row);
        }
        if (_rows.Children.Count == 0) _rows.Children.Add(PanelKit.Muted("No NPCs yet. Pick one nearby or type a name."));
        UpdatePicker();
    }

    void Update(string name, Func<NpcEntry, NpcEntry> change) =>
        Change(_settings with { Npcs = _settings.Npcs.Select(n => Same(n.Name, name) ? change(n) : n).ToArray() });

    void Change(Settings settings)
    {
        lock (_gate)
        {
            _settings = settings;
            _tracker.Keep(Enabled(settings));
            Render();
            try { _context.Storage.Save("settings", settings); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _context.Log("NPC Highlighter could not save settings: " + e.Message);
            }
        }
    }

    static string[] Enabled(Settings settings) => settings.Npcs.Where(n => n.On).Select(n => n.Name).ToArray();

    /// <summary>
    /// Follows the tags onto every new frame so outlines move with the scene. Starts over when the game window
    /// moves or resizes.
    /// </summary>
    void OnFrame(GameFrame frame)
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;
            if (_followed != null && !SamePlace(_followed, frame))
                _tracker.Reset($"game picture moved: {_followed.Frame.Bounds} at {_followed.Offset} → {frame.Frame.Bounds} at {frame.Offset}, window {_followed.Window.Handle == frame.Window.Handle}");
            _followed = frame;
            _tracker.Follow(frame.Frame, frame.Time);
            Render();
        }
    }

    static bool SamePlace(GameFrame a, GameFrame b) =>
        a.Window.Handle == b.Window.Handle && a.Frame.Bounds == b.Frame.Bounds && a.Offset == b.Offset;

    /// <summary>
    /// Reads the scene with OCR twice a second to find listed NPCs (new ones, or ones lost) and, with the nearby list
    /// on, the names of every NPC in view.
    /// </summary>
    async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        GameFrame? previous = null;
        Settings? previousSettings = null;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var options = _settings;
                var frame = _context.Frames.Latest;
                var names = Enabled(options);
                if (!options.Nearby) Nearby(_roster.Clear);
                if (!_context.Ocr.Available) { Show("Text recognition is unavailable."); continue; }
                if (!IsCurrent(frame))
                {
                    // No fresh pictures (capture stopped, game minimized): outlines would be stale.
                    lock (_gate) { _tracker.Reset(frame == null ? "no picture" : $"picture not current: {_context.Frames.Status.State}, {(DateTimeOffset.Now - frame.Time).TotalSeconds:0.0} s old"); Render(); }
                    if (options.Nearby) Nearby(() => _roster.Update(DateTimeOffset.Now, null, null));
                    Show("Waiting for the game picture…");
                    continue;
                }
                if (names.Length == 0 && !options.Nearby)
                {
                    Show(options.Npcs.Length == 0 ? "Add an NPC to start." : "Every NPC is turned off.");
                    continue;
                }
                if (ReferenceEquals(frame, previous) && ReferenceEquals(options, previousSettings)) continue;
                previous = frame;
                previousSettings = options;
                try
                {
                    var scan = await NpcTags.ScanAsync(_context.Ocr, frame!.Frame, names, options.Nearby, ct).ConfigureAwait(false);
                    if (options.Nearby) Nearby(() => _roster.Update(DateTimeOffset.Now, scan.InView, frame.Time));
                    List<string> inView;
                    lock (_gate)
                    {
                        if (_stop.IsCancellationRequested) return;
                        if (_followed != null && !SamePlace(_followed, frame)) continue;
                        _tracker.Keep(Enabled(_settings));
                        _tracker.Confirm(frame.Frame, scan.Found.Where(f => Enabled(_settings).Contains(f.Name, StringComparer.OrdinalIgnoreCase)), frame.Time);
                        var latest = _followed ?? frame;
                        _tracker.Follow(latest.Frame, latest.Time);
                        Render();
                        inView = _tracker.Current(latest.Frame).Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    }
                    Show(names.Length == 0 ? (options.Npcs.Length == 0 ? "Pick a nearby NPC or type a name to start." : "Every NPC is turned off.")
                        : inView.Count == 0 ? "None of the listed NPCs are in view." : "In view: " + string.Join(", ", inView));
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _context.Log($"NPC Highlighter scan failed: {e.Message}");
                    Show("Could not read the game picture.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>Changes the nearby list (worker thread only) and shows it.</summary>
    void Nearby(Func<string[]> update)
    {
        var nearby = update();
        _context.OnUi(() => { if (!_stop.IsCancellationRequested) ShowNearby(nearby); });
    }

    void Nearby(Action clear) => Nearby(() => { clear(); return []; });

    bool IsCurrent(GameFrame? game) => game != null && _context.Frames.Status.State == CaptureState.Capturing
        && DateTimeOffset.Now - game.Time < TimeSpan.FromSeconds(2.5);

    /// <summary>Draws the followed tags, only when something changed. Call under the lock.</summary>
    void Render()
    {
        var items = _followed == null ? [] : Items(_tracker.Current(_followed.Frame), _settings.Npcs);
        if (items.SequenceEqual(_drawn)) return;
        _drawn = items;
        if (items.Count == 0) _context.Overlay.Clear();
        else _context.Overlay.Draw(items);
    }

    string? _shown;

    void Show(string status)
    {
        if (status == _shown) return;
        _shown = status;
        _context.OnUi(() =>
        {
            if (_status != null && !_stop.IsCancellationRequested) _status.Text = status;
        });
    }

    /// <summary>An outline around each found NPC that is still listed and turned on, with its name above, in its color.</summary>
    internal static List<OverlayItem> Items(IEnumerable<TagSighting> found, IReadOnlyList<NpcEntry> entries)
    {
        var items = new List<OverlayItem>();
        foreach (var npc in found)
        {
            if (entries.FirstOrDefault(e => Same(e.Name, npc.Name)) is not { On: true } entry) continue;
            var rgb = ParseRgb(entry.Color);
            var body = npc.Body;
            items.Add(new OverlayItem(body.X + body.Width / 2.0, Math.Max(8, body.Y - 9), npc.Name, Size: 12)
            {
                Rectangle = body, ColorRgb = rgb, LabelAccentRgb = rgb,
            });
        }
        return items;
    }

    /// <summary>"#RRGGBB" or "#AARRGGBB" to 0xRRGGBB; the default pink when it can't be read.</summary>
    internal static uint ParseRgb(string? color)
    {
        var hex = (color ?? "").Trim().TrimStart('#');
        return hex.Length is 6 or 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            ? v & 0xFFFFFF : 0xFF55CC;
    }

    static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
