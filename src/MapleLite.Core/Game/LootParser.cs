// From the MapleMemory app; shared in Core so the Loot and Chat History plugins (and others) read pickups the same way.
using System.Text.RegularExpressions;
namespace MapleLite.Core.Game;

// One pickup read from the game's own notification text.
// Kind is "Item", "Mesos" (gained) or "MesosLost" (dropped or lost).
public sealed record LootDrop(string Kind, string Name, long Quantity) {
 public string Summary => Kind switch {
  "Mesos" => $"+{Quantity:N0} mesos",
  "MesosLost" => $"−{Quantity:N0} mesos",
  _ => Quantity > 1 ? $"{Name} ×{Quantity:N0}" : Name,
 };
}

// Recognises pickup notifications such as:
//   You have gained mesos (+5800)
//   You have gained an item in the Etc tab (Snail Shell)
//   You have gained items in the Use tab (Red Potion 5)
//   You have lost mesos (-50)
//   Slime Bubble x1 earned. (Etc)
// Wording is matched loosely because OCR drops punctuation and the text is read while it fades.
public static class LootParser {
 const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline;
 static readonly Regex MesosGained = new(@"[gq]a[il1]n\w*\s+(?:\w+\s+)?mes[o0]s?\b\D{0,6}(?<n>\d[\d,\. ]*)", Options);
 static readonly Regex MesosLost = new(@"(?:lost|dropped)\s+(?:\w+\s+)?mes[o0]s?\b\D{0,6}(?<n>\d[\d,\. ]*)", Options);
 static readonly Regex ItemGained = new(@"[gq]a[il1]n\w*\s+(?:an?\s+)?[il1I|]te(?:m|rn)s?\b[^(\[{]*[\(\[{]\s*(?<item>[^)\]}]{2,60})", Options);
 static readonly Regex TrailingCount = new(@"^(?<name>.*[^\d\s].*?)\s+[x×]?\s*(?<qty>\d{1,5})$", Options);

 static long Number(string text) {
  var digits = new string(text.Where(char.IsDigit).ToArray());
  return digits.Length is > 0 and <= 12 && long.TryParse(digits, out long n) ? n : 0;
 }

 public static LootDrop? Parse(string raw) {
  string text = GameText.StripStamp(raw);
  var gained = MesosGained.Match(text);
  if (gained.Success && Number(gained.Groups["n"].Value) is > 0 and var plus) return new("Mesos", "Mesos", plus);
  var lost = MesosLost.Match(text);
  if (lost.Success && Number(lost.Groups["n"].Value) is > 0 and var minus) return new("MesosLost", "Mesos", minus);
  var item = ItemGained.Match(text);
  if (item.Success) {
   string name = item.Groups["item"].Value.Trim();
   long quantity = 1;
   var count = TrailingCount.Match(name);
   if (count.Success && long.TryParse(count.Groups["qty"].Value, out long q) && q > 0) { name = count.Groups["name"].Value.Trim(); quantity = q; }
   if (name.Length >= 2) return new("Item", name, quantity);
  }
  var earned = ItemEarned.Match(text);
  if (earned.Success && !earned.Groups["body"].Value.TrimStart().StartsWith("you ", StringComparison.OrdinalIgnoreCase)) {
   string body = earned.Groups["body"].Value.Trim();
   long quantity = 1;
   var count = EarnedCount.Match(body);
   if (count.Success) {
    body = count.Groups["name"].Value;
    string q = count.Groups["q1"].Success ? count.Groups["q1"].Value : count.Groups["q2"].Value;
    if (long.TryParse(OcrDigits(q), out long n) && n > 0) quantity = n;
   }
   string name = body.Trim(' ', ':', '-', '.', ',', ';', '•', '\'', '"');
   if (name.Count(char.IsLetter) >= 2) return new("Item", name, quantity);
  }
  return null;
 }

 // The pickup notice for items: "Slime Bubble x1 earned. (Etc)". OCR often reads "x1" as "zi", "ZI" or ":-1",
 // "earned" as "eamed", and drops the tab.
 static readonly Regex ItemEarned = new(@"^(?<body>.*?\p{L}.*?)\s+ea\w{1,4}d\b\W{0,3}(?:[\(\[{]\s*\w{2,8}.{0,3})?$", Options);
 static readonly Regex EarnedCount = new(@"^(?<name>.*?\p{L}.*?)(?:\s*[:\-]?\s*[xz×]\s?(?<q1>[0-9il|!o]{1,4})|[\s:\-]+(?<q2>\d{1,4}))$", Options);
 static string OcrDigits(string s) => new(s.Select(c => c switch { 'i' or 'I' or 'l' or 'L' or '|' or '!' => '1', 'o' or 'O' => '0', _ => c }).ToArray());
}
