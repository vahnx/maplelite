using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MapleLite.Core.Imaging;
using MapleLite.Plugins.Camera.Imaging;
using MapleLite.Plugins.Camera.Model;
using MapleLite.Plugins.Camera.Video;
using MapleLite.Sdk;
using MapleLite.Sdk.Theming;
using static MapleLite.Sdk.PanelKit;
using Path = System.IO.Path;

namespace MapleLite.Plugins.Camera.Ui;

/// <summary>
/// The editor for one photo or clip: a live preview you can click on (place and move text, draw, pick a crop) and
/// tabs of tools. Previews render at reduced size off the UI thread with the same renderer the export uses.
/// Edits are saved beside the capture as you go; the original file is never changed.
/// </summary>
internal sealed class EditorView : UserControl
{
    enum Tool { Move, Text, Draw, Crop }

    static readonly string[] Swatches = ["#FFFFFF", "#111111", "#FF9F1C", "#FF4D6D", "#FFD60A", "#6BCB77", "#4CC9F0", "#9B5DE5", "#F15BB5"];
    const int PhotoPreviewWidth = 1100, ClipPreviewWidth = 800, MaxPreviewFrames = 600;

    readonly CameraPlugin _p;
    readonly MediaItem _item;
    readonly Action _close;
    MediaInfo _info;
    Edit _edit;

    // what the preview draws from
    Frame? _photoFull, _photoPreview;
    ClipSource? _clip;
    readonly List<(double Ms, byte[] Jpeg)> _clipFrames = new();
    List<OverlayAt> _timeline = new();
    int _pos;
    double _from, _to, _previewFps = 15;
    double _ps = 1; // preview pixels per original pixel
    int _origW, _origH;

