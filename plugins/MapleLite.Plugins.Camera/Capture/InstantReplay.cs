using System.Globalization;
using MapleLite.Core.Imaging;
using MapleLite.Plugins.Camera.Model;
using MapleLite.Plugins.Camera.Video;
using MapleLite.Sdk;

namespace MapleLite.Plugins.Camera.Capture;

/// <summary>A saved instant replay: the file, when it starts, how long, and what the overlay showed over it.</summary>
public sealed record SavedReplay(string Path, DateTimeOffset Start, double Seconds, int W, int H, List<OverlayAt> Overlays, Frame? Last);

/// <summary>Keeps the last few seconds of the game so a moment can be saved after it happened.</summary>
public interface IInstantReplay : IAsyncDisposable
{
    double Seconds { get; set; }
    /// <summary>How much is held right now.</summary>
    TimeSpan Held { get; }
    /// <summary>Space used: memory, or disk when <see cref="OnDisk"/>.</summary>
    long Bytes { get; }
    bool OnDisk { get; }
    /// <summary>Pictures not kept because compression fell behind.</summary>
    int Skipped { get; }
    /// <summary>Takes a picture from the capture thread. Never blocks.</summary>
    void Add(Frame frame, DateTimeOffset time, IReadOnlyList<OverlayItem>? overlays);
    /// <summary>Saves what's held as a new capture in <paramref name="library"/>; null when nothing is held yet.</summary>
    Task<SavedReplay?> SaveAsync(Library library, CancellationToken ct = default);
}

/// <summary>Without ffmpeg: full-color JPEGs in memory (<see cref="ReplayBuffer"/>), saved as a ".frames" folder.</summary>
public sealed class MemoryReplay(double seconds, double fps) : IInstantReplay
{
    readonly ReplayBuffer _buffer = new(seconds);
    public double Seconds { get => _buffer.Seconds; set => _buffer.Seconds = value; }
    public TimeSpan Held => _buffer.Held;
    public long Bytes => _buffer.Bytes;
    public bool OnDisk => false;
    public int Skipped => _buffer.Skipped;

    public void Add(Frame frame, DateTimeOffset time, IReadOnlyList<OverlayItem>? overlays) => _buffer.Add(frame, time, overlays);

    public async Task<SavedReplay?> SaveAsync(Library library, CancellationToken ct = default)
    {
        var entries = _buffer.Snapshot();
        if (entries.Count < 2) return null;
        var path = await ClipRecorder.SaveReplayAsync(library, entries, fps, null, VideoQuality.High, null, 1, null, ct).ConfigureAwait(false);
        return new SavedReplay(path, entries[0].Time, (entries[^1].Time - entries[0].Time).TotalSeconds, entries[^1].W, entries[^1].H,
            ClipRecorder.Timeline(entries), FrameFiles.Decode(entries[^1].Jpeg));
    }

    public ValueTask DisposeAsync() { _buffer.Clear(); return ValueTask.CompletedTask; }
}

/// <summary>
/// With ffmpeg: the replay is encoded as it happens into two-second H.264 pieces in a cache folder, and pieces older
/// than the replay length are deleted. So it takes almost no memory and keeps every frame, at the recording quality.
/// Saving closes the piece being written (so the clip runs right up to the moment you pressed save), then joins the
/// pieces without re-encoding, which takes a moment and adds no quality loss. Recording resumes on the next picture.
/// </summary>
public sealed class SegmentReplay : IInstantReplay
{
    const double PieceSeconds = 2;
    readonly string _ffmpeg, _root;
    readonly double _fps;
    readonly VideoQuality _quality;
    readonly Action<string> _log;
    readonly object _lock = new();
    readonly List<Piece> _closed = new();
    readonly List<(DateTimeOffset Time, IReadOnlyList<OverlayItem> Items)> _overlays = new();
    readonly Timer _prune;
    Run? _run;
    bool _starting, _disposed;
    int _runs, _skipped;
    Frame? _last;

    sealed record Piece(string Path, DateTimeOffset Start, DateTimeOffset End, int W, int H);
    sealed record Run(string Folder, SteadyFeed Feed);

