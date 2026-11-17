using System.Text.Json;
using System.Text.RegularExpressions;

namespace MapleLite.Sdk.Theming;

/// <summary>
/// The color tokens every theme defines. The host publishes each as an application resource:
/// "ml.{Token}" is a brush and "ml.{Token}.Color" a color. Plugin UI should use these
/// (e.g. <c>Background="{DynamicResource ml.Surface}"</c>) so it follows the theme live.
/// </summary>
public static class ThemeTokens
{
    public const string Background = nameof(Background);   // window background
    public const string Surface = nameof(Surface);         // cards and panels
    public const string SurfaceAlt = nameof(SurfaceAlt);   // sidebar, inputs, alternating rows
    public const string Border = nameof(Border);
    public const string Text = nameof(Text);
    public const string TextMuted = nameof(TextMuted);
    public const string Accent = nameof(Accent);           // selection, buttons, links
    public const string AccentText = nameof(AccentText);   // text on Accent
    public const string Success = nameof(Success);
    public const string Warning = nameof(Warning);
    public const string Danger = nameof(Danger);

    public static readonly string[] All = [Background, Surface, SurfaceAlt, Border, Text, TextMuted, Accent, AccentText, Success, Warning, Danger];

    public static string Brush(string token) => "ml." + token;
    public static string Color(string token) => "ml." + token + ".Color";
}

/// <summary>A theme: a name, light or dark base, and a color for every token ("#RRGGBB" or "#AARRGGBB").</summary>
public sealed record ThemeDefinition(string Id, string Name, bool Dark, IReadOnlyDictionary<string, string> Colors)
{
    public bool BuiltIn { get; init; }
    public string Color(string token) => Colors.TryGetValue(token, out var c) ? c : "#FF00FF";
}

public interface IThemeService
{
    ThemeDefinition Current { get; }
    IReadOnlyList<ThemeDefinition> All { get; }
    event Action<ThemeDefinition>? Changed;
}

public static class BuiltInThemes
{
    static ThemeDefinition T(string id, string name, bool dark, string bg, string surface, string alt, string border,
        string text, string muted, string accent, string accentText, string ok = "#3FB27F", string warn = "#E0A43A", string bad = "#E5534B") =>
        new(id, name, dark, new Dictionary<string, string>
        {
            [ThemeTokens.Background] = bg, [ThemeTokens.Surface] = surface, [ThemeTokens.SurfaceAlt] = alt, [ThemeTokens.Border] = border,
            [ThemeTokens.Text] = text, [ThemeTokens.TextMuted] = muted, [ThemeTokens.Accent] = accent, [ThemeTokens.AccentText] = accentText,
            [ThemeTokens.Success] = ok, [ThemeTokens.Warning] = warn, [ThemeTokens.Danger] = bad,
        }) { BuiltIn = true };

    public static readonly ThemeDefinition Midnight = T("midnight", "Midnight", true, "#14161C", "#1C1F27", "#232733", "#2E3340", "#E6E8EE", "#9AA1B2", "#F28C38", "#1A1206");
    public static readonly ThemeDefinition Daybreak = T("daybreak", "Daybreak", false, "#F4F2EE", "#FFFFFF", "#EAE6DF", "#D6D0C4", "#23211D", "#6B655A", "#D9661F", "#FFFFFF", "#2E8B57", "#B7791F", "#C53030");
    public static readonly ThemeDefinition Maple = T("maple", "Maple", true, "#1E1410", "#2A1C16", "#33231B", "#4A3226", "#F6E9DF", "#C2A897", "#FF7A2F", "#1E1410");
    public static readonly ThemeDefinition Forest = T("forest", "Forest", true, "#101A14", "#16231B", "#1C2C22", "#2A3F31", "#E3EEE6", "#93AB9B", "#5FCB82", "#0C1A10");
    public static readonly ThemeDefinition Ocean = T("ocean", "Ocean", true, "#0F1722", "#152030", "#1B2A3D", "#28405C", "#E2ECF7", "#8FA6C0", "#4DA3FF", "#06121F");
    public static readonly ThemeDefinition Sakura = T("sakura", "Sakura", false, "#FBF3F5", "#FFFFFF", "#F4E4E9", "#E5C9D2", "#2B1F23", "#7A5F68", "#D6457A", "#FFFFFF", "#2F855A", "#B7791F", "#C53030");
    public static readonly ThemeDefinition HighContrast = T("high-contrast", "High contrast", true, "#000000", "#000000", "#0A0A0A", "#FFFFFF", "#FFFFFF", "#E0E0E0", "#FFD400", "#000000", "#00FF7F", "#FFD400", "#FF4D4D");

