using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using MapleLite.Core.Capture;
using MapleLite.Core.Game;
using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;

namespace MapleLite.Plugins.ChatHistory;

/// <summary>
/// The MapleMemory app's CapturePipeline on MapleLite's shared services. Same design:
/// frames never wait for OCR; a bounded queue holds small chat crops in observation order; one worker reads
/// them; trade dialogs (about every 3 s) are read separately from a latest-only snapshot. The differences:
/// frames come from the host's capture loop and text from the host's OCR service, instead of the MapleMemory
/// app capturing and running Windows OCR itself; and pickup notices are now the Loot plugin's job.
/// </summary>
internal sealed class ChatPipeline
{
    public const double Fps = 20;

    readonly IFrameSource _source;
    readonly ChatReader _reader;
    readonly Region _chat;
    readonly int _frameW, _frameH;
    readonly Channel<Snapshot> _frames;
    readonly CancellationTokenSource _stop = new();
    readonly CancellationTokenSource _stopDialog = new();
    readonly ConcurrentQueue<Entry> _entries = new();
    readonly ScrollTracker _tracker = new();
    readonly MessageSettler _settler = new();
    readonly Stopwatch _uptime = Stopwatch.StartNew();
    IDisposable? _subscription;
    Task? _completion;
    Snapshot? _dialog;
    Frame? _previous;
    DateTimeOffset _lastEnqueued = DateTimeOffset.MinValue, _nextDialog = DateTimeOffset.MinValue;
    long _captured, _processed, _dropped, _queued, _gaps, _lastCaptureTicks, _lastProcessTicks, _ocrTicks;
    volatile string? _captureError, _ocrError, _dialogError;

    sealed record Snapshot(Frame Frame, DateTimeOffset Time);

