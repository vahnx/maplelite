using MapleLite.Sdk;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MapleLite.Core.Capture;
using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;
using MapleLite.Core.Platform;
using MapleLite.Sdk.Theming;

namespace MapleLite.Host.Pages;

/// <summary>The live game picture, what MapleLite knows about the window, and a test of the shared OCR.</summary>
internal sealed class GamePage : UserControl
{
    readonly Shell _shell;
    readonly Image _preview = new() { Stretch = Stretch.Uniform, MaxHeight = 560 };
    readonly TextBlock _state, _window, _process, _mode, _size, _scale, _fps, _ocr;
    readonly Ellipse _dot = new() { Width = 10, Height = 10 };
    readonly ComboBox _windows = new() { MinWidth = 360, PlaceholderText = "Pick a window to follow" };
    readonly TextBox _processName = new() { Width = 180 };
    readonly TextBox _ocrOut = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas,Menlo,DejaVu Sans Mono,monospace"), MinHeight = 120, MaxHeight = 320 };
    readonly ComboBox _recRate = new() { MinWidth = 150 };
    readonly Button _recButton;
    readonly TextBlock _recStatus = Ui.Muted("Not recording.");
    readonly DispatcherTimer _recTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Border _previewHost;
    readonly Button _previewToggle;
    /// <summary>Whether the live picture is shown; hidden by default and remembered while the app runs.</summary>
    static bool s_previewExpanded;
    WriteableBitmap? _bitmap;
    IDisposable? _sub;
    volatile GameFrame? _pending;

