using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MapleLite.Sdk;
using MapleLite.Sdk.Theming;
using static MapleLite.Sdk.PanelKit;

namespace MapleLite.Plugins.XpTracker;

internal sealed class PartyQuestPanel : StackPanel
{
    static readonly (PqExpSource Source, string Label, string Color)[] Sources =
    [
        (PqExpSource.Monster, "Monster EXP", ThemeTokens.Accent),
        (PqExpSource.Stage, "Stage rewards", ThemeTokens.Warning),
        (PqExpSource.Completion, "Completion reward", ThemeTokens.Success),
        (PqExpSource.Unclassified, "Unclassified rewards", ThemeTokens.TextMuted),
    ];

    readonly PqRunLog _runs;
    readonly XpLog _gains;
    readonly ComboBox _range = new() { ItemsSource = new[] { "All time", "Today", "Last 7 days", "Last 30 days" }, SelectedIndex = 0 };
    readonly ComboBox _name = new() { IsEditable = true, PlaceholderText = "Party quest name", Width = 208, MaxDropDownHeight = 240 };
    readonly ListBox _quests = new() { MaxHeight = 370, Name = "QuestList", Background = Brushes.Transparent };
    readonly ListBox _runList = new() { MaxHeight = 248, Name = "RunList", Background = Brushes.Transparent };
    readonly TextBlock _total = Big("0 EXP");
    readonly TextBlock _count = Muted("");
    readonly TextBlock _status = Muted("");
    readonly TextBlock _error = Text("", ThemeTokens.Danger);
    readonly StackPanel _detail = Stack(12);
    readonly StackPanel _breakdown = Stack(10);
    readonly Button _start;
    readonly Button _end;
    IReadOnlyList<PqRun> _loadedRuns = [];
    IReadOnlyList<PqRunSummary> _summaries = [];
    long _revision = -1;
    string? _preferQuest;
    Guid? _preferRun;
    bool _updating, _attached, _filling;
    DateTime _day = DateTime.Today;

