using Avalonia.Controls;
using Avalonia.Threading;

namespace MapleLite.Sdk;

/// <summary>An optional lightweight view for the companion window. Must not start another reader.</summary>
public interface ICompanionPanel
{
    Control CreateCompanionPanel();
}

public static class CompanionPanel
{
    /// <summary>Refresh only while visible; a single wrapped text control keeps long logs bounded.</summary>
    public static Control Text(Func<string> read)
    {
        var text = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(12) };
        var scroll = new ScrollViewer { Content = text };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        void Refresh() { var next = read(); if (text.Text != next) text.Text = next; }
        timer.Tick += (_, _) => Refresh();
        scroll.AttachedToVisualTree += (_, _) => { Refresh(); timer.Start(); };
        scroll.DetachedFromVisualTree += (_, _) => timer.Stop();
        return scroll;
    }
}