    public ChatPipeline(IFrameSource source, ChatReader reader, Region chat, int frameW, int frameH)
    {
        _source = source; _reader = reader; _chat = chat; _frameW = frameW; _frameH = frameH;
        // Bound crop storage to about 32 MiB, or 120 crops, whichever is smaller.
        int capacity = Math.Clamp(32 * 1024 * 1024 / Math.Max(1, chat.Width * chat.Height * 4), 2, 120);
        _frames = Channel.CreateBounded<Snapshot>(new BoundedChannelOptions(capacity) { SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    }

    public long Captured => Interlocked.Read(ref _captured);
    public long Processed => Interlocked.Read(ref _processed);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long Queued => Math.Max(0, Interlocked.Read(ref _queued));
    public long Gaps => Interlocked.Read(ref _gaps);
    public double Rate => Captured / Math.Max(.1, _uptime.Elapsed.TotalSeconds);
    public double OcrMs => TimeSpan.FromTicks(Interlocked.Read(ref _ocrTicks)).TotalMilliseconds;
    public string? Error => _captureError ?? _ocrError ?? _dialogError;
    /// <summary>The game's picture size changed, so the saved chat area no longer fits.</summary>
    public bool AreaInvalid { get; private set; }
    public bool TryTake(out Entry? e) => _entries.TryDequeue(out e);

    public void Start()
    {
        if (_completion != null) throw new InvalidOperationException("Already running.");
        _completion = Task.WhenAll(Task.Run(ReadLoop), Task.Run(DialogLoop));
        _subscription = _source.Subscribe(OnFrame, Fps);
    }

    public async Task StopAsync()
    {
        _subscription?.Dispose();
        _frames.Writer.TryComplete();
        _stopDialog.Cancel();
        // Give the remaining chat crops a short chance to finish without extending shutdown for a full OCR queue.
        _stop.CancelAfter(TimeSpan.FromSeconds(1));
        if (_completion != null) await _completion.ConfigureAwait(false);
        _uptime.Stop();
        _stop.Dispose();
        _stopDialog.Dispose();
    }

    // Runs on the host's capture thread: only cheap work here (crop, compare, enqueue).
    void OnFrame(GameFrame gf)
    {
        if (_stop.IsCancellationRequested) return;
        try
        {
            var f = gf.Frame;
            if (f.W != _frameW || f.H != _frameH)
            {
                AreaInvalid = true;
                throw new InvalidOperationException("The game's picture size changed. Finding the chat area again…");
            }
            var now = gf.Time;
            var crop = f.Crop(_chat);
            var signature = OcrPreprocessor.ChatGlyphs.Apply(crop);
            Interlocked.Increment(ref _captured);
            Interlocked.Exchange(ref _lastCaptureTicks, now.UtcTicks);
            if (_previous == null || !signature.Px.AsSpan().SequenceEqual(_previous.Px) || now - _lastEnqueued > TimeSpan.FromMilliseconds(350))
            {
                // Never silently drop a queued frame or overwrite a newer capture with an old one.
                Interlocked.Increment(ref _queued);
                if (!_frames.Writer.TryWrite(new(crop, now))) { Interlocked.Decrement(ref _queued); Interlocked.Increment(ref _dropped); }
                else { _previous = signature; _lastEnqueued = now; }
            }
            // Trade request popups appear in the lower-right part of the game window, not only the center.
            if (now >= _nextDialog) { Interlocked.Exchange(ref _dialog, new Snapshot(f.Crop(new Region(f.W / 3, f.H / 3, f.W - f.W / 3, f.H - f.H / 3)), now)); _nextDialog = now.AddSeconds(3); }
            _captureError = null;
        }
        catch (Exception e) { _captureError = e.Message; }
    }

    async Task ReadLoop()
    {
        try
        {
            await foreach (var snapshot in _frames.Reader.ReadAllAsync(_stop.Token))
            {
                _stop.Token.ThrowIfCancellationRequested();
                Interlocked.Decrement(ref _queued);
                try
                {
                    var sw = Stopwatch.StartNew();
                    var rows = await _reader.ReadChatAsync(snapshot.Frame, _stop.Token).ConfigureAwait(false);
                    var blocks = ChatLayout.Assemble(rows, snapshot.Frame.W, snapshot.Frame.H);
                    var added = _tracker.Read(blocks.Select(b => b.Text).ToList());
                    if (_tracker.LastHadGap) Interlocked.Increment(ref _gaps);
                    var classified = added.Select(line =>
                    {
                        var block = blocks.First(b => b.Text == line);
                        var e = Classifier.Parse(line);
                        if ((e.Channel == "General" || e.Channel == "Unknown") && block.Hint != null) e = e with { Channel = block.Hint };
                        return e with { Time = snapshot.Time, Source = block.Partial ? "OCR · partial" : "OCR · assembled" };
                    });
                    foreach (var entry in _settler.Push(classified, snapshot.Time)) _entries.Enqueue(entry);
                    Interlocked.Increment(ref _processed);
                    Interlocked.Exchange(ref _lastProcessTicks, snapshot.Time.UtcTicks);
                    Interlocked.Exchange(ref _ocrTicks, sw.Elapsed.Ticks);
                    _ocrError = null;
                }
                catch (Exception e) when (e is not OperationCanceledException || !_stop.IsCancellationRequested)
                { _ocrError = "Chat OCR: " + e.Message; Interlocked.Increment(ref _dropped); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            foreach (var entry in _settler.Flush()) _entries.Enqueue(entry);
        }
    }

    async Task DialogLoop()
    {
        string last = "";
        var ct = _stopDialog.Token;
        while (!ct.IsCancellationRequested)
        {
            var snapshot = Interlocked.Exchange(ref _dialog, null);
            if (snapshot != null)
                try
                {
                    var rows = await _reader.ReadPlainAsync(snapshot.Frame, snapshot.Frame.Bounds, hints: false, ct).ConfigureAwait(false);
                    var text = string.Join(" ", rows.Select(r => r.Text));
                    var entry = Classifier.Parse(text);
                    if (entry.Channel == "Trade" && text != last) { _entries.Enqueue(entry with { Time = snapshot.Time, Source = "OCR · dialog" }); last = text; }
                    else if (entry.Channel != "Trade") last = "";
                    _dialogError = null;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception e) { _dialogError = "Trade OCR: " + e.Message; }
            try { await Task.Delay(100, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }
}
