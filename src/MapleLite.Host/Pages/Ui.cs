using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MapleLite.Sdk.Theming;

namespace MapleLite.Host.Pages;

/// <summary>Small builders so pages bind to theme tokens and restyle live when the theme changes.</summary>
internal static class Ui
{
    public static T Res<T>(this T control, AvaloniaProperty property, string token) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(ThemeTokens.Brush(token)));
        return control;
    }

    public static TextBlock Text(string text, string token = ThemeTokens.Text, double size = 13, FontWeight weight = FontWeight.Normal) =>
        new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap }.Res(TextBlock.ForegroundProperty, token);

    public static TextBlock Heading(string text) => Text(text, ThemeTokens.Text, 22, FontWeight.SemiBold);
    public static TextBlock Subheading(string text) => Text(text, ThemeTokens.Text, 15, FontWeight.SemiBold);
    public static TextBlock Muted(string text) => Text(text, ThemeTokens.TextMuted, 12.5);

    /// <summary>A rounded panel on the Surface color.</summary>
    public static Border Card(Control child, double padding = 16) =>
        new Border { Child = child, CornerRadius = new CornerRadius(10), Padding = new Thickness(padding), BorderThickness = new Thickness(1) }
            .Res(Border.BackgroundProperty, ThemeTokens.Surface).Res(Border.BorderBrushProperty, ThemeTokens.Border);

    public static StackPanel Stack(double spacing, params Control[] children)
    {
        var s = new StackPanel { Spacing = spacing };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var s = new StackPanel { Spacing = spacing, Orientation = Orientation.Horizontal };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static Button Button(string text, Action onClick)
    {
        var b = new Button { Content = text };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>A label/value grid of two columns.</summary>
    public static Grid Facts(out Func<string, TextBlock> add)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), RowSpacing = 6 };
        add = label =>
        {
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var l = Muted(label); Grid.SetRow(l, row);
            var v = Text("–"); Grid.SetRow(v, row); Grid.SetColumn(v, 1);
            grid.Children.Add(l); grid.Children.Add(v);
            return v;
        };
        return grid;
    }

    public static ScrollViewer Page(Control content) =>
        new ScrollViewer { Content = new Border { Padding = new Thickness(24), Child = content }, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
}
