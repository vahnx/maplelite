using System.Diagnostics;
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
    Task? _pause;
    bool _closed, _detecting;

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
        Journal = new Journal(ctx.Storage.Folder);
        ClockZone.Zone = ClockZone.Find(Settings.TimeZoneId);
        MessageParser.StripTrailingAt = Settings.StripTrailingAt;
        foreach (var e in Journal.Entries.Where(e => e.Time >= DateTimeOffset.Now - SpamFilter.Window)) { _spam.Remember(e); _partials.Remember(e); }
        PublishArea();
        _tick.Tick += (_, _) => Tick();
        _retry.Tick += async (_, _) => { if (!_detecting) await AutoStartAsync(); };
        Status = Settings.Chat == null ? "Looking for the chat box. Keep chat scrolled to the newest messages." : "Ready. Your saved chat area will be used.";
        if (Journal.UnreadableLines > 0) Status += $" {Journal.UnreadableLines} damaged log lines were skipped (files preserved).";
    }

    public void SaveSettings() => _ctx.Storage.Save("settings", Settings);

    /// <summary>Tells other plugins (Loot) where the chat box is, so they can leave it out of their reads.</summary>
    public void PublishArea()
    {
        if (Settings.Chat is { } c) _ctx.Events.Publish(new ChatAreaEvent(c.ToRegion(), Settings.FrameWidth, Settings.FrameHeight, _ctx.Manifest.Id));
    }

    void SetStatus(string s) { Status = s; Changed?.Invoke(); }

    /// <summary>On plugin start: find the chat area (unless one was chosen by hand) and start logging, retrying every 5 s.</summary>
    public async Task AutoStartAsync()
    {
        if (_closed || Running || _pause is { IsCompleted: false }) return;
        bool found = Settings.ManualChat || await DetectChatAsync();
        if (_closed) return;
        if (!found) { _retry.Start(); return; }
        if (Settings.Chat == null) { SetStatus("Select the chat area first."); _retry.Start(); return; }
        _retry.Stop();
        Start();
    }

    public async Task ToggleAsync()
    {
        if (_closed || _pause is { IsCompleted: false }) return;
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
        _pause = null;
        _pipeline = new ChatPipeline(_ctx.Frames, _reader, Settings.Chat!.ToRegion(), Settings.FrameWidth, Settings.FrameHeight);
        _pipeline.Start();
        Running = true;
        _tick.Start();
        SetStatus("Starting chat capture…");
    }

    public Task StopAsync() => _pause ??= PauseAsync();

    public async Task CloseAsync()
    {
        _closed = true;
        _retry.Stop();
        await StopAsync();
        Flush(); // Retry any remaining entries if the pause encountered a write error.
    }

    async Task PauseAsync()
    {
        if (_pipeline is not { } pipeline) return;
        Running = false;
        _tick.Stop();
        SetStatus("Finishing buffered chat…");
        var timer = Stopwatch.StartNew();
        await pipeline.StopAsync();
        var ocrTime = timer.ElapsedMilliseconds;
        timer.Restart();
        try
        {
            Flush();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus("Unable to save chat (kept in memory): " + e.Message);
            return;
        }
        finally
        {
            _pipeline = null;
            _ctx.Log($"Chat stop: OCR {ocrTime} ms, final journal flush {timer.ElapsedMilliseconds} ms, {pipeline.Queued} frames left in the OCR queue, {_pending.Count} messages pending.");
        }
        SetStatus($"Paused. Recognized chat is saved. {SpamSkipped:N0} repeats skipped this session.");
    }

    /// <summary>Finds the chat box in the latest game picture. Stops logging first if it was running.</summary>
    public async Task<bool> DetectChatAsync()
    {
        if (_closed || _detecting) return false;
        _detecting = true;
        try
        {
            if (Running || _pause is { IsCompleted: false }) await StopAsync();
            if (_closed) return false;
            var latest = _ctx.Frames.Latest;
            if (latest == null || _ctx.Frames.Status.State != CaptureState.Capturing)
            {
                SetStatus("Waiting for the game: " + _ctx.Frames.Status.Message);
                return false;
            }
            if (!_ctx.Ocr.Available) { SetStatus("This system has no text reader, so chat can't be read."); return false; }
            var region = await ChatDetector.DetectAsync(_reader, latest.Frame);
            if (_closed) return false;
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
        if (_closed) return;
        Settings.Chat = ChatArea.From(area);
        Settings.FrameWidth = frame.W; Settings.FrameHeight = frame.H;
        Settings.ManualChat = manual;
        SaveSettings();
        PublishArea();
    }

    void Flush()
    {
        if (_pipeline != null) while (_pipeline.TryTake(out var e)) _pending.Enqueue(e!);
        while (_pending.TryPeek(out var e))
        {
            if (e.Channel != "Loot" && Settings.SuppressSpam && (_spam.ShouldSuppress(e) || _partials.ShouldSkip(e))) { _pending.Dequeue(); continue; }
            Journal.Add(e);   // throws on a disk error: the entry stays pending and is retried next tick
            _spam.Remember(e); _partials.Remember(e);
            _ctx.Events.Publish(new ChatMessageEvent(e.Time, e.Channel, e.Text, e.Source, _ctx.Manifest.Id));
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
            if (p.AreaInvalid)
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
