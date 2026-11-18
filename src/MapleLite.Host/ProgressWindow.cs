using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MapleLite.Host.Pages;
using MapleLite.Sdk.Theming;

namespace MapleLite.Host;

/// <summary>
/// A small window with a progress bar, shown while MapleLite starts and while it closes. Each finished step can be
/// listed under the bar, so a slow or stuck step (a plugin that won't finish saving) is easy to spot.
/// </summary>
internal sealed class ProgressWindow : Window
{
    readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, Height = 8 };
    readonly TextBlock _percent = Ui.Text("0%", ThemeTokens.Accent, 13, FontWeight.SemiBold);
    readonly TextBlock _step = Ui.Text("", ThemeTokens.Text, 13);
    readonly TextBlock _elapsed = Ui.Muted("");
    readonly StackPanel _log = new() { Spacing = 3 };
    readonly Button _quit;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    DateTime _stepStarted = DateTime.Now;

    public ProgressWindow(string heading, string? quitText = null, Action? onQuit = null)
    {
        Title = heading;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://MapleLite/Assets/MapleLite.ico")));
        Width = 480; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        this.Res(BackgroundProperty, ThemeTokens.Background);
        _bar.Res(ProgressBar.ForegroundProperty, ThemeTokens.Accent);

        _quit = Ui.Button(quitText ?? "", () => onQuit?.Invoke());
        _quit.IsVisible = false;
        var stepRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        stepRow.Children.Add(_step);
        Grid.SetColumn(_elapsed, 1); stepRow.Children.Add(_elapsed);

        Content = new Border
        {
            Padding = new Thickness(24, 20),
            Child = Ui.Stack(12,
                Ui.Row(10, Ui.Text(heading, ThemeTokens.Text, 17, FontWeight.SemiBold), _percent),
                _bar, stepRow, _log, _quit),
        };
        _tick.Tick += (_, _) =>
        {
            var s = (DateTime.Now - _stepStarted).TotalSeconds;
            _elapsed.Text = s >= 1 ? $"{s:0} s" : "";
            if (s >= 3 && onQuit != null) _quit.IsVisible = true; // something is slow: offer a way out
        };
        Opened += (_, _) => _tick.Start();
        Closed += (_, _) => _tick.Stop();
    }

    /// <summary>Moves the bar to <paramref name="percent"/> and shows what's happening now.</summary>
    public void Report(double percent, string step)
    {
        percent = Math.Clamp(percent, 0, 100);
        _bar.Value = percent;
        _percent.Text = $"{percent:0}%";
        _step.Text = step;
        _stepStarted = DateTime.Now;
        _elapsed.Text = "";
    }

    /// <summary>Adds a line under the bar about a finished step.</summary>
    public void Note(string line, bool problem = false)
    {
        if (problem) Problems++;
        _log.Children.Add(Ui.Text(line, problem ? ThemeTokens.Danger : ThemeTokens.TextMuted, 12.5));
    }

    /// <summary>How many noted lines were problems.</summary>
    public int Problems { get; private set; }

    /// <summary>Lets the window draw before the next step runs on the UI thread.</summary>
    public static Task Yield() => Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask();
}
