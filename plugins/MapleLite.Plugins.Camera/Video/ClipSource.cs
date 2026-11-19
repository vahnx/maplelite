using System.Runtime.CompilerServices;
using MapleLite.Core.Imaging;
using MapleLite.Sdk;

namespace MapleLite.Plugins.Camera.Video;

/// <summary>A clip's frames at a steady rate, from an MP4 (through ffmpeg) or a folder of frames.</summary>
public abstract class ClipSource
{
    public int W { get; protected init; }
    public int H { get; protected init; }
    public double Fps { get; protected init; }
    public double Seconds { get; protected init; }

    /// <summary>
    /// Frames from <paramref name="from"/> to <paramref name="to"/> seconds, one every 1/<paramref name="fps"/> s,
    /// with each frame's time in milliseconds from the clip's start. Sized <paramref name="w"/>x<paramref name="h"/>.
    /// </summary>
    public abstract IAsyncEnumerable<(double Ms, Frame Frame)> ReadAsync(double from, double to, double fps, int w, int h, CancellationToken ct = default);

    public static async Task<ClipSource> OpenAsync(string path, string? ffmpeg, CancellationToken ct = default)
    {
        if (Directory.Exists(path)) return new FolderClip(path);
        if (ffmpeg == null) throw new InvalidOperationException("Opening an MP4 needs ffmpeg. " + Ffmpeg.InstallHint);
        var probe = await Ffmpeg.ProbeAsync(ffmpeg, path, ct).ConfigureAwait(false) ?? throw new InvalidDataException("ffmpeg couldn't read that video.");
        return new VideoClip(path, ffmpeg, probe.W, probe.H, probe.Fps, probe.Seconds);
    }

    sealed class VideoClip : ClipSource
    {
        readonly string _path, _ffmpeg;
        public VideoClip(string path, string ffmpeg, int w, int h, double fps, double seconds)
        {
            _path = path; _ffmpeg = ffmpeg; W = w; H = h; Fps = fps; Seconds = seconds;
        }

        public override async IAsyncEnumerable<(double, Frame)> ReadAsync(double from, double to, double fps, int w, int h, [EnumeratorCancellation] CancellationToken ct = default)
        {
            int i = 0;
            await foreach (var f in Ffmpeg.ReadAsync(_ffmpeg, _path, from, Math.Max(0.05, to - from), w, h, fps, ct).ConfigureAwait(false))
                yield return (from * 1000 + i++ * 1000 / fps, f);
        }
    }

    /// <summary>A Recording-format folder (frames.tsv + frames/): what clips are without ffmpeg.</summary>
    sealed class FolderClip : ClipSource
    {
        readonly string _folder;
        readonly List<Recording.Entry> _index;

        public FolderClip(string folder)
        {
            _folder = folder;
            _index = Recording.ReadIndex(folder);
            if (_index.Count == 0) throw new InvalidDataException("That clip has no frames.");
            var first = FrameFiles.Decode(File.ReadAllBytes(Path.Combine(folder, _index[0].File)));
            W = first.W; H = first.H;
            Seconds = (_index[^1].Ms - _index[0].Ms) / 1000 + (_index.Count > 1 ? (_index[^1].Ms - _index[0].Ms) / 1000 / (_index.Count - 1) : 0.1);
            Fps = _index.Count > 1 ? Math.Round((_index.Count - 1) / Math.Max(0.001, (_index[^1].Ms - _index[0].Ms) / 1000)) : 1;
        }

        public override async IAsyncEnumerable<(double, Frame)> ReadAsync(double from, double to, double fps, int w, int h, [EnumeratorCancellation] CancellationToken ct = default)
        {
            double start = _index[0].Ms;
            string? lastFile = null; Frame? last = null;
            int j = 0;
            for (double t = from * 1000; t < to * 1000 - 0.5; t += 1000 / fps)
            {
                ct.ThrowIfCancellationRequested();
                // the frame showing at time t: the last one that started at or before it
                while (j + 1 < _index.Count && _index[j + 1].Ms - start <= t) j++;
                var file = _index[j].File;
                if (file != lastFile || last == null)
                {
                    var bytes = await File.ReadAllBytesAsync(Path.Combine(_folder, file), ct).ConfigureAwait(false);
                    var f = FrameFiles.Decode(bytes);
                    last = f.W == w && f.H == h ? f : FrameFiles.Resize(f, w, h);
                    lastFile = file;
                }
                yield return (t, last);
            }
        }
    }
}
