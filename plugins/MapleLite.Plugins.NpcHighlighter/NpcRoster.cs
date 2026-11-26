using MapleLite.Core.Game;

namespace MapleLite.Plugins.NpcHighlighter;

/// <summary>
/// The NPC names offered in the "nearby" list. A tag partly covered by a player's tag reads as a fragment
/// ("Sil", "Irons"), and a covered name can leave only the title below it showing, so a new name joins only once it
/// has been read in <see cref="Votes"/> scans; a misread or fragment of a name already listed counts for that name.
/// </summary>
internal sealed class NpcRoster
{
    const int Votes = 3;
    readonly NearbyRoster _roster = new();
    readonly Dictionary<string, (int Count, DateTimeOffset Last)> _pending = new(StringComparer.OrdinalIgnoreCase);
    string[] _listed = [];

    public void Clear()
    {
        _roster.Clear();
        _pending.Clear();
        _listed = [];
    }

    /// <summary>Takes one scan's names (null when there was no readable picture) and gives the list to show.</summary>
    public string[] Update(DateTimeOffset now, IReadOnlyList<string>? read, DateTimeOffset? capturedAt)
    {
        List<string>? names = null;
        if (read != null)
        {
            names = [];
            foreach (var text in read)
            {
                var key = NpcTags.Compact(text);
                var known = _listed.FirstOrDefault(n => NpcTags.Matches(key, NpcTags.Compact(n)) || NpcTags.Compact(n).Contains(key));
                if (known != null) { names.Add(known); continue; }
                var (count, last) = _pending.GetValueOrDefault(text);
                // Votes count only while they keep coming; one stray read now and then never adds up.
                _pending[text] = (now - last > TimeSpan.FromSeconds(6) ? 1 : count + 1, now);
                if (_pending[text].Count >= Votes) { names.Add(text); _pending.Remove(text); }
            }
        }
        _listed = _roster.Update(now, names, capturedAt);
        if (_pending.Count > 200) _pending.Clear();
        return _listed;
    }
}