    public static readonly IReadOnlyList<ThemeDefinition> All = [Midnight, Daybreak, Maple, Forest, Ocean, Sakura, HighContrast];
}

/// <summary>
/// Reads user themes: JSON files in the themes folder. A theme may set only some tokens;
/// the rest come from the theme named in "base" (or Midnight/Daybreak by "dark").
/// <code>
/// { "id": "my-theme", "name": "My theme", "dark": true, "base": "midnight",
///   "colors": { "Accent": "#7C5CFF", "Background": "#101018" } }
/// </code>
/// </summary>
public static partial class ThemeLoader
{
    [GeneratedRegex("^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")] private static partial Regex HexRx();

    sealed class ThemeFile
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public bool? Dark { get; set; }
        public string? Base { get; set; }
        public Dictionary<string, string>? Colors { get; set; }
    }

    static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Parses one theme file. Throws FormatException with a readable reason when it's invalid.</summary>
    public static ThemeDefinition Parse(string json, IReadOnlyList<ThemeDefinition> bases)
    {
        ThemeFile file;
        try { file = JsonSerializer.Deserialize<ThemeFile>(json, Options) ?? throw new FormatException("The file is empty."); }
        catch (JsonException e) { throw new FormatException("It isn't valid JSON: " + e.Message); }
        if (string.IsNullOrWhiteSpace(file.Id)) throw new FormatException("It needs an \"id\".");
        var baseTheme = file.Base != null ? bases.FirstOrDefault(b => b.Id == file.Base) ?? throw new FormatException($"There is no base theme \"{file.Base}\".")
            : file.Dark == false ? BuiltInThemes.Daybreak : BuiltInThemes.Midnight;
        var colors = new Dictionary<string, string>(baseTheme.Colors);
        foreach (var (token, value) in file.Colors ?? new())
        {
            var known = ThemeTokens.All.FirstOrDefault(t => t.Equals(token, StringComparison.OrdinalIgnoreCase))
                ?? throw new FormatException($"\"{token}\" isn't a theme color. Use: {string.Join(", ", ThemeTokens.All)}.");
            if (!HexRx().IsMatch(value.Trim())) throw new FormatException($"{known} is \"{value}\"; use #RRGGBB or #AARRGGBB.");
            colors[known] = value.Trim();
        }
        return new ThemeDefinition(file.Id.Trim(), string.IsNullOrWhiteSpace(file.Name) ? file.Id.Trim() : file.Name.Trim(), file.Dark ?? baseTheme.Dark, colors);
    }

    /// <summary>Serializes a theme so users can copy a built-in as a starting point.</summary>
    public static string ToJson(ThemeDefinition t, string? newId = null) => JsonSerializer.Serialize(new ThemeFile
    {
        Id = newId ?? t.Id, Name = newId == null ? t.Name : t.Name + " (copy)", Dark = t.Dark, Colors = new(t.Colors),
    }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    /// <summary>Loads every *.json in a folder; bad files are reported, not fatal.</summary>
    public static (List<ThemeDefinition> themes, List<string> errors) LoadFolder(string folder, IReadOnlyList<ThemeDefinition> bases)
    {
        var themes = new List<ThemeDefinition>(); var errors = new List<string>();
        if (!Directory.Exists(folder)) return (themes, errors);
        foreach (var path in Directory.EnumerateFiles(folder, "*.json").Order())
        {
            try
            {
                var t = Parse(File.ReadAllText(path), bases);
                if (bases.Any(b => b.Id == t.Id) || themes.Any(x => x.Id == t.Id)) errors.Add($"{Path.GetFileName(path)}: the id \"{t.Id}\" is already used.");
                else themes.Add(t);
            }
            catch (Exception e) when (e is FormatException or IOException) { errors.Add($"{Path.GetFileName(path)}: {e.Message}"); }
        }
        return (themes, errors);
    }
}
