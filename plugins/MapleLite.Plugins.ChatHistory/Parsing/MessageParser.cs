// Ported unchanged from the MapleMemory app (only the namespace differs).
using System.Text.RegularExpressions;
namespace MapleLite.Plugins.ChatHistory;
public sealed record MessageParts(string GameTime,string Username,string Body,string Item="",string Side="");
public static class MessageParser {
 // Display option: drop the "@@@@" filler people pad their ads with. The saved log keeps the raw text.
 public static bool StripTrailingAt=true;
 // OCR often reads zero as O and one as I/l inside a timestamp; the minute bracket can read as 1, I or l.
 static readonly Regex Stamp=new(@"^\s*[\[\(]?(?<h>[\dOoIl]\s*[\dOoIl]?)\s*[:;]\s*(?<m>[\dOoIl]\s*[\dOoIl])[\]\)1|Il]?\s*",RegexOptions.Compiled|RegexOptions.Singleline);
 static int Digits(string s){var d=s.Replace(" ","").Replace('O','0').Replace('o','0').Replace('I','1').Replace('l','1');return int.TryParse(d,out int v)?v:-1;}
 static readonly Regex Label=new(@"^\[(?:Buddy|Party|Guild|Alliance|Whisper|General)\]\s*",RegexOptions.IgnoreCase|RegexOptions.Compiled|RegexOptions.Singleline);
 // Maple names cannot contain spaces. Repair only a split numeric suffix; do not
 // fuzzy-match names, merge letter substitutions, or infer a sender from ad text.
 static readonly Regex Sender=new(@"^(?<name>[A-Za-z0-9_]{2,16}(?:\s+\d{1,4})?)\s*[:：•]\s*(?<body>.*)$",RegexOptions.Compiled|RegexOptions.Singleline);
 static readonly Regex Whisper=new(@"^\[(?:From|To)\s+(?<name>[A-Za-z0-9_]{2,16})\]\s*:?\s*(?<body>.*)$",RegexOptions.IgnoreCase|RegexOptions.Compiled|RegexOptions.Singleline);
 // Global lines: the first [brackets] hold the player name, the second [brackets] hold a linked item.
 static readonly Regex Bracketed=new(@"^\[(?<name>[^\[\]]{2,25})\]\s*(?:\[(?<item>[^\[\]]{1,60})\])?\s*:?\s*(?<body>.*)$",RegexOptions.Compiled|RegexOptions.Singleline);
 static readonly Regex NotAPlayer=new(@"^(?:Buddy|Party|Guild|Alliance|Whisper|General|Notice|System|Megaphone|Trade|Fame|From|To)\b",RegexOptions.IgnoreCase|RegexOptions.Compiled);
 // A linked item at the start of a message: "[Work Gloves] S>…".
 static readonly Regex ItemStrict=new(@"^\[(?<item>[^\[\]]{2,60})\]\s*(?<body>.*)$",RegexOptions.Compiled|RegexOptions.Singleline);
 // OCR often reads the closing bracket as ) l I 1 or |: "[Black Cat Beaniel B< …". Accepted only when a
 // buy/sell arrow follows, so ordinary words are not cut in half.
 static readonly Regex ItemLoose=new(@"^\[(?<item>[^\[\]]{2,60}?)[\)lI1|]\s*(?<body>[SsBb]\s*[<>].*)$",RegexOptions.Compiled|RegexOptions.Singleline);
 // Green whisper lines appear as "Name>> message" (no colon). Requires a 2+ character name so
 // ad fragments like "S>int+10" are not read as senders.
 static readonly Regex Arrow=new(@"^(?<name>[A-Za-z0-9_]{2,16}(?:\s+\d{1,4})?)\s*>{1,2}\s*(?<body>.*)$",RegexOptions.Compiled|RegexOptions.Singleline);
 // Buying/selling prefixes at the start of an ad: S> S< S > (selling), B> B< (buying), or a lowercase s / b token.
 static readonly Regex SellPrefix=new(@"^\s*(?:S\s*[<>]|s\s*[<>]|s\s)",RegexOptions.Compiled);
 static readonly Regex BuyPrefix=new(@"^\s*(?:B\s*[<>]|b\s*[<>]|b\s)",RegexOptions.Compiled);
 // OCR reads "@@@@" decoration as "aaaa"; a standalone run of 3+ a's is restored to @.
 static readonly Regex AFiller=new(@"(?<![A-Za-z0-9])a{3,}(?![A-Za-z0-9])",RegexOptions.Compiled);
 static readonly Regex TrailingAt=new(@"[ \t]*@[@ \t]*(?=\r?\n|$)",RegexOptions.Compiled);
 public static string StripStamp(string raw)=>Stamp.Replace(raw,"",1);
 public static string Side(string body)=>SellPrefix.IsMatch(body)?"Selling":BuyPrefix.IsMatch(body)?"Buying":"";
 // Splits a leading [item] off the text. With requireSide, a properly closed bracket only counts as an
 // item when a buy/sell prefix follows it (otherwise it may be a player name).
 static bool TakeItem(string text,bool requireSide,out string item,out string body){
  var strict=ItemStrict.Match(text);
  if(strict.Success&&!NotAPlayer.IsMatch(strict.Groups["item"].Value.Trim())&&(!requireSide||Side(strict.Groups["body"].Value).Length>0)){item=strict.Groups["item"].Value.Trim();body=strict.Groups["body"].Value;return true;}
  var loose=ItemLoose.Match(text);
  if(loose.Success&&!NotAPlayer.IsMatch(loose.Groups["item"].Value.Trim())){item=loose.Groups["item"].Value.Trim();body=loose.Groups["body"].Value;return true;}
  item="";body=text;return false;
 }
 public static MessageParts Parse(string raw){
  string time="",text=raw;var stamp=Stamp.Match(text);
  if(stamp.Success){int hour=Digits(stamp.Groups["h"].Value),minute=Digits(stamp.Groups["m"].Value);if(hour>=0&&minute>=0&&hour<24&&minute<60){time=$"{hour:00}:{minute:00}";text=text[stamp.Length..];}}
  text=Regex.Replace(text,@"^[\s:：•|]+","");
  var whisper=Whisper.Match(text);if(whisper.Success){string w=Regex.Replace(whisper.Groups["name"].Value,@"\s+","");if(w.Length<=16)return Finish(time,w,whisper.Groups["body"].Value,"");}
  // "[Work Gloves] S>…": a single bracket followed by a buy/sell prefix is the item, not a name.
  if(TakeItem(text,true,out var onlyItem,out var afterItem))return Finish(time,"",afterItem,onlyItem);
  var bracket=Bracketed.Match(text);
  if(bracket.Success&&!NotAPlayer.IsMatch(bracket.Groups["name"].Value.Trim())){string n=bracket.Groups["name"].Value.Trim();if(n.Length>0)return Finish(time,n,bracket.Groups["body"].Value,bracket.Groups["item"].Value.Trim());}
  var match=Sender.Match(Label.Replace(text,""));if(!match.Success)match=Arrow.Match(Label.Replace(text,""));
  if(match.Success){string name=Regex.Replace(match.Groups["name"].Value,@"\s+","");if(name.Length<=16)return Finish(time,name,match.Groups["body"].Value,"");}
  return Finish(time,"",text,"");
 }
 static MessageParts Finish(string time,string name,string body,string item){
  body=body.Trim();
  // "Name : [Item] B< …": once the sender is known, a leading bracket is the linked item.
  if(item.Length==0&&TakeItem(body,name.Length==0,out var found,out var rest)){item=found;body=rest.Trim();}
  body=AFiller.Replace(body,m=>new string('@',m.Length));
  if(StripTrailingAt)body=TrailingAt.Replace(body,"").Trim();
  return new(time,name,body,item,Side(body));
 }
}
