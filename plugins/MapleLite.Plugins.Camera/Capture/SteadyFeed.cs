using System.Collections.Concurrent;
using MapleLite.Core.Imaging;
using MapleLite.Plugins.Camera.Video;
using MapleLite.Sdk;

namespace MapleLite.Plugins.Camera.Capture;

/// <summary>
/// Feeds an ffmpeg writer at a steady frame rate from frames that arrive at uneven times: each frame fills its
/// nearest time slot and any empty slots before it (so a late frame is repeated), and a frame whose slot is already
/// filled is skipped. <see cref="Add"/> never blocks the capture thread; if ffmpeg falls behind, frames are dropped
/// and counted. If the capture stalls for more than two seconds, the gap is shortened.
/// </summary>
internal sealed class SteadyFeed : IAsyncDisposable
{
    const int MaxQueued = 12;
    readonly Ffmpeg.Writer _writer;
    readonly BlockingCollection<(Frame Frame, double Ms)> _queue = new(MaxQueued);
    readonly Task _worker;
    readonly double _fps;
    int _dropped, _written;
    string? _error;

    /// <param name="started">The time of slot 0 (normally the first frame's time).</param>
    public SteadyFeed(Ffmpeg.Writer writer, double fps, DateTimeOffset started)
    {
        _writer = writer; _fps = fps; Started = started;
        _worker = Task.Factory.StartNew(Work, TaskCreationOptions.LongRunning);
    }

    public DateTimeOffset Started { get; }
    public int W => _writer.W;
    public int H => _writer.H;
    public int Written => _written;
    public int Dropped => _dropped;
    /// <summary>Set when ffmpeg failed.</summary>
    public string? Error => _error;
    /// <summary>The time covered so far.</summary>
    public TimeSpan Covered => TimeSpan.FromSeconds(_written / _fps);

    public void Add(Frame frame, DateTimeOffset time)
    {
        if (_queue.IsAddingCompleted) return;
        if (!_queue.TryAdd((frame, (time - Started).TotalMilliseconds))) Interlocked.Increment(ref _dropped);
    }

    void Work()
    {
        foreach (var (frame, ms) in _queue.GetConsumingEnumerable())
        {
            if (_error != null) continue;
            try
            {
                // Nearest slot, not rounded down: frame i of a steady 30/s stream sits at i × 33.33 ms, and rounding
                // down put 0.99999 into slot 0 and dropped it.
                int due = (int)Math.Round(ms * _fps / 1000) + 1;
                if (due <= _written) continue; // its slot is filled
                var f = frame.W == W && frame.H == H ? frame : FrameFiles.Resize(frame, W, H);
                for (int n = Math.Min(due - _written, (int)Math.Ceiling(_fps * 2)); n > 0; n--) _writer.Write(f);
                _written = Math.Max(_written, due);
            }
            catch (IOException e) { _error = e.Message; }
        }
    }

    /// <summary>Writes what's queued and finishes the file. Errors are kept in <see cref="Error"/>, not thrown.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_queue.IsAddingCompleted) return;
        _queue.CompleteAdding();
        await _worker.ConfigureAwait(false);
        try { await _writer.DisposeAsync().ConfigureAwait(false); }
        catch (IOException e) { _error ??= e.Message; }
        _queue.Dispose();
    }
}
