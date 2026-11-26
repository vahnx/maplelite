using System.Text.RegularExpressions;

namespace MapleLite.Core.Game;

/// <summary>Small facts about MapleStory's on-screen text that several plugins need.</summary>
public static partial class GameText
{
    // OCR often reads zero as O and one as I/l inside a timestamp; the minute bracket can read as 1, I or l.
    // Same pattern as Chat History's MessageParser.
    [GeneratedRegex(@"^\s*[\[\(]?(?<h>[\dOoIl]\s*[\dOoIl]?)\s*[:;]\s*(?<m>[\dOoIl]\s*[\dOoIl])[\]\)1|Il]?\s*", RegexOptions.Singleline)]
    private static partial Regex Stamp();

    [GeneratedRegex(@"^\w{2,16}\s*[:：]")]
    private static partial Regex PlayerPrefix();

    /// <summary>
    /// Undoes the English OCR's habit of reading game text as European letters. The game's chat font has no accents,
    /// so "idöwolf" is "idowolf"; its tilde reads as "æ". Other symbols (•, —, €) are left alone.
    /// </summary>
    public static string FoldOcrLetters(string text)
    {
        bool plain = true;
        foreach (char c in text) if (c > '~') { plain = false; break; }
        if (plain) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text.Replace("æ", "~").Replace("Æ", "~").Normalize(System.Text.NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c switch { 'ø' => 'o', 'Ø' => 'O', 'ð' => 'd', 'ł' => 'l', 'ı' => 'i', _ => c });
        }
        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }

    /// <summary>Removes a leading chat timestamp such as "[19:57] ".</summary>
    public static string StripStamp(string raw) => Stamp().Replace(raw, "", 1);

    /// <summary>True for a player's chat line ("Name : …"), which is never a system notice such as a pickup.</summary>
    public static bool IsPlayerChat(string raw) => PlayerPrefix().IsMatch(StripStamp(raw));

    /// <summary>A pickup notice ("You have gained …") that isn't a player quoting one.</summary>
    public static LootDrop? Pickup(string raw) => IsPlayerChat(raw) ? null : LootParser.Parse(raw);
}
