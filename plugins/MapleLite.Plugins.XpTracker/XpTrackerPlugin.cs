using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MapleLite.Core.Game;
using MapleLite.Sdk;
using static MapleLite.Sdk.PanelKit;

namespace MapleLite.Plugins.XpTracker;

/// <summary>
/// XP tracker: EXP per hour, kills (rises in EXP), time to the next level, and a history of past sessions.
/// EXP is read from the status bar with Core's pixel-font reader (<see cref="StatusBarReader"/>), which matches the
/// game's own digits exactly. Each rise is published as an <see cref="ExpGainedEvent"/> for other plugins.
/// </summary>
public sealed class XpTrackerPlugin : IPlugin
{
    const double ReadFps = 5;
    const int HistoryKept = 100;

    IPluginContext? _ctx;
    IDisposable? _sub;
    readonly StatusBarReader _reader = new();
    readonly object _lock = new();
    XpSession _session = new();
    DateTimeOffset? _lastSeen;
    StatusBarReading? _last;
    List<XpSessionRecord> _history = new();
    double? _startPercent;
    XpLog? _log;
    MapNames _maps = new();
    string? _lastMapRead;
    DateTimeOffset _lastFlush = DateTimeOffset.MinValue;

    public PluginManifest Manifest { get; } = new("xptracker", "XP Tracker", "0.1.0-alpha", "CheesyFrog Technologies",
        "EXP per hour, kills, time to the next level, past sessions, and every gain by map and time, read from the status bar.");

    public Task StartAsync(IPluginContext context)
    {
        _ctx = context;
        _history = context.Storage.Load<History>("history").Sessions;
        // A session still running when MapleLite last closed (or crashed) is saved every 30 s: file it now.
        if (context.Storage.Load<History>("current").Sessions is [{ Gained: > 0 } leftover, ..])
        {
            _history.Insert(0, leftover);
            SaveHistory();
            SaveCurrent(null);
        }
        _log = new XpLog(context.Storage.Folder);
        // the map names seen before, so today's slightly different read of a map files under the same name
        var log = _log;
        Task.Run(() =>
        {
            try
            {
                var known = new MapNames(log.All().Select(g => g.Map));
                lock (_lock) { _maps = known; _lastMapRead = null; }
            }
            catch (Exception e) { context.Log("couldn't read the EXP log: " + e.Message); }
        });
        _sub = context.Frames.Subscribe(OnFrame, ReadFps);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _sub?.Dispose(); _sub = null;
        List<(XpGain Gain, int Session)> left;
        lock (_lock) { left = [.. _held]; _held.Clear(); }
        foreach (var (g, s) in left) FileGain(g, s); // not confirmed in chat in time: kills
        EndSession();
        Flush();
        return Task.CompletedTask;
    }

    void Flush()
    {
        try { _log?.Flush(); }
        catch (IOException e) { _ctx?.Log("couldn't save the EXP log: " + e.Message); }
    }

    void OnFrame(MapleLite.Core.Capture.GameFrame gf)
    {
        var r = _reader.Read(gf.Frame); // well under a millisecond once the bar has been found
        if (r == null) return;
        long gain;
        string map;
        lock (_lock)
        {
            _lastSeen = gf.Time;
            _last = r;
            _startPercent ??= r.ExpPercent;
            gain = _session.Add(gf.Time, r.Exp, r.ExpPercent);
            if (_ctx?.Map.Map is { } read && read != _lastMapRead) { _lastMapRead = read; _maps.Add(read); }
            map = _maps.Current;
        }
        if (gain > 0)
        {
            var g = new XpGain(gf.Time, gain, map);
            bool hold;
            lock (_lock) hold = _spotter.Worth(gain);
            if (hold) lock (_lock) _held.Add((g, _sessionNo));
            else FileGain(g, _sessionNo);
            _ctx?.Events.Publish(new ExpGainedEvent(gf.Time, gain, r.Exp, r.ExpPercent, Manifest.Id));
        }
        CheckHeld(gf);
        if (gf.Time - _lastFlush > TimeSpan.FromSeconds(10))
        {
            _lastFlush = gf.Time;
            Task.Run(Flush);
        }
        if (gf.Time - _lastSaved > TimeSpan.FromSeconds(30))
        {
            _lastSaved = gf.Time;
            var rec = Current();
            Task.Run(() => SaveCurrent(rec));
        }
    }

    DateTimeOffset _lastSaved = DateTimeOffset.MinValue;

    // ---- telling rewards (PQ stage clears and completions, quests) from kills ----

