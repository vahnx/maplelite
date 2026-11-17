namespace MapleLite.Core;

/// <summary>Where MapleLite keeps its data on each OS.</summary>
public static class AppPaths
{
    /// <summary>
    /// Windows: %LOCALAPPDATA%\MapleLite. macOS: ~/Library/Application Support/MapleLite.
    /// Linux: $XDG_DATA_HOME/MapleLite (normally ~/.local/share/MapleLite).
    /// Set MAPLELITE_DATA to override (tests use this).
    /// </summary>
    public static string Root
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable("MAPLELITE_DATA");
            string root;
            if (!string.IsNullOrWhiteSpace(overridePath)) root = overridePath;
            else if (OperatingSystem.IsMacOS())
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "MapleLite");
            else
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MapleLite");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    public static string Sub(params string[] parts)
    {
        var p = Path.Combine([Root, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    public static string Themes => Sub("themes");
    public static string PluginData(string pluginId) => Sub("plugins", pluginId);
}
