// Ported from the MapleMemory app's Engine.cs (Entry, ClockZone, Classifier, ScrollTracker), unchanged apart from the namespace.
using System.Text.RegularExpressions;
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
public class ScrollTracker {
 List<string> previous=[];
 public bool LastHadGap {get;private set;}

 static string Key(string s)=>Regex.Replace(Regex.Replace(s.ToLowerInvariant(),@"^\s*\[?\d\s*\d?\s*[:;]\s*\d\s*\d[\]\)1|]?\s*", ""),@"[^\p{L}\p{N}]","");
 static bool Similar(string a,string b) {a=Key(a);b=Key(b);if(a==b)return true; if(string.Concat(a.Where(char.IsDigit))!=string.Concat(b.Where(char.IsDigit)))return false;if(Math.Min(a.Length,b.Length)<12)return false; int[] row=Enumerable.Range(0,b.Length+1).ToArray();for(int i=1;i<=a.Length;i++){int diag=row[0];row[0]=i;for(int j=1;j<=b.Length;j++){int old=row[j];row[j]=Math.Min(Math.Min(row[j]+1,row[j-1]+1),diag+(a[i-1]==b[j-1]?0:1));diag=old;}}return row[b.Length]<=Math.Max(a.Length,b.Length)*.12;}
 public List<string> Read(List<string> current) {
  LastHadGap=false;
  if(current.Count==0)return [];
  int overlap=0;
  // Match the old suffix against the new prefix, preserving repeated messages in a scroll.
  for(int n=Math.Min(previous.Count,current.Count);n>0;n--) if(Enumerable.Range(0,n).All(i=>Similar(previous[previous.Count-n+i],current[i]))){overlap=n;break;}
  // OCR can omit an old row. Ordered alignment keeps the last surviving row as an
  // anchor instead of treating the entire viewport as new. New rows follow it.
  if(overlap==0&&previous.Count>0){
   int[,] matches=new int[previous.Count+1,current.Count+1];
   for(int i=1;i<=previous.Count;i++)for(int j=1;j<=current.Count;j++)matches[i,j]=Similar(previous[i-1],current[j-1])?matches[i-1,j-1]+1:Math.Max(matches[i-1,j],matches[i,j-1]);
   int pi=previous.Count,ci=current.Count;
   while(pi>0&&ci>0){if(Similar(previous[pi-1],current[ci-1])){overlap=ci;break;}if(matches[pi-1,ci]>=matches[pi,ci-1])pi--;else ci--;}
  }  // A complete viewport replacement can indicate missed scrolls; report it rather than hide it.
  LastHadGap=overlap==0 && previous.Count>0;
  var added=current.Skip(overlap).ToList();previous=current;return added;
 }
 public void Reset()=>previous=[];
}
