using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MapleLite.Sdk.Theming;

namespace MapleLite.Sdk;

/// <summary>Themed building blocks for plugin pages, so plugins match the host and follow theme changes.</summary>
public static class PanelKit
{
    public static T Themed<T>(this T control, AvaloniaProperty property, string token) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(ThemeTokens.Brush(token)));
        return control;
    }

    public static TextBlock Text(string text, string token = ThemeTokens.Text, double size = 13, FontWeight weight = FontWeight.Normal) =>
        new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap }.Themed(TextBlock.ForegroundProperty, token);

    public static TextBlock Muted(string text) => Text(text, ThemeTokens.TextMuted, 12.5);
    public static TextBlock Title(string text) => Text(text, ThemeTokens.Text, 15, FontWeight.SemiBold);
    public static TextBlock Big(string text) => Text(text, ThemeTokens.Accent, 26, FontWeight.Bold);

    public static Border Card(Control child, double padding = 16) =>
        new Border { Child = child, CornerRadius = new CornerRadius(10), Padding = new Thickness(padding), BorderThickness = new Thickness(1) }
            .Themed(Border.BackgroundProperty, ThemeTokens.Surface).Themed(Border.BorderBrushProperty, ThemeTokens.Border);

    public static StackPanel Stack(double spacing, params Control[] children)
    {
        var s = new StackPanel { Spacing = spacing };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }
}
