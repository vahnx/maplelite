using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MapleLite.Sdk;
using MapleLite.Sdk.Theming;
using static MapleLite.Sdk.PanelKit;

namespace MapleLite.Plugins.SaleCard;

/// <summary>
/// The minimap as it will go on the card, inside the Design page: drag a box on it to keep only that part, or use the
/// whole map. The crop is kept as shares of the map, so it still fits as the picture refreshes (your dot moves).
/// </summary>
internal sealed class MapCropper : UserControl
{
    const double BoxW = 420, BoxH = 240, MaxZoom = 3;

    readonly SaleCardEngine _engine;
    readonly Canvas _canvas = new() { Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left };
    readonly Image _image = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    readonly Rectangle _shade = new() { IsHitTestVisible = false };
    readonly Rectangle _box = new() { Stroke = Brushes.OrangeRed, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(40, 255, 120, 40)), IsHitTestVisible = false, IsVisible = false };
    readonly TextBlock _note = Muted("");
    readonly Button _whole;
    int[]? _shown;
    int _w, _h;
    double _zoom = 1;
    Point _start;
    bool _dragging;
    // the Seller location marker, drawn as on the card; its name tag can be dragged
    Control? _marker;
    Rect _tagBox;
    (double x, double y)? _dot;
    bool _movingTag;
    Vector _grab;
    readonly Button _resetTag;

    public MapCropper(SaleCardEngine engine)
    {
        _engine = engine;
        RenderOptions.SetBitmapInterpolationMode(_image, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        _canvas.Children.Add(_image);
        _canvas.Children.Add(_box);
        _canvas.PointerPressed += Down;
        _canvas.PointerMoved += Move;
        _canvas.PointerReleased += Up;
        _canvas.Cursor = new Cursor(StandardCursorType.Cross);
        _whole = Button("Use whole map", () => { _engine.S.MapCrop = null; _engine.SettingsChanged(); Refresh(); });
        ToolTip.SetTip(_whole, "Shows the whole map on the card again");
        _resetTag = Button("Reset tag position", () => { _engine.S.MarkerTag = null; _engine.SettingsChanged(); Refresh(); });
        ToolTip.SetTip(_resetTag, "Puts your name tag back where it goes by itself (the side of your dot with the most room)");
        var frame = Card(_canvas, 6);
        frame.HorizontalAlignment = HorizontalAlignment.Left;
        Content = Stack(6, frame, Wrap(_whole, _resetTag, _note));
        Refresh();
    }

    /// <summary>Shows the newest map and the current crop. Cheap when nothing changed.</summary>
    public void Refresh()
    {
        var m = _engine.MapTrimmed();
        if (m == null)
        {
            _shown = null;
            _canvas.Width = BoxW; _canvas.Height = 60;
            _image.Source = null;
            _box.IsVisible = false;
            _note.Text = "No map yet: open the minimap in the game (full or middle size) while watching the game.";
            _whole.IsEnabled = false;
            return;
        }
        var (px, w, h) = m.Value;
        if (!ReferenceEquals(px, _shown) || w != _w || h != _h)
        {
            _shown = px; _w = w; _h = h;
            _zoom = Math.Min(MaxZoom, Math.Min(BoxW / w, BoxH / h));
            _canvas.Width = w * _zoom; _canvas.Height = h * _zoom;
            _image.Width = _canvas.Width; _image.Height = _canvas.Height;
            _image.Source = CardRenderer.Bitmap(px, w, h);
        }
        _whole.IsEnabled = _engine.S.MapCrop != null;
        DrawMarker();
        if (_engine.S.MapCrop is { Length: 4 } c && !_dragging)
        {
            Place(c[0] * _canvas.Width, c[1] * _canvas.Height, c[2] * _canvas.Width, c[3] * _canvas.Height);
            _note.Text = $"Cropped to {Math.Round(c[2] * w)} x {Math.Round(c[3] * h)} of {w} x {h}. Drag again to change it.";
        }
        else if (!_dragging)
        {
            _box.IsVisible = false;
            _note.Text = $"Whole map ({w} x {h}). Drag a box on it to keep only that part.";
        }
    }

    /// <summary>The Seller location marker over the map, as the card will show it (colours, tag position).</summary>
    void DrawMarker()
    {
        if (_marker != null) { _canvas.Children.Remove(_marker); _marker = null; }
        var S = _engine.S;
        _dot = !S.NoSellerMarker && _shown != null ? MapleLite.Core.Game.MapReader.PlayerDot(_shown, _w, _h) : null;
        _resetTag.IsVisible = _dot != null;
        _resetTag.IsEnabled = S.MarkerTag != null;
        if (_dot is not { } d) { _tagBox = default; return; }
        var colours = new CardRenderer.MarkerColours(S.MarkerRing ?? "#E5332A", S.MarkerTagColour ?? "#E5332A", S.MarkerText ?? "#FFFFFF");
        var at = S.MarkerTag is { Length: 2 } t ? new Vector(t[0] * _zoom, t[1] * _zoom) : (Vector?)null;
        var (layer, box) = CardRenderer.Marker((d.x + 0.5) * _zoom, (d.y + 0.5) * _zoom, _canvas.Width, _canvas.Height, Math.Max(9, 4.5 * _zoom), S.Seller, colours, at);
        layer.IsHitTestVisible = false;
        _marker = layer; _tagBox = box;
        _canvas.Children.Add(layer);
    }

    void Place(double x, double y, double w, double h)
    {
        Canvas.SetLeft(_box, x); Canvas.SetTop(_box, y);
        _box.Width = Math.Max(1, w); _box.Height = Math.Max(1, h);
        _box.IsVisible = true;
    }

    Point Clamp(Point p) => new(Math.Clamp(p.X, 0, _canvas.Width), Math.Clamp(p.Y, 0, _canvas.Height));

    void Down(object? s, PointerPressedEventArgs e)
    {
        if (_shown == null) return;
        var p = e.GetPosition(_canvas);
        e.Pointer.Capture(_canvas);
        // on your name tag: move it; anywhere else: draw a crop box
        if (_dot != null && _tagBox.Inflate(3).Contains(p))
        {
            _movingTag = true;
            _grab = p - _tagBox.Center;
            return;
        }
        _start = Clamp(p);
        _dragging = true;
        Place(_start.X, _start.Y, 1, 1);
    }

    void Move(object? s, PointerEventArgs e)
    {
        var raw = e.GetPosition(_canvas);
        if (_movingTag && _dot is { } d)
        {
            // the tag's new centre, relative to your dot, in map pixels; the arrow is redrawn to stay attached
            var centre = raw - _grab;
            _engine.S.MarkerTag = [(centre.X - (d.x + 0.5) * _zoom) / _zoom, (centre.Y - (d.y + 0.5) * _zoom) / _zoom];
            DrawMarker();
            return;
        }
        if (!_dragging)
        {
            _canvas.Cursor = new Cursor(_dot != null && _tagBox.Inflate(3).Contains(raw) ? StandardCursorType.SizeAll : StandardCursorType.Cross);
            return;
        }
        var p = Clamp(raw);
        Place(Math.Min(_start.X, p.X), Math.Min(_start.Y, p.Y), Math.Abs(p.X - _start.X), Math.Abs(p.Y - _start.Y));
    }

    void Up(object? s, PointerReleasedEventArgs e)
    {
        if (_movingTag)
        {
            _movingTag = false;
            e.Pointer.Capture(null);
            _engine.SettingsChanged();   // the card follows
            Refresh();
            return;
        }
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        double x = Canvas.GetLeft(_box), y = Canvas.GetTop(_box), w = _box.Width, h = _box.Height;
        // a click or a sliver (under 8 map pixels) is not a crop: keep what was there
        if (w / _zoom < 8 || h / _zoom < 8) { Refresh(); return; }
        _engine.S.MapCrop = [x / _canvas.Width, y / _canvas.Height, w / _canvas.Width, h / _canvas.Height];
        _engine.SettingsChanged();
        Refresh();
    }
}
