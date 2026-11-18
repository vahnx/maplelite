using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using MapleLite.Host.Themes;

namespace MapleLite.Host;

internal sealed class App : Application
{
    Shell? _shell;
    MainWindow? _window;
    IClassicDesktopStyleApplicationLifetime? _desktop;
    bool _closing, _closed;

    public override void Initialize()
    {
        Name = "MapleLite";
        Styles.Add(new FluentTheme());
        // DataGrid ships with the host for plugins; its styles must be loaded once, here.
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://MapleLite/"))
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml"),
        });
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }
        _desktop = desktop;
        // Windows come and go (startup window, main window, closing window): only an explicit Shutdown ends the app.
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        desktop.ShutdownRequested += (_, e) =>
        {
            if (_closed) return;
            e.Cancel = true; // the OS or a script asked to quit: save first
            _ = CloseAsync();
        };

        // Settings and the theme first (quick), so the startup window shows at once in the user's colors.
        var settings = HostSettings.Load();
        var themes = new ThemeService();
        themes.Apply(Options.Theme ?? settings.ThemeId);
        var splash = new ProgressWindow("Starting MapleLite");
        desktop.MainWindow = splash;
        splash.Show();
        base.OnFrameworkInitializationCompleted();

        var clock = Stopwatch.StartNew();
        var timings = new List<string>();
        async Task Step(double pct, string text, Func<Task> work)
        {
            splash.Report(pct, text);
            await ProgressWindow.Yield();
            var t = clock.Elapsed;
            await work();
            timings.Add($"{text.TrimEnd('…')} {(clock.Elapsed - t).TotalMilliseconds:0} ms");
        }

        try
        {
            await Step(5, "Setting up game capture and the text reader…", async () => _shell = await Task.Run(() => new Shell(settings, themes)));
            var shell = _shell!;
            shell.Overlay = new OverlayService(shell); // owns a UI timer and window: made here, on the UI thread
            await Step(25, "Finding plugins…", () => Task.Run(shell.Plugins.Discover));
            await Step(40, "Loading the item catalog…", () => Task.Run(() => _ = shell.Items));
            shell.Capture.Start();
            await shell.Plugins.StartEnabledAsync(async (p, i, n) =>
            {
                splash.Report(55 + 35.0 * i / n, $"Starting {p.Manifest.Name}… ({i + 1} of {n})");
                await ProgressWindow.Yield();
            });
            timings.Add($"plugins {clock.Elapsed.TotalMilliseconds:0} ms total so far");
            if (Options.InstallPath != null) shell.Notify((await shell.Plugins.InstallAsync(Options.InstallPath)).message);
            if (Options.UninstallId != null && shell.Plugins.Entries.FirstOrDefault(p => p.Manifest.Id == Options.UninstallId) is { } doomed) shell.Notify(await shell.Plugins.RemoveAsync(doomed));

            await Step(95, "Opening the window…", () =>
            {
                _window = new MainWindow(shell);
                _window.Closing += (_, e) =>
                {
                    if (_closed) return;
                    e.Cancel = true; // keep the app alive while plugins save
                    _ = CloseAsync();
                };
                desktop.MainWindow = _window;
                _window.RefreshNav();
                _window.Show();
                return Task.CompletedTask;
            });
            splash.Close();
            shell.Log($"started in {clock.Elapsed.TotalMilliseconds:0} ms: {string.Join(", ", timings)}");
            if (Options.Page != null) _window.Navigate(Options.Page);
            if (Options.SnapshotPath != null) _window.SnapshotAndExit(Options.SnapshotPath, () =>
            {
                if (Options.OverlaySnapshotPath != null) shell.Overlay.SaveComposite(Options.OverlaySnapshotPath);
                _ = CloseAsync();
            });
        }
        catch (Exception e)
        {
            _shell?.Log("startup failed: " + e);
            splash.Report(100, "MapleLite couldn't start: " + e.Message);
            splash.Note("Details are in maplelite.log. Close this window to quit.", true);
            splash.Closed += (_, _) => desktop.Shutdown(1);
        }
    }

    /// <summary>Hides the main window and closes everything with a progress window, then quits.</summary>
    async Task CloseAsync()
    {
        if (_closing || _desktop == null) return;
        _closing = true;
        var progress = new ProgressWindow("Closing MapleLite", "Quit now", Quit);
        _desktop.MainWindow = progress;
        progress.Show();
        _window?.Hide();
        // Last resort, in case something never returns: quit anyway after a minute.
        _ = Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(Quit));
        try
        {
            if (_shell != null)
                await _shell.CloseAsync(
                    async (pct, step) => { progress.Report(pct, step); await ProgressWindow.Yield(); },
                    (line, problem) => progress.Note(line, problem));
        }
        catch (Exception e) { _shell?.Log("closing failed: " + e); }
        if (progress.IsVisible && HadProblems(progress)) await Task.Delay(1500); // leave a skipped plugin on screen long enough to read
        Quit();
    }

    static bool HadProblems(ProgressWindow w) => w.Problems > 0;

    void Quit()
    {
        if (_closed) return;
        _closed = true;
        _desktop?.Shutdown();
    }
}
