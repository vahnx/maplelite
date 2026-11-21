using System.Globalization;
using System.Text.RegularExpressions;

namespace MapleLite.Core.Market;

public static class MesoAmount
{
    static readonly Regex Amount = new(@"^~?\s*(\d[\d,]*(?:\.\d+)?)\s*([kmb])?\s*(?:mesos?|ea(?:ch)?|obo)?\s*$", RegexOptions.IgnoreCase);

    public static long? Parse(string text)
    {
        var match = Amount.Match(text.Trim());
        if (!match.Success || !decimal.TryParse(match.Groups[1].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return null;
        int multiplier = match.Groups[2].Value.ToLowerInvariant() switch { "k" => 1_000, "m" => 1_000_000, "b" => 1_000_000_000, _ => 1 };
        if (value > long.MaxValue) return null;
        decimal mesos = Math.Round(value * multiplier);
        return mesos <= long.MaxValue ? (long)mesos : null;
    }
}
