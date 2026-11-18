using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using MapleLite.Core.Capture;
using MapleLite.Core.Platform;
using MapleLite.Sdk;

namespace MapleLite.Host;

/// <summary>
/// The overlay: one click-through, always-on-top MapleLite window laid exactly over the game's client area.
/// Rules from docs/OVERLAYS.md: never inside the game, display only (no focus, no input), off unless the user turns
/// it on, never read back by MapleLite's capture (it captures only the game window's own picture), shown only while the game is the active window and not in
/// exclusive fullscreen. Each plugin draws on its own layer.
/// </summary>
internal sealed class OverlayService : IDisposable
{
    readonly Shell _shell;
    readonly ConcurrentDictionary<string, IReadOnlyList<OverlayItem>> _layers = new();
    readonly DispatcherTimer _follow = new() { Interval = TimeSpan.FromMilliseconds(33) }; // ~30 times a second, so timers move smoothly with the game
    OverlayWindow? _window;
    volatile bool _dirty;
    DateTimeOffset _lastTop;

    public OverlayService(Shell shell)
    {
        _shell = shell;
        _follow.Tick += (_, _) => Follow();
        _follow.Start();
    }

    static bool Replay => Options.ReplayPath != null;
    const string TestLayer = "\u0000test";
    DateTimeOffset _testUntil = Options.OverlayTest ? DateTimeOffset.MaxValue : DateTimeOffset.MinValue;
    string? _loggedStatus = "";

    public bool Visible { get; private set; }
    public bool Enabled => _shell.Settings.OverlayEnabled && OperatingSystem.IsWindows();
    public string? Status { get; private set; } = "Overlays are off. Turn them on in Settings.";

    public void Draw(string layer, IReadOnlyList<OverlayItem> items)
    {
        if (_layers.TryGetValue(layer, out var old) && old.SequenceEqual(items)) return; // unchanged: no redraw
        _layers[layer] = items; _dirty = true;
    }
    public void Clear(string layer) { _layers.TryRemove(layer, out _); _dirty = true; }

    /// <summary>
    /// Saves the latest game picture with every layer drawn on it, as the overlay shows it (for screenshots and checks:
    /// <c>--overlay-snapshot</c>). Works whether or not the overlay window is showing.
    /// </summary>
    public void SaveComposite(string path)
    {
        if (_shell.Capture.Latest is not { } gf) { _shell.Log("overlay snapshot: no game picture yet"); return; }
        int w = gf.Frame.W, h = gf.Frame.H;
        var canvas = new Canvas { Width = w, Height = h };
        canvas.Children.Add(new Image { Source = PanelKit.ToBitmap(gf.Frame), Width = w, Height = h });
        OverlayWindow.Draw(canvas, _layers.Values.SelectMany(l => l).ToList(), (0, 0), 1);
        canvas.Measure(new Size(w, h));
        canvas.Arrange(new Rect(0, 0, w, h));
        using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        bmp.Render(canvas);
        bmp.Save(path);
        _shell.Log($"overlay snapshot: {_layers.Values.Sum(l => l.Count)} item(s) saved to {path}");
    }

    /// <summary>Puts a test pattern over the game for a while, so the overlay can be checked without waiting for a plugin to draw.</summary>
    public void ShowTest(TimeSpan howLong) { _testUntil = DateTimeOffset.Now + howLong; _dirty = true; }
    public bool Testing => DateTimeOffset.Now < _testUntil;

    void Follow()
    {
        UpdateTest();
        var (show, why, frame) = ShouldShow();
        Status = why;
        if (why != _loggedStatus || show != Visible)
        {
            _loggedStatus = why;
            _shell.Log("overlay: " + (show && frame != null
                ? $"showing over the game at {frame.Window.Client.X},{frame.Window.Client.Y} {frame.Window.Client.Width}x{frame.Window.Client.Height} ({frame.Window.Mode})"
                : (why ?? "hidden: nothing to draw") + (why?.Contains("active window") == true ? $" (in front: {ForegroundName(_inFront)})" : "")));
        }
        if (!show || frame == null)
        {
            if (_window is { IsVisible: true }) _window.Hide();
            Visible = false;
            return;
        }
        if (_window == null) { _window = new OverlayWindow(); _dirty = true; }
        var w = frame.Window;
        _window.Place(w.Client.X, w.Client.Y, w.Client.Width, w.Client.Height);
        if (!_window.IsVisible) { _window.Show(); _window.MakeClickThrough(); _dirty = true; }
        if (DateTimeOffset.Now - _lastTop > TimeSpan.FromSeconds(1)) { _window.KeepOnTop(); _lastTop = DateTimeOffset.Now; }
        Visible = true;
        if (_dirty || _window.Offset != frame.Offset)
        {
            _dirty = false;
            _window.Offset = frame.Offset;
            _window.Render(_layers.Values.SelectMany(l => l).ToList());
        }
    }

