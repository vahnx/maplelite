using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace MapleLite.Host;

internal sealed class App : Application
{
    Shell? _shell;

    public override void Initialize()
    {
        Name = "MapleLite";
        Styles.Add(new FluentTheme());
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _shell = new Shell();
            _shell.Themes.Apply(Options.Theme ?? _shell.Settings.ThemeId);
            _shell.Plugins.LoadAll(_shell);
            var window = new MainWindow(_shell);
            desktop.MainWindow = window;
            desktop.ShutdownRequested += async (_, _) => { if (_shell != null) await _shell.DisposeAsync(); };
            base.OnFrameworkInitializationCompleted();
            _shell.Capture.Start();
            await _shell.Plugins.StartAllAsync();
            window.RefreshNav();
            if (Options.Page != null) window.Navigate(Options.Page);
            if (Options.SnapshotPath != null) window.SnapshotAndExit(Options.SnapshotPath, desktop);
            return;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
