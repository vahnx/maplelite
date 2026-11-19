using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using MapleLite.Core.Imaging;

namespace MapleLite.Plugins.Camera.Video;

/// <summary>How MP4s are compressed (H.264 through ffmpeg).</summary>
public enum VideoQuality
{
    /// <summary>CRF 16, 4:2:0 color: looks like the game, plays everywhere (phones, Discord, browsers).</summary>
    High,
    /// <summary>CRF 12, full-resolution color (4:4:4): small colored text stays crisp. Some phones and sites won't play it.</summary>
    Sharpest,
    /// <summary>Exact pixels (lossless RGB). Several times larger, and many players can't open it; for editing elsewhere.</summary>
    Lossless,
}

/// <summary>
/// ffmpeg, when the user has it: MapleLite doesn't ship it. Frames go in and out as raw BGRA through pipes, so
/// nothing but the finished file touches the disk.
/// </summary>
public static partial class Ffmpeg
{
    static string Exe => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    /// <summary>The ffmpeg to use: the chosen file, then the PATH, then the usual install places. Null when none.</summary>
    public static string? Find(string? chosen)
    {
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            var c = chosen.Trim().Trim('"');
            if (Directory.Exists(c)) c = Path.Combine(c, Exe);
            if (File.Exists(c)) return c;
        }
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            dirs.Add(Path.Combine(local, "Microsoft", "WinGet", "Links"));
            dirs.Add(@"C:\ffmpeg\bin");
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims"));
            dirs.Add(@"C:\ProgramData\chocolatey\bin");
        }
        else dirs.AddRange(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"]);
        foreach (var d in dirs)
        {
            try { var p = Path.Combine(d.Trim('"'), Exe); if (File.Exists(p)) return p; }
            catch (ArgumentException) { }
        }
        // Installed with winget while MapleLite (or what started it) was already running: PATH here is still the old one.
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(packages))
                    foreach (var pkg in Directory.EnumerateDirectories(packages, "*FFmpeg*"))
                        if (Directory.EnumerateFiles(pkg, Exe, SearchOption.AllDirectories).FirstOrDefault() is { } found) return found;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>How to get ffmpeg on this OS, for the panel.</summary>
    public static string InstallHint => OperatingSystem.IsWindows()
        ? "Install it with \"winget install ffmpeg\" in a terminal (or download it from ffmpeg.org), then restart MapleLite or pick ffmpeg.exe here."
        : OperatingSystem.IsMacOS() ? "Install it with \"brew install ffmpeg\", or pick the ffmpeg file here."
        : "Install it from your package manager (e.g. \"sudo apt install ffmpeg\"), or pick the ffmpeg file here.";

    static string[] EncodeArgs(VideoQuality q, bool live) => q switch
    {
        VideoQuality.Sharpest => ["-c:v", "libx264", "-preset", live ? "veryfast" : "medium", "-crf", "12", "-pix_fmt", "yuv444p"],
        VideoQuality.Lossless => ["-c:v", "libx264rgb", "-preset", live ? "ultrafast" : "medium", "-qp", "0", "-pix_fmt", "bgr0"],
        _ => ["-c:v", "libx264", "-preset", live ? "veryfast" : "medium", "-crf", "16", "-pix_fmt", "yuv420p"],
    };

    /// <summary>
    /// Writes an MP4 from frames handed to <see cref="Write"/>, at a steady <c>fps</c>. <paramref name="live"/> picks a quicker
    /// encoder setting (for recording while playing); exports use a slower one that makes smaller files.
    /// </summary>
    public sealed class Writer : IAsyncDisposable
    {
        readonly Process _p;
        readonly Stream _in;
        readonly StringBuilder _err = new();
        readonly Task _errTask;
        public int W { get; }
        public int H { get; }
        public string Output { get; }
        public int Frames { get; private set; }

        /// <param name="outputArgs">Replaces the usual "-movflags +faststart <paramref name="output"/>" ending, for other kinds of
        /// output (such as instant replay's segments).</param>
        public Writer(string ffmpeg, string output, int w, int h, double fps, VideoQuality quality, bool live, IEnumerable<string>? outputArgs = null)
        {
            W = w; H = h; Output = output;
            var psi = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in (string[])["-hide_banner", "-loglevel", "error", "-y",
                         "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{w}x{h}", "-framerate", fps.ToString("0.###", CultureInfo.InvariantCulture), "-i", "-",
                         // H.264 with 4:2:0 color needs even sizes: drop a stray last row or column.
                         "-vf", "crop=trunc(iw/2)*2:trunc(ih/2)*2",
                         .. EncodeArgs(quality, live), .. (outputArgs ?? ["-movflags", "+faststart", output])])
                psi.ArgumentList.Add(a);
            _p = Process.Start(psi) ?? throw new IOException("ffmpeg didn't start.");
            _in = _p.StandardInput.BaseStream;
            _errTask = Task.Run(async () =>
            {
                var buf = new char[1024];
                int n;
                while ((n = await _p.StandardError.ReadAsync(buf).ConfigureAwait(false)) > 0)
                    lock (_err) { _err.Append(buf, 0, n); if (_err.Length > 4000) _err.Remove(0, _err.Length - 4000); }
            });
            _ = _p.StandardOutput.ReadToEndAsync();
        }

        /// <summary>Adds one frame (must be the writer's size). Blocks while ffmpeg catches up.</summary>
        public void Write(Frame f)
        {
            if (f.W != W || f.H != H) throw new ArgumentException($"Frames must be {W}x{H}, not {f.W}x{f.H}.");
            try { _in.Write(f.Bgra); Frames++; }
            catch (IOException e) { throw new IOException("ffmpeg stopped: " + Error(e.Message), e); }
        }

        string Error(string fallback) { lock (_err) return _err.Length > 0 ? _err.ToString().Trim() : fallback; }

        /// <summary>Finishes the file. Throws when ffmpeg failed.</summary>
        public async ValueTask DisposeAsync()
        {
            try { _in.Close(); } catch (IOException) { }
            if (!_p.WaitForExit(120_000)) { try { _p.Kill(); } catch { } }
            await _errTask.ConfigureAwait(false);
            int code = _p.ExitCode;
            _p.Dispose();
            if (code != 0) throw new IOException($"ffmpeg failed (exit {code}): {Error("no details")}");
        }
    }

    /// <summary>
    /// Joins video pieces with the same settings into one MP4 without re-encoding (stream copy): quick, and no
    /// further quality loss.
    /// </summary>
    public static async Task ConcatAsync(string ffmpeg, IReadOnlyList<string> pieces, string output, CancellationToken ct = default)
    {
        var list = output + ".list.txt";
        // the concat list's own quoting: wrap in single quotes, and a quote inside becomes '\''
        await File.WriteAllLinesAsync(list, pieces.Select(p => "file '" + p.Replace("\\", "/").Replace("'", "'\\''") + "'"), ct).ConfigureAwait(false);
        try
        {
            var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in (string[])["-hide_banner", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", list, "-c", "copy", "-movflags", "+faststart", output])
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new IOException("ffmpeg didn't start.");
            var err = p.StandardError.ReadToEndAsync(ct);
            _ = p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            if (p.ExitCode != 0) throw new IOException($"ffmpeg couldn't join the replay (exit {p.ExitCode}): {(await err).Trim()}");
        }
        finally { try { File.Delete(list); } catch (IOException) { } }
    }

    /// <summary>Size, frame rate and length of a video, read from ffmpeg's description of it.</summary>
    public static async Task<(int W, int H, double Fps, double Seconds)?> ProbeAsync(string ffmpeg, string file, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in (string[])["-hide_banner", "-i", file]) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p == null) return null;
        var text = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        var size = SizeRx().Match(text);
        if (!size.Success) return null;
        var fps = FpsRx().Match(text);
        var dur = DurationRx().Match(text);
        double seconds = dur.Success ? int.Parse(dur.Groups[1].Value) * 3600 + int.Parse(dur.Groups[2].Value) * 60 + double.Parse(dur.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        return (int.Parse(size.Groups[1].Value), int.Parse(size.Groups[2].Value),
            fps.Success ? double.Parse(fps.Groups[1].Value, CultureInfo.InvariantCulture) : 30, seconds);
    }

    /// <summary>
    /// Decodes a video's frames from <paramref name="from"/> seconds for <paramref name="length"/> seconds, resized to
    /// <paramref name="w"/>x<paramref name="h"/>, at <paramref name="fps"/> (frames are repeated or skipped to keep it steady).
    /// </summary>
    public static async IAsyncEnumerable<Frame> ReadAsync(string ffmpeg, string file, double from, double length, int w, int h, double fps,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        var inv = CultureInfo.InvariantCulture;
        foreach (var a in (string[])["-hide_banner", "-loglevel", "error", "-ss", from.ToString("0.###", inv), "-i", file, "-t", length.ToString("0.###", inv),
                     "-vf", $"fps={fps.ToString("0.###", inv)},scale={w}:{h}:flags=bicubic", "-f", "rawvideo", "-pix_fmt", "bgra", "-"])
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new IOException("ffmpeg didn't start.");
        _ = p.StandardError.ReadToEndAsync(ct);
        var stream = p.StandardOutput.BaseStream;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var f = new Frame(w, h);
                int got = 0;
                var buffer = new byte[w * h * 4];
                while (got < buffer.Length)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(got), ct).ConfigureAwait(false);
                    if (n == 0) break;
                    got += n;
                }
                if (got < buffer.Length) yield break;
                Buffer.BlockCopy(buffer, 0, f.Px, 0, buffer.Length);
                for (int i = 0; i < f.Px.Length; i++) f.Px[i] |= Pixel.Black;
                yield return f;
            }
        }
        finally
        {
            if (!p.HasExited) { try { p.Kill(); } catch { } }
        }
    }

    [GeneratedRegex(@"Video:.*?(\d{2,5})x(\d{2,5})")] private static partial Regex SizeRx();
    [GeneratedRegex(@"([\d.]+) fps")] private static partial Regex FpsRx();
    [GeneratedRegex(@"Duration: (\d+):(\d+):([\d.]+)")] private static partial Regex DurationRx();
}
