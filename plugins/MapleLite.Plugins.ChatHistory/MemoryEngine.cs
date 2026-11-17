using Avalonia.Threading;
using MapleLite.Core.Capture;
using MapleLite.Core.Imaging;
using MapleLite.Sdk;

namespace MapleLite.Plugins.ChatHistory;

/// <summary>
/// The MapleMemory app's main-window logic without the window: logging keeps running while the panel
/// isn't shown. All state changes happen on the UI thread (timers and calls from the panel).
/// </summary>
internal sealed class MemoryEngine
{
    readonly IPluginContext _ctx;
    readonly ChatReader _reader;
    readonly SpamFilter _spam = new();
    readonly PartialRepeatFilter _partials = new();
    readonly Queue<Entry> _pending = new();
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromSeconds(5) };
    ChatPipeline? _pipeline;
    bool _stopping, _detecting;

    public Journal Journal { get; }
    public Settings Settings { get; }
    public bool Running { get; private set; }
    public string Status { get; private set; } = "";
    public DateTimeOffset SessionStart { get; } = DateTimeOffset.Now;
    public long SpamSkipped => _spam.Suppressed + _partials.Suppressed;
    /// <summary>Raised on the UI thread when entries were saved or the status changed.</summary>
    public event Action? Changed;
    public MapleLite.Core.Capture.GameFrame? LatestFrame => _ctx.Frames.Latest;

    public MemoryEngine(IPluginContext ctx)
    {
        _ctx = ctx;
        _reader = new ChatReader(ctx.Ocr);
        Settings = ctx.Storage.Load<Settings>("settings");
        var imported = ImportFromAppOnce();
        Journal = new Journal(ctx.Storage.Folder);
        ClockZone.Zone = ClockZone.Find(Settings.TimeZoneId);
        MessageParser.StripTrailingAt = Settings.StripTrailingAt;
        foreach (var e in Journal.Entries.Where(e => e.Time >= DateTimeOffset.Now - SpamFilter.Window)) { _spam.Remember(e); _partials.Remember(e); }
        _tick.Tick += (_, _) => Tick();
        _retry.Tick += async (_, _) => { if (!Running && !_stopping && !_detecting) await AutoStartAsync(); };
        Status = Settings.Chat == null ? "Looking for the chat box. Keep chat scrolled to the newest messages." : "Ready. Your saved chat area will be used.";
        if (imported.lines > 0) Status = $"Imported {imported.lines:N0} messages from the MapleMemory app. " + Status;
        if (Journal.UnreadableLines > 0) Status += $" {Journal.UnreadableLines} damaged log lines were skipped (files preserved).";
    }

    public void SaveSettings() => _ctx.Storage.Save("settings", Settings);

    (int files, int lines) ImportFromAppOnce()
    {
        if (Settings.ImportChecked) return (0, 0);
        var result = (0, 0);
        try
        {
            result = Journal.Import(Journal.AppFolder, _ctx.Storage.Folder);
            if (Settings.Chat == null && Journal.AppSettings(Journal.AppFolder) is { } app)
            {
                Settings.Chat = app.Chat; Settings.FrameWidth = app.FrameWidth; Settings.FrameHeight = app.FrameHeight;
                Settings.ManualChat = app.ManualChat; Settings.SuppressSpam = app.SuppressSpam; Settings.StripTrailingAt = app.StripTrailingAt;
                Settings.ShowGameTime = app.ShowGameTime; Settings.TimeZoneId = app.TimeZoneId;
            }
        }
        catch (IOException e) { _ctx.Log("import failed: " + e.Message); }
        Settings.ImportChecked = true;
        SaveSettings();
        return result;
    }

    /// <summary>Imports again (only lines not already in the journal), then reloads it.</summary>
    public int ImportAgain()
    {
        var (_, lines) = Journal.Import(Journal.AppFolder, _ctx.Storage.Folder);
        if (lines > 0)
        {
            var fresh = new Journal(_ctx.Storage.Folder);
            Journal.Entries.Clear();
            Journal.Entries.AddRange(fresh.Entries);
        }
        SetStatus(lines > 0 ? $"Imported {lines:N0} more messages from the MapleMemory app." : "Nothing new to import from the MapleMemory app.");
        return lines;
    }

    void SetStatus(string s) { Status = s; Changed?.Invoke(); }

    /// <summary>On plugin start: find the chat area (unless one was chosen by hand) and start logging, retrying every 5 s.</summary>
    public async Task AutoStartAsync()
    {
        if (Running || _stopping) return;
        if (!Settings.ManualChat && !await DetectChatAsync()) { _retry.Start(); return; }
        if (Settings.Chat == null) { SetStatus("Select the chat area first."); _retry.Start(); return; }
        _retry.Stop();
        Start();
    }

    public async Task ToggleAsync()
    {
        if (_stopping) return;
        if (Running) { await StopAsync(); return; }
        if (Settings.Chat == null) { SetStatus("Select the chat area first."); return; }
        Start();
    }

    void Start()
    {
        var latest = _ctx.Frames.Latest;
        if (latest != null && (latest.Frame.W != Settings.FrameWidth || latest.Frame.H != Settings.FrameHeight) && Settings.ManualChat)
        {
            SetStatus("The game's picture size changed since you selected the chat area. Select it again.");
            return;
        }
        _pipeline = new ChatPipeline(_ctx.Frames, _reader, Settings.Chat!.ToRegion(), Settings.FrameWidth, Settings.FrameHeight);
        _pipeline.Start();
        Running = true;
        _tick.Start();
        SetStatus("Starting chat capture…");
    }

    public async Task StopAsync()
    {
        if (_stopping || !Running) return;
        _stopping = true; Running = false;
        SetStatus("Finishing buffered chat…");
        try
        {
            if (_pipeline != null) await _pipeline.StopAsync();
            Flush();
            _tick.Stop();
            SetStatus($"Paused. Buffered chat and history are saved. {SpamSkipped:N0} repeats skipped this session.");
        }
        finally { _stopping = false; Changed?.Invoke(); }
    }

    /// <summary>Finds the chat box in the latest game picture. Stops logging first if it was running.</summary>
    public async Task<bool> DetectChatAsync()
    {
        if (_detecting) return false;
        _detecting = true;
        try
        {
            if (Running) await StopAsync();
            var latest = _ctx.Frames.Latest;
            if (latest == null || _ctx.Frames.Status.State != CaptureState.Capturing)
            {
                SetStatus("Waiting for the game: " + _ctx.Frames.Status.Message);
                return false;
            }
            if (!_ctx.Ocr.Available) { SetStatus("This system has no text reader, so chat can't be read."); return false; }
            var region = await ChatDetector.DetectAsync(_reader, latest.Frame);
            if (region == null)
            {
                SetStatus("Can't see timestamped chat lines yet. Scroll chat to the newest messages, or select the chat area by hand.");
                return false;
            }
            SetArea(region.Value, latest.Frame, manual: false);
            SetStatus("Chat area found automatically.");
            return true;
        }
        catch (Exception e) { SetStatus(e.Message); return false; }
        finally { _detecting = false; }
    }

    public void SetArea(Region area, Frame frame, bool manual)
    {
        Settings.Chat = ChatArea.From(area);
        Settings.FrameWidth = frame.W; Settings.FrameHeight = frame.H;
        Settings.ManualChat = manual;
        SaveSettings();
    }

    void Flush()
    {
        if (_pipeline != null) while (_pipeline.TryTake(out var e)) _pending.Enqueue(e!);
        while (_pending.TryPeek(out var e))
        {
            if (e.Channel != "Loot" && Settings.SuppressSpam && (_spam.ShouldSuppress(e) || _partials.ShouldSkip(e))) { _pending.Dequeue(); continue; }
            Journal.Add(e);   // throws on a disk error: the entry stays pending and is retried next tick
            _spam.Remember(e); _partials.Remember(e);
            _pending.Dequeue();
        }
    }

    async void Tick()
    {
        if (!Running || _pipeline == null) return;
        try
        {
            Flush();
            var p = _pipeline;
            if (p.AreaInvalid && !_stopping)
            {
                await StopAsync();
                if (Settings.ManualChat) SetStatus("The game's picture size changed. Select the chat area again.");
                else await AutoStartAsync();
                return;
            }
            var game = _ctx.Frames.Status;
            string s = game.State != CaptureState.Capturing ? "Waiting for the game · " + game.Message
                : p.Error != null ? "Waiting · " + p.Error
                : $"● Logging · {p.Rate:F1} frames/s · OCR {p.OcrMs:F0} ms · {p.Queued} queued · {p.Dropped} skipped frames · {SpamSkipped:N0} repeats skipped";
            if (p.Gaps > 0) s += $" · {p.Gaps} possible scroll gaps (a taller chat box helps)";
            Status = s;
            Changed?.Invoke();
        }
        catch (Exception e) { SetStatus("Unable to save chat (kept in memory): " + e.Message); }
    }
}
