using MapleLite.Sdk;

namespace MapleLite.Plugins.XpTracker;

internal enum PqExpSource { Monster, Stage, Completion, Unclassified }

internal sealed record PqRun(Guid Id, string Quest, DateTimeOffset Started, DateTimeOffset? Ended,
    Dictionary<DateTimeOffset, PqExpSource> Sources)
{
    public PqExpSource SourceOf(XpGain gain) => Sources.TryGetValue(gain.Time, out var source)
        ? source : gain.Reward ? PqExpSource.Unclassified : PqExpSource.Monster;
}

internal sealed record PqRunSummary(PqRun Run, IReadOnlyList<XpGain> Gains)
{
    public long Exp => Gains.Sum(g => g.Amount);
    public long ExpFrom(PqExpSource source) => Gains.Where(g => Run.SourceOf(g) == source).Sum(g => g.Amount);
}

internal sealed class PqRunLog(IPluginStorage storage)
{
    const string Key = "party-quests";
    PqRun[] _runs = storage.Load<RunState>(Key).Runs;
    public IReadOnlyList<PqRun> Runs => _runs;
    public PqRun? Active => _runs.LastOrDefault(r => r.Ended == null);

    public PqRun Start(string quest, DateTimeOffset time)
    {
        if (Active != null) throw new InvalidOperationException("End the current run first.");
        if (string.IsNullOrWhiteSpace(quest)) throw new ArgumentException("Enter a party quest name.", nameof(quest));
        if (_runs.LastOrDefault()?.Ended > time) throw new InvalidOperationException("The last run ended after this time.");
        var run = new PqRun(Guid.NewGuid(), quest.Trim(), time, null, []);
        Save([.. _runs, run]);
        return run;
    }

    public void End(DateTimeOffset time)
    {
        if (Active is not { } run) return;
        Replace(run with { Ended = time < run.Started ? run.Started : time });
    }

    public void SetSource(Guid id, XpGain gain, PqExpSource? source)
    {
        var run = _runs.Single(r => r.Id == id);
        if (gain.Time < run.Started || gain.Time >= run.Ended)
            throw new InvalidOperationException("This gain is outside the run.");
        var sources = new Dictionary<DateTimeOffset, PqExpSource>(run.Sources);
        if (source is { } value) sources[gain.Time] = value;
        else sources.Remove(gain.Time);
        Replace(run with { Sources = sources });
    }

    void Replace(PqRun run) => Save(_runs.Select(r => r.Id == run.Id ? run : r).ToArray());

    void Save(PqRun[] runs)
    {
        storage.Save(Key, new RunState { Runs = runs });
        _runs = runs;
    }

    public sealed class RunState { public PqRun[] Runs { get; set; } = []; }

    public static IReadOnlyList<PqRunSummary> Summarize(IReadOnlyList<PqRun> runs, IReadOnlyList<XpGain> gains)
    {
        if (runs.Count == 0) return [];
        using var ordered = gains.Where(g => g.Time >= runs[0].Started).OrderBy(g => g.Time).GetEnumerator();
        bool hasGain = ordered.MoveNext();
        var summaries = new List<PqRunSummary>(runs.Count);
        foreach (var run in runs)
        {
            while (hasGain && ordered.Current.Time < run.Started) hasGain = ordered.MoveNext();
            var included = new List<XpGain>();
            while (hasGain && (run.Ended == null || ordered.Current.Time < run.Ended))
            {
                included.Add(ordered.Current);
                hasGain = ordered.MoveNext();
            }
            summaries.Add(new(run, included));
        }
        return summaries;
    }
}
