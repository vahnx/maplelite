using System.Diagnostics;
using MapleLite.Core.Imaging;

namespace MapleLite.Core.Ocr;

/// <summary>
/// Text reading shared by the host and every plugin. Plugins get it as <c>context.Ocr</c>
/// and never create OCR engines of their own.
/// </summary>
public interface IOcr
{
    /// <summary>False when the OS has no usable recognizer; reads then throw <see cref="OcrUnavailableException"/>.</summary>
    bool Available { get; }
    string EngineName { get; }
    OcrStats Stats { get; }

    /// <summary>Reads a region of a frame. Boxes in the result are in frame coordinates.</summary>
    Task<OcrResult> ReadAsync(Frame frame, OcrRequest request, CancellationToken ct = default);
}

public static class OcrExtensions
{
    /// <summary>Reads a region with the default settings.</summary>
    public static Task<OcrResult> ReadAsync(this IOcr ocr, Frame frame, Region region, CancellationToken ct = default) =>
        ocr.ReadAsync(frame, new OcrRequest(region), ct);

    /// <summary>Reads the whole frame with the default settings.</summary>
    public static Task<OcrResult> ReadAllAsync(this IOcr ocr, Frame frame, CancellationToken ct = default) =>
        ocr.ReadAsync(frame, new OcrRequest(frame.Bounds), ct);
}

public sealed record OcrStats(long Reads, long CacheHits, long Failures, double LastMs, double AverageMs);

/// <summary>
/// The one implementation of <see cref="IOcr"/>: crop → cleanup → enlarge → platform engine → map boxes back
/// → optional row grouping. Identical reads of the same frame run once and are shared (the capture loop
/// hands every plugin the same frame object, so two plugins reading the chat box cost one OCR pass).
/// A gate caps how many reads run at once so OCR can't starve capture.
/// </summary>
public sealed class OcrService : IOcr, IDisposable
{
    const int FramesKept = 6;

    readonly IOcrEngine? _engine;
    readonly SemaphoreSlim _gate;
    readonly object _lock = new();
    readonly Dictionary<(long frame, string key), Task<OcrResult>> _cache = new();
    readonly LinkedList<long> _frameOrder = new();
    long _reads, _hits, _failures, _totalTicks, _lastTicks;

    public OcrService(IOcrEngine? engine, int maxConcurrent = 0)
    {
        _engine = engine;
        if (maxConcurrent <= 0) maxConcurrent = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        _gate = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    public bool Available => _engine != null;
    public string EngineName => _engine?.Name ?? "none";

    public OcrStats Stats
    {
        get
        {
            long reads = Interlocked.Read(ref _reads);
            return new(reads, Interlocked.Read(ref _hits), Interlocked.Read(ref _failures),
                TimeSpan.FromTicks(Interlocked.Read(ref _lastTicks)).TotalMilliseconds,
                reads == 0 ? 0 : TimeSpan.FromTicks(Interlocked.Read(ref _totalTicks)).TotalMilliseconds / reads);
        }
    }

    public Task<OcrResult> ReadAsync(Frame frame, OcrRequest request, CancellationToken ct = default)
    {
        if (_engine == null) throw new OcrUnavailableException("No text recognizer is available on this system.");
        var region = request.Region.Intersect(frame.Bounds);
        if (region.IsEmpty || region.Width < 4 || region.Height < 4) return Task.FromResult(OcrResult.Empty(region, EngineName));
        request = request with { Region = region };
        if (!request.Cache) return RunAsync(frame, request, ct);

        Task<OcrResult> task;
        lock (_lock)
        {
            var key = (frame.Id, request.Key);
            if (_cache.TryGetValue(key, out var existing))
            {
                Interlocked.Increment(ref _hits);
                // awaiting (not ContinueWith) so a failed shared read gives every waiter the real error
                task = Shared(existing);
                static async Task<OcrResult> Shared(Task<OcrResult> t) => (await t.ConfigureAwait(false)) with { FromCache = true };
            }
            else
            {
                // The shared read is not tied to one caller's token: another plugin may still want it.
                task = RunAsync(frame, request, CancellationToken.None);
                _cache[key] = task;
                Remember(frame.Id);
                task.ContinueWith(t => { lock (_lock) _cache.Remove(key); }, CancellationToken.None,
                    TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Default);
            }
        }
        return ct.CanBeCanceled ? task.WaitAsync(ct) : task;
    }

    void Remember(long frameId)
    {
        if (_frameOrder.Last?.Value == frameId || _frameOrder.Contains(frameId)) return;
        _frameOrder.AddLast(frameId);
        while (_frameOrder.Count > FramesKept)
        {
            long old = _frameOrder.First!.Value;
            _frameOrder.RemoveFirst();
            foreach (var k in _cache.Keys.Where(k => k.frame == old).ToList()) _cache.Remove(k);
        }
    }

    async Task<OcrResult> RunAsync(Frame frame, OcrRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        try
        {
            var r = request.Region;
            var crop = request.Preprocess.Apply(frame.Crop(r));
            int pad = Math.Max(0, request.Pad);
            int largest = Math.Max(crop.W, crop.H) + 2 * pad;
            int fits = _engine!.MaxImageDimension / Math.Max(1, largest);
            int scale = request.Scale > 0 ? Math.Min(request.Scale, fits) : Math.Min(fits, 3);
            if (scale < 1) throw new ArgumentException($"Region {r} is larger than the OCR engine accepts ({_engine.MaxImageDimension}px). Read it in parts.");
            var image = ImageOps.Scale(crop, scale, request.Scaling == OcrScaling.Smooth, pad);

            var raw = await _engine.RecognizeAsync(image, ct).ConfigureAwait(false);
            IReadOnlyList<OcrLine> lines = raw.Select(l => new OcrLine(l.Text.Trim(),
                    l.Box.FromScaled(scale, pad, r.X, r.Y),
                    l.Words.Select(w => new OcrWord(w.Text, w.Box.FromScaled(scale, pad, r.X, r.Y))).ToList()))
                .Where(l => l.Text.Length > 0).ToList();
            if (request.Grouping == OcrGrouping.Rows) lines = OcrLayout.GroupRows(lines);

            sw.Stop();
            Interlocked.Increment(ref _reads);
            Interlocked.Add(ref _totalTicks, sw.Elapsed.Ticks);
            Interlocked.Exchange(ref _lastTicks, sw.Elapsed.Ticks);
            return new OcrResult(lines, r, scale, sw.Elapsed, _engine.Name);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Interlocked.Increment(ref _failures);
            throw;
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _gate.Dispose();
        (_engine as IDisposable)?.Dispose();
    }
}
