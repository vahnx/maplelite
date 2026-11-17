using Avalonia;
using MapleLite.Core.Ocr;
using MapleLite.Core.Platform;

namespace MapleLite.Host;

internal static class Program
{
    /// <summary>
    /// Test switches (never touch the live game):
    ///   --replay &lt;png&gt;                       run the host on a saved capture instead of the game
    ///   --snapshot &lt;png&gt; [--theme id] [--page name]   render the host window to a PNG, then exit
    ///   --ocrtest &lt;png&gt; [--out &lt;txt&gt;]       read a saved capture with the shared OCR and write the lines
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        var ocrTest = Arg(args, "--ocrtest");
        if (ocrTest != null) return OcrTest(ocrTest, Arg(args, "--out"));
        Options.ReplayPath = Arg(args, "--replay");
        Options.SnapshotPath = Arg(args, "--snapshot");
        Options.Theme = Arg(args, "--theme");
        Options.Page = Arg(args, "--page");
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
            var frame = FrameIo.LoadPng(png);
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
    /// <summary>--snapshot &lt;png&gt; [--theme id] [--page game|settings|plugin-id]: render the window to a PNG and exit.</summary>
    public static string? SnapshotPath, Theme, Page;
}