    readonly RewardSpotter _spotter = new();
    readonly List<(XpGain Gain, int Session)> _held = new();
    int _sessionNo, _sessionRewards;
    long _sessionRewardExp;
    bool _chatReading;
    DateTimeOffset _lastChatRead = DateTimeOffset.MinValue;

    /// <summary>Keeps a gain in the log as a kill or a reward, and in the session's reward count.</summary>
    void FileGain(XpGain g, int session)
    {
        lock (_lock)
        {
            if (!g.Reward) _spotter.Killed(g.Amount);
            else if (session == _sessionNo) { _sessionRewards++; _sessionRewardExp += g.Amount; }
        }
        _log?.Add(g);
    }

    /// <summary>
    /// For gains held as possible rewards: reads the chat box (a few times a second) for their amounts. Found means a
    /// reward; still not found after <see cref="RewardSpotter.LookFor"/> means a kill.
    /// </summary>
    void CheckHeld(MapleLite.Core.Capture.GameFrame gf)
    {
        List<(XpGain Gain, int Session)> due;
        lock (_lock)
        {
            if (_held.Count == 0) return;
            due = _held.Where(h => gf.Time - h.Gain.Time > RewardSpotter.LookFor).ToList();
            foreach (var d in due) _held.Remove(d);
        }
        foreach (var (g, s) in due) FileGain(g, s);
        if (_ctx is not { Ocr.Available: true } ctx || _chatReading || gf.Time - _lastChatRead < TimeSpan.FromMilliseconds(300)) return;
        lock (_lock) if (_held.Count == 0) return;
        _chatReading = true;
        _lastChatRead = gf.Time;
        var known = ctx.Events.Last<ChatAreaEvent>() is { } a && a.FrameWidth == gf.Frame.W && a.FrameHeight == gf.Frame.H ? a.Area : (MapleLite.Core.Imaging.Region?)null;
        var chat = gf.Frame.Crop(RewardSpotter.ChatArea(gf.Frame, known)); // a copy: the frame isn't ours to keep
        Task.Run(async () =>
        {
            try
            {
                var text = (await ctx.Ocr.ReadAsync(chat, new MapleLite.Core.Ocr.OcrRequest(new MapleLite.Core.Imaging.Region(0, 0, chat.W, chat.H)))).Text;
                List<(XpGain Gain, int Session)> found;
                lock (_lock)
                {
                    found = _held.Where(h => RewardSpotter.InChat(text, h.Gain.Amount)).ToList();
                    foreach (var f in found) _held.Remove(f);
                }
                foreach (var (g, s) in found) FileGain(g with { Reward = true }, s);
            }
            catch (Exception e) { ctx.Log("couldn't read the chat for a reward: " + e.Message); }
            finally { _chatReading = false; }
        });
    }

    XpSessionRecord? Current()
    {
        lock (_lock)
            return _session is { Started: { } s, LastTime: { } e, Gained: > 0 } && _startPercent is { } p0
                ? new XpSessionRecord(s, e, _session.Gained, _session.Gains, _session.Levels, p0, _session.Percent ?? p0)
                : null;
    }

    void SaveCurrent(XpSessionRecord? rec)
    {
        try { _ctx?.Storage.Save("current", new History { Sessions = rec == null ? new() : [rec] }); }
        catch (IOException e) { _ctx?.Log("couldn't save the running session: " + e.Message); }
    }

    void SaveHistory()
    {
        try { _ctx?.Storage.Save("history", new History { Sessions = _history }); }
        catch (IOException e) { _ctx?.Log("couldn't save the session history: " + e.Message); }
    }

    /// <summary>Saves the running session to the history (if it gained anything) and starts a fresh one.</summary>
    void EndSession()
    {
        var rec = Current();
        lock (_lock)
        {
            _session = new XpSession();
            _startPercent = null;
            _sessionNo++;
            _sessionRewards = 0;
            _sessionRewardExp = 0;
        }
        if (rec == null || _ctx == null) return;
        _history.Insert(0, rec);
        if (_history.Count > HistoryKept) _history.RemoveRange(HistoryKept, _history.Count - HistoryKept);
        SaveHistory();
        SaveCurrent(null);
    }

    public sealed class History { public List<XpSessionRecord> Sessions { get; set; } = new(); }

    // ---- panel ----

