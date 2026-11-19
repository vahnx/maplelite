using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MapleLite.Plugins.Camera.Model;
using MapleLite.Plugins.Camera.Video;
using MapleLite.Sdk;
using MapleLite.Sdk.Theming;
using static MapleLite.Sdk.PanelKit;

namespace MapleLite.Plugins.Camera.Ui;

/// <summary>The Camera page: capture buttons, the gallery, settings, and the editor (which takes over the page while open).</summary>
internal sealed class CameraPanel : ContentControl
{
    readonly CameraPlugin _p;
    readonly StackPanel _home;
    readonly Button _snap, _record, _replay;
    readonly TextBlock _status = Muted(""), _recLine = Text(""), _replayLine = Muted("");
    readonly WrapPanel _gallery = new();
    readonly TextBlock _galleryEmpty = Muted("Nothing yet. Take a snapshot, record, or save an instant replay.");
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly Dictionary<string, Bitmap> _thumbs = new();

    public CameraPanel(CameraPlugin plugin)
    {
        _p = plugin;
        _snap = Button("Snapshot", () => _ = _p.SnapshotAsync(), accent: true);
        _record = Button("Start recording", () => _ = _p.ToggleRecordingAsync());
        _replay = Button("Save instant replay", () => _ = _p.SaveReplayAsync());

        var capture = Card(Stack(10,
            Title("Capture"),
            Wrap(_snap, _record, _replay),
            _recLine, _replayLine, _status,
            Muted("Pictures come from the game window only, so MapleLite's own overlay isn't in them unless you switch it on in the editor. "
                + "Snapshots are lossless PNGs. Recordings run until you stop them.")));

        var galleryHead = new DockPanel();
        var actions = Wrap(Button("Open folder", () => OpenFolder(_p.Library.Folder)), Button("Refresh", RefreshGallery));
        DockPanel.SetDock(actions, Dock.Right);
        galleryHead.Children.Add(actions);
        galleryHead.Children.Add(Title("Gallery"));
        var gallery = Card(Stack(10, galleryHead, _galleryEmpty, _gallery));

        _home = Stack(14, capture, gallery, Card(BuildSettings()));
        Content = _home;

        _p.Changed += OnChanged;
        _p.Saved += OnSaved;
        _tick.Tick += (_, _) => UpdateStatus();
        AttachedToVisualTree += (_, _) => { UpdateStatus(); RefreshGallery(); _tick.Start(); };
        DetachedFromVisualTree += (_, _) => _tick.Stop();
    }

    void OnChanged() => Dispatcher.UIThread.Post(UpdateStatus);
    void OnSaved(string _) => Dispatcher.UIThread.Post(RefreshGallery);

    void UpdateStatus()
    {
        var rec = _p.Recorder;
        _record.Content = rec != null ? "Stop recording" : "Start recording";
        _record.Classes.Set("accent", rec != null);
        if (rec != null)
        {
            var t = rec.Elapsed;
            _recLine.Text = $"● Recording {(int)t.TotalMinutes}:{t.Seconds:00} · {rec.Frames} frames"
                + (rec.Dropped > 0 ? $" · {rec.Dropped} skipped to keep up" : "")
                + (rec.IsMp4 ? $" · MP4 ({QualityName(_p.Settings.Quality)})" : " · full-color JPEG frames (no ffmpeg)")
                + (rec.Error != null ? " · ffmpeg problem: " + rec.Error : "");
            _recLine.IsVisible = true;
        }
        else _recLine.IsVisible = false;
        _recLine.Themed(TextBlock.ForegroundProperty, ThemeTokens.Danger);

        _replay.IsEnabled = _p.Settings.ReplayOn && !_p.SavingReplay;
        _replay.Content = _p.SavingReplay ? "Saving replay…" : $"Save last {_p.Settings.ReplaySeconds} s";
        _replayLine.Text = _p.Replay is { } r
            ? $"Instant replay is holding {r.Held.TotalSeconds:0} s ({r.Bytes / 1048576.0:0} MB {(r.OnDisk ? "of MP4 on disk" : "in memory")})."
            : "Instant replay is off.";

        var keys = new[] { _p.SnapshotKey, _p.ReplayKey, _p.RecordKey }.Where(k => k?.Active == true).Select(k => $"{k!.Gesture} {k.Name.ToLowerInvariant()}");
        var status = _p.Ctx.Frames.Status;
        _status.Text = (status.State == MapleLite.Core.Capture.CaptureState.Capturing ? "Game found." : status.Message)
            + (keys.Any() ? "  Hotkeys: " + string.Join(", ", keys) + "." : "")
            + (_p.FfmpegPath == null ? "  Without ffmpeg, clips save as frames and export to GIF (see Settings for MP4)." : "");
    }

