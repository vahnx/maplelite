using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MapleLite.Sdk;
using MapleLite.Sdk.Theming;
using static MapleLite.Sdk.PanelKit;

namespace MapleLite.Plugins.ChatHistory;

/// <summary>The Chat History page: controls, filters, the journal table and the loot tracker.</summary>
internal sealed class MemoryPanel : UserControl
{
    const int MaxRows = 3000;
    readonly MemoryEngine _engine;
    readonly Button _start;
    readonly TextBlock _status = Muted(""), _summary = Text(""), _rangeError = Text("", ThemeTokens.Danger, 12);
    readonly TextBox _search = new() { Watermark = "Search names, messages or events", MinWidth = 320 };
    readonly WrapPanel _channels = new();
    readonly CalendarDatePicker _fromDate = new() { Width = 140, Watermark = "Any day" }, _toDate = new() { Width = 140, Watermark = "Any day" };
    readonly TextBox _fromTime = new() { Text = "00:00", Width = 80 }, _toTime = new() { Text = "23:59", Width = 80 };
    readonly ComboBox _zone = new() { MinWidth = 320 };
    readonly DataGrid _grid;
    readonly LootView _loot;
    string _channel = "All";
    TimeFilter _time = TimeFilter.Open;
    bool _rangeValid = true;
    int _shownCount = -1;

    public MemoryPanel(MemoryEngine engine)
    {
        _engine = engine;
        _start = Button("", async () => await _engine.ToggleAsync(), accent: true);

        var actions = Wrap(_start,
            Button("Find chat automatically", async () => { _engine.Settings.ManualChat = false; if (await _engine.DetectChatAsync()) await _engine.AutoStartAsync(); }),
            Button("Select chat area…", async () => await SelectArea()),
            Button("Export CSV…", async () => await Export()),
            Button("Open logs folder", () => OpenFolder(_engine.Journal.Folder)),
            Button("Import from MapleMemory app", () => { _engine.ImportAgain(); Refresh(force: true); }));

        foreach (var c in Classifier.Channels)
        {
            var b = new ToggleButton { Content = c, IsChecked = c == _channel, Margin = new Thickness(0, 0, 6, 6) };
            b.Click += (_, _) => { _channel = c; foreach (ToggleButton x in _channels.Children) x.IsChecked = (string)x.Content! == c; Refresh(force: true); };
            _channels.Children.Add(b);
        }
        _search.TextChanged += (_, _) => Refresh(force: true);

        foreach (var z in ClockZone.Choices())
        {
            var item = new ComboBoxItem { Content = z.DisplayName, Tag = z };
            _zone.Items.Add(item);
            if (z.Id == ClockZone.Zone.Id) _zone.SelectedItem = item;
        }
        _zone.SelectionChanged += (_, _) =>
        {
            if (_zone.SelectedItem is ComboBoxItem { Tag: TimeZoneInfo z })
            {
                ClockZone.Zone = z; _engine.Settings.TimeZoneId = z.Id; _engine.SaveSettings(); Refresh(force: true);
            }
        };
        _fromDate.SelectedDateChanged += (_, _) => UpdateRange();
        _toDate.SelectedDateChanged += (_, _) => UpdateRange();
        _fromTime.TextChanged += (_, _) => UpdateRange();
        _toTime.TextChanged += (_, _) => UpdateRange();
        ToolTip.SetTip(_fromTime, "24-hour time: HH:mm or HH:mm:ss. Blank = no limit.");
        ToolTip.SetTip(_toTime, "24-hour time. To includes the whole minute.");

        var range = Wrap(Muted("Observed from"), _fromDate, _fromTime, Muted("to"), _toDate, _toTime,
            Button("Clear dates", () => { _fromDate.SelectedDate = null; _toDate.SelectedDate = null; UpdateRange(); }));
        var zoneRow = Wrap(Muted("Show times in"), _zone);

        var options = Stack(6,
            Check("Skip similar repeats from the same sender (10 min)", _engine.Settings.SuppressSpam, v => _engine.Settings.SuppressSpam = v),
            Check("Hide trailing @@@@ filler in messages", _engine.Settings.StripTrailingAt, v => { _engine.Settings.StripTrailingAt = MessageParser.StripTrailingAt = v; Refresh(force: true); }),
            Check("Show the in-game time instead of the observed time", _engine.Settings.ShowGameTime, v => { _engine.Settings.ShowGameTime = v; Refresh(force: true); }));
        var help = new Expander
        {
            Header = "How it works",
            Content = Stack(4,
                Muted("• Chat History reads the chat box from the game picture with MapleLite's shared text reader. Keep chat scrolled to the newest messages."),
                Muted("• A taller chat box with higher opacity gives the best coverage. Fast chat, wrapped lines and misread text can cause missing or duplicate lines."),
                Muted("• Whisper, buddy, party and guild come from labels or classic chat colors. Trade dialogs are checked about every 3 s, pickups about every 0.6 s."),
                Muted("• Times are when Chat History saw the text. Leave a date blank to apply the clock time on every day (From 19:00 = 7 PM onward each day)."),
                Muted("• Logs stay on this computer. The table shows the newest 3,000 matching lines; CSV export includes all of them.")),
        };

        _grid = BuildGrid();
        _loot = new LootView(engine);

        var journalTab = new DockPanel();
        var settings = new Expander { Header = "Options", Content = Stack(10, zoneRow, options) };
        var top = Stack(10, _channels, _search, range, _rangeError, Wrap(settings, help));
        top.Margin = new Thickness(0, 0, 0, 10);
        DockPanel.SetDock(top, Dock.Top);
        journalTab.Children.Add(top);
        var tableBox = Card(_grid, 0);
        tableBox.Height = 560;
        journalTab.Children.Add(tableBox);

        var tabs = new TabControl
        {
            ItemsSource = new[]
            {
                new TabItem { Header = "Journal", Content = journalTab },
                new TabItem { Header = "Loot", Content = _loot },
            },
        };

        Content = Stack(12, actions, Card(Stack(4, _summary, _status), 12), tabs);
        _engine.Changed += () => Refresh(force: false);
        AttachedToVisualTree += (_, _) => Refresh(force: true);
        Refresh(force: true);
    }