    public Control? CreatePanel()
    {
        var status = Muted("Waiting for the game…");
        var rate = Big("–");
        var facts = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*"), RowSpacing = 6 };
        TextBlock Fact(string label)
        {
            int row = facts.RowDefinitions.Count;
            facts.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var l = Muted(label); Grid.SetRow(l, row);
            var v = Text("–"); Grid.SetRow(v, row); Grid.SetColumn(v, 1);
            facts.Children.Add(l); facts.Children.Add(v);
            return v;
        }
        var gained = Fact("Gained this session");
        var kills = Fact("Kills (EXP rises)");
        var rewards = Fact("PQ / quest rewards");
        var level = Fact("Level progress");
        var toLevel = Fact("To next level");
        var time = Fact("Session time");
        var hpmp = Fact("HP / MP");
        var history = Stack(4);

        void Refresh()
        {
            XpSession s; StatusBarReading? last; DateTimeOffset? seen;
            int nRewards; long rewardExp;
            lock (_lock) { s = _session; last = _last; seen = _lastSeen; nRewards = _sessionRewards; rewardExp = _sessionRewardExp; }
            bool live = seen is { } t && DateTimeOffset.Now - t < TimeSpan.FromSeconds(3);
            status.Text = seen == null ? "Looking for the status bar at the bottom of the game…"
                : live ? "Reading EXP from the status bar."
                : $"The status bar hasn't been readable for {(DateTimeOffset.Now - seen.Value).TotalSeconds:0} s (covered, or the game isn't showing it).";
            rate.Text = s.PerHour is { } ph ? $"{ph:N0} EXP/h" : "– EXP/h";
            gained.Text = s.Started == null ? "–" : $"{s.Gained:N0} EXP{(s.Levels > 0 ? $" · {s.Levels} level{(s.Levels == 1 ? "" : "s")}" : "")}";
            int nKills = Math.Max(0, s.Gains - nRewards);
            kills.Text = nKills == 0 ? "–" : $"{nKills:N0}{(s.Elapsed.TotalMinutes >= 1 ? $" · {nKills / s.Elapsed.TotalHours:N0}/h" : "")} · last +{s.LastGain:N0}, average +{(double)(s.Gained - rewardExp) / nKills:0.#}";
            rewards.Text = nRewards == 0 ? "–" : $"{rewardExp:N0} EXP from {nRewards:N0} (stage clears, completions, quests)";
            level.Text = s.Exp is { } e && s.Percent is { } p ? $"{e:N0} EXP · {p:0.00}%" : "–";
            toLevel.Text = s.ToLevel is { } left ? $"{left:N0} EXP{(s.TimeToLevel is { } ttl ? $" · about {Duration(ttl)} at this rate" : "")}" : "–";
            time.Text = s.Started == null ? "–" : Duration(s.Elapsed);
            hpmp.Text = last is { Hp: { } hp, HpMax: { } hm } ? $"{hp:N0}/{hm:N0}" + (last is { Mp: { } mp, MpMax: { } mm } ? $" · {mp:N0}/{mm:N0}" : "") : "–";
        }

        void ShowHistory()
        {
            history.Children.Clear();
            if (_history.Count == 0) { history.Children.Add(Muted("Sessions with EXP gained are kept here when you start a new one or close MapleLite.")); return; }
            foreach (var h in _history.Take(10))
                history.Children.Add(Text($"{h.Started.LocalDateTime:MMM d HH:mm} · {Duration(h.Ended - h.Started)} · {h.Gained:N0} EXP · {h.PerHour:N0}/h · {h.Gains:N0} kills · {h.StartPercent:0.00}% → {h.EndPercent:0.00}%"));
        }

        var panel = Stack(14,
            Card(Stack(10, Title("This session"), rate, status, facts,
                Wrap(Button("New session", () => { EndSession(); Refresh(); ShowHistory(); }, accent: true)))),
            Card(Stack(8, Title("Past sessions"), history,
                Wrap(Button("Export CSV…", () => ExportCsv(history))))),
            BrowseCard());

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Refresh();
        panel.AttachedToVisualTree += (_, _) => { Refresh(); ShowHistory(); timer.Start(); };
        panel.DetachedFromVisualTree += (_, _) => timer.Stop();
        return panel;
    }

    // ---- browsing every gain by map and time ----

    const string AllMaps = "All maps";
    static readonly string[] Ranges = ["Today", "Yesterday", "Last 7 days", "Last 30 days", "All time", "Custom"];
    static readonly (string Name, XpGrouping By)[] Groupings =
        [("By map", XpGrouping.Map), ("By day", XpGrouping.Day), ("By hour", XpGrouping.Hour), ("By day and map", XpGrouping.MapAndDay)];
    static readonly (string Name, XpKind Kind)[] Kinds =
        [("Kills and rewards", XpKind.All), ("Kills only", XpKind.Kills), ("PQ / quest rewards only", XpKind.Rewards)];