    // The test pattern: a ring in each corner of the game picture and a label in the middle.
    void UpdateTest()
    {
        if (!Testing) { if (_layers.ContainsKey(TestLayer)) Clear(TestLayer); return; }
        if (_shell.Capture.Latest is not { } f) return;
        int w = f.Frame.W, h = f.Frame.H;
        double left = (_testUntil - DateTimeOffset.Now).TotalSeconds;
        string label = left > 3600 ? "MapleLite overlay test" : $"MapleLite overlay test · {left:0} s";
        var items = new List<OverlayItem>
        {
            new(30, 30, "", OverlayTone.Accent, 1), new(w - 30, 30, "", OverlayTone.Accent, 1),
            new(30, h - 30, "", OverlayTone.Accent, 1), new(w - 30, h - 30, "", OverlayTone.Accent, 1),
            new(w / 2.0, h / 2.0, label, OverlayTone.Good, null, 20),
        };
        _layers[TestLayer] = items;
        _dirty = true;
    }

    (bool, string?, GameFrame?) ShouldShow()
    {
        if (!_shell.Settings.OverlayEnabled) return (false, "Overlays are off. Turn them on in Settings.", null);
        if (!OperatingSystem.IsWindows()) return (false, "Overlays only work on Windows so far.", null);
        var status = _shell.Capture.Status;
        var frame = _shell.Capture.Latest;
        if (status.State != CaptureState.Capturing || frame == null) return (false, "Waiting for the game.", null);
        if (frame.Window.Mode == WindowMode.ExclusiveFullscreen) return (false, "Overlay paused: switch the game to borderless or windowed to see it.", null);
        if (frame.Window.Mode == WindowMode.Minimized) return (false, "The game is minimized.", null);
        // In replay there's no game window to follow: draw where the replayed picture would sit (top-left of the screen) so the overlay can be checked.
        if (!Replay && !Options.OverlayTest)
        {
            var fg = GetForegroundWindow();
            // the overlay itself counts as the game (it should never become active, but if Windows hands it focus, don't flicker)
            if (fg != frame.Window.Handle && (_window?.Handle is not { } own || fg != own))
            {
                _inFront = fg;
                return (false, "Shown while the game is the active window.", null);
            }
        }
        if (_layers.IsEmpty || _layers.Values.All(l => l.Count == 0)) return (false, null, null); // nothing to draw: stay out of the way
        return (true, null, frame);
    }

    public void Dispose()
    {
        _follow.Stop();
        _window?.Close();
    }

    nint _inFront; // the active window when the overlay last hid for focus (named in the log)

    static string ForegroundName(nint h)
    {
        try
        {
            GetWindowThreadProcessId(h, out uint pid);
            var title = new System.Text.StringBuilder(128);
            GetWindowText(h, title, title.Capacity);
            return $"{System.Diagnostics.Process.GetProcessById((int)pid).ProcessName} \"{title}\"";
        }
        catch { return h == 0 ? "nothing" : "another window"; }
    }

    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint h, System.Text.StringBuilder s, int n);
}

/// <summary>The transparent window itself. Items arrive in game-picture pixels and are placed in its own units.</summary>
internal sealed class OverlayWindow : Window
{
    readonly Canvas _canvas = new();
    public (int X, int Y) Offset { get; set; }
    public nint? Handle => TryGetPlatformHandle()?.Handle;

    public OverlayWindow()
    {
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true; ShowInTaskbar = false; CanResize = false; ShowActivated = false;
        Focusable = false; IsHitTestVisible = false;
        Title = "MapleLite overlay";
        Content = _canvas;
    }

    public void Place(int x, int y, int w, int h)
    {
        var scale = DesktopScaling;
        if (Position != new PixelPoint(x, y)) Position = new PixelPoint(x, y);
        double dw = w / scale, dh = h / scale;
        if (Math.Abs(Width - dw) > 0.5) Width = dw;
        if (Math.Abs(Height - dh) > 0.5) Height = dh;
    }

