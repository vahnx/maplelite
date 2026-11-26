using System.Text.Json;
using MapleLite.Core.Imaging;
using MapleLite.Sdk;

namespace MapleLite.Plugins.ChatHistory;

/// <summary>The chat area, saved as {"X","Y","Width","Height"} like the MapleMemory app's settings.json.</summary>
public sealed record ChatArea(int X, int Y, int Width, int Height)
{
    public Region ToRegion() => new(X, Y, Width, Height);
    public static ChatArea From(Region r) => new(r.X, r.Y, r.Width, r.Height);
}

/// <summary>Same fields as the MapleMemory app's settings.json.</summary>
public sealed class Settings
{
    public bool SuppressSpam { get; set; } = true;
    public bool ManualChat { get; set; }
    public bool StripTrailingAt { get; set; } = true;
    public bool ShowGameTime { get; set; }
    /// <summary>Show each chat line as read, in one column, instead of splitting it into username/channel/message columns.</summary>
    public bool ShowRawLine { get; set; } = true;
    /// <summary>The player's character name(s), comma-separated, for the "mentions of me" filter.</summary>
    public string MyNames { get; set; } = "";
    public bool OnlyMentions { get; set; }
    public string TimeZoneId { get; set; } = ClockZone.DefaultId;
    public ChatArea? Chat { get; set; }
    public int FrameWidth { get; set; }
    public int FrameHeight { get; set; }
}

/// <summary>
/// The journal: one messages-YYYY-MM-DD.jsonl file per day, one Entry per line, appended as messages arrive.
/// Same line format as the MapleMemory app (which keeps one file per month), in the plugin's own data folder.
/// Older monthly files are split into days the first time the journal opens.
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
        DailyFiles.SplitMonthly(Folder, Prefix, Ext, TimeOf);
        foreach (var path in Directory.EnumerateFiles(Folder, "messages-*.jsonl").Order())
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0) continue;
                try { if (JsonSerializer.Deserialize<Entry>(line) is { } e) Entries.Add(e); }
                catch (JsonException) { UnreadableLines++; }
            }
    }

    const string Prefix = "messages-", Ext = ".jsonl";

    static DateTimeOffset? TimeOf(string line)
    {
        try { return JsonSerializer.Deserialize<Entry>(line)?.Time; }
        catch (JsonException) { return null; }
    }

    public void Add(Entry e)
    {
        File.AppendAllText(DailyFiles.PathFor(Folder, Prefix, Ext, e.Time), JsonSerializer.Serialize(e) + Environment.NewLine);
        Entries.Add(e);
    }
}
