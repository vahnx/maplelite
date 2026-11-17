using System.Text.RegularExpressions;

namespace MapleLite.Core.Game;

/// <summary>
/// Finds which lines of a scrolling text box are new since the last read, tolerating OCR noise, rows OCR
/// missed, and repeated lines. From the MapleMemory app (unchanged); shared so any plugin reading a scrolling
/// box (chat, pickup notices, …) gets the same de-duplication.
/// </summary>
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
