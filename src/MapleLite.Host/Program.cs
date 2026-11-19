using MapleLite.Sdk;
using Avalonia;
using MapleLite.Core.Ocr;
using MapleLite.Core.Platform;

namespace MapleLite.Host;

internal static class Program
{
    /// <summary>
    /// Test switches (never touch the live game):
    ///   --replay &lt;png|folder&gt; [--loop]      run the host on a saved capture or a recorded session instead of the game
    ///   --snapshot &lt;png&gt; [--theme id] [--page name]   render the host window to a PNG, then exit
    ///   --overlay-snapshot &lt;png&gt;                 with --snapshot: also save the game picture with the overlay on it
    ///   --install &lt;zip|dll&gt;, --uninstall &lt;plugin-id&gt;      add or remove a plugin at startup
    ///   --ocrtest &lt;png&gt; [--out &lt;txt&gt;]       read a saved capture with the shared OCR and write the lines
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog(e.ExceptionObject);
        var ocrTest = Arg(args, "--ocrtest");
        if (ocrTest != null) return OcrTest(ocrTest, Arg(args, "--out"));
        Options.ReplayPath = Arg(args, "--replay");
        Options.ReplayLoop = args.Contains("--loop");
        Options.OverlayTest = args.Contains("--overlay-test");
        Options.SnapshotPath = Arg(args, "--snapshot");
        Options.OverlaySnapshotPath = Arg(args, "--overlay-snapshot");
        Options.Theme = Arg(args, "--theme");
        Options.Page = Arg(args, "--page");
        Options.InstallPath = Arg(args, "--install");
        Options.UninstallId = Arg(args, "--uninstall");
        if (double.TryParse(Arg(args, "--wait"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var wait)) Options.SnapshotWait = wait;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Saves what killed the app to crash.log in the data folder (and prints it), so a crash can be reported.
    /// Native crashes inside the OS frameworks don't come through here; macOS shows those in its own crash report.
    /// </summary>
    static void WriteCrashLog(object error)
    {
        var text = $"MapleLite crashed at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} on {Environment.OSVersion}:{Environment.NewLine}{error}{Environment.NewLine}";
        try { Console.Error.WriteLine(text); } catch { }
        try
        {
            var path = Path.Combine(MapleLite.Core.AppPaths.Root, "crash.log");
            File.WriteAllText(path, text);
            Console.Error.WriteLine("Saved to " + path);
        }
        catch { }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static IPlatform CreatePlatform()
    {
#if WINDOWS
        return new MapleLite.Platform.Windows.WindowsPlatform();
#else
        if (OperatingSystem.IsMacOS()) return new MapleLite.Platform.Mac.MacPlatform();
        if (OperatingSystem.IsLinux()) return new MapleLite.Platform.Linux.LinuxPlatform();
        throw new PlatformNotSupportedException("On Windows, run the net8.0-windows build of MapleLite.");
#endif
    }

    static int OcrTest(string png, string? outPath)
    {
        var lines = new List<string>();
        int code = 0;
        try
        {
            using var platform = CreatePlatform();
            using var ocr = new OcrService(platform.Ocr);
            var frame = FrameFiles.LoadPng(png);
            if (Arg(Environment.GetCommandLineArgs(), "--region") is { } spec)
            {
                // --region x,y,w,h: read just that area, chat-glyph cleanup + rows, with each enlarge mode
                var n = spec.Split(',').Select(int.Parse).ToArray();
                var crop = frame.Crop(new MapleLite.Core.Imaging.Region(n[0], n[1], n[2], n[3]));
                // --pre chat | dark:N | bright:N | none: the cleanup to apply (default chat)
                var preSpec = Arg(Environment.GetCommandLineArgs(), "--pre") ?? "chat";
                var pre = preSpec switch
                {
                    "none" => OcrPreprocessor.None,
                    _ when preSpec.StartsWith("dark:") => OcrPreprocessor.DarkText(int.Parse(preSpec[5..])),
                    _ when preSpec.StartsWith("strokes:") => OcrPreprocessor.DarkStrokes(int.Parse(preSpec.Split(':')[1]), int.Parse(preSpec.Split(':')[2])),
                    _ when preSpec.StartsWith("bright:") =>OcrPreprocessor.BrightText(int.Parse(preSpec[7..])),
                    _ => OcrPreprocessor.ChatGlyphs,
                };
                lines.Add($"engine: {ocr.EngineName}  region {spec}  cleanup {pre.Key}");
                foreach (var scaling in Enum.GetValues<OcrScaling>())
                {
                    var r = ocr.ReadAsync(crop, new OcrRequest(crop.Bounds) { Scale = int.TryParse(Arg(Environment.GetCommandLineArgs(), "--scale"), out var sc) ? sc : 0, Pad = int.TryParse(Arg(Environment.GetCommandLineArgs(), "--pad"), out var pd) ? pd : 0, Preprocess = pre, Grouping = OcrGrouping.Rows, Scaling = scaling, Cache = false }).GetAwaiter().GetResult();
                    lines.Add($"-- {scaling}: {r.Lines.Count} rows, scale {r.Scale}");
                    lines.AddRange(r.Lines.Select(l => $"  [{l.Box.X:0},{l.Box.Y:0}] {l.Text}"));
                }
                File.WriteAllText(outPath ?? "ocrtest.txt", string.Join(Environment.NewLine, lines));
                return 0;
            }
            var plain = ocr.ReadAllAsync(frame).GetAwaiter().GetResult();
            var chat = ocr.ReadAsync(frame, new OcrRequest(frame.Bounds) { Preprocess = OcrPreprocessor.ChatGlyphs, Grouping = OcrGrouping.Rows }).GetAwaiter().GetResult();
            var again = ocr.ReadAllAsync(frame).GetAwaiter().GetResult();
            lines.Add($"engine: {ocr.EngineName}");
            lines.Add($"frame: {frame.W}x{frame.H}");
            lines.Add($"plain read: {plain.Lines.Count} lines, scale {plain.Scale}, {plain.Elapsed.TotalMilliseconds:0} ms");
            lines.Add($"repeat read served from cache: {again.FromCache}");
            lines.AddRange(plain.Lines.Select(l => $"  [{l.Box.X:0},{l.Box.Y:0} {l.Box.Width:0}x{l.Box.Height:0}] {l.Text}"));
            lines.Add($"chat-glyph rows: {chat.Lines.Count}");
            lines.AddRange(chat.Lines.Select(l => $"  [{l.Box.X:0},{l.Box.Y:0}] {l.Text}"));
        }
        catch (Exception e) { lines.Add("FAILED: " + e); code = 1; }
        var text = string.Join(Environment.NewLine, lines);
        if (outPath != null) File.WriteAllText(outPath, text); else Console.WriteLine(text);
        return code;
    }
}

internal static class Options
{
    public static string? ReplayPath;
    /// <summary>--loop: start a replayed recording over when it ends instead of holding its last frame.</summary>
    public static bool ReplayLoop;
    /// <summary>--snapshot &lt;png&gt; [--theme id] [--page game|settings|plugin-id]: render the window to a PNG and exit.</summary>
    public static string? SnapshotPath, Theme, Page;
    /// <summary>--overlay-snapshot &lt;png&gt; (with --snapshot): also save the game picture with the overlay drawn on it.</summary>
    public static string? OverlaySnapshotPath;
    /// <summary>--overlay-test: keep the overlay test pattern up and let screen captures see it (for checking placement).</summary>
    public static bool OverlayTest;
    /// <summary>--wait &lt;seconds&gt;: how long to run before the snapshot (default 3).</summary>
    public static double SnapshotWait = 3;
    /// <summary>--install &lt;zip|dll&gt; / --uninstall &lt;plugin-id&gt;: add or remove a plugin at startup (for scripts and tests).</summary>
    public static string? InstallPath, UninstallId;
}