    /// <param name="cacheFolder">Where pieces go. Each MapleLite process works in its own subfolder (two can run at
    /// once); subfolders left by processes that are gone (a crash) are removed.</param>
    public SegmentReplay(string ffmpeg, string cacheFolder, double seconds, double fps, VideoQuality quality, Action<string> log)
    {
        _ffmpeg = ffmpeg; Seconds = seconds; _fps = fps; _quality = quality; _log = log;
        int me = Environment.ProcessId;
        _root = Path.Combine(cacheFolder, $"p{me}-{Interlocked.Increment(ref _instances)}");
        if (Directory.Exists(cacheFolder))
            foreach (var dir in Directory.EnumerateDirectories(cacheFolder))
            {
                var name = Path.GetFileName(dir);
                int dash = name.IndexOf('-');
                if (name.StartsWith('p') && dash > 1 && int.TryParse(name[1..dash], out int pid) && (pid == me || Running(pid))) continue;
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        Directory.CreateDirectory(_root);
        _prune = new Timer(_ => Prune(), null, 2000, 2000);
    }

    static int _instances;

    static bool Running(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public double Seconds { get; set; }
    public bool OnDisk => true;
    public int Skipped => _skipped + (_run?.Feed.Dropped ?? 0);

    public long Bytes
    {
        get
        {
            try { return new DirectoryInfo(_root).EnumerateFiles("*.ts", SearchOption.AllDirectories).Sum(f => f.Length); }
            catch (IOException) { return 0; }
        }
    }

    public TimeSpan Held
    {
        get
        {
            var pieces = AllPieces();
            var now = _run is { } r ? r.Feed.Started + r.Feed.Covered : pieces.Count > 0 ? pieces[^1].End : DateTimeOffset.Now;
            var first = pieces.Count > 0 ? pieces[0].Start : _run?.Feed.Started ?? now;
            var held = now - first;
            return held > TimeSpan.FromSeconds(Seconds) ? TimeSpan.FromSeconds(Seconds) : held < TimeSpan.Zero ? TimeSpan.Zero : held;
        }
    }

    public void Add(Frame frame, DateTimeOffset time, IReadOnlyList<OverlayItem>? overlays)
    {
        Run? run;
        lock (_lock)
        {
            if (_disposed) return;
            _last = frame;
            if (overlays != null && (_overlays.Count == 0 || !_overlays[^1].Items.SequenceEqual(overlays))) _overlays.Add((time, overlays));
            run = _run;
            if (run != null && (run.Feed.W != frame.W || run.Feed.H != frame.H)) { _run = null; _ = CloseAsync(run); run = null; } // window resized
            if (run == null)
            {
                _skipped++;
                if (!_starting)
                {
                    // starting ffmpeg takes a moment: not on the capture thread
                    _starting = true;
                    int w = frame.W, h = frame.H;
                    Task.Run(() =>
                    {
                        Run? started = null;
                        try { started = Start(w, h, time); }
                        catch (Exception e) { _log("replay couldn't start ffmpeg: " + e.Message); }
                        lock (_lock) { _starting = false; if (_disposed) { if (started != null) _ = CloseAsync(started); } else _run = started; }
                    });
                }
                return;
            }
        }
        run.Feed.Add(frame, time);
    }

    Run Start(int w, int h, DateTimeOffset started)
    {
        var folder = Path.Combine(_root, $"run{Interlocked.Increment(ref _runs):D4}");
        Directory.CreateDirectory(folder);
        string seconds = PieceSeconds.ToString(CultureInfo.InvariantCulture);
        var writer = new Ffmpeg.Writer(_ffmpeg, folder, w, h, _fps, _quality, live: true,
        [
            // a keyframe at every piece boundary, so pieces can be cut and joined without re-encoding
            "-force_key_frames", $"expr:gte(t,n_forced*{seconds})",
            "-f", "segment", "-segment_time", seconds, "-segment_format", "mpegts", "-reset_timestamps", "1",
            "-segment_list", Path.Combine(folder, "list.csv"), "-segment_list_type", "csv",
            Path.Combine(folder, "piece%05d.ts"),
        ]);
        return new Run(folder, new SteadyFeed(writer, _fps, started));
    }

    async Task CloseAsync(Run run)
    {
        await run.Feed.DisposeAsync().ConfigureAwait(false);
        if (run.Feed.Error != null) _log("replay ffmpeg: " + run.Feed.Error);
        var pieces = Pieces(run);
        lock (_lock) _closed.AddRange(pieces);
    }

    /// <summary>The pieces ffmpeg has finished in a run (it lists each in list.csv as "name,start,end" once done).</summary>
    static List<Piece> Pieces(Run run)
    {
        var list = new List<Piece>();
        var csv = Path.Combine(run.Folder, "list.csv");
        try
        {
            using var stream = new FileStream(csv, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                var parts = line.Split(',');
                if (parts.Length < 3) continue;
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ||
                    !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var e)) continue;
                var path = Path.Combine(run.Folder, parts[0]);
                if (File.Exists(path)) list.Add(new Piece(path, run.Feed.Started.AddSeconds(s), run.Feed.Started.AddSeconds(e), run.Feed.W, run.Feed.H));
            }
        }
        catch (IOException) { } // not written yet
        return list;
    }

