using System.Globalization;
using MapleLite.Core.Imaging;
using MapleLite.Plugins.Camera.Model;
using MapleLite.Plugins.Camera.Video;
using MapleLite.Sdk;

namespace MapleLite.Plugins.Camera.Capture;

/// <summary>
/// Writes a clip while frames arrive: an MP4 through ffmpeg when there is one (H.264 at the chosen quality, at a
/// steady frame rate, see <see cref="SteadyFeed"/>), otherwise a ".frames" folder of full-color JPEGs (quality 92: no
/// visible loss, about a quarter smaller than 95) in the Recording layout. Runs until stopped: there's no length limit
/// beyond disk space. <see cref="Add"/> never blocks the capture thread; if the encoder falls behind, frames are
/// dropped and counted.
/// </summary>
public sealed class ClipRecorder : IAsyncDisposable
{
    const int FolderJpegQuality = 92;
    readonly SteadyFeed? _mp4;
    readonly Recording.Writer? _folder;
    readonly List<OverlayAt> _overlays = new();
    IReadOnlyList<OverlayItem>? _lastOverlays;

    public string Path { get; }
    public DateTimeOffset Started { get; }
    public int W { get; }
    public int H { get; }
    public bool IsMp4 => _mp4 != null;
    public int Frames => _mp4 != null ? _mp4.Written : _folder!.Frames;
    public int Dropped => _mp4 != null ? _mp4.Dropped : _folder!.Dropped;
    public TimeSpan Elapsed => DateTimeOffset.Now - Started;
    public IReadOnlyList<OverlayAt> OverlayTimeline => _overlays;
    /// <summary>Set when ffmpeg failed while recording.</summary>
    public string? Error => _mp4?.Error;

    /// <param name="ffmpeg">Null records a folder of frames instead of an MP4.</param>
    public ClipRecorder(Library library, DateTimeOffset started, int w, int h, double fps, string? ffmpeg, VideoQuality quality, string? windowTitle, double scale)
    {
        Started = started; W = w; H = h;
        if (ffmpeg != null)
        {
            Path = library.NewPath(started, ".mp4");
            _mp4 = new SteadyFeed(new Ffmpeg.Writer(ffmpeg, Path, w, h, fps, quality, live: true), fps, started);
        }
        else
        {
            Path = library.NewPath(started, Library.FramesSuffix);
            _folder = new Recording.Writer(Path, new Recording.Info(started, fps, windowTitle, "camera", scale), FolderJpegQuality);
        }
    }

    /// <summary>Adds a frame taken at <paramref name="time"/>. Frames of another size (the game window was resized) are scaled to fit.</summary>
    public void Add(Frame frame, DateTimeOffset time, IReadOnlyList<OverlayItem>? overlays)
    {
        if (overlays != null && (_lastOverlays == null || !_lastOverlays.SequenceEqual(overlays)))
        {
            lock (_overlays) _overlays.Add(new OverlayAt((time - Started).TotalMilliseconds, overlays.ToList()));
            _lastOverlays = overlays;
        }
        if (_folder != null) _folder.Add(frame.W == W && frame.H == H ? frame : FrameFiles.Resize(frame, W, H), time);
        else _mp4!.Add(frame, time);
    }

    /// <summary>Finishes the file.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_mp4 != null) await _mp4.DisposeAsync().ConfigureAwait(false);
        if (_folder != null) await _folder.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Saves an instant replay as a clip: an MP4 when there's ffmpeg (re-encoded from the buffer's JPEGs), else a
    /// ".frames" folder holding those same JPEGs (no second compression).
    /// </summary>
    public static async Task<string> SaveReplayAsync(Library library, IReadOnlyList<ReplayBuffer.Entry> entries, double fps, string? ffmpeg,
        VideoQuality quality, string? windowTitle, double scale, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (entries.Count == 0) throw new InvalidOperationException("The replay is empty.");
        var start = entries[0].Time;
        int w = entries[^1].W, h = entries[^1].H;
        if (ffmpeg != null)
        {
            var path = library.NewPath(start, ".mp4");
            await using (var mp4 = new Ffmpeg.Writer(ffmpeg, path, w, h, fps, quality, live: false))
            {
                double end = (entries[^1].Time - start).TotalMilliseconds;
                int j = 0; byte[]? lastJpeg = null; Frame? last = null;
                for (double t = 0; t <= end + 0.5; t += 1000 / fps)
                {
                    ct.ThrowIfCancellationRequested();
                    while (j + 1 < entries.Count && (entries[j + 1].Time - start).TotalMilliseconds <= t) j++;
                    if (!ReferenceEquals(entries[j].Jpeg, lastJpeg))
                    {
                        var f = FrameFiles.Decode(entries[j].Jpeg);
                        last = f.W == w && f.H == h ? f : FrameFiles.Resize(f, w, h);
                        lastJpeg = entries[j].Jpeg;
                    }
                    await Task.Run(() => mp4.Write(last!), ct).ConfigureAwait(false);
                    progress?.Report(Math.Min(1, t / Math.Max(1, end)));
                }
            }
            return path;
        }

        var folder = library.NewPath(start, Library.FramesSuffix);
        var frames = System.IO.Path.Combine(folder, Recording.FramesDir);
        Directory.CreateDirectory(frames);
        var lines = new List<string>();
        for (int i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = $"{Recording.FramesDir}/{i:D6}.jpg";
            await File.WriteAllBytesAsync(System.IO.Path.Combine(folder, file), entries[i].Jpeg, ct).ConfigureAwait(false);
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{(entries[i].Time - start).TotalMilliseconds:0.0}\t{file}"));
            progress?.Report((i + 1.0) / entries.Count);
        }
        await File.WriteAllLinesAsync(System.IO.Path.Combine(folder, Recording.IndexFile), lines, ct).ConfigureAwait(false);
        var info = new Recording.Info(start, fps, windowTitle, "camera replay", scale, entries.Count, entries.Count, 0,
            Math.Round((entries[^1].Time - start).TotalSeconds, 1));
        await File.WriteAllTextAsync(System.IO.Path.Combine(folder, Recording.InfoFile), System.Text.Json.JsonSerializer.Serialize(info), ct).ConfigureAwait(false);
        return folder;
    }

    /// <summary>The overlay items over a replay's time, keeping only changes.</summary>
    public static List<OverlayAt> Timeline(IReadOnlyList<ReplayBuffer.Entry> entries)
    {
        var list = new List<OverlayAt>();
        IReadOnlyList<OverlayItem>? last = null;
        if (entries.Count == 0) return list;
        foreach (var e in entries)
        {
            if (e.Overlays == null || (last != null && last.SequenceEqual(e.Overlays))) continue;
            list.Add(new OverlayAt((e.Time - entries[0].Time).TotalMilliseconds, e.Overlays.ToList()));
            last = e.Overlays;
        }
        return list;
    }
}
