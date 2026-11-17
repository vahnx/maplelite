using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using MapleLite.Core.Imaging;
using MapleLite.Sdk;
using MapleLite.Sdk.Theming;
using static MapleLite.Sdk.PanelKit;

namespace MapleLite.Plugins.ChatHistory;

/// <summary>Drag a rectangle over the chat message lines in a still of the game. Returns the area or null.</summary>
internal sealed class AreaPicker : Window
{
    readonly Canvas _canvas = new();
    readonly Rectangle _box = new() { StrokeThickness = 2 };
    readonly Frame _frame;
    Point _from;
    bool _drawing;
    Region? _selection;

    public AreaPicker(Frame frame, Region? current)
    {
        _frame = frame;
        Title = "Select the chat message lines";
        Width = Math.Min(frame.W + 40, 1250); Height = Math.Min(frame.H + 150, 880);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Themed(BackgroundProperty, ThemeTokens.Background);
        _box.Themed(Shape.StrokeProperty, ThemeTokens.Accent);
        _box.Fill = new SolidColorBrush(Color.FromArgb(50, 255, 140, 0));

        _canvas.Width = frame.W; _canvas.Height = frame.H;
        _canvas.Children.Add(new Image { Source = ToBitmap(frame), Width = frame.W, Height = frame.H });
        _canvas.Children.Add(_box);
        if (current is { } c) Show(c);

        _canvas.PointerPressed += (_, e) => { _from = Clamp(e.GetPosition(_canvas)); _drawing = true; e.Pointer.Capture(_canvas); };
        _canvas.PointerMoved += (_, e) =>
        {
            if (!_drawing) return;
            var to = Clamp(e.GetPosition(_canvas));
            Show(new Region((int)Math.Min(_from.X, to.X), (int)Math.Min(_from.Y, to.Y), (int)Math.Abs(to.X - _from.X), (int)Math.Abs(to.Y - _from.Y)));
        };
        _canvas.PointerReleased += (_, e) => { _drawing = false; e.Pointer.Capture(null); };

        var save = Button("Save area", () => { if (_selection is { Width: > 30, Height: > 15 } s) Close(s); }, accent: true);
        var cancel = Button("Cancel", () => Close(null));
        var tip = Muted("Drag over the message lines only: not the tabs or the typing box. A taller chat box catches more lines.");
        var bar = new DockPanel { Margin = new Thickness(12) };
        var buttons = Wrap(save, cancel);
        DockPanel.SetDock(buttons, Avalonia.Controls.Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(tip);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Avalonia.Controls.Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new Viewbox { Child = _canvas, Stretch = Stretch.Uniform, Margin = new Thickness(12, 0, 12, 12) });
        Content = root;
    }

    Point Clamp(Point p) => new(Math.Clamp(p.X, 0, _frame.W), Math.Clamp(p.Y, 0, _frame.H));

    void Show(Region r)
    {
        _selection = r;
        Canvas.SetLeft(_box, r.X); Canvas.SetTop(_box, r.Y);
        _box.Width = r.Width; _box.Height = r.Height;
    }
}
