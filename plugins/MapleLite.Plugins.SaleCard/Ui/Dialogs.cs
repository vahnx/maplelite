using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MapleLite.Core.Imaging;
using MapleLite.Sdk;
using MapleLite.Sdk.Theming;
using static MapleLite.Sdk.PanelKit;

namespace MapleLite.Plugins.SaleCard;

/// <summary>Draw a box around your character on the newest game picture; letting go uses it. Escape cancels.</summary>
internal sealed class SnipWindow : Window
{
    readonly Frame _frame;
    readonly Canvas _canvas = new();
    readonly Rectangle _selection = new() { Stroke = Brushes.OrangeRed, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(55, 255, 120, 40)), IsVisible = false, IsHitTestVisible = false };
    readonly TextBlock _hint = Text("Drag a box around your character. Letting go of the mouse uses it. Escape cancels.");
    readonly CheckBox _removeBackground = new() { Content = "Remove plain background" };
    Point _start; bool _dragging;
    int[]? _result;

    SnipWindow(Frame frame)
    {
        _frame = frame;
        Title = "Draw around your character";
        Width = 1120; Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Themed(BackgroundProperty, ThemeTokens.Background);
        ToolTip.SetTip(_removeBackground, "Tick this before dragging when the box is around a portrait on a flat, single-colour backdrop (the Character Info or storage window). The backdrop is made see-through so only your character shows. Leave it unticked for a snip of the game world.");
        var bar = new DockPanel { Margin = new Thickness(10, 6) };
        DockPanel.SetDock(_removeBackground, Dock.Right);
        bar.Children.Add(_removeBackground);
        _hint.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(_hint);
        _canvas.Width = frame.W; _canvas.Height = frame.H;
        _canvas.Background = Brushes.Transparent;
        _canvas.Children.Add(new Image { Source = ToBitmap(frame), Width = frame.W, Height = frame.H, IsHitTestVisible = false });
        _canvas.Children.Add(_selection);
        _canvas.PointerPressed += Down; _canvas.PointerMoved += Move; _canvas.PointerReleased += Up;
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new ScrollViewer { Content = _canvas, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        Content = root;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    public static async Task<int[]?> SelectAsync(Window owner, Frame frame)
    {
        var w = new SnipWindow(frame);
        await w.ShowDialog(owner);
        return w._result;
    }

    void Down(object? s, PointerPressedEventArgs e)
    {
        _start = e.GetPosition(_canvas); _dragging = true; _selection.IsVisible = true;
        e.Pointer.Capture(_canvas);
        Track(e.GetPosition(_canvas));
    }

    void Move(object? s, PointerEventArgs e) { if (_dragging) Track(e.GetPosition(_canvas)); }

    void Track(Point p)
    {
        double x = Math.Min(_start.X, p.X), y = Math.Min(_start.Y, p.Y);
        Canvas.SetLeft(_selection, x); Canvas.SetTop(_selection, y);
        _selection.Width = Math.Abs(p.X - _start.X); _selection.Height = Math.Abs(p.Y - _start.Y);
    }

    void Up(object? s, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        Track(e.GetPosition(_canvas)); _dragging = false;
        e.Pointer.Capture(null);
        // Letting go finishes the snip. A box too small to be meant (a stray click) leaves the window open with a hint.
        if (TryCrop() is { } px) { _result = px; Close(); }
    }

    int[]? TryCrop()
    {
        double x = Canvas.GetLeft(_selection), y = Canvas.GetTop(_selection), w = _selection.Width, h = _selection.Height;
        if (double.IsNaN(x) || double.IsNaN(y) || w < 8 || h < 8) { _hint.Text = "Draw a larger box around the character first."; return null; }
        int x0 = Math.Clamp((int)Math.Round(x), 0, _frame.W - 1), y0 = Math.Clamp((int)Math.Round(y), 0, _frame.H - 1);
        int ww = Math.Clamp((int)Math.Round(w), 1, _frame.W - x0), hh = Math.Clamp((int)Math.Round(h), 1, _frame.H - y0);
        if (_removeBackground.IsChecked == true)
        {
            // Fill the left-over space with the box's own corner colour, so the backdrop and the filling are one area
            // and both are cleared. If the box's corners are not one colour, nothing is cleared: fall through.
            var cleared = AvatarReader.ClearSnipBackground(Fit(_frame, x0, y0, ww, hh, _frame.At(x0, y0) | Pixel.Black));
            if (cleared.Any(p => p == 0)) return cleared;
        }
        return Fit(_frame, x0, y0, ww, hh, 0);   // stored as shown, so a session reload brings back exactly this
    }

    /// <summary>The box fitted into the portrait's shape (nearest neighbour, centred); the left-over sides get <paramref name="around"/>.</summary>
    internal static int[] Fit(Frame f, int x0, int y0, int ww, int hh, int around)
    {
        const int W = AvatarReader.W, H = AvatarReader.H;
        var o = Enumerable.Repeat(around, W * H).ToArray();
        double scale = Math.Min((double)W / ww, (double)H / hh);
        int width = Math.Max(1, (int)Math.Round(ww * scale)), height = Math.Max(1, (int)Math.Round(hh * scale));
        int ox = (W - width) / 2, oy = (H - height) / 2;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int sx = Math.Min(ww - 1, (int)((x + 0.5) / scale)), sy = Math.Min(hh - 1, (int)((y + 0.5) / scale));
                o[(oy + y) * W + ox + x] = f.At(x0 + sx, y0 + sy) | Pixel.Black;
            }
        return o;
    }
}

/// <summary>
/// A warning before something that cannot be undone from inside the plugin: what will be cleared, what is kept, and
/// two plainly named buttons. Cancel is the default, so Enter or Escape never goes ahead by accident.
/// </summary>
internal sealed class ConfirmWindow : Window
{
    bool _go;

    ConfirmWindow(string question, string[] clears, string[] keeps, string goLabel)
    {
        Title = "Shop Card";
        Width = 470; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.Themed(BackgroundProperty, ThemeTokens.Surface);
        var body = Stack(6, Title(question));
        body.Margin = new Thickness(22, 18);
        void List(string head, string[] items, string token)
        {
            if (items.Length == 0) return;
            body.Children.Add(Text(head, token, 13, FontWeight.SemiBold));
            foreach (var i in items) body.Children.Add(Text("•  " + i));
        }
        List("This clears", clears, ThemeTokens.Danger);
        List("This keeps", keeps, ThemeTokens.Text);
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true, MinWidth = 90 };
        cancel.Click += (_, _) => Close();
        var go = new Button { Content = goLabel, Margin = new Thickness(10, 0, 0, 0) };
        go.Classes.Add("accent");
        go.Click += (_, _) => { _go = true; Close(); };
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { cancel, go } });
        Content = body;
        Opened += (_, _) => cancel.Focus();
    }

    /// <summary>True only when the person pressed the go-ahead button.</summary>
    public static async Task<bool> AskAsync(Window owner, string question, string[] clears, string[] keeps, string goLabel)
    {
        var w = new ConfirmWindow(question, clears, keeps, goLabel);
        await w.ShowDialog(owner);
        return w._go;
    }
}

/// <summary>
/// Never asks for more width than it is given. A trimmed TextBlock still reports its whole text's width, and a
/// StackPanel lays a child out at least that wide, so one long name would push its row past the list's edge.
/// </summary>
internal sealed class FitWidth : Decorator
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var s = base.MeasureOverride(availableSize);
        return double.IsInfinity(availableSize.Width) ? s : new Size(Math.Min(s.Width, availableSize.Width), s.Height);
    }
}
