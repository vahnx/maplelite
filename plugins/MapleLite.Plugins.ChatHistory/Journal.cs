using System.Text.Json;
using MapleLite.Core.Imaging;

namespace MapleLite.Plugins.ChatHistory;

/// <summary>The chat area, saved as {"X","Y","Width","Height"} like the MapleMemory app's settings.json.</summary>
public sealed record ChatArea(int X, int Y, int Width, int Height)
{
    public Region ToRegion() => new(X, Y, Width, Height);
    public static ChatArea From(Region r) => new(r.X, r.Y, r.Width, r.Height);
}

/// <summary>Same fields as the MapleMemory app's settings.json, plus the one-time import flag.</summary>
public sealed class Settings
{
    public bool SuppressSpam { get; set; } = true;
    public bool ManualChat { get; set; }
    public bool StripTrailingAt { get; set; } = true;
    public bool ShowGameTime { get; set; }
    public string TimeZoneId { get; set; } = ClockZone.DefaultId;
    public ChatArea? Chat { get; set; }
    public int FrameWidth { get; set; }
    public int FrameHeight { get; set; }
    /// <summary>True once the MapleMemory app's history has been offered for import.</summary>
    public bool ImportChecked { get; set; }
}

/// <summary>
/// The journal: monthly messages-YYYY-MM.jsonl files, one Entry per line, appended as messages arrive.
/// Same format as the MapleMemory app, in the plugin's own data folder.
/// </summary>
public sealed class Journal
{
    public string Folder { get; }
    public readonly List<Entry> Entries = [];
    public int UnreadableLines { get; private set; }

    public Journal(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(Folder);
        foreach (var path in Directory.EnumerateFiles(Folder, "messages-*.jsonl").Order())
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0) continue;
                try { if (JsonSerializer.Deserialize<Entry>(line) is { } e) Entries.Add(e); }
                catch (JsonException) { UnreadableLines++; }
            }
    }

    public void Add(Entry e)
    {
        File.AppendAllText(Path.Combine(Folder, $"messages-{e.Time:yyyy-MM}.jsonl"), JsonSerializer.Serialize(e) + Environment.NewLine);
        Entries.Add(e);
    }

    /// <summary>Where the MapleMemory app keeps its history (Windows only).</summary>
    public static string AppFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MapleMemory");

    /// <summary>
    /// Copies the MapleMemory app's history in. Months are merged line by line and exact duplicate lines are
    /// skipped, so importing twice adds nothing. The app's files are only read, never changed.
    /// </summary>
    public static (int files, int lines) Import(string from, string to)
    {
        if (!Directory.Exists(from) || Path.GetFullPath(from) == Path.GetFullPath(to)) return (0, 0);
        Directory.CreateDirectory(to);
        int files = 0, added = 0;
        foreach (var src in Directory.EnumerateFiles(from, "messages-*.jsonl").Order())
        {
            var dst = Path.Combine(to, Path.GetFileName(src));
            var have = File.Exists(dst) ? new HashSet<string>(File.ReadLines(dst)) : new HashSet<string>();
            var fresh = File.ReadLines(src).Where(l => l.Length > 0 && have.Add(l)).ToList();
            if (fresh.Count == 0) continue;
            File.AppendAllLines(dst, fresh);
            files++; added += fresh.Count;
        }
        return (files, added);
    }

    /// <summary>The MapleMemory app's settings, if it has any.</summary>
    public static Settings? AppSettings(string from)
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(from, "settings.json"))); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}
