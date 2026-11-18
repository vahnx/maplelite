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

    public static WrapPanel Wrap(params Control[] children)
    {
        var w = new WrapPanel();
        foreach (var c in children) { c.Margin = new Thickness(0, 0, 8, 8); w.Children.Add(c); }
        return w;
    }

    public static Button Button(string text, Action onClick, bool accent = false)
    {
        var b = new Button { Content = text };
        if (accent) b.Classes.Add("accent");
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>A bitmap of a frame, for showing game pictures in a panel. Call on the UI thread.</summary>
    public static Avalonia.Media.Imaging.WriteableBitmap ToBitmap(MapleLite.Core.Imaging.Frame f, Avalonia.Media.Imaging.WriteableBitmap? reuse = null)
    {
        var bmp = reuse != null && reuse.PixelSize.Width == f.W && reuse.PixelSize.Height == f.H ? reuse
            : new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(f.W, f.H), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
        using var fb = bmp.Lock();
        for (int y = 0; y < f.H; y++) System.Runtime.InteropServices.Marshal.Copy(f.Px, y * f.W, fb.Address + y * fb.RowBytes, f.W);
        return bmp;
    }

    /// <summary>Opens a web page in the user's default browser. Only http and https links are opened.</summary>
    public static bool OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)) return false;
        try
        {
            if (OperatingSystem.IsWindows()) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) System.Diagnostics.Process.Start("open", u.AbsoluteUri);
            else System.Diagnostics.Process.Start("xdg-open", u.AbsoluteUri);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Opens a folder in the OS file manager (Explorer, Finder, or the Linux default).</summary>
    public static bool OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            if (OperatingSystem.IsWindows()) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) System.Diagnostics.Process.Start("open", folder);
            else System.Diagnostics.Process.Start("xdg-open", folder);
            return true;
        }
        catch (Exception) { return false; }
    }
}
