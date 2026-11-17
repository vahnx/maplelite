// Ported unchanged from the MapleMemory app (only the namespace differs).
using System.Text.RegularExpressions;
namespace MapleLite.Plugins.ChatHistory;

// OCR often reads the same megaphone or chat line twice: once with its beginning cut off
// ("Scroll: Intermediatel TRADING…") and once complete ("[Gloves Atta'! croll: Intermediatel TRADING…").
// A later line is a partial repeat when its text is the tail of an already-saved line in the same channel
// within a few minutes. Lines with different player names are never merged, and the match must cover
// almost all of the shorter line, so unrelated ads that merely end alike are kept.
public sealed class PartialRepeatFilter {
 static readonly TimeSpan Window = TimeSpan.FromMinutes(3);
 const int MaxItems = 500;
 sealed record Seen(string Channel, string User, string Key, DateTimeOffset Time);
 readonly List<Seen> recent = [];
 public long Suppressed { get; private set; }

 static string Key(string text) => Regex.Replace(MessageParser.Parse(text).Body.ToLowerInvariant(), @"[^\p{L}\p{N}]", "");
 static string User(string text) => MessageParser.Parse(text).Username;
 static int CommonSuffix(string a, string b) {
  int n = 0;
  while (n < a.Length && n < b.Length && a[a.Length - 1 - n] == b[b.Length - 1 - n]) n++;
  return n;
 }
 static bool SameUserOrUnknown(string a, string b) => a.Length == 0 || b.Length == 0 || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

 void Prune(DateTimeOffset now) {
  recent.RemoveAll(s => now - s.Time > Window || now < s.Time - Window);
  if (recent.Count > MaxItems) recent.RemoveRange(0, recent.Count - MaxItems);
 }

 // True when the entry is a shorter partial copy of a line already saved. A longer copy is kept, so the
 // complete text still reaches the journal.
 public bool ShouldSkip(Entry e) {
  Prune(e.Time);
  string key = Key(e.Text), user = User(e.Text);
  if (key.Length < 16) return false;
  foreach (var s in recent) {
   if (s.Channel != e.Channel || !SameUserOrUnknown(s.User, user) || s.Key == key) continue;
   var shorter = s.Key.Length <= key.Length ? s.Key : key;
   var longer = ReferenceEquals(shorter, s.Key) ? key : s.Key;
   if (shorter.Length < 16 || longer.Length <= shorter.Length) continue;
   // The shorter copy must end where the longer one ends. Two characters of slack covers OCR at the cut.
   if (CommonSuffix(shorter, longer) < shorter.Length - 2) continue;
   if (ReferenceEquals(shorter, key)) { Suppressed++; return true; }
  }
  return false;
 }

 // Call only after the entry was saved successfully.
 public void Remember(Entry e) {
  Prune(e.Time);
  string key = Key(e.Text);
  if (key.Length > 0) recent.Add(new(e.Channel, User(e.Text), key, e.Time));
 }
}
