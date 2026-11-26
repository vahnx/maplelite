namespace MapleLite.Core.Game;

/// <summary>Names seen in recent scans, kept a while after they stop being read so the list doesn't flicker (shared by Player Finder and NPC Highlighter).</summary>
public sealed class NearbyRoster
{
    static readonly TimeSpan ReadableScanRetention = TimeSpan.FromSeconds(12);
    static readonly TimeSpan CaptureGapRetention = TimeSpan.FromSeconds(25);
    readonly Dictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.OrdinalIgnoreCase);

    public void Clear() => _lastSeen.Clear();

    // A missing picture or failed read is weaker evidence than a new, readable picture.
    // In both cases give temporarily obscured nameplates time to reappear.
    public string[] Update(DateTimeOffset now, IReadOnlyList<string>? names = null, DateTimeOffset? capturedAt = null)
    {
        if (names != null && capturedAt is { } at)
            foreach (string name in names)
                _lastSeen[name] = at;

        var retention = names == null ? CaptureGapRetention : ReadableScanRetention;
        foreach (string name in _lastSeen.Where(entry => now - entry.Value >= retention).Select(entry => entry.Key).ToArray())
            _lastSeen.Remove(name);

        return _lastSeen.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