    CheckBox Check(string text, bool value, Action<bool> set)
    {
        var c = new CheckBox { Content = text, IsChecked = value };
        c.IsCheckedChanged += (_, _) => { set(c.IsChecked == true); _engine.SaveSettings(); };
        return c;
    }

    DataGrid BuildGrid()
    {
        var grid = new DataGrid
        {
            IsReadOnly = true, AutoGenerateColumns = false, CanUserResizeColumns = true, CanUserSortColumns = false,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Single,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Observed", Binding = new Binding(nameof(Entry.When)), Width = DataGridLength.Auto });
        grid.Columns.Add(new DataGridTextColumn { Header = "Game time", Binding = new Binding(nameof(Entry.GameTime)), Width = DataGridLength.Auto });
        grid.Columns.Add(new DataGridTextColumn { Header = "Username", Binding = new Binding(nameof(Entry.Username)), Width = DataGridLength.Auto });
        grid.Columns.Add(new DataGridTextColumn { Header = "Channel", Binding = new Binding(nameof(Entry.Channel)), Width = DataGridLength.Auto });
        grid.Columns.Add(new DataGridTextColumn { Header = "Event", Binding = new Binding(nameof(Entry.Direction)), Width = DataGridLength.Auto });
        grid.Columns.Add(new DataGridTextColumn { Header = "Side", Binding = new Binding(nameof(Entry.Side)), Width = DataGridLength.Auto });
        grid.Columns.Add(new DataGridTextColumn { Header = "Item", Binding = new Binding(nameof(Entry.Item)), Width = DataGridLength.Auto });
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Message", Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            CellTemplate = new FuncDataTemplate<Entry>((_, _) => new TextBlock
            {
                [!TextBlock.TextProperty] = new Binding(nameof(Entry.Message)),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 6), VerticalAlignment = VerticalAlignment.Center,
            }),
        });

        var copyMessage = new MenuItem { Header = "Copy message" };
        copyMessage.Click += async (_, _) => { if (grid.SelectedItem is Entry e) await Copy(e.Message); };
        var copyRow = new MenuItem { Header = "Copy row" };
        copyRow.Click += async (_, _) =>
        {
            if (grid.SelectedItem is Entry e) await Copy(string.Join("\t", e.When, e.GameTime, e.Username, e.Channel, e.Direction, e.Side, e.Item, e.Message));
        };
        grid.ContextMenu = new ContextMenu { ItemsSource = new[] { copyMessage, copyRow } };
        return grid;
    }

    async Task Copy(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clip) { await clip.SetTextAsync(text); _status.Text = "Copied to clipboard."; }
    }


    void UpdateRange()
    {
        try
        {
            _time = TimeWindow.Build(_fromDate.SelectedDate, _fromTime.Text ?? "", _toDate.SelectedDate, _toTime.Text ?? "");
            _rangeValid = true; _rangeError.Text = "";
        }
        catch (FormatException e) { _rangeValid = false; _rangeError.Text = e.Message; }
        Refresh(force: true);
    }

    List<Entry> Filtered()
    {
        var q = _search.Text ?? "";
        return _engine.Journal.Entries.Where(e => _rangeValid && _time.Matches(e.Time)
            && (_channel == "All" || e.Channel == _channel)
            && (string.IsNullOrWhiteSpace(q) || e.Text.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Direction.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    void Refresh(bool force)
    {
        _start.Content = _engine.Running ? "Ⅱ  Pause logging" : "▶  Start logging";
        _status.Text = _engine.Status;
        int count = _engine.Journal.Entries.Count;
        if (!force && count == _shownCount) return;
        _shownCount = count;
        _grid.Columns[0].Header = $"Observed ({ClockZone.Offset(ClockZone.Zone)})";
        _grid.Columns[0].IsVisible = !_engine.Settings.ShowGameTime;
        _grid.Columns[1].IsVisible = _engine.Settings.ShowGameTime;
        var rows = Filtered();
        _grid.ItemsSource = rows.AsEnumerable().Reverse().Take(MaxRows).ToList();
        var all = _engine.Journal.Entries;
        _summary.Text = $"{rows.Count:N0} matching messages  ·  {all.Count:N0} saved  ·  {all.Count(e => e.Channel == "Trade")} trade events  ·  {all.Count(e => e.Channel == "Fame")} fame events";
        _loot.Refresh();
    }

    async Task SelectArea()
    {
        var latest = _engine.LatestFrame;
        if (latest == null) { _status.Text = "No game picture yet. Start the game (it can stay behind other windows)."; return; }
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (_engine.Running) await _engine.StopAsync();
        var picker = new AreaPicker(latest.Frame, _engine.Settings.Chat?.ToRegion());
        var area = await picker.ShowDialog<MapleLite.Core.Imaging.Region?>(owner);
        if (area is { } a)
        {
            _engine.SetArea(a, latest.Frame, manual: true);
            _status.Text = "Chat area saved. It will be used instead of finding chat automatically.";
            await _engine.ToggleAsync();
        }
        Refresh(force: true);
    }

    async Task Export()
    {
        if (!_rangeValid) { _status.Text = _rangeError.Text; return; }
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export messages", SuggestedFileName = $"ChatHistory-{DateTime.Now:yyyyMMdd-HHmm}.csv", DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV file") { Patterns = ["*.csv"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        File.WriteAllLines(path, Csv.Lines(Filtered()), new UTF8Encoding(true));
        _status.Text = "Exported matching messages to " + Path.GetFileName(path);
    }
}

/// <summary>CSV in the MapleMemory app's format, with spreadsheet formula injection neutralized.</summary>
internal static class Csv
{
    public const string Header = "Observed,GameTime,Username,Channel,Event,Side,Item,Message,Source,RawOCR";

    public static IEnumerable<string> Lines(IEnumerable<Entry> entries)
    {
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        static string Safe(string s) => s.Length > 0 && "=+-@\t\r".Contains(s[0]) ? "'" + s : s;
        yield return Header;
        foreach (var e in entries)
            yield return string.Join(",", new[] { ClockZone.Convert(e.Time).ToString("yyyy-MM-dd HH:mm:ss zzz"), e.GameTime, e.Username, e.Channel, e.Direction, e.Side, e.Item, e.Message, e.Source, e.Text }.Select(s => Q(Safe(s))));
    }
}