    public void Render(List<OverlayItem> items)
    {
        _canvas.Children.Clear();
        Draw(_canvas, items, Offset, RenderScaling);
    }

    /// <summary>Adds the items to <paramref name="canvas"/>: shared by the live overlay and <see cref="OverlayService.SaveComposite"/>.</summary>
    internal static void Draw(Canvas canvas, IEnumerable<OverlayItem> items, (int X, int Y) offset, double scale)
    {
        foreach (var it in items)
        {
            double x = (it.X + offset.X) / scale, y = (it.Y + offset.Y) / scale;
            var color = Tone(it.Tone);
            if (it.Ring is { } ring)
            {
                const double r = 13;
                var back = new Ellipse { Width = 2 * r, Height = 2 * r, Fill = new SolidColorBrush(Color.FromArgb(150, 10, 12, 18)), Stroke = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), StrokeThickness = 3 };
                Canvas.SetLeft(back, x - r); Canvas.SetTop(back, y - r);
                canvas.Children.Add(back);
                if (ring > 0.001) canvas.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Arc(x, y, r - 1.5, Math.Min(ring, 0.9999)), Stroke = new SolidColorBrush(color), StrokeThickness = 3.5 });
            }
            if (it.Text.Length > 0)
            {
                var label = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(170, 10, 12, 18)),
                    CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1),
                    Child = new TextBlock { Text = it.Text, Foreground = new SolidColorBrush(color), FontSize = it.Size, FontWeight = FontWeight.SemiBold },
                };
                label.Measure(Size.Infinity);
                Canvas.SetLeft(label, x - label.DesiredSize.Width / 2);
                Canvas.SetTop(label, it.Ring != null ? y + 15 : y - label.DesiredSize.Height / 2);
                canvas.Children.Add(label);
            }
        }
    }

    static Geometry Arc(double cx, double cy, double r, double share)
    {
        double a = share * 2 * Math.PI;
        var start = new Point(cx, cy - r);
        var end = new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(start, false);
            c.ArcTo(end, new Size(r, r), 0, share > 0.5, SweepDirection.Clockwise);
            c.EndFigure(false);
        }
        return g;
    }

    static Color Tone(OverlayTone t) => t switch
    {
        OverlayTone.Accent => Color.FromRgb(255, 170, 60),
        OverlayTone.Good => Color.FromRgb(110, 220, 120),
        OverlayTone.Warning => Color.FromRgb(255, 215, 80),
        OverlayTone.Danger => Color.FromRgb(255, 100, 90),
        OverlayTone.Muted => Color.FromRgb(180, 185, 195),
        _ => Colors.White,
    };

    /// <summary>Makes the window ignore the mouse, never take focus and stay off Alt+Tab.</summary>
    public void MakeClickThrough()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == 0) return;
        const int GWL_EXSTYLE = -20;
        const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOOLWINDOW = 0x80;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new nint(ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        // A window made layered by hand is drawn only once its layering is set: fully opaque (the picture's own alpha still applies).
        if ((ex & WS_EX_LAYERED) == 0) SetLayeredWindowAttributes(hwnd, 0, 255, 0x2 /* LWA_ALPHA */);
        // Not hidden from screen captures (WDA_EXCLUDEFROMCAPTURE): with it on, the overlay didn't show on screen in live testing. MapleLite captures only the
        // game window's own picture, which never includes windows on top of it, so its OCR can't read the overlay back anyway.
    }

    /// <summary>Stays at the top of the always-on-top band (another topmost window, or the game going topmost, could cover it).</summary>
    public void KeepOnTop()
    {
        if (!OperatingSystem.IsWindows() || Handle is not { } hwnd || hwnd == 0) return;
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
        SetWindowPos(hwnd, -1 /* HWND_TOPMOST */, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(nint hWnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hWnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern nint GetWindowLongPtr(nint hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint value);
}

/// <summary>One plugin's layer of the overlay; cleared when the plugin stops.</summary>
internal sealed class PluginOverlay(OverlayService service, string layer) : IOverlay
{
    bool _closed;
    public bool Visible => !_closed && service.Visible;
    public bool Enabled => service.Enabled;
    public string? Status => service.Status;
    public void Draw(IReadOnlyList<OverlayItem> items) { if (!_closed) service.Draw(layer, items); }
    public void Clear() => service.Clear(layer);
    public void Close() { _closed = true; service.Clear(layer); }
}
