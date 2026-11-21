using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MapleLite.Core.Capture;
using MapleLite.Core.Game;
using MapleLite.Sdk;
using static MapleLite.Sdk.PanelKit;

namespace MapleLite.Plugins.Loot;

/// <summary>
/// Loot tracker: totals of mesos and items picked up, per session, day, week or all time.
/// Pickups come from two places: the game's pickup notices (read here) and pickup lines in chat
/// (published by Chat History as ChatMessageEvents, when it's on). Moved out of Chat History.
/// </summary>
public sealed class LootPlugin : IPlugin, ICompanionPanel
{
    IPluginContext? _ctx;
    LootLog? _log;
    LootReader? _reader;
    MesoWatcher? _mesos;
    IMapSource? _map;
    ItemNames _names = new([]);
    IDisposable? _chat;
    readonly DateTimeOffset _sessionStart = DateTimeOffset.Now;
    string _note = "";

    public PluginManifest Manifest { get; } = new("loot", "Loot", "0.1.0-alpha", "CheesyFrog Technologies",
        "Totals of the mesos and items you pick up, by session, day, week or all time.");

    /// <summary>Raised on the UI thread after a pickup is saved.</summary>
    internal event Action? Changed;

    public Task StartAsync(IPluginContext context)
    {
        _ctx = context;
        _log = new LootLog(context.Storage.Folder);
        var settings = context.Storage.Load<LootSettings>("settings");
        if (!settings.ImportChecked)
        {
            try
            {
                int n = _log.ImportJournals(LootLog.AppFolder);
                if (n > 0) _note = $"Imported {n:N0} pickups from the MapleMemory app.";
            }
            catch (IOException e) { context.Log("import failed: " + e.Message); }
            settings.ImportChecked = true;
            context.Storage.Save("settings", settings);
        }
        _names = new ItemNames(context.Items);
        _reader = new LootReader(context.Frames, context.Ocr, context.Events, (t, d, text) =>
            {
                if (d.Kind == "Mesos") _mesos?.Reconciler.Noticed(d.Quantity);
                Save(t, d, text, "OCR · notice");
            },
            _names);
        // The meso total in the inventory, while it's open, catches meso pickups past the six notices the game shows.
        var reader = _reader;
        _mesos = new MesoWatcher(context.Frames, context.Ocr,
            (t, amount, text) => Save(t, new LootDrop("Mesos", "Mesos", amount), text, "OCR · meso total"), reader.Note);
        // The map name on the minimap, so each pickup is filed under the map it happened on.
        _map = context.Map; // shared with other plugins: MapleLite reads the minimap once
        _reader.Start();
        _mesos.Start();
        // Pickup lines printed in chat, as Chat History saves them.
        _chat = context.Events.Subscribe<ChatMessageEvent>(e =>
        {
            if (e.Channel == "Loot" && GameText.Pickup(e.Text) is { } d) Save(e.Time, d, e.Text, "chat");
        });
        return Task.CompletedTask;
    }

    void Save(DateTimeOffset time, LootDrop drop, string text, string source)
    {
        var map = _map?.Map; // where you are now, not when the UI thread gets to it
        _ctx?.OnUi(() => Store(LootRecord.From(time, drop, text, source, map)));
    }

    void Store(LootRecord record)
    {
        if (_log == null || _ctx == null) return;
        try { _log.Add(record); Changed?.Invoke(); }
        catch (IOException e) { _ctx.Log("couldn't save a pickup: " + e.Message); }
    }

    public async Task StopAsync()
    {
        _chat?.Dispose();
        if (_reader != null) await _reader.StopAsync();
        if (_mesos != null) await _mesos.StopAsync();
    }

    public Control CreateCompanionPanel() => CompanionPanel.Text(() =>
        "Recent pickups · newest first\n\n" + (_log == null ? "Waiting for loot…" :
        string.Join("\n", _log.Records.TakeLast(50).Reverse().Select(r => $"{r.Time.LocalDateTime:HH:mm:ss}  {r.Name} ×{r.Quantity:N0}"))));

    public Control? CreatePanel()
    {
        if (_ctx == null || _log == null || _reader == null) return null;
        var view = new LootView(_log, _names, _sessionStart);
        var status = Muted("");
        var ctx = _ctx; var reader = _reader; var log = _log; var mesos = _mesos; var map = _map;

        void Refresh()
        {
            var game = ctx.Frames.Status;
            status.Text = (_note.Length > 0 ? _note + "  " : "")
                + (!ctx.Ocr.Available ? "This system has no text reader, so pickup notices can't be read."
                : game.State != CaptureState.Capturing ? "Waiting for the game · " + game.Message
                : reader.Error ?? mesos?.Error ?? $"● Watching pickup notices · {reader.Reads:N0} reads"
                  + (map?.Map is { } here ? $" · on {here}" : map?.Problem is { } why ? $" · can't tell the map: {why}" : "")
                  + (mesos?.Box != null && mesos.Reconciler.Total is { } total ? $" · meso total {total:N0}" : " · open your inventory to also check the meso total")
                  + (ctx.Events.Last<ChatAreaEvent>() == null ? " · turn on Chat History to also count pickups printed in chat" : " · also counting pickups in chat"));
            view.Refresh();
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Refresh();
        Changed += Refresh;
        reader.RecordingSaved += folder => ctx.OnUi(() => { _note = "Recording saved to " + folder; Refresh(); OpenFolder(folder); });
        var actions = Wrap(
            Button("Export CSV…", async () => await Export(view)),
            Button("Open loot log folder", () => OpenFolder(log.Folder)),
            Button("Record 20 s for diagnosis (Developer)", () =>
            {
                if (reader.IsRecording) return;
                reader.StartRecording(log.Folder, 20);
                _note = "● Recording what the reader sees for 20 seconds: pick things up now.";
                Refresh();
            }));
        var panel = Stack(12, actions, Card(status, 12), view);
        panel.AttachedToVisualTree += (_, _) => { timer.Start(); Refresh(); };
        panel.DetachedFromVisualTree += (_, _) => timer.Stop();
        return panel;
    }

    async Task Export(Control anchor)
    {
        if (_log == null || TopLevel.GetTopLevel(anchor) is not { } top) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export pickups", SuggestedFileName = $"Loot-{DateTime.Now:yyyyMMdd-HHmm}.csv", DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV file") { Patterns = ["*.csv"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        static string Safe(string s) => s.Length > 0 && "=+-@\t\r".Contains(s[0]) ? "'" + s : s;
        var lines = new List<string> { "Time,Kind,Name,Quantity,Source,RawOCR" };
        lines.AddRange(_log.Records.Select(r => string.Join(",", new[] { r.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"), r.Kind, r.Name, r.Quantity.ToString(), r.Source, r.Text }.Select(s => Q(Safe(s))))));
        File.WriteAllLines(path, lines, new UTF8Encoding(true));
        _ctx?.Notify("Exported pickups to " + Path.GetFileName(path));
    }
}
