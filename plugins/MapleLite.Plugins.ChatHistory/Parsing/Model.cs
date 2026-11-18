// Ported from the MapleMemory app's Engine.cs (Entry, ClockZone, Classifier), unchanged apart from the namespace. ScrollTracker moved to MapleLite.Core.Game.
using System.Text.RegularExpressions;
using MapleLite.Core.Game;
namespace MapleLite.Plugins.ChatHistory;

public record Entry(DateTimeOffset Time, string Channel, string Direction, string Text, string Source = "OCR") {
 [System.Text.Json.Serialization.JsonIgnore] public string When => ClockZone.Convert(Time).ToString("MMM dd  HH:mm:ss");
 [System.Text.Json.Serialization.JsonIgnore] public string GameTime => MessageParser.Parse(Text).GameTime;
 [System.Text.Json.Serialization.JsonIgnore] public string Username => MessageParser.Parse(Text).Username;
 [System.Text.Json.Serialization.JsonIgnore] public string Message => MessageParser.Parse(Text).Body;
 [System.Text.Json.Serialization.JsonIgnore] public string Item => MessageParser.Parse(Text).Item;
 [System.Text.Json.Serialization.JsonIgnore] public string Side => MessageParser.Parse(Text).Side;
}
// Display time zone for observed times. Stored times stay in UTC; only display, filters and export use this.
public static class ClockZone {
 public const string DefaultId = "Eastern Standard Time";
 public static TimeZoneInfo Zone = TimeZoneInfo.Local;
 public static DateTimeOffset Convert(DateTimeOffset time) => TimeZoneInfo.ConvertTime(time, Zone);
 public static TimeZoneInfo Find(string id) {
  try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
  catch (Exception) { try { return TimeZoneInfo.FindSystemTimeZoneById(DefaultId); } catch (Exception) { return TimeZoneInfo.Local; } }
 }
 // Choices for the dropdown, ordered by UTC offset; UTC and the machine's local zone are always included.
 public static List<TimeZoneInfo> Choices() {
  var list = TimeZoneInfo.GetSystemTimeZones().ToList();
  if (!list.Any(z => z.Id == "UTC")) list.Add(TimeZoneInfo.Utc);
  if (!list.Any(z => z.Id == TimeZoneInfo.Local.Id)) list.Add(TimeZoneInfo.Local);
  return list.OrderBy(z => z.BaseUtcOffset).ThenBy(z => z.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
 }
 public static string Offset(TimeZoneInfo zone) {
  var off = zone.GetUtcOffset(DateTimeOffset.UtcNow);
  return $"UTC{(off < TimeSpan.Zero ? "-" : "+")}{Math.Abs(off.Hours):00}:{Math.Abs(off.Minutes):00}";
 }
}
// Lines where someone else mentions one of the player's names (their own chat lines don't count).
public static class Mentions {
 public static string[] Split(string names) => names.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
 public static bool Of(Entry e, IReadOnlyList<string> names) {
  if (names.Count == 0) return false;
  string sender = e.Username;
  string body = sender.Length > 0 ? e.Message : e.Text;
  foreach (var n in names) {
   if (sender.Equals(n, StringComparison.OrdinalIgnoreCase)) return false;
   if (Regex.IsMatch(body, $@"(?<![A-Za-z0-9]){Regex.Escape(n)}(?![A-Za-z0-9])", RegexOptions.IgnoreCase)) return true;
  }
  return false;
 }
}
public static class Classifier {
 public static readonly string[] Channels = ["All", "Whisper", "Buddy", "Party", "Guild", "Alliance", "General", "Megaphone", "System", "Trade", "Fame", "Loot", "Unknown"];
 // Trade notifications and dialogs in either word order, with OCR-tolerant spacing ("tr ade").
 // Deliberately excludes bare "offer"/"from": megaphone ads use those words without a player prefix.
 const string TradeWord=@"\btr\s?a\s?de\w*";
 const string TradeEvent=@"(request|requests|requested|requesting|invite|invites|invited|inviting|invitation|accept\w*|declin\w*|cancel\w*|complet\w*|confirm\w*|window|with you)";
 public static readonly Regex TradeRx=new(
  $@"{TradeWord}\b.{{0,40}}?\b{TradeEvent}\b|\b{TradeEvent}\b.{{0,40}}?{TradeWord}|\b(wants|would like|requests?|invited|invites) (you )?to\s+{TradeWord}|^\W*{TradeWord}\W*$",
  RegexOptions.IgnoreCase|RegexOptions.Compiled);
 public static Entry Parse(string text) {
  string original=text; text=MessageParser.StripStamp(text); string type="Unknown", direction=""; bool playerChat=Regex.IsMatch(text,@"^\w{2,16}\s*[:：]");
  if (!playerChat && Regex.IsMatch(text,@"\b(fame|famed|defame|defamed|defammed|popularity)\b",RegexOptions.IgnoreCase) && Regex.IsMatch(text,@"\b(increased|decreased|raised|lowered|gained|lost|famed|defamed|defammed)\b",RegexOptions.IgnoreCase)) {
   type="Fame"; direction=Regex.IsMatch(text,@"\b(decreased|lowered|lost|defamed|defammed)\b",RegexOptions.IgnoreCase)?"− fame":"+ fame";
   direction += Regex.IsMatch(text,@"^\s*you (have )?(increased|decreased|raised|lowered|famed|defamed)",RegexOptions.IgnoreCase)?" · given":Regex.IsMatch(text,@"\byour (fame|popularity)\b",RegexOptions.IgnoreCase)?" · received":" · review";
  } else if (!playerChat && TradeRx.IsMatch(text)) type="Trade";
  else if (Regex.IsMatch(text,@"(\bwhisper\b|\[(from|to)\s|^(from|to)\s+\w+\s*[:>])",RegexOptions.IgnoreCase)) {type="Whisper"; direction=Regex.IsMatch(text,@"(\[to\s|^to\s)",RegexOptions.IgnoreCase)?"Sent":"Received";}
  else if(Regex.IsMatch(text,@"^[A-Za-z0-9_]{2,16}\s*>{1,2}\s")) type="Whisper"; // green "Name>> message"
  else foreach(var c in new[]{"Buddy","Party","Guild","Alliance","System"}) if(Regex.IsMatch(text,@"^\s*\[?"+c+@"\b",RegexOptions.IgnoreCase)){type=c;break;}
  // Pickup notifications ("You have gained mesos (+5800)") go to their own channel; the Event column shows what was gained.
  if(!playerChat && LootParser.Parse(text) is {} loot){type="Loot";direction=loot.Summary;}
  if(type=="Unknown" && MessageParser.Parse(original).Username.Length>0) type="General";
  return new(DateTimeOffset.Now,type,direction,original);
 }
}
