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
// Wording is matched loosely because OCR drops punctuation and the text is read while it fades.
public static class LootParser {
 const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline;
 static readonly Regex MesosGained = new(@"gain\w*\s+(?:\w+\s+)?mes[o0]s?\b\D{0,6}(?<n>\d[\d,\. ]*)", Options);
 static readonly Regex MesosLost = new(@"(?:lost|dropped)\s+(?:\w+\s+)?mes[o0]s?\b\D{0,6}(?<n>\d[\d,\. ]*)", Options);
 static readonly Regex ItemGained = new(@"ga[il1]n\w*\s+(?:an?\s+)?[il1I|]te(?:m|rn)s?\b[^(\[{]*[\(\[{]\s*(?<item>[^)\]}]{2,60})", Options);
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
  return null;
 }
}