    Control BrowseCard()
    {
        var map = new ComboBox { MinWidth = 260, ItemsSource = new[] { AllMaps }, SelectedIndex = 0 };
        var range = new ComboBox { Width = 150, ItemsSource = Ranges, SelectedIndex = 2 };
        var from = new CalendarDatePicker { Width = 140, Watermark = "Any day" };
        var to = new CalendarDatePicker { Width = 140, Watermark = "Any day" };
        var group = new ComboBox { Width = 170, ItemsSource = Groupings.Select(g => g.Name).ToArray(), SelectedIndex = 0 };
        var kind = new ComboBox { Width = 190, ItemsSource = Kinds.Select(k => k.Name).ToArray(), SelectedIndex = 0 };
        var table = new Grid { ColumnDefinitions = new ColumnDefinitions("*,90,110,100,70,70,80,110"), RowSpacing = 4, ColumnSpacing = 10 };
        var note = Muted("");
        List<XpSummary> shown = new();
        bool filling = false;

        (DateTime? From, DateTime? To) RangeDates(int index)
        {
            var today = DateTime.Today;
            return index switch
            {
                0 => (today, today),
                1 => (today.AddDays(-1), today.AddDays(-1)),
                2 => (today.AddDays(-6), today),
                3 => (today.AddDays(-29), today),
                4 => ((DateTime?)null, (DateTime?)null),
                _ => (from.SelectedDate, to.SelectedDate),
            };
        }

        void SetRange()
        {
            var (f, t) = RangeDates(range.SelectedIndex);
            filling = true;
            from.SelectedDate = f; to.SelectedDate = t;
            filling = false;
        }

        async void Show()
        {
            if (_log is not { } log) return;
            string? pick = map.SelectedItem as string is { } m && m != AllMaps ? m : null;
            DateTime? f = from.SelectedDate, t = to.SelectedDate;
            var by = Groupings[Math.Max(0, group.SelectedIndex)].By;
            var only = Kinds[Math.Max(0, kind.SelectedIndex)].Kind;
            // reading every gain can take a moment once there are months of them: off the UI thread
            var (rows, total, maps, count) = await Task.Run(() =>
            {
                var all = log.All();
                var inRange = XpBrowse.Filter(all, null, f, t);
                var maps = XpBrowse.Group(inRange, XpGrouping.Map).Select(r => r.Label).ToList();
                var picked = XpBrowse.OfKind(pick == null ? inRange : XpBrowse.Filter(inRange, pick, null, null), only);
                return (XpBrowse.Group(picked, by), XpBrowse.Total(picked), maps, all.Count);
            });
            filling = true;
            var keep = map.SelectedItem as string;
            map.ItemsSource = new[] { AllMaps }.Concat(maps.Where(x => x != "Unknown map")).ToArray();
            map.SelectedItem = keep != null && (keep == AllMaps || maps.Contains(keep)) ? keep : AllMaps;
            filling = false;
            shown = rows;
            FillTable(table, rows, total);
            note.Text = count == 0
                ? "Every EXP gain is kept with the map you were on (read from the minimap). Hunt for a bit and it fills in here."
                : $"{total.Kills:N0} gains in this view, {count:N0} kept in all. Hunting time counts the gaps between gains up to {XpBrowse.BreakAfter.TotalMinutes:0} minutes, so breaks don't lower EXP/h."
                  + (pick == null && rows.Any(r => r.Label == "Unknown map") ? " \"Unknown map\" is EXP from times the minimap's name wasn't showing." : "");
        }

        map.SelectionChanged += (_, _) => { if (!filling) Show(); };
        group.SelectionChanged += (_, _) => { if (!filling) Show(); };
        kind.SelectionChanged += (_, _) => { if (!filling) Show(); };
        range.SelectionChanged += (_, _) => { if (filling) return; SetRange(); Show(); };
        void DatePicked()
        {
            if (filling) return;
            // the pickers also report a change when they first show their dates: only a real change makes it "Custom"
            if (range.SelectedIndex != Ranges.Length - 1 && RangeDates(range.SelectedIndex) is var (f0, t0) && f0?.Date == from.SelectedDate?.Date && t0?.Date == to.SelectedDate?.Date) return;
            filling = true; range.SelectedIndex = Ranges.Length - 1; filling = false;
            Show();
        }
        from.SelectedDateChanged += (_, _) => DatePicked();
        to.SelectedDateChanged += (_, _) => DatePicked();
        SetRange();

        var card = Card(Stack(10, Title("Browse EXP"),
            Muted("Every EXP gain, by the map you were on and when. Pick a map and a time span, and how to group them."),
            Wrap(Stack(4, Muted("Map"), map), Stack(4, Muted("When"), range), Stack(4, Muted("From"), from), Stack(4, Muted("To"), to), Stack(4, Muted("Group"), group), Stack(4, Muted("EXP from"), kind)),
            table, note,
            Wrap(Button("Refresh", Show), Button("Export these rows…", () => ExportRows(table, shown)))));
        card.AttachedToVisualTree += (_, _) => Show();
        return card;
    }