    // preview
    readonly Image _image = new() { Stretch = Stretch.Uniform, MaxHeight = 540 };
    readonly Canvas _marks = new() { Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Arrow) };
    readonly TextBlock _status = Muted("Opening…");
    Avalonia.Media.Imaging.WriteableBitmap? _bmp;
    RenderResult? _last;
    int _wanted, _done;
    bool _rendering;

    // tools
    Tool _tool = Tool.Move;
    int _selected = -1;
    (double X, double Y)? _dragFrom;
    (double X, double Y) _dragStart;
    Stroke? _drawing;
    string _penColor = "#FF4D6D"; double _penWidth = 0.008;
    string _textColor = "#FFFFFF"; TextStyle _textStyle = TextStyle.Outline;
    double? _aspect;
    readonly TabControl _tabs = new();
    TextBox _textBox = null!;
    ComboBox _textStyleBox = null!;
    Slider _textSize = null!;
    StackPanel _textEditor = null!;
    TextBlock _textHint = null!;
    readonly List<(Look Look, Button Button, Image Thumb)> _lookButtons = new();
    readonly List<(FrameStyle Style, Button Button)> _frameButtons = new();
    StackPanel _frameColorRow = null!, _captionRow = null!;
    TextBox _caption = null!;
    readonly List<Action> _syncers = new(); // refresh controls from the edit (after reset)
    readonly DispatcherTimer _saveSoon = new() { Interval = TimeSpan.FromMilliseconds(600) };

    // clip transport and export
    readonly DispatcherTimer _play = new();
    Button? _playButton;
    Slider? _position, _trimFrom, _trimTo;
    TextBlock? _timeText;
    ProgressBar? _progress;
    CancellationTokenSource? _export;

    public EditorView(CameraPlugin plugin, MediaItem item, Action close)
    {
        _p = plugin; _item = item; _close = close;
        _info = _p.Library.Info(item.Path) ?? new MediaInfo { Taken = new DateTimeOffset(item.Modified) };
        _edit = _info.Edit?.Clone() ?? new Edit();
        _saveSoon.Tick += (_, _) => { _saveSoon.Stop(); SaveEdit(); };
        _play.Tick += (_, _) => Step();

        Content = Stack(12, Card(new TextBlock { Text = "Opening " + item.Name + "…" }));
        AttachedToVisualTree += async (_, _) => await LoadAsync();
        DetachedFromVisualTree += (_, _) => { _play.Stop(); _export?.Cancel(); SaveEdit(); };
    }

    bool IsClip => _item.IsClip;

    async Task LoadAsync()
    {
        if (_photoPreview != null || _clip != null) return;
        try
        {
            if (!IsClip)
            {
                var full = await Task.Run(() => FrameFiles.Decode(File.ReadAllBytes(_item.Path)));
                _photoFull = full; _origW = full.W; _origH = full.H;
                int pw = Math.Min(PhotoPreviewWidth, full.W);
                _ps = pw / (double)full.W;
                _photoPreview = await Task.Run(() => FrameFiles.Resize(full, pw, Math.Max(1, (int)Math.Round(full.H * _ps))));
                Build();
                _status.Text = $"{full.W}x{full.H}" + (_info.Map is { } m ? $", taken in {m}" : "") + ". Click the picture to place text or draw; the original file is never changed.";
                RequestRender();
                RenderLookThumbs();
            }
            else
            {
                _clip = await ClipSource.OpenAsync(_item.Path, _p.FfmpegPath);
                _origW = _clip.W; _origH = _clip.H;
                _timeline = _p.Library.Overlays(_item.Path);
                int pw = Math.Min(ClipPreviewWidth, _clip.W), ph = Math.Max(1, (int)Math.Round(_clip.H * pw / (double)_clip.W));
                _ps = pw / (double)_clip.W;
                _previewFps = Math.Max(1, Math.Min(Math.Min(_clip.Fps, 15), MaxPreviewFrames / Math.Max(0.1, _clip.Seconds)));
                _from = 0; _to = _clip.Seconds;
                Build();
                _status.Text = "Loading the clip…";
                bool first = true;
                await foreach (var (ms, f) in _clip.ReadAsync(0, _clip.Seconds, _previewFps, pw, ph))
                {
                    var jpeg = await Task.Run(() => FrameFiles.EncodeJpeg(f, 85, fullColor: false));
                    _clipFrames.Add((ms, jpeg));
                    if (first) { first = false; RequestRender(); RenderLookThumbs(); }
                    if (_clipFrames.Count % 20 == 0) _status.Text = $"Loading the clip… {ms / 1000:0} of {_clip.Seconds:0} s";
                    if (!IsAttached) return;
                }
                if (_clipFrames.Count == 0) throw new InvalidDataException("No frames could be read from the clip.");
                _to = Math.Min(_to, _clipFrames[^1].Ms / 1000 + 1 / _previewFps);
                if (_trimTo != null) { _trimTo.Maximum = _to; _trimTo.Value = _to; }
                if (_trimFrom != null) _trimFrom.Maximum = _to;
                if (_position != null) _position.Maximum = _clipFrames.Count - 1;
                _status.Text = $"{_clip.W}x{_clip.H}, {_clip.Seconds:0.0} s. Preview at {_previewFps:0} per second.";
            }
        }
        catch (Exception e)
        {
            _p.Ctx.Log("editor failed to open " + _item.Path + ": " + e);
            Content = Card(Stack(10, Text("Couldn't open " + _item.Name + ": " + e.Message, ThemeTokens.Danger), Button("Back", _close)));
        }
    }

    bool IsAttached => this.GetVisualRoot() != null;

    // ---- layout ----

    void Build()
    {
        var back = Button("← Back to the gallery", _close);
        var header = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(back, Dock.Left);
        header.Children.Add(back);
        var name = Title(_item.Name);
        name.Margin = new Thickness(12, 4, 0, 0);
        header.Children.Add(name);

        var surface = new Grid { HorizontalAlignment = HorizontalAlignment.Center, Children = { _image, _marks } };
        _marks.PointerPressed += OnPress;
        _marks.PointerMoved += OnMove;
        _marks.PointerReleased += OnRelease;
        var stage = new Border { Background = new SolidColorBrush(Color.FromRgb(12, 12, 16)), CornerRadius = new CornerRadius(8), Padding = new Thickness(8), MinHeight = 200, Child = surface };
        var left = Stack(10, stage, _status);
        if (IsClip) left.Children.Add(BuildTransport());

        _tabs.Items.Add(new TabItem { Header = "Looks", Content = BuildLooks() });
        _tabs.Items.Add(new TabItem { Header = "Adjust", Content = BuildAdjust() });
        _tabs.Items.Add(new TabItem { Header = "Frame", Content = BuildFrames() });
        _tabs.Items.Add(new TabItem { Header = "Text", Content = BuildText() });
        _tabs.Items.Add(new TabItem { Header = "Draw", Content = BuildDraw() });
        _tabs.Items.Add(new TabItem { Header = "Crop", Content = BuildCrop() });
        _tabs.Items.Add(new TabItem { Header = "More", Content = BuildMore() });
        _tabs.SelectionChanged += (_, _) =>
        {
            // the Draw and Crop tabs switch their tool on; leaving them goes back to moving text
            var header = (_tabs.SelectedItem as TabItem)?.Header as string;
            SetTool(header == "Draw" ? Tool.Draw : header == "Crop" ? Tool.Crop : _tool == Tool.Text ? Tool.Text : Tool.Move);
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,360") };
        Grid.SetColumn(left, 0);
        var tools = Card(_tabs, 10);
        Grid.SetColumn(tools, 2);
        grid.Children.Add(left);
        grid.Children.Add(tools);

        Content = Stack(12, header, grid, Card(IsClip ? BuildClipExport() : BuildPhotoExport()));
        SyncAll();
    }

    Control BuildLooks()
    {
        var wrap = new WrapPanel();
        foreach (var look in Enum.GetValues<Look>())
        {
            var thumb = new Image { Width = 96, Height = 56, Stretch = Stretch.UniformToFill };
            var b = new Button
            {
                Padding = new Thickness(4), Margin = new Thickness(0, 0, 6, 6),
                Content = Stack(4, new Border { Child = thumb, CornerRadius = new CornerRadius(4), ClipToBounds = true, Background = Brushes.Black }, new TextBlock { Text = Edit.LookName(look), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center }),
            };
            var l = look;
            b.Click += (_, _) => { _edit.Look = l; Changed(); };
            _lookButtons.Add((look, b, thumb));
            wrap.Children.Add(b);
        }
        var strength = Slider(0, 1, () => _edit.Strength, v => _edit.Strength = v);
        _syncers.Add(() => { foreach (var (l, b, _) in _lookButtons) b.Classes.Set("accent", l == _edit.Look); });
        return Scroll(Stack(10, wrap, Labeled("Strength", strength)));
    }

    Control BuildAdjust() => Scroll(Stack(10,
        Labeled("Brightness", Slider(-1, 1, () => _edit.Brightness, v => _edit.Brightness = v)),
        Labeled("Contrast", Slider(-1, 1, () => _edit.Contrast, v => _edit.Contrast = v)),
        Labeled("Saturation", Slider(-1, 1, () => _edit.Saturation, v => _edit.Saturation = v)),
        Labeled("Warmth", Slider(-1, 1, () => _edit.Warmth, v => _edit.Warmth = v)),
        Labeled("Vignette", Slider(0, 1, () => _edit.Vignette, v => _edit.Vignette = v)),
        Button("Reset adjustments", () => { _edit.Brightness = _edit.Contrast = _edit.Saturation = _edit.Warmth = _edit.Vignette = 0; Changed(); SyncAll(); })));

    Control BuildFrames()
    {
        var wrap = new WrapPanel();
        foreach (var style in Enum.GetValues<FrameStyle>())
        {
            var b = new Button { Content = style.ToString(), Margin = new Thickness(0, 0, 6, 6), MinWidth = 90 };
            var s = style;
            b.Click += (_, _) =>
            {
                if (_edit.Frame != s && Frames.TakesColor(s)) _edit.FrameColor = Frames.DefaultColor(s);
                if (Frames.TakesCaption(s) && string.IsNullOrWhiteSpace(_edit.Caption) && _info.Map is { } map) _edit.Caption = map;
                _edit.Frame = s; Changed(); SyncAll();
            };
            _frameButtons.Add((style, b));
            wrap.Children.Add(b);
        }
        var swatches = Swatch(c => { _edit.FrameColor = c; Changed(); });
        _frameColorRow = Stack(4, Muted("Frame color"), swatches);
        _caption = new TextBox { Watermark = "A caption (the map name to start with)" };
        _caption.TextChanged += (_, _) => { if (_caption.Text != _edit.Caption) { _edit.Caption = _caption.Text ?? ""; Changed(); } };
        _captionRow = Stack(4, Muted("Caption"), _caption);
        _syncers.Add(() =>
        {
            foreach (var (s, b) in _frameButtons) b.Classes.Set("accent", s == _edit.Frame);
            _frameColorRow.IsVisible = Frames.TakesColor(_edit.Frame);
            _captionRow.IsVisible = Frames.TakesCaption(_edit.Frame);
            if (_caption.Text != _edit.Caption) _caption.Text = _edit.Caption;
        });
        return Scroll(Stack(10, wrap, _frameColorRow, _captionRow));
    }

    Control BuildText()
    {
        _textHint = Muted("");
        _textBox = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60 };
        _textBox.TextChanged += (_, _) => { if (Selected is { } t && t.Text != _textBox.Text) { t.Text = _textBox.Text ?? ""; Changed(); } };
        _textStyleBox = new ComboBox { Width = 160 };
        foreach (var s in Enum.GetValues<TextStyle>()) _textStyleBox.Items.Add(new ComboBoxItem { Content = StyleName(s), Tag = s });
        _textStyleBox.SelectedIndex = (int)_textStyle; // the style new text gets
        _textStyleBox.SelectionChanged += (_, _) =>
        {
            if (_textStyleBox.SelectedItem is not ComboBoxItem { Tag: TextStyle s }) return;
            _textStyle = s;
            if (Selected is { } t && t.Style != s) { t.Style = s; Changed(); }
        };
        _textSize = new Slider { Minimum = 0.02, Maximum = 0.3, Width = 200 };
        _textSize.ValueChanged += (_, _) => { if (Selected is { } t && Math.Abs(t.Size - _textSize.Value * _origH) > 0.5) { t.Size = _textSize.Value * _origH; Changed(); } };
        var colors = Swatch(c => { _textColor = c; if (Selected is { } t) { t.Color = c; Changed(); } });
        var delete = Button("Delete this text", () => { if (_selected >= 0) { _edit.Texts.RemoveAt(_selected); Select(-1); Changed(); } });
        _textEditor = Stack(8, _textBox, Labeled("Size", _textSize), delete);
        var add = Button("Add text", () => { SetTool(Tool.Text); _textHint.Text = "Click the picture where the text should go."; }, accent: true);
        _syncers.Add(SyncText);
        return Scroll(Stack(10, add, _textHint, Stack(4, Muted("Style"), _textStyleBox), Stack(4, Muted("Color"), colors), _textEditor,
            Muted("Drag text on the picture to move it. Click a text to pick it.")));
    }

    Control BuildDraw()
    {
        var colors = Swatch(c => _penColor = c);
        var width = new Slider { Minimum = 0.002, Maximum = 0.04, Value = _penWidth, Width = 200 };
        width.ValueChanged += (_, _) => _penWidth = width.Value;
        return Scroll(Stack(10, Muted("Draw on the picture with the mouse while this tab is open."),
            Stack(4, Muted("Color"), colors), Labeled("Width", width),
            Wrap(Button("Undo last line", () => { if (_edit.Strokes.Count > 0) { _edit.Strokes.RemoveAt(_edit.Strokes.Count - 1); Changed(); } }),
                Button("Clear drawing", () => { _edit.Strokes.Clear(); Changed(); }))));
    }

    Control BuildCrop()
    {
        var aspects = new (string Name, double? Ratio)[] { ("Free", null), ("Square", 1), ("4:3", 4 / 3.0), ("16:9", 16 / 9.0), ("Phone 9:16", 9 / 16.0) };
        var row = new WrapPanel();
        var buttons = new List<(double?, Button)>();
        foreach (var (n, r) in aspects)
        {
            var b = new Button { Content = n, Margin = new Thickness(0, 0, 6, 6) };
            b.Click += (_, _) =>
            {
                _aspect = r;
                foreach (var (ar, bb) in buttons) bb.Classes.Set("accent", ar == r);
                if (r is { } ratio) { _edit.Crop = CenteredCrop(ratio); Changed(); }
            };
            buttons.Add((r, b));
            row.Children.Add(b);
        }
        buttons[0].Item2.Classes.Add("accent");
        return Scroll(Stack(10, Muted("Drag on the picture to pick the part to keep. The whole picture shows while this tab is open."), row,
            Button("Use the whole picture", () => { _edit.Crop = null; Changed(); })));
    }

    CropBox CenteredCrop(double ratio)
    {
        int w = _origW, h = (int)Math.Round(w / ratio);
        if (h > _origH) { h = _origH; w = (int)Math.Round(h * ratio); }
        return new CropBox((_origW - w) / 2, (_origH - h) / 2, w, h);
    }

    Control BuildMore()
    {
        var stamp = new CheckBox { Content = "Date stamp in the corner, like a film camera" };
        stamp.IsCheckedChanged += (_, _) => { if (_edit.Stamp != (stamp.IsChecked == true)) { _edit.Stamp = stamp.IsChecked == true; Changed(); } };
        int overlayCount = IsClip ? _timeline.Count : _info.Overlays?.Count ?? 0;
        var overlays = new CheckBox
        {
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = overlayCount > 0
                ? "Show what other plugins drew over the game at the time (respawn timers, …)"
                : "Show other plugins' overlay items (none were showing when this was taken)" },
            IsEnabled = overlayCount > 0,
        };
        overlays.IsCheckedChanged += (_, _) => { if (_edit.Overlays != (overlays.IsChecked == true)) { _edit.Overlays = overlays.IsChecked == true; Changed(); } };
        _syncers.Add(() => { stamp.IsChecked = _edit.Stamp; overlays.IsChecked = _edit.Overlays; });
        return Scroll(Stack(12, stamp, overlays,
            Button("Reset everything", () => { _edit = new Edit(); Select(-1); Changed(); SyncAll(); })));
    }

    Control BuildPhotoExport()
    {
        var png = Button("Save as PNG", () => _ = SavePhotoAsync(".png"), accent: true);
        var jpg = Button("Save as JPEG", () => _ = SavePhotoAsync(".jpg"));
        var copy = Button("Copy", () => _ = CopyPhotoAsync());
        return Stack(8, Title("Save"), Wrap(png, jpg, copy),
            Muted("Saves a new file next to the original (\"… (edited)\"); the original stays as it was. PNG is lossless; JPEG (quality 95, full color) is smaller."));
    }

    Control BuildTransport()
    {
        _playButton = Button("Play", TogglePlay);
        _position = new Slider { Minimum = 0, Maximum = 1, Width = 360, VerticalAlignment = VerticalAlignment.Center };
        _position.ValueChanged += (_, _) => { int i = (int)Math.Round(_position.Value); if (i != _pos && i < _clipFrames.Count) { _pos = i; RequestRender(); ShowTime(); } };
        _timeText = Muted("0:00");
        _trimFrom = new Slider { Minimum = 0, Maximum = Math.Max(0.1, _to), Value = 0, Width = 220 };
        _trimTo = new Slider { Minimum = 0, Maximum = Math.Max(0.1, _to), Value = _to, Width = 220 };
        _trimFrom.ValueChanged += (_, _) => { _from = Math.Min(_trimFrom.Value, _to - 0.1); JumpTo(_from); ShowTime(); };
        _trimTo.ValueChanged += (_, _) => { _to = Math.Max(_trimTo.Value, _from + 0.1); JumpTo(_to - 0.01); ShowTime(); };
        var frameButton = Button("Save this frame as a photo", () => _ = SaveFrameAsync());
        return Stack(8,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _playButton, _position, _timeText } },
            Wrap(Labeled("Start", _trimFrom), Labeled("End", _trimTo)),
            frameButton);
    }

    Control BuildClipExport()
    {
        bool hasFfmpeg = _p.FfmpegPath != null;
        var format = new ComboBox { Width = 200 };
        format.Items.Add(new ComboBoxItem { Content = hasFfmpeg ? "MP4 video" : "MP4 video (needs ffmpeg)", Tag = ClipFormat.Mp4, IsEnabled = hasFfmpeg });
        format.Items.Add(new ComboBoxItem { Content = "GIF (loops, 256 colors)", Tag = ClipFormat.Gif });
        format.SelectedIndex = hasFfmpeg ? 0 : 1;
        var size = new ComboBox { Width = 160 };
        foreach (var (label, w) in new[] { ("Full size", 0), ("1280 wide", 1280), ("960 wide", 960), ("720 wide", 720), ("480 wide", 480) })
            size.Items.Add(new ComboBoxItem { Content = label, Tag = w });
        var fps = new ComboBox { Width = 140 };
        foreach (var f in new[] { 10.0, 15, 20, 24, 30, 60 }) fps.Items.Add(new ComboBoxItem { Content = $"{f:0} per second", Tag = f });
        void Defaults()
        {
            bool gif = (format.SelectedItem as ComboBoxItem)?.Tag is ClipFormat.Gif;
            size.SelectedIndex = gif ? 3 : 0;
            double want = gif ? 15 : Math.Min(60, Math.Max(10, Math.Round(_clip?.Fps ?? 30)));
            int best = 0; double bestD = double.MaxValue;
            for (int i = 0; i < fps.Items.Count; i++) { double d = Math.Abs((double)((ComboBoxItem)fps.Items[i]!).Tag! - want); if (d < bestD) { bestD = d; best = i; } }
            fps.SelectedIndex = best;
        }
        Defaults();
        format.SelectionChanged += (_, _) => Defaults();
        _progress = new ProgressBar { Minimum = 0, Maximum = 1, Width = 260, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        var note = Muted("");
        var cancel = Button("Cancel", () => _export?.Cancel());
        cancel.IsVisible = false;
        Button? go = null;
        go = Button("Export", async () =>
        {
            if (_clip == null || _export != null) return;
            var fmt = (ClipFormat)((ComboBoxItem)format.SelectedItem!).Tag!;
            var opts = new ClipExport(fmt, _from, _to, (double)((ComboBoxItem)fps.SelectedItem!).Tag!, (int)((ComboBoxItem)size.SelectedItem!).Tag!, _p.Settings.Quality);
            var path = _p.Library.EditedPath(_item.Path, fmt == ClipFormat.Gif ? ".gif" : ".mp4");
            _export = new CancellationTokenSource();
            go!.IsEnabled = false; cancel.IsVisible = true; _progress.IsVisible = true; _progress.Value = 0;
            note.Text = "Exporting…";
            var edit = _edit.Clone();
            var progress = new Progress<double>(v => _progress.Value = v);
            try
            {
                var started = DateTime.Now;
                await Task.Run(() => Exporter.ExportClipAsync(_clip, _info, _timeline, edit, opts, path, _p.FfmpegPath, progress, _export.Token));
                var mb = new FileInfo(path).Length / 1048576.0;
                note.Text = $"Saved {Path.GetFileName(path)} ({mb:0.0} MB, {(DateTime.Now - started).TotalSeconds:0} s).";
                if (_last != null) _p.WriteThumb(path, _last.Picture);
                _p.Ctx.Notify("Exported " + Path.GetFileName(path));
                _p.AnnounceSaved(path);
            }
            catch (OperationCanceledException) { note.Text = "Export cancelled."; }
            catch (Exception e) { note.Text = "Export failed: " + e.Message; _p.Ctx.Log("export failed: " + e); }
            finally
            {
                _export.Dispose(); _export = null;
                go.IsEnabled = true; cancel.IsVisible = false; _progress.IsVisible = false;
            }
        }, accent: true);
        return Stack(8, Title("Export"),
            Wrap(Stack(4, Muted("Format"), format), Stack(4, Muted("Size"), size), Stack(4, Muted("Smoothness"), fps)),
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { go, cancel, _progress } }, note,
            Muted(hasFfmpeg
                ? $"MP4 uses the quality from the Camera settings ({CameraPanel.QualityName(_p.Settings.Quality)}). GIFs are small and loop, with 256 colors per frame."
                : "Without ffmpeg, clips export as GIF. " + Ffmpeg.InstallHint));
    }

    // ---- helpers for building ----

    static ScrollViewer Scroll(Control c) => new() { Content = c, MaxHeight = 520, Padding = new Thickness(0, 8, 8, 0) };

    static Control Labeled(string label, Control c) => Stack(2, Muted(label), c);

    Slider Slider(double min, double max, Func<double> get, Action<double> set)
    {
        var s = new Slider { Minimum = min, Maximum = max, Value = get(), Width = 260 };
        s.ValueChanged += (_, _) => { if (Math.Abs(get() - s.Value) > 1e-6) { set(s.Value); Changed(); } };
        s.DoubleTapped += (_, _) => { set(min < 0 ? 0 : max); s.Value = get(); Changed(); }; // double-click resets
        _syncers.Add(() => s.Value = get());
        return s;
    }

    static StackPanel Swatch(Action<string> pick)
    {
        var row = new WrapPanel();
        foreach (var hex in Swatches)
        {
            var b = new Button { Width = 26, Height = 26, Padding = new Thickness(0), Margin = new Thickness(0, 0, 5, 5), Background = new SolidColorBrush(Color.Parse(hex)), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray };
            ToolTip.SetTip(b, hex);
            var h = hex;
            b.Click += (_, _) => pick(h);
            row.Children.Add(b);
        }
        return Stack(0, row);
    }

    static string StyleName(TextStyle s) => s switch
    {
        TextStyle.Outline => "Bold outline",
        TextStyle.Pill => "Label (like the overlay)",
        TextStyle.Shadow => "Soft shadow",
        _ => "Speech bubble",
    };

    // ---- edit state ----

    TextMark? Selected => _selected >= 0 && _selected < _edit.Texts.Count ? _edit.Texts[_selected] : null;

    void Select(int i)
    {
        _selected = i;
        SyncText();
        DrawMarks();
    }

    void SyncText()
    {
        if (_textEditor == null) return;
        var t = Selected;
        _textEditor.IsVisible = t != null;
        if (t == null) { if (_tool != Tool.Text) _textHint.Text = _edit.Texts.Count > 0 ? "Click a text on the picture to change it." : ""; return; }
        if (_textBox.Text != t.Text) _textBox.Text = t.Text;
        _textStyleBox.SelectedIndex = (int)t.Style;
        _textSize.Value = Math.Clamp(t.Size / Math.Max(1, _origH), _textSize.Minimum, _textSize.Maximum);
        _textHint.Text = "";
    }

    void SyncAll() { foreach (var s in _syncers) s(); }

    void Changed()
    {
        SyncAll();
        RequestRender();
        _saveSoon.Stop(); _saveSoon.Start();
    }

    void SaveEdit()
    {
        _info.Edit = _edit.IsPlain ? null : _edit.Clone();
        if (_info.W == 0) { _info.W = _origW; _info.H = _origH; }
        _p.Library.SaveInfo(_item.Path, _info);
    }

    void SetTool(Tool t)
    {
        _tool = t;
        _marks.Cursor = new Cursor(t switch { Tool.Draw or Tool.Crop => StandardCursorType.Cross, Tool.Text => StandardCursorType.Ibeam, _ => StandardCursorType.Arrow });
        RequestRender(); // the crop tool shows the whole picture
    }

    // ---- rendering ----

    void RequestRender()
    {
        _wanted++;
        if (!_rendering) _ = RenderLoopAsync();
    }

    Func<RenderSource>? SourceNow()
    {
        if (_photoPreview is { } photo)
            return () => new RenderSource(photo, _info.Taken, _info.Overlays, _info.GameScale, _ps);
        if (_clipFrames.Count == 0) return null;
        var (ms, jpeg) = _clipFrames[Math.Clamp(_pos, 0, _clipFrames.Count - 1)];
        var items = OverlaysAt(ms);
        var taken = _info.Taken.AddMilliseconds(ms);
        return () => new RenderSource(FrameFiles.Decode(jpeg), taken, items, _info.GameScale, _ps);
    }

    IReadOnlyList<OverlayItem>? OverlaysAt(double ms)
    {
        OverlayAt? found = null;
        foreach (var o in _timeline) { if (o.Ms <= ms) found = o; else break; }
        return found?.Items;
    }

    async Task RenderLoopAsync()
    {
        _rendering = true;
        try
        {
            while (_done != _wanted)
            {
                int v = _wanted;
                var source = SourceNow();
                if (source == null) { _done = v; break; }
                var edit = _edit.Clone();
                bool whole = _tool == Tool.Crop;
                var result = await Task.Run(() => EditRenderer.Render(source(), edit, _ps, ignoreCrop: whole));
                _done = v;
                Show(result);
            }
        }
        catch (Exception e) { _status.Text = "Preview failed: " + e.Message; _p.Ctx.Log("preview failed: " + e); }
        finally { _rendering = false; }
    }

    void Show(RenderResult r)
    {
        _last = r;
        _bmp = ToBitmap(r.Picture, _bmp);
        _image.Source = _bmp;
        _image.InvalidateVisual();
        DrawMarks();
    }

    void RenderLookThumbs()
    {
        var source = SourceNow();
        if (source == null) return;
        _ = Task.Run(() =>
        {
            var src = source();
            int tw = 192, th = Math.Max(1, src.Picture.H * tw / src.Picture.W);
            var small = FrameFiles.Resize(src.Picture, tw, th);
            var tiny = src with { Picture = small, PictureScale = src.PictureScale * tw / src.Picture.W, Overlays = null };
            foreach (var look in Enum.GetValues<Look>())
            {
                var pic = EditRenderer.Render(tiny, new Edit { Look = look }, tiny.PictureScale).Picture;
                var l = look;
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var (lk, _, img) in _lookButtons) if (lk == l) img.Source = ToBitmap(pic);
                });
            }
        });
    }

    /// <summary>Display pixels per rendered pixel.</summary>
    double ViewScale => _last == null || _image.Bounds.Width <= 0 ? 1 : _image.Bounds.Width / _last.Picture.W;

    (double X, double Y)? ToSource(Point p)
    {
        if (_last == null) return null;
        double vs = ViewScale;
        return _last.Layout.ToSource(p.X / vs, p.Y / vs);
    }

    Point ToView(double sx, double sy)
    {
        var (ox, oy) = _last!.Layout.ToOutput(sx, sy);
        return new Point(ox * ViewScale, oy * ViewScale);
    }

    void DrawMarks()
    {
        _marks.Children.Clear();
        if (_last == null) return;
        if (_tool != Tool.Crop && Selected != null && _selected < _last.TextBounds.Count)
        {
            var b = _last.TextBounds[_selected];
            AddRect(ToView(b.Left, b.Top), ToView(b.Right, b.Bottom), Colors.White);
        }
        if (_tool == Tool.Crop && _edit.Crop is { } c && _cropDrag == null)
            AddRect(ToView(c.X, c.Y), ToView(c.X + c.W, c.Y + c.H), Color.FromRgb(255, 170, 60));
        if (_cropDrag is { } d) AddRect(ToView(d.X, d.Y), ToView(d.X + d.W, d.Y + d.H), Color.FromRgb(255, 170, 60));
    }

    void AddRect(Point a, Point b, Color color)
    {
        var r = new Rectangle
        {
            Width = Math.Max(1, Math.Abs(b.X - a.X)), Height = Math.Max(1, Math.Abs(b.Y - a.Y)),
            Stroke = new SolidColorBrush(color), StrokeThickness = 1.5, StrokeDashArray = new AvaloniaList<double> { 4, 3 },
            Fill = new SolidColorBrush(Color.FromArgb(25, color.R, color.G, color.B)), IsHitTestVisible = false,
        };
        Canvas.SetLeft(r, Math.Min(a.X, b.X)); Canvas.SetTop(r, Math.Min(a.Y, b.Y));
        _marks.Children.Add(r);
    }

    // ---- pointer ----

    CropBox? _cropDrag;

    void OnPress(object? sender, PointerPressedEventArgs e)
    {
        if (ToSource(e.GetPosition(_image)) is not { } p) return;
        e.Pointer.Capture(_marks);
        switch (_tool)
        {
            case Tool.Text:
            {
                var t = new TextMark { Text = "Text", X = p.X, Y = p.Y, Size = Math.Max(12, _origH * 0.08), Color = _textColor, Style = _textStyle };
                _edit.Texts.Add(t);
                SetTool(Tool.Move);
                Select(_edit.Texts.Count - 1);
                Changed();
                _textBox.Focus();
                _textBox.SelectAll();
                break;
            }
            case Tool.Draw:
                _drawing = new Stroke { Color = _penColor, Width = Math.Max(1, _penWidth * _origH), Points = { p.X, p.Y } };
                _edit.Strokes.Add(_drawing);
                RequestRender();
                break;
            case Tool.Crop:
                _dragStart = p;
                _cropDrag = new CropBox((int)p.X, (int)p.Y, 1, 1);
                break;
            default:
            {
                int hit = -1;
                if (_last != null)
                    for (int i = _last.TextBounds.Count - 1; i >= 0; i--)
                        if (_last.TextBounds[i].Contains((float)p.X, (float)p.Y)) { hit = i; break; }
                Select(hit);
                if (hit >= 0) { _dragFrom = p; _dragStart = (_edit.Texts[hit].X, _edit.Texts[hit].Y); _tabs.SelectedIndex = 3; }
                break;
            }
        }
    }

    void OnMove(object? sender, PointerEventArgs e)
    {
        if (ToSource(e.GetPosition(_image)) is not { } p) return;
        if (_drawing != null)
        {
            var pts = _drawing.Points;
            double dx = p.X - pts[^2], dy = p.Y - pts[^1];
            if (dx * dx + dy * dy < 4) return;
            pts.Add(p.X); pts.Add(p.Y);
            RequestRender();
        }
        else if (_cropDrag != null)
        {
            double x0 = Math.Clamp(_dragStart.X, 0, _origW), y0 = Math.Clamp(_dragStart.Y, 0, _origH);
            double x1 = Math.Clamp(p.X, 0, _origW), y1 = Math.Clamp(p.Y, 0, _origH);
            double w = Math.Abs(x1 - x0), h = Math.Abs(y1 - y0);
            if (_aspect is { } r) { if (w / Math.Max(1, h) > r) w = h * r; else h = w / r; }
            double left = x1 < x0 ? x0 - w : x0, top = y1 < y0 ? y0 - h : y0;
            _cropDrag = new CropBox((int)left, (int)top, Math.Max(1, (int)w), Math.Max(1, (int)h));
            DrawMarks();
        }
        else if (_dragFrom is { } from && Selected is { } t)
        {
            t.X = _dragStart.X + p.X - from.X;
            t.Y = _dragStart.Y + p.Y - from.Y;
            RequestRender();
        }
    }

    void OnRelease(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        if (_drawing != null) { _drawing = null; Changed(); }
        if (_cropDrag is { } c)
        {
            _cropDrag = null;
            if (c.W >= 8 && c.H >= 8) _edit.Crop = EditRenderer.Clamp(c, _origW, _origH);
            Changed();
        }
        if (_dragFrom != null) { _dragFrom = null; Changed(); }
    }

    // ---- clip transport ----

    void TogglePlay()
    {
        if (_play.IsEnabled) { _play.Stop(); _playButton!.Content = "Play"; return; }
        if (_clipFrames.Count == 0) return;
        _play.Interval = TimeSpan.FromMilliseconds(1000 / _previewFps);
        _play.Start();
        _playButton!.Content = "Pause";
    }

    void Step()
    {
        if (_clipFrames.Count == 0) return;
        int next = _pos + 1;
        if (next >= _clipFrames.Count || _clipFrames[next].Ms / 1000 >= _to) next = IndexAt(_from);
        _pos = next;
        if (_position != null) _position.Value = _pos;
        RequestRender();
        ShowTime();
    }

    int IndexAt(double seconds)
    {
        int i = 0;
        while (i + 1 < _clipFrames.Count && _clipFrames[i + 1].Ms / 1000 <= seconds) i++;
        return i;
    }

    void JumpTo(double seconds)
    {
        if (_clipFrames.Count == 0) return;
        _pos = IndexAt(seconds);
        if (_position != null) _position.Value = _pos;
        RequestRender();
    }

    void ShowTime()
    {
        if (_timeText == null || _clipFrames.Count == 0) return;
        static string T(double s) => $"{(int)(s / 60)}:{s % 60:00.0}";
        _timeText.Text = $"{T(_clipFrames[_pos].Ms / 1000)}  ·  keeping {T(_from)}–{T(_to)} ({_to - _from:0.0} s)";
    }

    // ---- saving ----

    async Task SavePhotoAsync(string ext)
    {
        if (_photoFull == null) return;
        try
        {
            var path = _p.Library.EditedPath(_item.Path, ext);
            var edit = _edit.Clone();
            await Task.Run(() => Exporter.ExportPhoto(_photoFull, _info, edit, path));
            if (_last != null) _p.WriteThumb(path, _last.Picture);
            _status.Text = "Saved " + Path.GetFileName(path);
            _p.Ctx.Notify("Saved " + Path.GetFileName(path));
            _p.AnnounceSaved(path);
        }
        catch (Exception e) { _status.Text = "Couldn't save: " + e.Message; _p.Ctx.Log("photo save failed: " + e); }
    }

    async Task CopyPhotoAsync()
    {
        if (_photoFull == null) return;
        var edit = _edit.Clone();
        var pic = await Task.Run(() => EditRenderer.Render(Exporter.PhotoSource(_photoFull, _info), edit).Picture);
        _status.Text = await _p.Ctx.CopyImageAsync(pic) ? "Copied to the clipboard." : "Copying pictures isn't supported on this system yet: save it instead.";
    }

    async Task SaveFrameAsync()
    {
        if (_clip == null || _clipFrames.Count == 0) return;
        try
        {
            double t = _clipFrames[_pos].Ms / 1000;
            Frame? full = null;
            await foreach (var (_, f) in _clip.ReadAsync(t, t + 1.5 / _clip.Fps, _clip.Fps, _clip.W, _clip.H)) { full = f; break; }
            if (full == null) { _status.Text = "That frame couldn't be read."; return; }
            var taken = _info.Taken.AddSeconds(t);
            var path = _p.Library.NewPath(taken, ".png");
            await Task.Run(() => FrameFiles.SavePng(full, path));
            _p.Library.SaveInfo(path, new MediaInfo
            {
                Taken = taken, W = full.W, H = full.H, GameScale = _info.GameScale, Map = _info.Map,
                Overlays = OverlaysAt(t * 1000)?.ToList(), Edit = _edit.IsPlain ? null : _edit.Clone(),
            });
            _p.WriteThumb(path, full);
            _status.Text = "Saved the frame as " + Path.GetFileName(path) + " (in the gallery, ready to edit).";
            _p.AnnounceSaved(path);
        }
        catch (Exception e) { _status.Text = "Couldn't save the frame: " + e.Message; }
    }
}
