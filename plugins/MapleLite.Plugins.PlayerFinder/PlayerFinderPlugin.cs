using System.Globalization;
using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MapleLite.Core.Capture;
using MapleLite.Core.Imaging;
using MapleLite.Sdk;

namespace MapleLite.Plugins.PlayerFinder;

public sealed class PlayerFinderPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = new("player-finder", "Player Finder", "0.2.0", "CheesyFrog Technologies",
        "Pick nearby characters and save a watchlist with sightings, highlights, and alerts.");

    sealed record Settings
    {
        public string[] Names { get; init; } = [];
        public bool Nearby { get; init; } = true;
        public bool Notify { get; init; } = true;
        public bool Focus { get; init; }
        public bool Signs { get; init; } = true;
        public bool Map { get; init; } = true;
        public bool Chat { get; init; } = true;
    }

    sealed record LastSeen(DateTimeOffset At, string? Location, int? Channel);
    sealed record ScannedFrame(GameFrame Frame, PlayerScan Scan, Region? Chat);

    readonly object _gate = new();
    readonly CancellationTokenSource _stop = new();
    readonly Dictionary<string, LastSeen> _seen = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTimeOffset> _encounters = new(StringComparer.OrdinalIgnoreCase);
    readonly NearbyRoster _nearbyRoster = new();
    readonly Dictionary<string, TextBlock> _watchDetails = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _notes = new(StringComparer.OrdinalIgnoreCase);
    readonly DispatcherTimer _noteSave = new() { Interval = TimeSpan.FromMilliseconds(500) };
    IPluginContext _context = null!;
    volatile Settings _settings = new();
    volatile string? _picked;
    IDisposable? _frames;
    Task? _worker;
    GameFrame? _chatFrame;
    Region? _liveChat;
    ScannedFrame? _result;
    OverlayItem? _alert;
    LastSeen? _pickedSeen;
    DateTimeOffset _alertUntil;
    DateTimeOffset _savedAt;
    bool _seenChanged;
    bool _notesChanged;

    ComboBox? _nearby;
    TextBlock? _status;
    TextBlock? _matches;
    TextBlock? _noteStatus;
    Image? _preview;
    StackPanel? _watchRows;
    Button? _addNearby, _clearPick, _exportWatchlist;
    WriteableBitmap? _bitmap;
    bool _syncPicker;

    public Task StartAsync(IPluginContext context)
    {
        _context = context;
        _settings = context.Storage.Load<Settings>("settings");
        foreach (var (name, sighting) in context.Storage.Load<Dictionary<string, LastSeen>>("last-seen"))
            _seen[name] = sighting;
        foreach (var (name, note) in context.Storage.Load<Dictionary<string, string>>("notes"))
            _notes[name] = note;
        _noteSave.Tick += (_, _) => AutosaveNotes();
        _frames = context.Frames.Subscribe(TrackChat, 8);
        _worker = Task.Run(() => RunAsync(_stop.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _frames?.Dispose();
        _stop.Cancel();
        try { await Dispatcher.UIThread.InvokeAsync(SaveNotes); }
        finally
        {
            if (_worker != null) await _worker.ConfigureAwait(false);
            lock (_gate) SaveSightings();
            _context.Overlay.Clear();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_preview != null) _preview.Source = null;
                _bitmap?.Dispose();
                _bitmap = null;
            });
            _stop.Dispose();
        }
    }

    public Control CreatePanel()
    {
        var input = new TextBox { Watermark = "Type a character name" };
        var error = PanelKit.Muted("");
        error.IsVisible = false;
        _status = PanelKit.Muted("Waiting for a scan…");
        _matches = PanelKit.Text("");
        _preview = new Image { Height = 130, Stretch = Stretch.Uniform, IsVisible = false };
        _noteStatus = PanelKit.Muted(_notesChanged ? "Notes have unsaved changes." : "");
        _noteStatus.IsVisible = _notesChanged;
        _watchRows = new StackPanel { Spacing = 8 };
        _exportWatchlist = PanelKit.Button("Export CSV…", async () => await ExportWatchlistAsync());
        PopulateWatchlist();

        void AddTyped()
        {
            var names = (input.Text ?? "").Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            error.IsVisible = names.Any(name => !PlayerReader.IsName(name));
            if (error.IsVisible)
            {
                error.Text = "Use 2–16 letters, digits, or underscores.";
                return;
            }
            if (names.Length == 0) return;
            AddToWatchlist(names);
            input.Text = "";
        }

        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            AddTyped();
            e.Handled = true;
        };

        _nearby = new ComboBox { PlaceholderText = "Choose a nearby player", MinWidth = 230, MaxDropDownHeight = 320 };
        _addNearby = PanelKit.Button("+ Watch", () =>
        {
            if (_picked is { } name) AddToWatchlist([name]);
        });
        _clearPick = PanelKit.Button("Clear pick", () =>
        {
            Pick(null);
            if (_nearby != null) _nearby.SelectedItem = null;
        });
        _nearby.SelectionChanged += (_, _) =>
        {
            if (!_syncPicker && _nearby.SelectedItem is string name) Pick(name);
        };

        var nearbyControls = PanelKit.Wrap(_nearby, _addNearby, _clearPick);
        nearbyControls.IsVisible = _settings.Nearby;
        UpdatePickerButtons();

        CheckBox Option(string label, bool value, Action<bool> update)
        {
            var box = new CheckBox { Content = label, IsChecked = value };
            box.IsCheckedChanged += (_, _) => update(box.IsChecked == true);
            return box;
        }

        return PanelKit.Stack(12,
            PanelKit.Card(PanelKit.Stack(8, PanelKit.Title("Nearby players"),
                Option("List nearby players", _settings.Nearby, value =>
                {
                    nearbyControls.IsVisible = value;
                    if (!value) Pick(null);
                    Change(_settings with { Nearby = value });
                }), nearbyControls)),
            PanelKit.Card(PanelKit.Stack(8, PanelKit.Title("Watchlist"),
                PanelKit.Wrap(input, PanelKit.Button("+ Add to watchlist", AddTyped, accent: true), _exportWatchlist),
                error, _watchRows, _noteStatus, Option("Notify when a watched player appears", _settings.Notify,
                    value => Change(_settings with { Notify = value })),
                PanelKit.Muted("Channel comes from MapleLite’s last read of the game’s Change Channel window."))),
            PanelKit.Card(PanelKit.Stack(8, _status, _matches, _preview)),
            PanelKit.Card(PanelKit.Stack(4,
                Option("Highlight shop signs and speech bubbles", _settings.Signs, value => Change(_settings with { Signs = value })),
                Option("Highlight matching chat lines", _settings.Chat, value => Change(_settings with { Chat = value })),
                Option("Mark matches on the minimap", _settings.Map, value => Change(_settings with { Map = value })),
                Option("Focus mode · dim the surrounding scene", _settings.Focus, value => Change(_settings with { Focus = value })),
                PanelKit.Muted("For in-game markers, turn on Settings → Overlay and use windowed or borderless mode."))));
    }

    void PopulateWatchlist()
    {
        if (_watchRows == null) return;
        _watchRows.Children.Clear();
        _watchDetails.Clear();
        foreach (string name in _settings.Names)
        {
            var details = PanelKit.Muted("");
            _watchDetails.Add(name, details);
            var row = new DockPanel { LastChildFill = true };
            var remove = PanelKit.Button("Remove", () =>
            {
                Change(_settings with { Names = _settings.Names.Where(n => !Same(n, name)).ToArray() });
                PopulateWatchlist();
                UpdatePickerButtons();
            });
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(PanelKit.Stack(2, PanelKit.Text(name), details));
            var note = new TextBox
            {
                Text = _notes.GetValueOrDefault(name), Watermark = "Add a note…",
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 112,
            };
            AutomationProperties.SetName(note, $"Note for {name}");
            note.PropertyChanged += (_, e) =>
            {
                if (e.Property != TextBox.TextProperty || _stop.IsCancellationRequested) return;
                var value = note.Text ?? "";
                if (value == _notes.GetValueOrDefault(name, "")) return;
                if (value.Length == 0) _notes.Remove(name);
                else _notes[name] = value;
                _notesChanged = true;
                ShowNoteStatus("Saving notes…");
                _noteSave.Stop();
                _noteSave.Start();
            };
            _watchRows.Children.Add(PanelKit.Stack(4, row, note));
        }
        if (_watchRows.Children.Count == 0) _watchRows.Children.Add(PanelKit.Muted("No watched players yet."));
        if (_exportWatchlist != null) _exportWatchlist.IsEnabled = _settings.Names.Length > 0;
        lock (_gate) UpdateWatchDetails(_seen);
    }

    async Task ExportWatchlistAsync()
    {
        if (TopLevel.GetTopLevel(_exportWatchlist) is not { } top) return;
        try
        {
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Player Finder watchlist",
                SuggestedFileName = $"PlayerFinder-Watchlist-{DateTime.Now:yyyyMMdd-HHmm}.csv",
                DefaultExtension = "csv",
                FileTypeChoices = [new FilePickerFileType("CSV file") { Patterns = ["*.csv"] }],
            });
            if (file?.TryGetLocalPath() is not { } path) return;

            string[] lines;
            lock (_gate)
            {
                lines = ["Name,Last Seen,Map,Channel,Notes", .. _settings.Names.Select(name =>
                {
                    var last = _seen.GetValueOrDefault(name);
                    return string.Join(",",
                        CsvField(name),
                        CsvField(last?.At.ToString("O", CultureInfo.InvariantCulture)),
                        CsvField(last?.Location),
                        CsvField(last?.Channel?.ToString(CultureInfo.InvariantCulture)),
                        CsvField(_notes.GetValueOrDefault(name)));
                })];
            }
            await File.WriteAllLinesAsync(path, lines, new UTF8Encoding(true));
            _context.Notify("Exported watchlist to " + Path.GetFileName(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _context.Log("Player Finder could not export the watchlist: " + e.Message);
            _context.Notify("Could not export the watchlist. " + e.Message);
        }
    }

    static string CsvField(string? value)
    {
        value ??= "";
        // Keep spreadsheet apps from treating free-text fields as formulas.
        var trimmed = value.AsSpan().TrimStart();
        if ((value.Length > 0 && value[0] is '\t' or '\r' or '\n') ||
            (trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@'))
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    void ShowNoteStatus(string message)
    {
        if (_noteStatus == null) return;
        _noteStatus.Text = message;
        _noteStatus.IsVisible = message.Length > 0;
    }

    void SaveNotes()
    {
        _noteSave.Stop();
        if (!_notesChanged) return;
        _context.Storage.Save("notes", _notes);
        _notesChanged = false;
        ShowNoteStatus("");
    }

    void AutosaveNotes()
    {
        try { SaveNotes(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowNoteStatus("Could not save notes. " + e.Message);
            _context.Log("Player Finder could not save notes: " + e.Message);
        }
    }

    void UpdateWatchDetails(IReadOnlyDictionary<string, LastSeen> seen)
    {
        foreach (var (name, details) in _watchDetails)
            details.Text = seen.TryGetValue(name, out var last)
                ? Describe(last)
                : "No sightings yet";
    }

    static string Describe(LastSeen seen) =>
        $"{Where(seen)} · {seen.At.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}";

    static string Where(LastSeen seen) =>
        $"{seen.Location ?? "Unknown map"} · {(seen.Channel is int channel ? $"Channel {channel}" : "Channel unknown")}";

    void AddToWatchlist(IEnumerable<string> names)
    {
        var updated = _settings.Names.Concat(names).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (updated.Length == _settings.Names.Length) return;
        Change(_settings with { Names = updated });
        PopulateWatchlist();
        UpdatePickerButtons();
    }

    void UpdatePickerButtons()
    {
        if (_addNearby != null)
            _addNearby.IsEnabled = _picked != null && !_settings.Names.Contains(_picked, StringComparer.OrdinalIgnoreCase);
        if (_clearPick != null) _clearPick.IsEnabled = _picked != null;
    }

    void Pick(string? name)
    {
        lock (_gate)
        {
            if (Same(_picked, name)) return;
            _picked = name;
            _pickedSeen = null;
            ResetOverlay();
        }
        UpdatePickerButtons();
    }

    void Change(Settings settings)
    {
        lock (_gate)
        {
            var old = _settings;
            _settings = settings;
            foreach (var name in old.Names.Except(settings.Names, StringComparer.OrdinalIgnoreCase))
            {
                _encounters.Remove(name);
                _seenChanged |= _seen.Remove(name);
                _alert = null;
            }
            if (!settings.Nearby) _nearbyRoster.Clear();
            if (!settings.Notify) _alert = null;
            ResetOverlay();
            _context.Storage.Save("settings", settings);
            SaveSightings();
        }
    }

    void ResetOverlay()
    {
        _result = null;
        _context.Overlay.Clear();
    }

    void TrackChat(GameFrame frame)
    {
        var chat = ChatPanel.Find(frame.Frame);
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;
            bool changed = chat != _liveChat;
            _chatFrame = frame;
            _liveChat = chat;
            if (_result is { } result && frame.Time - result.Frame.Time > TimeSpan.FromSeconds(2.5))
            {
                ResetOverlay();
                return;
            }
            if (changed || _alert != null && frame.Time >= _alertUntil) RenderOverlay(frame);
        }
    }

    void RenderOverlay(GameFrame current)
    {
        if (_alertUntil <= current.Time) _alert = null;
        if (_result is not { } result || result.Frame.Window.Handle != current.Window.Handle
            || result.Frame.Frame.Bounds != current.Frame.Bounds || result.Frame.Offset != current.Offset)
        {
            _context.Overlay.Clear();
            return;
        }
        var items = Draw(result.Scan, _settings.Focus && _liveChat != null, _liveChat ?? default,
            result.Chat == _liveChat);
        if (_alert != null) items.Add(_alert);
        _context.Overlay.Draw(items);
    }

    async Task RunAsync(CancellationToken ct)
    {
        var reader = new PlayerReader(_context.Ocr);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(450));
        GameFrame? previous = null;
        Settings? previousSettings = null;
        string? previousPick = null;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var options = _settings;
                var picked = _picked;
                var frame = _context.Frames.Latest;
                if (!_context.Ocr.Available || !IsCurrent(frame))
                {
                    Publish(options, picked, _context.Ocr.Available ? "Waiting for the game picture…" : "Text recognition is unavailable.");
                    continue;
                }
                if (!options.Nearby && options.Names.Length == 0)
                {
                    Publish(options, picked, "Add a player to the watchlist to start.");
                    continue;
                }
                if (ReferenceEquals(frame, previous) && ReferenceEquals(options, previousSettings) && Same(picked, previousPick)) continue;
                previous = frame;
                previousSettings = options;
                previousPick = picked;

                var gameFrame = frame!;
                var image = gameFrame.Frame;
                var chat = ChatPanel.Find(image);
                var names = options.Names;
                if (picked != null && !names.Contains(picked, StringComparer.OrdinalIgnoreCase)) names = [picked, .. names];
                var location = _context.Game.Location;
                var channel = _context.Game.Channel;
                try
                {
                    var scan = await reader.ReadAsync(image, names, _context.Game.Ign, chat ?? default,
                        options.Signs, options.Map, options.Chat, ct).ConfigureAwait(false);
                    var latest = _context.Frames.Latest;
                    if (latest == null || latest.Window.Handle != gameFrame.Window.Handle
                        || latest.Frame.Bounds != image.Bounds || latest.Offset != gameFrame.Offset)
                    {
                        Publish(options, picked, "Waiting for a current game picture…");
                        continue;
                    }
                    var result = new ScannedFrame(gameFrame, scan, chat);
                    if (!IsCurrent(gameFrame))
                    {
                        Publish(options, picked, "Waiting for a current game picture…", result: result, positionsCurrent: false);
                        continue;
                    }
                    var details = string.Join(Environment.NewLine, scan.Players.Select(p =>
                        p.Name + (p.FromBubble ? " · position estimated from speech bubble" : "")
                        + (p.Caption == null ? "" : $" · {p.Caption}")));
                    Frame? preview = null;
                    if (scan.Players.FirstOrDefault() is { } first)
                        preview = image.Crop(first.Sign is { } sign ? Union(first.Player, sign) : first.Player);

                    string status = names.Length == 0 ? "Choose a nearby player or add one to the watchlist."
                        : scan.Players.Count == 0 ? "No selected or watched players in view."
                        : $"Found {scan.Players.Count} of {names.Length} · {scan.Players.Count(p => p.MapMarker != null)} marked on minimap";
                    if (scan.Messages.Count > 0) status += $" · {scan.Messages.Count} matching chat";
                    Publish(options, picked, status, details, preview, result, location, channel);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _context.Log($"Player Finder scan failed: {e.Message}");
                    Publish(options, picked, "Could not read the game picture.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    bool IsCurrent(GameFrame? game) => game != null && _context.Frames.Status.State == CaptureState.Capturing
        && DateTimeOffset.Now - game.Time < TimeSpan.FromSeconds(2.5);

    void Publish(Settings options, string? picked, string status, string details = "", Frame? preview = null,
        ScannedFrame? result = null, string? location = null, int? channel = null, bool positionsCurrent = true)
    {
        var alerts = new List<string>();
        Dictionary<string, LastSeen> seen;
        string[] nearby;
        LastSeen? pickedSeen;
        lock (_gate)
        {
            if (!ReferenceEquals(options, _settings) || !Same(picked, _picked) || _stop.IsCancellationRequested) return;
            nearby = options.Nearby
                ? _nearbyRoster.Update(DateTimeOffset.Now, result?.Scan.Nearby, result?.Frame.Time)
                : [];
            _result = positionsCurrent ? result : null;
            if (_result != null)
            {
                var at = _result.Frame.Time;
                foreach (var player in _result.Scan.Players)
                {
                    var sighting = new LastSeen(at, location, channel);
                    if (Same(player.Name, picked)) _pickedSeen = sighting;
                    if (!options.Names.Contains(player.Name, StringComparer.OrdinalIgnoreCase)) continue;
                    if (_seen.TryGetValue(player.Name, out var last) && last.At >= at) continue;
                    _seen[player.Name] = sighting;
                    _seenChanged = true;
                    if ((!_encounters.TryGetValue(player.Name, out var lastEncounter) || at - lastEncounter > TimeSpan.FromSeconds(8))
                        && options.Notify)
                        alerts.Add($"{player.Name} spotted{(location == null ? " nearby" : $" in {location}")}"
                            + (channel is int knownChannel ? $" · Channel {knownChannel}" : ""));
                    _encounters[player.Name] = at;
                }
                if (_seenChanged && (alerts.Count > 0 || at - _savedAt >= TimeSpan.FromSeconds(8))) SaveSightings();
                if (alerts.Count > 0)
                {
                    _alert = new OverlayItem(_result.Scan.Scene.Width / 2.0, 32, alerts.Count == 1 ? alerts[0]
                        : $"{alerts.Count} watched players spotted", OverlayTone.Warning);
                    _alertUntil = at.AddSeconds(5);
                }
                if (_chatFrame == null || _chatFrame.Time < at)
                {
                    _chatFrame = _result.Frame;
                    _liveChat = _result.Chat;
                }
                RenderOverlay(_chatFrame);
            }
            else
            {
                _context.Overlay.Clear();
            }
            seen = new Dictionary<string, LastSeen>(_seen, StringComparer.OrdinalIgnoreCase);
            pickedSeen = _pickedSeen;
        }
        foreach (var alert in alerts) _context.Notify(alert);
        _context.OnUi(() =>
        {
            if (!ReferenceEquals(options, _settings) || !Same(picked, _picked) || _stop.IsCancellationRequested) return;
            UpdateWatchDetails(seen);
            Show(status, details, preview, nearby, picked, pickedSeen);
        });
    }

    void SaveSightings()
    {
        if (!_seenChanged) return;
        try
        {
            _context.Storage.Save("last-seen", _seen);
            _seenChanged = false;
            _savedAt = DateTimeOffset.Now;
        }
        catch (IOException e) { _context.Log($"Player Finder could not save sightings: {e.Message}"); }
        catch (UnauthorizedAccessException e) { _context.Log($"Player Finder could not save sightings: {e.Message}"); }
    }

    void Show(string status, string details, Frame? preview, string[] nearby, string? picked, LastSeen? pickedSeen)
    {
        if (_status == null || _matches == null || _preview == null) return;
        if (_nearby is { IsDropDownOpen: false } && (_nearby.ItemsSource is not string[] old || !old.SequenceEqual(nearby)))
        {
            _syncPicker = true;
            _nearby.ItemsSource = nearby;
            _nearby.SelectedItem = nearby.FirstOrDefault(name => Same(name, picked));
            _nearby.PlaceholderText = picked ?? (nearby.Length == 0 ? "No readable player names" : "Choose a nearby player");
            _syncPicker = false;
        }
        _status.Text = status;
        if (pickedSeen != null) details = string.IsNullOrEmpty(details)
            ? $"{picked} last seen · {Describe(pickedSeen)}"
            : $"{details}{Environment.NewLine}{picked} last seen · {Describe(pickedSeen)}";
        _matches.Text = details;
        _matches.IsVisible = details.Length > 0;
        _preview.IsVisible = preview != null;
        if (preview == null || !_preview.IsLoaded) return;
        var bitmap = PanelKit.ToBitmap(preview, _bitmap);
        if (_bitmap != bitmap)
        {
            _preview.Source = bitmap;
            _bitmap?.Dispose();
            _bitmap = bitmap;
        }
        _preview.InvalidateVisual();
    }

    static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static Region Union(Region a, Region b)
    {
        int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new Region(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }

    static List<OverlayItem> Draw(PlayerScan scan, bool focus, Region chat, bool sameChat)
    {
        var items = new List<OverlayItem>();
        if (focus && scan.Players.Count > 0)
        {
            var visible = new List<Region> { chat };
            if (scan.Minimap is { } map) visible.Add(map);
            foreach (var player in scan.Players)
                visible.Add(player.Sign is { } sign ? Union(player.Player, sign) : player.Player);
            var shaded = new List<Region> { scan.Scene };
            foreach (var cutout in visible)
                shaded = shaded.SelectMany(rect => Subtract(rect, cutout)).ToList();
            items.AddRange(shaded.Select(region => new OverlayItem(0, 0, "") { Rectangle = region, Dim = true }));
        }
        foreach (var player in scan.Players)
        {
            if (player.Player.Intersect(chat).IsEmpty)
                items.Add(new OverlayItem(player.Player.X + player.Player.Width / 2.0, player.Player.Bottom + 8,
                    player.Name, OverlayTone.Warning, Size: 12) { Rectangle = player.Player });
            if (player.Sign is { } sign && sign.Intersect(chat).IsEmpty) items.Add(Outline(sign));
            if (player.MapMarker is { } dot) items.Add(Outline(dot));
        }
        if (sameChat) items.AddRange(scan.Messages.Select(Outline));
        return items;
    }

    static OverlayItem Outline(Region region) => new(0, 0, "", OverlayTone.Warning) { Rectangle = region };

    static IEnumerable<Region> Subtract(Region area, Region cutout)
    {
        var hit = area.Intersect(cutout);
        if (hit.IsEmpty) { yield return area; yield break; }
        if (hit.Y > area.Y) yield return new Region(area.X, area.Y, area.Width, hit.Y - area.Y);
        if (hit.Bottom < area.Bottom) yield return new Region(area.X, hit.Bottom, area.Width, area.Bottom - hit.Bottom);
        if (hit.X > area.X) yield return new Region(area.X, hit.Y, hit.X - area.X, hit.Height);
        if (hit.Right < area.Right) yield return new Region(hit.Right, hit.Y, area.Right - hit.Right, hit.Height);
    }
}