    static void FillTable(Grid table, List<XpSummary> rows, XpSummary total)
    {
        table.Children.Clear();
        table.RowDefinitions.Clear();
        void Row(bool head, bool bold, params string[] cells)
        {
            int r = table.RowDefinitions.Count;
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < cells.Length; c++)
            {
                var tb = head ? Muted(cells[c]) : Text(cells[c], weight: bold ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal);
                tb.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
                if (c > 0) tb.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
                Grid.SetRow(tb, r); Grid.SetColumn(tb, c);
                table.Children.Add(tb);
            }
        }
        string Rate(double? v) => v is { } x ? x.ToString("N0") : "–";
        string Rewards(XpSummary x) => x.Rewards == 0 ? "–" : $"{x.RewardExp:N0} ({x.Rewards})";
        Row(true, false, "", "Hunting", "EXP", "EXP/h", "Kills", "Kills/h", "EXP/kill", "Rewards");
        if (rows.Count == 0) { Row(false, false, "Nothing in this view.", "", "", "", "", "", "", ""); return; }
        foreach (var x in rows.Take(200))
            Row(false, false, x.Label, Duration(x.Hunting), x.Exp.ToString("N0"), Rate(x.PerHour), x.Kills.ToString("N0"), Rate(x.KillsPerHour), x.AveragePerKill.ToString("N1"), Rewards(x));
        if (rows.Count > 1)
            Row(false, true, rows.Count > 200 ? $"Total ({rows.Count} rows, first 200 shown)" : "Total", Duration(total.Hunting), total.Exp.ToString("N0"),
                Rate(total.PerHour), total.Kills.ToString("N0"), Rate(total.KillsPerHour), total.AveragePerKill.ToString("N1"), Rewards(total));
    }

    async void ExportRows(Control anchor, List<XpSummary> rows)
    {
        if (TopLevel.GetTopLevel(anchor) is not { } top || rows.Count == 0) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export EXP", SuggestedFileName = $"exp-{DateTime.Now:yyyyMMdd}.csv", DefaultExtension = "csv",
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        static string Q(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        var sb = new StringBuilder("group,map,first,last,hunting_minutes,exp,exp_per_hour,kills,kills_per_hour,exp_per_kill,rewards,reward_exp\n");
        foreach (var x in rows)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{Q(x.Label)},{Q(x.Map)},{x.First.LocalDateTime:yyyy-MM-dd HH:mm:ss},{x.Last.LocalDateTime:yyyy-MM-dd HH:mm:ss},{x.Hunting.TotalMinutes:0.0},{x.Exp},{x.PerHour ?? 0:0},{x.Kills},{x.KillsPerHour ?? 0:0},{x.AveragePerKill:0.0},{x.Rewards},{x.RewardExp}"));
        try { await File.WriteAllTextAsync(path, sb.ToString()); _ctx?.Notify("Exported " + Path.GetFileName(path)); }
        catch (IOException e) { _ctx?.Notify("Couldn't export: " + e.Message); }
    }

    async void ExportCsv(Control anchor)
    {
        if (TopLevel.GetTopLevel(anchor) is not { } top || _history.Count == 0) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Export XP sessions", SuggestedFileName = $"xp-sessions-{DateTime.Now:yyyyMMdd}.csv", DefaultExtension = "csv",
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        var sb = new StringBuilder("started,ended,minutes,exp_gained,exp_per_hour,kills,levels,start_percent,end_percent\n");
        foreach (var h in _history)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{h.Started:yyyy-MM-dd HH:mm:ss},{h.Ended:yyyy-MM-dd HH:mm:ss},{(h.Ended - h.Started).TotalMinutes:0.0},{h.Gained},{h.PerHour:0},{h.Gains},{h.Levels},{h.StartPercent:0.00},{h.EndPercent:0.00}"));
        try { await File.WriteAllTextAsync(path, sb.ToString()); _ctx?.Notify("Exported " + Path.GetFileName(path)); }
        catch (IOException e) { _ctx?.Notify("Couldn't export: " + e.Message); }
    }

    static string Duration(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds:00}s" : $"{t.Seconds}s";
}