    public PartyQuestPanel(PqRunLog runs, XpLog gains)
    {
        _runs = runs;
        _gains = gains;
        Spacing = 12;
        _start = Button("Start run", () => Change(() =>
        {
            var run = _runs.Start(_name.Text ?? _name.SelectedItem as string ?? "", DateTimeOffset.Now);
            _preferQuest = run.Quest;
            _preferRun = run.Id;
            _range.SelectedIndex = 0;
        }), accent: true);
        _end = Button("End run", () => Change(() => _runs.End(DateTimeOffset.Now)), accent: true);
        _start.IsEnabled = false;
        _name.PropertyChanged += (_, e) =>
        {
            if (e.Property == ComboBox.TextProperty) _start.IsEnabled = !string.IsNullOrWhiteSpace(_name.Text);
        };

        _range.MinWidth = 116;
        var total = Stack(1, Muted("Total party quest EXP"), _total);
        Children.Add(Wrap(Stack(3, Muted("Date range"), _range), total, _count));
        total.Margin = new Thickness(8, 0, 12, 8);
        Children.Add(Wrap(_name, _start, _end, _status));
        Children.Add(_error);
        _error.IsVisible = false;
        var left = Card(Stack(8, Title("Party quests"), _quests), 12);
        var right = Card(_detail);
        var body = new Grid { ColumnDefinitions = new("1*,2*"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 12, RowSpacing = 12 };
        body.Children.Add(left);
        body.Children.Add(right);
        Grid.SetColumn(right, 1);
        SizeChanged += (_, e) =>
        {
            _count.Margin = new Thickness(0, e.NewSize.Width >= 510 ? 21 : 0, 8, 8);
            bool narrow = e.NewSize.Width < 640;
            if ((Grid.GetRow(right) == 1) == narrow) return;
            body.ColumnDefinitions = narrow ? new("*,0") : new("1*,2*");
            body.ColumnSpacing = narrow ? 0 : 12;
            Grid.SetColumn(right, narrow ? 0 : 1);
            Grid.SetRow(right, narrow ? 1 : 0);
        };
        Children.Add(body);

        _range.SelectionChanged += (_, _) => Render();
        _quests.SelectionChanged += (_, _) => { if (!_filling) ShowQuest(); };
        _runList.SelectionChanged += (_, _) => ShowRun();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Refresh();
        AttachedToVisualTree += (_, _) => { _attached = true; Refresh(); timer.Start(); };
        DetachedFromVisualTree += (_, _) => { _attached = false; timer.Stop(); };
        _detail.Children.Add(Muted("Loading party quest runs…"));
        UpdateControls();
    }

    void Change(Action action)
    {
        try
        {
            action();
            _error.IsVisible = false;
            UpdateControls();
            Refresh();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _error.Text = e.Message;
            _error.IsVisible = true;
        }
    }

    void UpdateControls()
    {
        var active = _runs.Active;
        var suggested = _runs.Runs.Select(r => r.Quest).Concat(["Henesys PQ", "Kerning City PQ", "Ludibrium PQ", "Orbis PQ"])
            .Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (_name.ItemsSource is not string[] names || !names.SequenceEqual(suggested))
        {
            var text = _name.Text;
            _name.ItemsSource = suggested;
            _name.Text = text;
        }
        _name.IsVisible = _start.IsVisible = active == null;
        _end.IsVisible = active != null;
        _status.Text = active == null ? "Start a run before entering the PQ." : $"Recording {active.Quest} since {active.Started.LocalDateTime:t}";
    }

    async void Refresh()
    {
        if (!_attached || _updating) return;
        var runs = _runs.Runs;
        var revision = _gains.Revision;
        var day = DateTime.Today;
        if (_revision >= 0 && ReferenceEquals(runs, _loadedRuns) && (runs.Count == 0 || revision == _revision))
        {
            if (_day != day) { _day = day; Render(); }
            return;
        }

        _updating = true;
        try
        {
            var summaries = runs.Count == 0 ? [] : await Task.Run(() => PqRunLog.Summarize(runs, _gains.All()));
            if (!_attached || !ReferenceEquals(runs, _runs.Runs)) return;
            var redraw = _revision < 0 || _day != day || !ReferenceEquals(runs, _loadedRuns) ||
                !_summaries.Zip(summaries).All(pair => pair.First.Gains.SequenceEqual(pair.Second.Gains));
            _summaries = summaries;
            _loadedRuns = runs;
            _revision = revision;
            _day = day;
            if (redraw) { UpdateControls(); Render(); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _error.Text = $"Couldn't read EXP gains. {e.Message}";
            _error.IsVisible = true;
        }
        finally { _updating = false; }
    }

    DateTime FirstDay() => _range.SelectedIndex switch
    {
        1 => _day,
        2 => _day.AddDays(-6),
        3 => _day.AddDays(-29),
        _ => DateTime.MinValue,
    };

    void Render()
    {
        var selected = _preferQuest ?? (_quests.SelectedItem as ListBoxItem)?.Tag as string;
        var first = FirstDay();
        var groups = _summaries.Where(r => r.Run.Started.LocalDateTime.Date >= first)
            .GroupBy(r => r.Run.Quest, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => (Name: g.Key, Count: g.Count(), Exp: g.Sum(r => r.Exp)))
            .OrderByDescending(g => g.Exp).ThenBy(g => g.Name).ToArray();
        _total.Text = $"{groups.Sum(g => g.Exp):N0} EXP";
        _count.Text = $"{RunCount(groups.Sum(g => g.Count))} across {groups.Length:N0} PQ{(groups.Length == 1 ? "" : "s")}";

        _filling = true;
        var rows = groups.Select(g => Row(g.Name, g.Name, RunCount(g.Count), g.Exp)).ToArray();
        _quests.ItemsSource = rows;
        _quests.SelectedItem = rows.FirstOrDefault(r => StringComparer.CurrentCultureIgnoreCase.Equals((string?)r.Tag, selected)) ?? rows.FirstOrDefault();
        _filling = false;
        _preferQuest = null;
        ShowQuest();
    }

    void ShowQuest()
    {
        var previous = _preferRun ?? (_runList.SelectedItem as ListBoxItem)?.Tag as Guid?;
        _detail.Children.Clear();
        if (_quests.SelectedItem is not ListBoxItem { Tag: string quest })
        {
            _runList.ItemsSource = Array.Empty<ListBoxItem>();
            _detail.Children.Add(Title("No runs in this range"));
            _detail.Children.Add(Muted("Choose a different date range or start a party quest run to track its EXP."));
            return;
        }
        var all = _summaries.Where(r => StringComparer.CurrentCultureIgnoreCase.Equals(r.Run.Quest, quest))
            .Select((summary, index) => (Summary: summary, Number: index + 1))
            .Where(r => r.Summary.Run.Started.LocalDateTime.Date >= FirstDay()).ToArray();
        var rows = Enumerable.Reverse(all).Select(r =>
        {
            var run = r.Summary.Run;
            var duration = run.Ended is { } ended ? XpTrackerPlugin.Duration(ended - run.Started) : "Recording";
            return Row(run.Id, $"Run {r.Number}", $"{run.Started.LocalDateTime:MMM d, h:mm tt} · {duration}", r.Summary.Exp);
        }).ToArray();
        _detail.Children.Add(Stack(5, Text(quest, size: 23, weight: FontWeight.SemiBold), Big($"{all.Sum(r => r.Summary.Exp):N0} EXP")));
        _detail.Children.Add(Divider());
        _detail.Children.Add(Title("Runs"));
        _detail.Children.Add(_runList);
        _detail.Children.Add(Divider());
        _detail.Children.Add(_breakdown);
        _runList.ItemsSource = rows;
        _runList.SelectedItem = rows.FirstOrDefault(r => Equals(r.Tag, previous)) ?? rows.FirstOrDefault();
        _preferRun = null;
        ShowRun();
    }

    void ShowRun()
    {
        _breakdown.Children.Clear();
        if (_runList.SelectedItem is not ListBoxItem { Tag: Guid id } row ||
            _summaries.FirstOrDefault(r => r.Run.Id == id) is not { } summary) return;

        var title = Avalonia.Automation.AutomationProperties.GetName(row);
        _breakdown.Children.Add(NameAndValue(Title($"{title} breakdown"), $"{summary.Exp:N0} EXP", ThemeTokens.Accent));
        var bar = new Grid { Height = 13, ColumnSpacing = 2, ClipToBounds = true };
        foreach (var (source, label, color) in Sources)
        {
            var exp = summary.ExpFrom(source);
            if (source == PqExpSource.Unclassified && exp == 0) continue;
            var swatch = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(2) }.Themed(Border.BackgroundProperty, color);
            var figure = NameAndValue(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { swatch, Text(label, size: 14) } }, $"{exp:N0} EXP");
            _breakdown.Children.Add(figure);
            if (exp == 0) continue;
            var segment = new Border().Themed(Border.BackgroundProperty, color);
            Grid.SetColumn(segment, bar.ColumnDefinitions.Count);
            bar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(exp, GridUnitType.Star)));
            bar.Children.Add(segment);
        }
        if (bar.Children.Count > 0) _breakdown.Children.Add(bar);
        if (summary.Gains.Count > 0)
            _breakdown.Children.Add(Wrap(Button("Classify reward EXP…", () => EditReward(summary))));
        else
            _breakdown.Children.Add(Muted("No EXP gains recorded for this run yet."));
    }

    async void EditReward(PqRunSummary summary)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var showAll = new CheckBox { Content = "Show all recorded gains" };
        var gains = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Select an EXP gain" };
        var classification = new ComboBox
        {
            ItemsSource = new[] { "Use detected source" }.Concat(Sources.Select(s => s.Label)).ToArray(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var dialog = new Window
        {
            Title = "Classify reward EXP", Width = 430, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false,
        }.Themed(Window.BackgroundProperty, ThemeTokens.Background);

        var save = Button("Save classification", () => dialog.Close(true), accent: true);
        void FillGains()
        {
            var chosen = (gains.SelectedItem as ComboBoxItem)?.Tag;
            var rows = summary.Gains.Where(g => showAll.IsChecked == true || g.Reward || summary.Run.Sources.ContainsKey(g.Time))
                .Reverse().Select(g => new ComboBoxItem
                {
                    Tag = g,
                    Content = $"{g.Time.LocalDateTime:HH:mm:ss} · +{g.Amount:N0} EXP · {Sources.Single(s => s.Source == summary.Run.SourceOf(g)).Label}",
                }).ToArray();
            gains.ItemsSource = rows;
            gains.SelectedItem = rows.FirstOrDefault(r => Equals(r.Tag, chosen)) ?? rows.FirstOrDefault();
            save.IsEnabled = rows.Length > 0;
        }
        showAll.IsChecked = !summary.Gains.Any(g => g.Reward || summary.Run.Sources.ContainsKey(g.Time));
        showAll.IsCheckedChanged += (_, _) => FillGains();
        gains.SelectionChanged += (_, _) =>
        {
            classification.SelectedIndex = gains.SelectedItem is ComboBoxItem { Tag: XpGain gain } &&
                summary.Run.Sources.TryGetValue(gain.Time, out var source) ? Array.FindIndex(Sources, s => s.Source == source) + 1 : 0;
        };
        FillGains();
        dialog.Content = new Border
        {
            Padding = new Thickness(18),
            Child = Stack(10, Muted("Choose a recorded gain and assign its source."), showAll, gains, classification,
                Wrap(save, Button("Cancel", () => dialog.Close(false)))),
        };
        if (await dialog.ShowDialog<bool>(owner) && gains.SelectedItem is ComboBoxItem { Tag: XpGain gain })
        {
            PqExpSource? source = classification.SelectedIndex > 0 ? Sources[classification.SelectedIndex - 1].Source : null;
            Change(() => _runs.SetSource(summary.Run.Id, gain, source));
        }
    }

    static string RunCount(int count) => $"{count:N0} run{(count == 1 ? "" : "s")}";

    static ListBoxItem Row(object key, string title, string subtitle, long exp)
    {
        var item = new ListBoxItem
        {
            Tag = key,
            Content = NameAndValue(Stack(3, Title(title), Muted(subtitle)), $"{exp:N0} EXP"),
            Template = new FuncControlTemplate<ListBoxItem>((owner, _) =>
            {
                var tint = new Border { CornerRadius = new CornerRadius(5), IsHitTestVisible = false }
                    .Themed(Border.BackgroundProperty, ThemeTokens.Accent);
                var edge = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Left, Width = 3, CornerRadius = new CornerRadius(2),
                    IsHitTestVisible = false,
                }.Themed(Border.BackgroundProperty, ThemeTokens.Accent);
                void Update()
                {
                    tint.Opacity = owner.IsSelected ? .16 : owner.IsPointerOver || owner.IsKeyboardFocusWithin ? .07 : 0;
                    edge.IsVisible = owner.IsSelected;
                }
                // The template follows the row's selection and pointer states while it is attached.
                tint.AttachedToVisualTree += (_, _) => { owner.PropertyChanged += StateChanged; Update(); };
                tint.DetachedFromVisualTree += (_, _) => owner.PropertyChanged -= StateChanged;
                void StateChanged(object? _, AvaloniaPropertyChangedEventArgs e)
                {
                    if (e.Property == ListBoxItem.IsSelectedProperty || e.Property == IsPointerOverProperty ||
                        e.Property == IsKeyboardFocusWithinProperty) Update();
                }
                var presenter = new ContentPresenter { Padding = new Thickness(11, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch };
                presenter.Bind(ContentPresenter.ContentProperty, new Binding(nameof(ContentControl.Content)) { Source = owner });
                return new Border
                {
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = new Grid { Background = Brushes.Transparent, Children = { tint, edge, presenter } },
                }.Themed(Border.BorderBrushProperty, ThemeTokens.Border);
            }),
        };
        Avalonia.Automation.AutomationProperties.SetName(item, title);
        return item;
    }

    static Grid NameAndValue(Control title, string value, string color = ThemeTokens.Text)
    {
        var grid = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        var amount = Text(value, color, size: 14, weight: FontWeight.SemiBold);
        amount.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(amount, 1);
        grid.Children.Add(title);
        grid.Children.Add(amount);
        return grid;
    }

    static Border Divider() => new Border { Height = 1 }.Themed(Border.BackgroundProperty, ThemeTokens.Border);
}