    public GamePage(Shell shell)
    {
        _shell = shell;
        var facts = Ui.Facts(out var add);
        _window = add("Window"); _process = add("Process"); _mode = add("Display mode"); _size = add("Game picture");
        _scale = add("Display scale"); _fps = add("Capture rate"); _ocr = add("Text reader");
        _state = Ui.Text("Starting…", ThemeTokens.Text, 14, FontWeight.SemiBold);

        var notes = Ui.Stack(4, shell.Platform.Notes.Select(n => (Control)Ui.Muted("• " + n)).ToArray());

        _processName.Text = shell.Settings.ProcessName;
        var target = Ui.Stack(10,
            Ui.Subheading("Which window"),
            Ui.Row(8, Ui.Muted("Game process name"), _processName,
                Ui.Button("Use name", () => _shell.SetTarget(new GameTarget(string.IsNullOrWhiteSpace(_processName.Text) ? "MapleStory" : _processName.Text.Trim())))),
            Ui.Row(8, _windows,
                Ui.Button("Refresh list", LoadWindows),
                Ui.Button("Follow this window", () =>
                {
                    if (_windows.SelectedItem is ComboBoxItem { Tag: WindowCandidate c })
                        _shell.SetTarget(new GameTarget(c.ProcessName, c.ProcessId));
                })));

        var ocrCard = Ui.Card(Ui.Stack(10,
            Ui.Subheading("Shared text reader (Developer)"),
            Ui.Muted("Every plugin reads text through this one service. Reading the same picture twice is served from its cache."),
            Ui.Row(8, Ui.Button("Read text in the game picture", ReadText), Ui.Button("Save picture…", SavePicture)),
            _ocrOut));

        foreach (var r in SessionRecorder.Rates) _recRate.Items.Add(new ComboBoxItem { Content = $"{r:0} pictures/s", Tag = r });
        _recRate.SelectedIndex = Array.IndexOf(SessionRecorder.Rates, SessionRecorder.DefaultFps);
        _recButton = Ui.Button("Start recording", ToggleRecording);
        var recordCard = Ui.Card(Ui.Stack(10,
            Ui.Subheading("Record a session (Developer)"),
            Ui.Muted("Saves the game picture, losslessly and with timings, so it can be replayed through MapleLite later "
                + "(run with --replay <folder>) to test and tune the readers. Only the game window is recorded. "
                + "A 1080p picture is about 1–2 MB, so 5 minutes at 5/s needs roughly 1–3 GB; unchanged pictures aren't saved twice."),
            Ui.Row(8, _recRate, _recButton, Ui.Button("Open recordings folder", () => OpenFolder(SessionRecorder.RecordingsFolder))),
            _recStatus));
        _recTimer.Tick += (_, _) => ShowRecording();

        _previewHost = new Border { Child = _preview, MinHeight = 200, IsVisible = s_previewExpanded }.Res(Border.BackgroundProperty, ThemeTokens.Background);
        // The heading on the left and an arrow at the right edge, both centred on the same line: ▲ hides the picture, ▼ shows it.
        _previewToggle = Ui.Button("", TogglePreview);
        _previewToggle.Width = 32; _previewToggle.Height = 28; _previewToggle.Padding = new Thickness(0);
        _previewToggle.HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center; _previewToggle.VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center;
        _previewToggle.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        ShowPreviewToggle();
        var previewTitle = Ui.Subheading("Game window");
        previewTitle.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        // inset to line up with the text of the other cards (16 in from their edge; this card's padding is 8)
        var previewHead = new DockPanel { Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(_previewToggle, Dock.Right);
        previewHead.Children.Add(_previewToggle);
        previewHead.Children.Add(previewTitle);
        var previewCard = Ui.Card(Ui.Stack(10, previewHead, _previewHost), 8);

        var statusRow = Ui.Row(10, _dot, _state);
        var content = Ui.Stack(18,
            Ui.Heading("Game"),
            Ui.Card(Ui.Stack(14, statusRow, facts, notes)),
            previewCard,
            recordCard,
            ocrCard,
            Ui.Card(target));
        Content = Ui.Page(content);

        AttachedToVisualTree += (_, _) =>
        {
            _shell.Capture.StatusChanged += OnStatus;
            if (s_previewExpanded) StartPreview();
            OnStatus(_shell.Capture.Status);
            if (_windows.ItemCount == 0) LoadWindows();
            ShowRecording();
            if (_shell.Recorder.Active) _recTimer.Start();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _recTimer.Stop(); // recording itself carries on while other pages are open
            _shell.Capture.StatusChanged -= OnStatus;
            _sub?.Dispose(); _sub = null;
        };
    }

    void TogglePreview()
    {
        s_previewExpanded = !s_previewExpanded;
        _previewHost.IsVisible = s_previewExpanded;
        ShowPreviewToggle();
        // Only take preview frames while the picture is on screen, so a hidden preview costs nothing.
        if (s_previewExpanded) StartPreview();
        else { _sub?.Dispose(); _sub = null; }
    }

    /// <summary>The toggle's arrow and tooltip for the picture's state: ▲ to collapse it while shown, ▼ to expand it while hidden.</summary>
    void ShowPreviewToggle()
    {
        _previewToggle.Content = s_previewExpanded ? "▲" : "▼";
        ToolTip.SetTip(_previewToggle, s_previewExpanded ? "Hide the game picture" : "Show the game picture");
    }

    void StartPreview() =>
        _sub ??= _shell.Capture.Subscribe(f => { if (Interlocked.Exchange(ref _pending, f) == null) Dispatcher.UIThread.Post(Draw, DispatcherPriority.Background); }, _shell.Settings.PreviewFps);

    void OnStatus(CaptureStatus s) => Dispatcher.UIThread.Post(() =>
    {
        _state.Text = s.Message;
        string token = s.State switch
        {
            CaptureState.Capturing => ThemeTokens.Success,
            CaptureState.Searching or CaptureState.Minimized => ThemeTokens.Warning,
            CaptureState.Stopped => ThemeTokens.TextMuted,
            _ => ThemeTokens.Danger,
        };
        _dot.Fill = this.FindResource(ThemeTokens.Brush(token)) as IBrush;
        var w = s.Window;
        _window.Text = w?.Title is { Length: > 0 } t ? t : "–";
        _process.Text = w == null ? "–" : $"{w.ProcessName} (PID {w.ProcessId})";
        _mode.Text = w == null ? "–" : CaptureLoop.ModeText(w.Mode) + (w.Mode == WindowMode.ExclusiveFullscreen ? " — borderless works better" : "");
        _size.Text = w == null ? "–" : $"{w.Client.Width} × {w.Client.Height} at {w.Client.X},{w.Client.Y}";
        _scale.Text = w == null ? "–" : $"{w.Scale * 100:0}%";
        _fps.Text = s.State == CaptureState.Capturing ? $"{s.Fps:0.0} frames/s of {_shell.Capture.TargetFps:0} set ({s.Frames} total)" : "–";
        var st = _shell.Ocr.Stats;
        _ocr.Text = _shell.Ocr.Available ? $"{_shell.Ocr.EngineName} · {st.Reads} reads, {st.CacheHits} shared, avg {st.AverageMs:0} ms" : "No text reader on this system";
    });

    void Draw()
    {
        var gf = Interlocked.Exchange(ref _pending, null);
        if (gf == null) return;
        var f = gf.Frame;
        if (_bitmap == null || _bitmap.PixelSize.Width != f.W || _bitmap.PixelSize.Height != f.H)
        {
            _bitmap = new WriteableBitmap(new PixelSize(f.W, f.H), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _preview.Source = _bitmap;
        }
        using (var fb = _bitmap.Lock())
        {
            for (int y = 0; y < f.H; y++) Marshal.Copy(f.Px, y * f.W, fb.Address + y * fb.RowBytes, f.W);
        }
        _preview.InvalidateVisual();
    }

    async void ToggleRecording()
    {
        var rec = _shell.Recorder;
        if (rec.Active)
        {
            _recButton.IsEnabled = false;
            _recStatus.Text = "Saving the last pictures…";
            var w = rec.Current!;
            var folder = await rec.StopAsync();
            _recTimer.Stop();
            _recButton.IsEnabled = true;
            ShowRecording();
            _recStatus.Text = $"Saved {w.Frames} pictures ({w.Elapsed:m\\:ss}, {w.Bytes / 1048576.0:0} MB{(w.Dropped > 0 ? $", {w.Dropped} dropped" : "")}) to {folder}";
            _shell.Notify("Recording saved: " + System.IO.Path.GetFileName(folder));
            return;
        }
        if (_shell.Capture.Status.State != CaptureState.Capturing) { _recStatus.Text = "MapleLite isn't capturing the game yet, so there's nothing to record."; return; }
        try
        {
            rec.Start(_recRate.SelectedItem is ComboBoxItem { Tag: double fps } ? fps : SessionRecorder.DefaultFps);
            _recTimer.Start();
            ShowRecording();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _recStatus.Text = "Couldn't start recording: " + e.Message; }
    }

    void ShowRecording()
    {
        var w = _shell.Recorder.Current;
        _recButton.Content = w == null ? "Start recording" : "Stop recording";
        _recRate.IsEnabled = w == null;
        if (w != null)
            _recStatus.Text = $"● Recording {w.Elapsed:m\\:ss} · {w.Frames} pictures · {w.Bytes / 1048576.0:0} MB{(w.Dropped > 0 ? $" · {w.Dropped} dropped (can't keep up; try a lower rate)" : "")}";
    }

    static void OpenFolder(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception) { /* no file manager: nothing to do */ }
    }

    void LoadWindows()
    {
        _windows.Items.Clear();
        foreach (var c in _shell.Platform.Finder.ListCandidates())
            _windows.Items.Add(new ComboBoxItem { Content = $"{c.ProcessName} — {c.Title}", Tag = c });
    }

    async void ReadText()
    {
        var gf = _shell.Capture.Latest;
        if (gf == null) { _ocrOut.Text = "No game picture yet."; return; }
        if (!_shell.Ocr.Available) { _ocrOut.Text = "This system has no text reader available."; return; }
        _ocrOut.Text = "Reading…";
        try
        {
            var r = await _shell.Ocr.ReadAllAsync(gf.Frame);
            _ocrOut.Text = $"{r.Lines.Count} lines · scale {r.Scale}× · {r.Elapsed.TotalMilliseconds:0} ms{(r.FromCache ? " · from cache" : "")}\n"
                + string.Join("\n", r.Lines.Select(l => $"[{l.Box.X,5:0},{l.Box.Y,5:0}] {l.Text}"));
        }
        catch (Exception e) { _ocrOut.Text = "Reading failed: " + e.Message; }
    }

    async void SavePicture()
    {
        var gf = _shell.Capture.Latest;
        if (gf == null || TopLevel.GetTopLevel(this) is not { } top) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Save game picture", SuggestedFileName = $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.png", DefaultExtension = "png",
        });
        if (file?.TryGetLocalPath() is { } path)
        {
            FrameFiles.SavePng(gf.Frame, path);
            _shell.Notify("Saved " + System.IO.Path.GetFileName(path));
        }
    }
}
