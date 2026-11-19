using System.Text.RegularExpressions;
using MapleLite.Core.Imaging;

namespace MapleLite.Plugins.XpTracker;

/// <summary>
/// Tells rewards (party quest stage clears and completions, quests) from kills. Both raise the EXP on the status bar,
/// but the game prints a reward's EXP in the chat box ("You have gained experience (+1200)"), while kill EXP shows in
/// the notices on the right. So a rise much bigger than your recent kills is held for a moment while the chat box is
/// read: the amount there makes it a reward; otherwise it's a kill (a boss, or several kills at once).
/// </summary>
public sealed partial class RewardSpotter
{
    /// <summary>How long to keep looking for the amount in chat after the rise.</summary>
    public static readonly TimeSpan LookFor = TimeSpan.FromSeconds(2.5);
    const long Smallest = 100;
    const int KillsKept = 40;

    readonly List<long> _kills = new();

    /// <summary>True when <paramref name="gain"/> is big enough next to your recent kills to be worth checking the chat for.</summary>
    public bool Worth(long gain)
    {
        if (gain < Smallest) return false;
        if (_kills.Count < 5) return true;
        var sorted = _kills.Order().ToList();
        return gain >= 4 * sorted[sorted.Count / 2];
    }

    /// <summary>Remembers a gain that turned out to be a kill.</summary>
    public void Killed(long gain)
    {
        _kills.Add(gain);
        if (_kills.Count > KillsKept) _kills.RemoveAt(0);
    }

    [GeneratedRegex(@"(?i)exp|experi|gain|receiv|\(\s*[+t]")]
    private static partial Regex ExpWords();

    /// <summary>True when a line of <paramref name="chat"/> shows <paramref name="amount"/> as EXP.</summary>
    public static bool InChat(string chat, long amount)
    {
        var number = new Regex($@"(?<![\d,.]){amount}(?![\d])");
        foreach (var raw in chat.Split('\n'))
        {
            var line = Regex.Replace(raw, @"(?<=\d)[,.](?=\d{3}\b)", ""); // 1,200 → 1200
            if (number.IsMatch(line) && ExpWords().IsMatch(line)) return true;
        }
        return false;
    }

    /// <summary>
    /// Where to read the chat box: where Chat History found it, else the usual spot (bottom left, above the status bar).
    /// </summary>
    public static Region ChatArea(Frame f, Region? known) =>
        known is { } k && k.Width > 0 && k.Height > 0 && k.Right <= f.W && k.Bottom <= f.H
            ? k
            : new Region(0, f.H * 72 / 100, f.W * 62 / 100, f.H * 93 / 100 - f.H * 72 / 100);
}