    List<Piece> AllPieces()
    {
        Run? run;
        List<Piece> all;
        lock (_lock) { all = _closed.ToList(); run = _run; }
        if (run != null) all.AddRange(Pieces(run));
        return all.Where(p => File.Exists(p.Path)).OrderBy(p => p.Start).ToList();
    }

    void Prune()
    {
        try
        {
            var all = AllPieces();
            if (all.Count == 0) return;
            var cutoff = all[^1].End - TimeSpan.FromSeconds(Seconds + PieceSeconds);
            foreach (var p in all.Where(p => p.End < cutoff))
            {
                try { File.Delete(p.Path); } catch (IOException) { }
            }
            lock (_lock) _closed.RemoveAll(p => !File.Exists(p.Path));
            // finished runs with nothing left
            string? live; lock (_lock) live = _run?.Folder;
            foreach (var dir in Directory.EnumerateDirectories(_root))
                if (dir != live && !Directory.EnumerateFiles(dir, "*.ts").Any()) { try { Directory.Delete(dir, true); } catch (IOException) { } }
            lock (_lock)
            {
                // keep the last change before the cutoff: it's what showed when the oldest kept piece starts
                int keep = _overlays.FindLastIndex(o => o.Time < cutoff);
                if (keep > 0) _overlays.RemoveRange(0, keep);
            }
        }
        catch (Exception e) { _log("replay prune failed: " + e.Message); }
    }

    public async Task<SavedReplay?> SaveAsync(Library library, CancellationToken ct = default)
    {
        Run? run;
        Frame? last;
        lock (_lock) { run = _run; _run = null; last = _last; }
        if (run != null) await CloseAsync(run).ConfigureAwait(false); // finishes the piece being written; the next picture starts a new run

        var all = AllPieces();
        if (all.Count == 0) return null;
        var end = all[^1].End;
        var pieces = all.Where(p => p.End > end - TimeSpan.FromSeconds(Seconds) && p.W == all[^1].W && p.H == all[^1].H).ToList();
        if (pieces.Count == 0) return null;
        var path = library.NewPath(pieces[0].Start, ".mp4");
        await Ffmpeg.ConcatAsync(_ffmpeg, pieces.Select(p => p.Path).ToList(), path, ct).ConfigureAwait(false);

        // what the overlay showed, on the joined clip's clock (pieces are back to back, gaps from restarts removed)
        List<(DateTimeOffset Time, IReadOnlyList<OverlayItem> Items)> changes;
        lock (_lock) changes = _overlays.ToList();
        var timeline = new List<OverlayAt>();
        double offset = 0;
        foreach (var p in pieces)
        {
            var atStart = changes.LastOrDefault(c => c.Time <= p.Start);
            if (atStart.Items != null) timeline.Add(new OverlayAt(offset, atStart.Items.ToList()));
            foreach (var c in changes.Where(c => c.Time > p.Start && c.Time < p.End))
                timeline.Add(new OverlayAt(offset + (c.Time - p.Start).TotalMilliseconds, c.Items.ToList()));
            offset += (p.End - p.Start).TotalMilliseconds;
        }
        // drop repeats (each piece restates what showed at its start)
        var trimmed = timeline.Where((o, i) => i == 0 || !o.Items.SequenceEqual(timeline[i - 1].Items)).ToList();
        return new SavedReplay(path, pieces[0].Start, offset / 1000, pieces[^1].W, pieces[^1].H, trimmed, last);
    }

    public async ValueTask DisposeAsync()
    {
        Run? run;
        lock (_lock) { _disposed = true; run = _run; _run = null; }
        await _prune.DisposeAsync().ConfigureAwait(false);
        if (run != null) await CloseAsync(run).ConfigureAwait(false);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
