using MapleLite.Core.Imaging;
using MapleLite.Sdk;

namespace MapleLite.Plugins.Camera.Capture;

/// <summary>
/// Instant replay: the last few seconds of the game, kept in memory as full-color JPEGs (quality 90, 4:4:4) so a
/// moment can be saved after it happened. Measured on 1680x944 game pictures: about 550–700 KB each (6 MB raw), an
/// average error of about 2.5 levels out of 255 (not visible), and about 55 ms to encode. So 15 s at 15 per second
/// holds roughly 120–160 MB. Encoding runs on two pool threads; if they fall behind, pictures are skipped (and
/// counted) rather than piling up.
/// </summary>
public sealed class ReplayBuffer
{
    public sealed record Entry(DateTimeOffset Time, byte[] Jpeg, int W, int H, IReadOnlyList<OverlayItem>? Overlays);

    const int Quality = 90, Encoders = 2;
    readonly LinkedList<Entry> _entries = new();
    readonly object _lock = new();
    int _busy;
    long _bytes;

    public ReplayBuffer(double seconds) => Seconds = seconds;

    public double Seconds { get; set; }
    public int Skipped { get; private set; }
    public long Bytes => Interlocked.Read(ref _bytes);
    public int Count { get { lock (_lock) return _entries.Count; } }
    /// <summary>How much is held right now.</summary>
    public TimeSpan Held { get { lock (_lock) return _entries.Count < 2 ? TimeSpan.Zero : _entries.Last!.Value.Time - _entries.First!.Value.Time; } }

    /// <summary>Keeps a picture. Cheap: the encoding happens on a pool thread. Safe from the capture thread.</summary>
    public void Add(Frame frame, DateTimeOffset time, IReadOnlyList<OverlayItem>? overlays)
    {
        if (Interlocked.Increment(ref _busy) > Encoders) { Interlocked.Decrement(ref _busy); Skipped++; return; }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var jpeg = FrameFiles.EncodeJpeg(frame, Quality);
                var e = new Entry(time, jpeg, frame.W, frame.H, overlays);
                lock (_lock)
                {
                    // two encoders can finish out of order: insert by time
                    var node = _entries.Last;
                    while (node != null && node.Value.Time > time) node = node.Previous;
                    if (node == null) _entries.AddFirst(e); else _entries.AddAfter(node, e);
                    Interlocked.Add(ref _bytes, jpeg.Length);
                    var cutoff = _entries.Last!.Value.Time - TimeSpan.FromSeconds(Seconds);
                    while (_entries.First is { } f && f.Value.Time < cutoff)
                    {
                        Interlocked.Add(ref _bytes, -f.Value.Jpeg.Length);
                        _entries.RemoveFirst();
                    }
                }
            }
            catch (Exception) { Skipped++; }
            finally { Interlocked.Decrement(ref _busy); }
        });
    }

    /// <summary>What's held now, oldest first (the buffer keeps going).</summary>
    public List<Entry> Snapshot() { lock (_lock) return _entries.ToList(); }

    public void Clear() { lock (_lock) { _entries.Clear(); Interlocked.Exchange(ref _bytes, 0); } }
}
