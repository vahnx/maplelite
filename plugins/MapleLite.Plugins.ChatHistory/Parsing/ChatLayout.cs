// Ported unchanged from the MapleMemory app (only the namespace differs).
using System.Text.RegularExpressions;
namespace MapleLite.Plugins.ChatHistory;
public record ChatRow(string Text,double X,double Y,double Right,double Bottom,string? Hint=null);
public record ChatBlock(string Text,string? Hint,bool Partial);
public static class ChatLayout {
 public static bool Header(string text){var p=MessageParser.Parse(text);return p.GameTime.Length>0||p.Username.Length>0||Regex.IsMatch(text,@"^\[(Notice|System|Buddy|Party|Guild|Alliance|From|To)\b",RegexOptions.IgnoreCase);}
 public static List<ChatBlock> Assemble(IReadOnlyList<ChatRow> rows,int width,int height){
  var blocks=new List<ChatBlock>();var text=new List<string>();ChatRow? first=null,last=null;bool partial=false;
  void Finish(){if(text.Count>0)blocks.Add(new(string.Join("\n",text),first?.Hint,partial));text.Clear();first=last=null;partial=false;}
  foreach(var row in rows.OrderBy(r=>r.Y)){
   bool header=Header(row.Text);
   // Every chat message starts with "[". A line that does not is a wrapped continuation of the open message.
   bool continuation=first!=null && last!=null && !row.Text.TrimStart().StartsWith('[') && row.Y-last.Bottom<=Math.Max(10,last.Bottom-last.Y);
   if(!continuation){Finish();first=row;partial=!header;}
   text.Add(row.Text);last=row;
   if(row.Y<1 || row.Bottom>height-1)partial=true;
  }Finish();return blocks;
 }
}
// Hold a newly observed message briefly while a wrapped continuation is appearing.
// Emit the longest observed version on settlement, disappearance, or shutdown.
public sealed class MessageSettler {
 sealed class Pending {public Entry Entry;public DateTimeOffset LastChanged;public Pending(Entry e){Entry=e;LastChanged=e.Time;}}
 readonly List<Pending> pending=[];
 static string Key(string s)=>Regex.Replace(s.ToLowerInvariant(),@"\s+","");
 static bool Extension(Entry a,Entry b){var x=MessageParser.Parse(a.Text);var y=MessageParser.Parse(b.Text);if(x.Username.Length==0||x.Username!=y.Username||x.GameTime!=y.GameTime)return false;var aa=Key(x.Body);var bb=Key(y.Body);return aa.Length>=8&&bb.StartsWith(aa,StringComparison.Ordinal);}
 public List<Entry> Push(IEnumerable<Entry> added,DateTimeOffset now){
  var ready=new List<Entry>();foreach(var e in added){var found=pending.LastOrDefault(p=>Extension(p.Entry,e));if(found!=null){if(e.Text.Length>found.Entry.Text.Length){found.Entry=e with{Time=found.Entry.Time};found.LastChanged=now;}}else pending.Add(new(e));}
  for(int i=0;i<pending.Count;){if(now-pending[i].LastChanged>=TimeSpan.FromMilliseconds(300)){ready.Add(pending[i].Entry);pending.RemoveAt(i);}else i++;}return ready;
 }
 public List<Entry> Flush(){var result=pending.Select(p=>p.Entry).ToList();pending.Clear();return result;}
}

