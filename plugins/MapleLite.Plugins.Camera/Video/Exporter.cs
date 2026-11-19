using MapleLite.Core.Imaging;
using MapleLite.Plugins.Camera.Imaging;
using MapleLite.Plugins.Camera.Model;
using MapleLite.Sdk;

namespace MapleLite.Plugins.Camera.Video;

public enum ClipFormat { Mp4, Gif }

/// <summary>What to export: the part of the clip, the format, the rate and the largest width (0 keeps full size).</summary>
public sealed record ClipExport(ClipFormat Format, double From, double To, double Fps, int MaxWidth, VideoQuality Quality, bool Dither = true);

/// <summary>Bakes an edit into pictures and clips.</summary>
public static class Exporter
{
    public static RenderSource PhotoSource(Frame picture, MediaInfo? info) =>
        new(picture, info?.Taken ?? DateTimeOffset.Now, info?.Overlays, info?.GameScale ?? 1);

    /// <summary>Saves the edited photo as PNG (lossless) or JPEG (quality 95, full color). Returns the path.</summary>
    public static string ExportPhoto(Frame picture, MediaInfo? info, Edit edit, string path)
    {
        var result = EditRenderer.Render(PhotoSource(picture, info), edit);
        var bytes = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            ? FrameFiles.EncodeJpeg(result.Picture, 95)
            : FrameFiles.EncodePng(result.Picture);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>The scale that fits an edit's output within <paramref name="maxWidth"/> (1 when 0 or already narrower).</summary>
    public static double ScaleFor(int picW, int picH, Edit edit, int maxWidth)
    {
        if (maxWidth <= 0) return 1;
        var (w, _) = EditRenderer.OutputSize(picW, picH, edit);
        return Math.Min(1, maxWidth / (double)w);
    }

    public static async Task<string> ExportClipAsync(ClipSource source, MediaInfo? info, IReadOnlyList<OverlayAt> overlays, Edit edit,
        ClipExport opts, string path, string? ffmpeg, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        double from = Math.Clamp(opts.From, 0, source.Seconds), to = Math.Clamp(opts.To, from + 0.05, Math.Max(source.Seconds, from + 0.05));
        double scale = ScaleFor(source.W, source.H, edit, opts.MaxWidth);
        var (w, h) = EditRenderer.OutputSize(source.W, source.H, edit, scale);
        var taken = info?.Taken ?? DateTimeOffset.Now;
        double total = (to - from) * 1000;
        string temp = path + ".part";

        Ffmpeg.Writer? mp4 = null;
        GifEncoder? gif = null;
        FileStream? gifFile = null;
        try
        {
            if (opts.Format == ClipFormat.Mp4)
            {
                if (ffmpeg == null) throw new InvalidOperationException("MP4 needs ffmpeg. " + Ffmpeg.InstallHint);
                // ffmpeg picks the container from the name, so give the temporary file a real extension
                temp = Path.ChangeExtension(path, ".part.mp4");
                mp4 = new Ffmpeg.Writer(ffmpeg, temp, w, h, opts.Fps, opts.Quality, live: false);
            }
            else
            {
                gifFile = File.Create(temp);
                gif = new GifEncoder(gifFile, w, h, loop: true, dither: opts.Dither);
            }

            int delay = (int)Math.Round(1000 / opts.Fps);
            int oi = 0;
            await foreach (var (ms, frame) in source.ReadAsync(from, to, opts.Fps, source.W, source.H, ct).ConfigureAwait(false))
            {
                while (oi + 1 < overlays.Count && overlays[oi + 1].Ms <= ms) oi++;
                var items = overlays.Count > 0 && overlays[oi].Ms <= ms ? overlays[oi].Items : null;
                var rendered = await Task.Run(() => EditRenderer.Render(new RenderSource(frame, taken.AddMilliseconds(ms), items, info?.GameScale ?? 1), edit, scale).Picture, ct).ConfigureAwait(false);
                if (mp4 != null) await Task.Run(() => mp4.Write(rendered), ct).ConfigureAwait(false);
                else gif!.Add(rendered, delay);
                progress?.Report(Math.Clamp((ms - from * 1000) / Math.Max(1, total), 0, 1));
            }
            if (mp4 != null) { await mp4.DisposeAsync().ConfigureAwait(false); mp4 = null; }
            if (gif != null) { gif.Dispose(); gif = null; gifFile!.Dispose(); gifFile = null; }
            File.Move(temp, path, overwrite: true);
            progress?.Report(1);
            return path;
        }
        finally
        {
            if (mp4 != null) { try { await mp4.DisposeAsync().ConfigureAwait(false); } catch (IOException) { } }
            gif?.Dispose();
            gifFile?.Dispose();
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }
}