    // ---- gallery ----

    void RefreshGallery()
    {
        _gallery.Children.Clear();
        var items = _p.Library.List(48);
        _galleryEmpty.IsVisible = items.Count == 0;
        foreach (var item in items) _gallery.Children.Add(Tile(item));
    }

    Control Tile(MediaItem item)
    {
        var image = new Image { Width = 200, Height = 112, Stretch = Stretch.UniformToFill };
        LoadThumb(item, image);
        var kind = item.Kind switch
        {
            MediaKind.Photo => "Photo",
            MediaKind.Video => _p.Library.Info(item.Path) is { Seconds: > 0 } i ? $"Video · {TimeSpan.FromSeconds(i.Seconds):m\\:ss}" : "Video",
            MediaKind.Frames => _p.Library.Info(item.Path) is { Seconds: > 0 } f ? $"Clip (frames) · {TimeSpan.FromSeconds(f.Seconds):m\\:ss}" : "Clip (frames)",
            _ => "GIF",
        };
        var name = Muted(item.Name);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.TextWrapping = TextWrapping.NoWrap;
        name.MaxWidth = 200;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (item.Kind is MediaKind.Photo or MediaKind.Video or MediaKind.Frames)
            buttons.Children.Add(Button("Edit", () => OpenEditor(item), accent: true));
        if (item.Kind == MediaKind.Photo) buttons.Children.Add(Button("Copy", () => _ = CopyAsync(item)));
        if (item.Kind != MediaKind.Frames) buttons.Children.Add(Button("Open", () => OpenFile(item.Path)));
        var delete = new Button { Content = "Delete" };
        delete.Click += (_, _) =>
        {
            if (delete.Tag is not true) { delete.Tag = true; delete.Content = "Sure?"; delete.Classes.Add("accent"); return; }
            try { _p.Library.Delete(item); RefreshGallery(); }
            catch (Exception e) { _p.Ctx.Notify("Couldn't delete it: " + e.Message); }
        };
        delete.LostFocus += (_, _) => { delete.Tag = null; delete.Content = "Delete"; delete.Classes.Remove("accent"); };
        buttons.Children.Add(delete);

        var tile = Stack(6,
            new Border { Child = image, CornerRadius = new CornerRadius(6), ClipToBounds = true, Background = Brushes.Black },
            Text(kind, ThemeTokens.Text, 12.5, FontWeight.SemiBold), name, buttons);
        tile.Width = 200;
        tile.Margin = new Thickness(0, 0, 12, 12);
        return tile;
    }

    void LoadThumb(MediaItem item, Image image)
    {
        var thumb = _p.Library.ThumbPath(item.Path);
        if (_thumbs.TryGetValue(thumb, out var cached) && File.Exists(thumb)) { image.Source = cached; return; }
        _ = Task.Run(() =>
        {
            try
            {
                if (!File.Exists(thumb))
                {
                    // made by another app, or before thumbnails: make one from the picture itself (GIFs: first frame)
                    if (item.Kind is MediaKind.Photo or MediaKind.Gif) _p.WriteThumb(item.Path, FrameFiles.Decode(File.ReadAllBytes(item.Path), 720));
                    else return;
                }
                var bytes = File.ReadAllBytes(thumb);
                Dispatcher.UIThread.Post(() =>
                {
                    var bmp = new Bitmap(new MemoryStream(bytes));
                    _thumbs[thumb] = bmp;
                    image.Source = bmp;
                });
            }
            catch (Exception e) { _p.Ctx.Log("thumbnail of " + item.Name + " failed: " + e.Message); }
        });
    }

