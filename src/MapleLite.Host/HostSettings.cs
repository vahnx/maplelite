using System.Text.Json;
using MapleLite.Core;

namespace MapleLite.Host;

internal sealed class HostSettings
{
    public string ThemeId { get; set; } = "midnight";
    public string ProcessName { get; set; } = "MapleStory";
    public int? ProcessId { get; set; }
    public double PreviewFps { get; set; } = 8;
    public List<string> DisabledPlugins { get; set; } = new();
    /// <summary>Overlays are off by default and not built yet. See docs/OVERLAYS.md.</summary>
    public bool OverlayEnabled { get; set; }

    static string PathOf => System.IO.Path.Combine(AppPaths.Root, "settings.json");
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static HostSettings Load()
    {
        try { return JsonSerializer.Deserialize<HostSettings>(File.ReadAllText(PathOf)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        var tmp = PathOf + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, PathOf, true);
    }
}
