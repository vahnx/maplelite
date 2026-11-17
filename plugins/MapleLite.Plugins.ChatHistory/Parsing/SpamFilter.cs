// Ported unchanged from the MapleMemory app (only the namespace differs).
using System.Text.RegularExpressions;
namespace MapleLite.Plugins.ChatHistory;

// This is separate from viewport deduplication: suppress advertisements repeated by
// the SAME parsed sender, never fuzzy-match player names or special event history.
public sealed class SpamFilter {
 public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
 const int MaxItems=10000;
 sealed record Seen(string Sender,string Channel,string Direction,string Body,string Numbers,DateTimeOffset Time);
 readonly List<Seen> recent=[];
 public long Suppressed {get;private set;}
 public static (string Sender,string Body)? ParseSender(Entry entry){
  if(entry.Channel is "Trade" or "Fame" or "System")return null;
  var parsed=MessageParser.Parse(entry.Text);
  return parsed.Username.Length>0&&parsed.Body.Length>0?(parsed.Username,parsed.Body):null;
 }
 static string ChannelKey(string channel)=>channel=="Unknown"?"General":channel;
 static string Normalize(string body)=>Regex.Replace(body.ToLowerInvariant(),@"[^\p{L}\p{N}]", "");
 // Numbers and signs are substantive: never suppress a new price, quantity or stat.
 static string Numbers(string body)=>string.Join("|",Regex.Matches(body,@"[+-]?\d+(?:[.,]\d+)*\s*[kKmMbB%]?").Select(m=>m.Value.Replace(" ","").ToLowerInvariant()));
 static bool Similar(string a,string b){
  if(a==b)return true;
  int budget=Math.Min(8,Math.Max(1,Math.Min(a.Length,b.Length)/12));
  if(Math.Min(a.Length,b.Length)<5||Math.Abs(a.Length-b.Length)>budget)return false;
  int[] row=Enumerable.Range(0,b.Length+1).ToArray();
  for(int i=1;i<=a.Length;i++){int diag=row[0],minimum=i;row[0]=i;for(int j=1;j<=b.Length;j++){int old=row[j];row[j]=Math.Min(Math.Min(row[j]+1,row[j-1]+1),diag+(a[i-1]==b[j-1]?0:1));diag=old;minimum=Math.Min(minimum,row[j]);}if(minimum>budget)return false;}
  return row[b.Length]<=budget;
 }
 void Prune(DateTimeOffset time){recent.RemoveAll(s=>time-s.Time>Window);if(recent.Count>MaxItems)recent.RemoveRange(0,recent.Count-MaxItems);}
 public bool ShouldSuppress(Entry e){
  var parsed=ParseSender(e);if(parsed==null)return false;
  Prune(e.Time);var (sender,body)=parsed.Value;string normal=Normalize(body),numbers=Numbers(body);if(normal.Length==0)return false;
  for(int i=recent.Count-1;i>=0;i--){var s=recent[i];if(e.Time<s.Time||e.Time-s.Time>Window)continue;
   if(string.Equals(sender,s.Sender,StringComparison.OrdinalIgnoreCase)&&ChannelKey(e.Channel)==ChannelKey(s.Channel)&&e.Direction==s.Direction&&numbers==s.Numbers&&Similar(normal,s.Body)){
    recent[i]=s with{Time=e.Time};Suppressed++;return true;
   }
  }return false;
 }
 // Call only after saving the first message successfully, so a failed disk write
 // cannot cause the unsaved message to be suppressed on retry.
 public void Remember(Entry e){var parsed=ParseSender(e);if(parsed==null)return;Prune(e.Time);var(sender,body)=parsed.Value;var normal=Normalize(body);if(normal.Length==0)return;recent.Add(new(sender,e.Channel,e.Direction,normal,Numbers(body),e.Time));if(recent.Count>MaxItems)recent.RemoveAt(0);}
}

