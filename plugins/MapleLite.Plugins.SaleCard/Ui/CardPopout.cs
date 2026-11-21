using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using static MapleLite.Sdk.PanelKit;

namespace MapleLite.Plugins.SaleCard;

internal sealed class CardPopout : Window
{
    readonly SaleCardEngine _engine;
    readonly Canvas _surface = new();
    readonly Viewbox _view = new() { Stretch = Stretch.Uniform };
    readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly TextBlock _notice = Muted("");
    readonly DispatcherTimer _render = new() { Interval = TimeSpan.FromMilliseconds(180) };
    Bitmap? _bitmap;
    TextBox? _editing;
    bool _closed;

    public CardPopout(SaleCardEngine engine)
    {
        _engine = engine;
        Title = "MapleLite · Shop Card";
        Width = 900;
        Height = 650;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var zoom = new ToggleButton { Content = "Fit width" };
        var wide = new Viewbox { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch };
        _scroll.Content = wide;
        zoom.IsCheckedChanged += (_, _) =>
        {
            wide.Child = null;
            _view.Child = null;
            if (zoom.IsChecked == true) wide.Child = _surface;
            else _view.Child = _surface;
            _scroll.IsVisible = zoom.IsChecked == true;
            _view.IsVisible = !_scroll.IsVisible;
        };
        var close = Button("Close popout", Close);
        var bar = Wrap(zoom, close, _notice);
        bar.Margin = new Thickness(10, 6);
        DockPanel.SetDock(bar, Dock.Top);
        _view.Child = _surface;
        _scroll.IsVisible = false;
        Content = new DockPanel { Children = { bar, new Grid { Children = { _view, _scroll } } } };
        _render.Tick += (_, _) => { _render.Stop(); Redraw(); };
        _engine.CardChanged += OnCardChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            _engine.CardChanged -= OnCardChanged;
            _render.Stop();
            _surface.Children.Clear();
            _bitmap?.Dispose();
        };
        Redraw();
    }

    void OnCardChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_closed || _editing != null || _render.IsEnabled) return;
        _render.Start();
    });

    void Redraw()
    {
        if (_closed || _editing != null) return;
        var (items, options) = _engine.Card();
        var layout = new List<CardRenderer.TileLayout>();
        Bitmap bitmap;
        try { (_, bitmap) = CardRenderer.Render(items, options, layout); }
        catch (Exception ex)
        {
            _notice.Text = $"Couldn't refresh the card: {ex.Message}";
            _engine.Report(_notice.Text);
            return;
        }
        var old = _bitmap;
        _bitmap = bitmap;
        _surface.Width = bitmap.Size.Width;
        _surface.Height = bitmap.Size.Height;
        _surface.Children.Clear();
        _surface.Children.Add(new Image { Source = bitmap, Width = _surface.Width, Height = _surface.Height });
        old?.Dispose();

        foreach (var entry in layout)
        {
            var item = entry.Item;
            bool merged = item.MergedItems?.Count > 1;
            var remove = Button("−", () => _engine.RemoveCardTile(item));
            remove.Padding = new Thickness(0);
            remove.HorizontalContentAlignment = HorizontalAlignment.Center;
            var removeLabel = merged ? $"Remove all grouped stacks of {item.Name} from the card" : $"Remove {item.Name} from the card";
            ToolTip.SetTip(remove, removeLabel);
            AutomationProperties.SetName(remove, removeLabel);
            Place(remove, new Rect(entry.Tile.Right - 24, entry.Tile.Top + 2, 22, 22));

            var bounds = entry.Price ?? new Rect(entry.Tile.Right - 59, entry.Tile.Bottom - 24, 55, 21);
            var edit = Button(entry.Price == null ? "Price" : "", () => BeginEdit(item, entry.Tile, bounds));
            edit.Padding = new Thickness(0);
            if (entry.Price != null)
            {
                edit.Background = Brushes.Transparent;
                edit.BorderBrush = Brushes.Transparent;
                edit.Cursor = new Cursor(StandardCursorType.Ibeam);
            }
            string priceLabel = $"Edit base price for {item.Name}";
            ToolTip.SetTip(edit, priceLabel);
            AutomationProperties.SetName(edit, priceLabel);
            Place(edit, bounds);
        }
    }

    void Place(Control control, Rect bounds)
    {
        control.Width = bounds.Width;
        control.Height = bounds.Height;
        Canvas.SetLeft(control, bounds.X);
        Canvas.SetTop(control, bounds.Y);
        _surface.Children.Add(control);
    }

    void BeginEdit(SaleItem item, Rect tile, Rect price)
    {
        if (_editing != null) return;
        _notice.Text = "Base price in mesos · Enter saves · Esc cancels";
        var box = _editing = new TextBox { Text = item.Custom, Watermark = item.FinalPrice, Padding = new Thickness(4, 1), FontSize = 13 };
        AutomationProperties.SetName(box, $"Base price for {item.Name}");
        double width = Math.Min(tile.Width - 8, Math.Max(price.Width, 108));
        Place(box, new Rect(Math.Min(price.X, tile.Right - width - 4), price.Y, width, 28));

        void Finish(bool save)
        {
            if (_editing != box) return;
            _editing = null;
            if (save && !_engine.EditCardPrice(item, box.Text ?? ""))
                _notice.Text = "Price unchanged. Enter a positive price such as 250k, or leave it blank for the going rate.";
            else _notice.Text = "";
            _surface.Children.Remove(box);
            OnCardChanged();
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape)) return;
            Finish(e.Key == Key.Enter);
            e.Handled = true;
        };
        box.LostFocus += (_, _) => Finish(!_closed);
        box.Focus();
        box.SelectAll();
    }
}