    async Task CopyAsync(MediaItem item)
    {
        try
        {
            var frame = await Task.Run(() => FrameFiles.Decode(File.ReadAllBytes(item.Path)));
            _p.Ctx.Notify(await _p.Ctx.CopyImageAsync(frame) ? "Copied to the clipboard." : "Copying pictures isn't supported on this system yet: use Open instead.");
        }
        catch (Exception e) { _p.Ctx.Notify("Couldn't copy it: " + e.Message); }
    }

    void OpenEditor(MediaItem item)
    {
        Content = new EditorView(_p, item, () => { Content = _home; RefreshGallery(); });
    }

    // ---- settings ----

    Control BuildSettings()
    {
        var s = _p.Settings;
        var hotkeys = Wrap(
            new HotkeyBox("Snapshot", _p.SnapshotKey, k => { s.SnapshotKey = k; _p.SaveSettings(); }),
            new HotkeyBox("Save instant replay", _p.ReplayKey, k => { s.ReplayKey = k; _p.SaveSettings(); }),
            new HotkeyBox("Start / stop recording", _p.RecordKey, k => { s.RecordKey = k; _p.SaveSettings(); }));
        var hotkeyNote = Muted(_p.Ctx.Hotkeys.Supported
            ? "Hotkeys are registered with Windows (no keyboard hooks, nothing sent to the game). While MapleLite runs, the game doesn't get these combinations, so pick ones it doesn't use."
            : "Hotkeys only work on Windows so far: use the buttons above.");

        var replayOn = new CheckBox { Content = "Keep an instant replay (the last few seconds, in memory)", IsChecked = s.ReplayOn };
        var seconds = new NumericUpDown { Minimum = 5, Maximum = 120, Increment = 5, Value = s.ReplaySeconds, FormatString = "0", Width = 130 };
        var replayFps = Choice([10, 15, 20, 30], s.ReplayFps, v => $"{v:0} per second");
        replayOn.IsCheckedChanged += (_, _) => { s.ReplayOn = replayOn.IsChecked == true; _p.SaveSettings(); _p.ApplyReplay(); };
        seconds.ValueChanged += (_, _) => { s.ReplaySeconds = (int)(seconds.Value ?? 20); _p.SaveSettings(); _p.ApplyReplay(); };
        replayFps.SelectionChanged += (_, _) => { if (replayFps.SelectedItem is ComboBoxItem { Tag: double v }) { s.ReplayFps = v; _p.SaveSettings(); _p.ApplyReplay(); } };

        var recordFps = Choice([15, 20, 30, 60], s.RecordFps, v => $"{v:0} per second");
        recordFps.SelectionChanged += (_, _) => { if (recordFps.SelectedItem is ComboBoxItem { Tag: double v }) { s.RecordFps = v; _p.SaveSettings(); } };
        var quality = new ComboBox { Width = 330 };
        foreach (var q in Enum.GetValues<VideoQuality>()) quality.Items.Add(new ComboBoxItem { Content = QualityName(q) + " — " + QualityHint(q), Tag = q });
        quality.SelectedIndex = (int)s.Quality;
        quality.SelectionChanged += (_, _) => { if (quality.SelectedItem is ComboBoxItem { Tag: VideoQuality q }) { s.Quality = q; _p.SaveSettings(); if (_p.FfmpegPath != null) _p.ApplyReplay(); } };

        var ffStatus = Muted("");
        var ffPath = new TextBox { Text = s.Ffmpeg ?? "", Watermark = "Found automatically, or the path to ffmpeg", Width = 360 };
        void ShowFfmpeg() => ffStatus.Text = _p.FfmpegPath is { } f
            ? $"Using ffmpeg at {f}. Clips save as MP4 (H.264)."
            : "ffmpeg wasn't found, so clips save as folders of full-color JPEG frames and export to GIF. For MP4: " + Ffmpeg.InstallHint;
        ShowFfmpeg();
        ffPath.LostFocus += (_, _) => { s.Ffmpeg = string.IsNullOrWhiteSpace(ffPath.Text) ? null : ffPath.Text.Trim(); _p.SaveSettings(); _p.LookForFfmpegAgain(); ShowFfmpeg(); };
        var browse = Button("Browse…", async () =>
        {
            if (TopLevel.GetTopLevel(this) is not { } top) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Pick ffmpeg", AllowMultiple = false });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } local) return;
            ffPath.Text = local; s.Ffmpeg = local; _p.SaveSettings(); _p.LookForFfmpegAgain(); ShowFfmpeg();
        });
        var again = Button("Look again", () => { _p.LookForFfmpegAgain(); ShowFfmpeg(); });

        var folder = Muted(_p.Library.Folder);
        var change = Button("Change…", async () =>
        {
            if (TopLevel.GetTopLevel(this) is not { } top) return;
            var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Where captures go", AllowMultiple = false });
            if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } local) return;
            s.Folder = local; _p.Library.MoveTo(local); _p.SaveSettings();
            folder.Text = local; RefreshGallery();
        });

        var showOnGame = Check("Show \"● REC\" and \"saved\" notes over the game (when overlays are on in Settings)", s.ShowOnGame, v => s.ShowOnGame = v);
        var copy = Check("Also copy snapshots to the clipboard", s.CopySnapshots, v => s.CopySnapshots = v);
        var overlays = Check("Start new captures with other plugins' overlay items shown (e.g. respawn timers); you can switch it per capture in the editor", s.IncludeOverlays, v => s.IncludeOverlays = v);

        return Stack(12,
            Title("Settings"),
            Text("Hotkeys", ThemeTokens.Text, 13, FontWeight.SemiBold), hotkeys, hotkeyNote,
            Text("Instant replay", ThemeTokens.Text, 13, FontWeight.SemiBold), replayOn,
            Wrap(Stack(4, Muted("Seconds kept"), seconds), Stack(4, Muted("Pictures"), replayFps)),
            Muted("With ffmpeg, the replay is compressed as it happens into short MP4 pieces on disk (at the MP4 quality below) and saving just joins them: "
                + "almost no memory, every frame kept, and no extra quality loss. Without ffmpeg it's kept in memory as full-color JPEGs, "
                + "about 0.5–0.7 MB a picture at 1680x944, so 15 s at 15 per second holds roughly 120–160 MB."),
            Text("Recording", ThemeTokens.Text, 13, FontWeight.SemiBold),
            Wrap(Stack(4, Muted("Pictures"), recordFps), Stack(4, Muted("MP4 quality"), quality)),
            Muted("Recordings can't be smoother than the capture rate set in MapleLite's Settings. With ffmpeg, recordings are H.264 MP4s, many times smaller than saved frames. "
                + "Without it, frames are saved as full-color JPEGs, about 0.6–0.8 MB each (around 1–1.5 GB a minute at 30 per second), so ffmpeg is strongly recommended for long recordings."),
            Text("ffmpeg (for MP4)", ThemeTokens.Text, 13, FontWeight.SemiBold), ffStatus,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { ffPath, browse, again } },
            Text("Folder", ThemeTokens.Text, 13, FontWeight.SemiBold), folder,
            Wrap(change, Button("Open", () => OpenFolder(_p.Library.Folder))),
            showOnGame, copy, overlays);
    }

    CheckBox Check(string text, bool value, Action<bool> set)
    {
        var c = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = value };
        c.IsCheckedChanged += (_, _) => { set(c.IsChecked == true); _p.SaveSettings(); };
        return c;
    }

    static ComboBox Choice(double[] values, double current, Func<double, string> label)
    {
        var box = new ComboBox { Width = 160 };
        foreach (var v in values) box.Items.Add(new ComboBoxItem { Content = label(v), Tag = v });
        int i = Array.FindIndex(values, v => Math.Abs(v - current) < 0.01);
        box.SelectedIndex = i >= 0 ? i : 0;
        return box;
    }

    internal static string QualityName(VideoQuality q) => q switch
    {
        VideoQuality.Sharpest => "Sharpest",
        VideoQuality.Lossless => "Lossless",
        _ => "High",
    };

    internal static string QualityHint(VideoQuality q) => q switch
    {
        VideoQuality.Sharpest => "full color detail, crisp small text; some phones and sites won't play it",
        VideoQuality.Lossless => "exact pixels, very large; for editing in other apps",
        _ => "looks like the game, plays everywhere",
    };

    internal static void OpenFile(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", [path]);
            else Process.Start("xdg-open", [path]);
        }
        catch (Exception) { }
    }
}
