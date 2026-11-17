using MapleLite.Core.Capture;
using MapleLite.Core.Game;
using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;
using MapleLite.Sdk;

namespace MapleLite.Plugins.Loot;

/// <summary>
/// Reads the game's pickup notices ("You have gained mesos (+5800)") from the lower-right of the game picture
/// with the shared OCR. Moved here from the MapleMemory app's loot pass, with the same area, cleanup and
/// de-duplication. When Chat History has published where the chat box is, that area is blanked out so a pickup
/// the game also prints in chat isn't read twice (the chat copy arrives as a ChatMessageEvent instead).
/// </summary>
internal sealed class LootReader
{
    public const double Fps = 1 / 0.6; // notices fade within a few seconds; sample about every 0.6 s

    readonly IFrameSource _frames;
    readonly IOcr _ocr;
    readonly IEventBus _events;
    readonly Action<DateTimeOffset, LootDrop, string> _onPickup;
    readonly CancellationTokenSource _stop = new();
    IDisposable? _sub;
    Task? _loop;
    Snapshot? _latest;
    long _reads;

    sealed record Snapshot(Frame Frame, DateTimeOffset Time);

    public LootReader(IFrameSource frames, IOcr ocr, IEventBus events, Action<DateTimeOffset, LootDrop, string> onPickup)
    {
        _frames = frames; _ocr = ocr; _events = events; _onPickup = onPickup;
    }

    public long Reads => Interlocked.Read(ref _reads);
    public string? Error { get; private set; }

    public void Start()
    {
        _loop = Task.Run(Loop);
        _sub = _frames.Subscribe(gf => Interlocked.Exchange(ref _latest, new Snapshot(Crop(gf.Frame, ChatArea(gf.Frame)), gf.Time)), Fps);
    }

    public async Task StopAsync()
    {
        _sub?.Dispose();
        _stop.Cancel();
        if (_loop != null) await _loop.ConfigureAwait(false);
    }

    Region? ChatArea(Frame f) =>
        _events.Last<ChatAreaEvent>() is { } a && a.FrameWidth == f.W && a.FrameHeight == f.H ? a.Area : null;

    /// <summary>The notice area: right 55% of the picture, from 45% down to 93% down, with the chat box blanked.</summary>
    internal static Frame Crop(Frame f, Region? chat)
    {
        var area = new Region(f.W * 45 / 100, f.H * 45 / 100, f.W - f.W * 45 / 100, f.H * 93 / 100 - f.H * 45 / 100);
        var crop = f.Crop(area);
        if (chat is { } c)
        {
            int x0 = Math.Max(c.X, area.X) - area.X, x1 = Math.Min(c.Right, area.Right) - area.X;
            int y0 = Math.Max(c.Y, area.Y) - area.Y, y1 = Math.Min(c.Bottom, area.Bottom) - area.Y;
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) crop.Px[y * crop.W + x] = Pixel.Black;
        }
        return crop;
    }

    async Task Loop()
    {
        var seen = new ScrollTracker();
        int empty = 0;
        while (!_stop.IsCancellationRequested)
        {
            var snapshot = Interlocked.Exchange(ref _latest, null);
            if (snapshot != null)
                try
                {
                    var result = await _ocr.ReadAsync(snapshot.Frame, new OcrRequest(snapshot.Frame.Bounds)
                    {
                        Preprocess = OcrPreprocessor.ChatGlyphs, // notices are bright text over the scene
                        Grouping = OcrGrouping.Rows,
                        Scaling = OcrScaling.Bicubic,
                        Cache = false,
                    }, _stop.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref _reads);
                    // Only lines that parse as a pickup count; scenery, names and damage numbers are ignored.
                    var lines = result.Lines.Select(l => l.Text).Where(t => GameText.Pickup(t) != null).ToList();
                    if (lines.Count == 0) { if (++empty >= 2) seen.Reset(); }
                    else
                    {
                        empty = 0;
                        foreach (var line in seen.Read(lines))
                            if (GameText.Pickup(line) is { } drop) _onPickup(snapshot.Time, drop, line);
                    }
                    Error = null;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception e) { Error = "Pickup OCR: " + e.Message; }
            try { await Task.Delay(100, _stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }
}
