using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using MapleLite.Sdk.Theming;

namespace MapleLite.Host.Pages;

internal sealed class SettingsPage : UserControl
{
    readonly Shell _shell;
    readonly ComboBox _themes = new() { MinWidth = 240 };
    readonly WrapPanel _swatches = new();
    readonly StackPanel _themeErrors = new() { Spacing = 4 };

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

        var pluginList = Ui.Stack(6, _shell.Plugins.Plugins.Select(p => (Control)Ui.Text($"{p.Plugin.Manifest.Name}  v{p.Plugin.Manifest.Version}  ({p.Plugin.Manifest.Id})")).ToArray());
        foreach (var e in _shell.Plugins.Errors) pluginList.Children.Add(Ui.Text(e, ThemeTokens.Danger));
        var pluginCard = Ui.Card(Ui.Stack(10,
            Ui.Subheading("Plugins"),
            Ui.Muted("Plugins get game pictures and the shared text reader. They never get input to the game or its memory."),
            pluginList,
            Ui.Row(8, Ui.Button("Open data folder", () => Open(MapleLite.Core.AppPaths.Root)))));

        Content = Ui.Page(Ui.Stack(18, Ui.Heading("Settings"), themeCard, overlayCard, pluginCard,
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
