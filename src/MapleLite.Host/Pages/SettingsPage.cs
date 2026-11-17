using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MapleLite.Sdk.Theming;

namespace MapleLite.Host.Pages;

internal sealed class SettingsPage : UserControl
{
    readonly Shell _shell;
    readonly ComboBox _themes = new() { MinWidth = 240 };
    readonly WrapPanel _swatches = new();
    readonly StackPanel _themeErrors = new() { Spacing = 4 };
    readonly StackPanel _pluginList = new() { Spacing = 8 };
    bool _busy;

    /// <summary>One row per plugin: name, version, author, description, an on/off switch, and Remove for added ones.</summary>
    void FillPlugins()
    {
        _pluginList.Children.Clear();
        foreach (var p in _shell.Plugins.Entries)
        {
            var m = p.Manifest;
            var toggle = new ToggleSwitch { IsChecked = _shell.Plugins.IsEnabled(p), OnContent = "On", OffContent = "Off", MinWidth = 90 };
            toggle.IsCheckedChanged += async (_, _) =>
            {
                if (_busy) return;
                _busy = true; toggle.IsEnabled = false;
                try
                {
                    bool on = toggle.IsChecked == true;
                    await _shell.Plugins.SetEnabledAsync(p, on);
                    _shell.Notify($"{m.Name} is {(on ? "on" : "off")}.");
                }
                finally { _busy = false; toggle.IsEnabled = true; }
            };
            var title = Ui.Row(8, Ui.Text(m.Name, ThemeTokens.Text, 14, FontWeight.SemiBold), Ui.Muted($"v{m.Version} · {m.Author}" + (p.Bundled ? " · comes with MapleLite" : " · added by you")));
            var info = Ui.Stack(2, title, Ui.Muted(m.Description));
            if (p.Error != null) info.Children.Add(Ui.Text("Problem: " + p.Error, ThemeTokens.Danger, 12));

            var right = Ui.Row(8, toggle);
            if (!p.Bundled) right.Children.Add(Ui.Button("Remove", async () => await RemovePlugin(p)));
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(info);
            Grid.SetColumn(right, 1); row.Children.Add(right);
            var box = new Border { Child = row, Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) }
                .Res(Border.BackgroundProperty, ThemeTokens.SurfaceAlt).Res(Border.BorderBrushProperty, ThemeTokens.Border);
            _pluginList.Children.Add(box);
        }
        foreach (var e in _shell.Plugins.Errors) _pluginList.Children.Add(Ui.Text(e, ThemeTokens.Danger));
    }

    async Task AddPlugin()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        bool trust = await Dialogs.ConfirmAsync(owner, "Add a plugin",
            "Plugins run inside MapleLite with the same access you have: they can read your chat (including whispers) and your files. " +
            "Only add plugins from people you trust. MapleLite will later offer reviewed plugins through a hub.",
            "I trust it, choose a file");
        if (!trust) return;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Choose a plugin (.zip or .dll)", AllowMultiple = false,
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("MapleLite plugin") { Patterns = ["*.zip", "*.dll"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        var (_, message) = await _shell.Plugins.InstallAsync(path);
        _shell.Notify(message);
        FillPlugins();
    }

    async Task RemovePlugin(MapleLite.Host.Plugins.PluginEntry p)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (!await Dialogs.ConfirmAsync(owner, $"Remove {p.Manifest.Name}?",
                $"This stops {p.Manifest.Name} and deletes its files. Its saved data stays in the data folder (plugins/{p.Manifest.Id}).", "Remove")) return;
        _shell.Notify(await _shell.Plugins.RemoveAsync(p));
    }

    public SettingsPage(Shell shell)
    {
        _shell = shell;
        _themes.SelectionChanged += (_, _) =>
        {
            if (_themes.SelectedItem is ComboBoxItem { Tag: ThemeDefinition t } && t.Id != _shell.Themes.Current.Id)
            {
                _shell.Themes.Apply(t);
                _shell.Settings.ThemeId = t.Id;
                _shell.Settings.Save();
            }
            DrawSwatches();
        };
        FillThemes();

        var themeCard = Ui.Card(Ui.Stack(12,
            Ui.Subheading("Theme"),
            Ui.Muted("Changes the MapleLite window and every plugin page. Add your own: copy a theme to a file, edit its colors, then reload."),
            Ui.Row(8, _themes,
                Ui.Button("Copy to a file to edit", () =>
                {
                    var path = _shell.Themes.ExportCopy(_shell.Themes.Current);
                    _shell.Notify("Theme copied to " + System.IO.Path.GetFileName(path));
                    Open(System.IO.Path.GetDirectoryName(path)!);
                }),
                Ui.Button("Open themes folder", () => Open(_shell.Themes.Folder)),
                Ui.Button("Reload themes", () => { _shell.Themes.Reload(); FillThemes(); })),
            _swatches,
            _themeErrors));

        var overlayToggle = new ToggleSwitch { IsChecked = false, IsEnabled = false, OnContent = "On", OffContent = "Off" };
        var overlayCard = Ui.Card(Ui.Stack(10,
            Ui.Subheading("Drawing on top of the game (overlay)"),
            Ui.Row(12, overlayToggle, Ui.Muted("Not available yet")),
            Ui.Muted("MapleLite shows everything in its own window. An overlay would be a separate, click-through window placed over the game, never drawn inside it. It stays off until Nexon's position on overlays is clear, and it can't work in exclusive fullscreen. See docs/OVERLAYS.md.")));

        var pluginCard = Ui.Card(Ui.Stack(12,
            Ui.Subheading("Plugins"),
            Ui.Muted("Switch plugins on or off; the change applies right away. Plugins get game pictures and the shared text reader. They never get input to the game or its memory."),
            _pluginList,
            Ui.Row(8,
                Ui.Button("Add plugin…", async () => await AddPlugin()),
                Ui.Button("Open data folder", () => Open(MapleLite.Core.AppPaths.Root)))));
        FillPlugins();
        _shell.Plugins.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(FillPlugins);

        Content = Ui.Page(Ui.Stack(18, Ui.Heading("Settings"), pluginCard, themeCard, overlayCard,
            Ui.Muted($"MapleLite {typeof(SettingsPage).Assembly.GetName().Version?.ToString(3)} · {_shell.Platform.Name} · plugin API {MapleLite.Sdk.SdkInfo.ApiVersion}")));
    }

    void FillThemes()
    {
        _themes.Items.Clear();
        foreach (var t in _shell.Themes.All)
        {
            var item = new ComboBoxItem { Content = t.BuiltIn ? t.Name : t.Name + " (yours)", Tag = t };
            _themes.Items.Add(item);
            if (t.Id == _shell.Themes.Current.Id) _themes.SelectedItem = item;
        }
        _themeErrors.Children.Clear();
        foreach (var e in _shell.Themes.LoadErrors) _themeErrors.Children.Add(Ui.Text(e, ThemeTokens.Danger));
        DrawSwatches();
    }

    void DrawSwatches()
    {
        _swatches.Children.Clear();
        var t = _shell.Themes.Current;
        foreach (var token in ThemeTokens.All)
        {
            var swatch = new Rectangle { Width = 28, Height = 28, RadiusX = 6, RadiusY = 6, Fill = new SolidColorBrush(Color.Parse(t.Color(token))), Stroke = Brushes.Gray, StrokeThickness = .5 };
            var cell = Ui.Stack(4, swatch, Ui.Muted(token));
            cell.Margin = new Thickness(0, 0, 14, 8);
            cell.Width = 78;
            _swatches.Children.Add(cell);
        }
    }

    static void Open(string folder)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", folder);
            else Process.Start("xdg-open", folder);
        }
        catch (Exception) { /* no file manager: nothing to do */ }
    }
}
