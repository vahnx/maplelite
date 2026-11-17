using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using MapleLite.Core;
using MapleLite.Sdk.Theming;

namespace MapleLite.Host.Themes;

/// <summary>
/// Applies themes to the whole app: publishes every token as "ml.{Token}" brushes/colors and maps them
/// onto the Fluent control keys, so stock buttons, inputs and lists follow the theme as well.
/// Built-in themes plus user JSON themes from the themes folder.
/// </summary>
internal sealed class ThemeService : IThemeService
{
    readonly List<ThemeDefinition> _all = new();
    public ThemeDefinition Current { get; private set; } = BuiltInThemes.Midnight;
    public IReadOnlyList<ThemeDefinition> All => _all;
    public IReadOnlyList<string> LoadErrors { get; private set; } = [];
    public event Action<ThemeDefinition>? Changed;
    public string Folder => AppPaths.Themes;

    public ThemeService() => Reload();

    public void Reload()
    {
        _all.Clear();
        _all.AddRange(BuiltInThemes.All);
        var (user, errors) = ThemeLoader.LoadFolder(Folder, BuiltInThemes.All);
        _all.AddRange(user);
        LoadErrors = errors;
    }

    public void Apply(string id) => Apply(_all.FirstOrDefault(t => t.Id == id) ?? BuiltInThemes.Midnight);

    public void Apply(ThemeDefinition theme)
    {
        var app = Application.Current ?? throw new InvalidOperationException("The app isn't running.");
        var res = app.Resources;
        Color C(string token) => Color.Parse(theme.Color(token));
        foreach (var token in ThemeTokens.All)
        {
            var c = C(token);
            res[ThemeTokens.Color(token)] = c;
            res[ThemeTokens.Brush(token)] = new SolidColorBrush(c);
        }
        app.RequestedThemeVariant = theme.Dark ? ThemeVariant.Dark : ThemeVariant.Light;

        // Fluent accent ramp
        var accent = C(ThemeTokens.Accent);
        res["SystemAccentColor"] = accent;
        res["SystemAccentColorLight1"] = Mix(accent, Colors.White, .15); res["SystemAccentColorLight2"] = Mix(accent, Colors.White, .30); res["SystemAccentColorLight3"] = Mix(accent, Colors.White, .45);
        res["SystemAccentColorDark1"] = Mix(accent, Colors.Black, .15); res["SystemAccentColorDark2"] = Mix(accent, Colors.Black, .30); res["SystemAccentColorDark3"] = Mix(accent, Colors.Black, .45);

        // Fluent control surfaces → theme tokens
        var surface = new SolidColorBrush(C(ThemeTokens.SurfaceAlt));
        var surfaceHover = new SolidColorBrush(Mix(C(ThemeTokens.SurfaceAlt), C(ThemeTokens.Text), .08));
        var text = new SolidColorBrush(C(ThemeTokens.Text));
        var border = new SolidColorBrush(C(ThemeTokens.Border));
        foreach (var key in new[] { "ButtonBackground", "TextControlBackground", "TextControlBackgroundFocused", "ComboBoxBackground", "ComboBoxDropDownBackground", "ToggleButtonBackground" }) res[key] = surface;
        foreach (var key in new[] { "ButtonBackgroundPointerOver", "TextControlBackgroundPointerOver", "ComboBoxBackgroundPointerOver", "ToggleButtonBackgroundPointerOver" }) res[key] = surfaceHover;
        foreach (var key in new[] { "ButtonForeground", "ButtonForegroundPointerOver", "TextControlForeground", "TextControlForegroundFocused", "TextControlForegroundPointerOver", "ComboBoxForeground", "ToggleButtonForeground" }) res[key] = text;
        foreach (var key in new[] { "ButtonBorderBrush", "TextControlBorderBrush", "ComboBoxBorderBrush" }) res[key] = border;

        Current = theme;
        Changed?.Invoke(theme);
    }

    /// <summary>Writes a copy of a theme into the themes folder for the user to edit, and returns its path.</summary>
    public string ExportCopy(ThemeDefinition theme)
    {
        string id = theme.Id + "-custom"; int n = 2;
        while (_all.Any(t => t.Id == id) || File.Exists(Path.Combine(Folder, id + ".json"))) id = $"{theme.Id}-custom{n++}";
        var path = Path.Combine(Folder, id + ".json");
        File.WriteAllText(path, ThemeLoader.ToJson(theme, id));
        return path;
    }

    static Color Mix(Color a, Color b, double t) => Color.FromArgb(a.A,
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
