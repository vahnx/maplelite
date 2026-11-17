// Ported unchanged from the MapleMemory app (only the namespace differs).
using System.Globalization;
namespace MapleLite.Plugins.ChatHistory;

// Minimum/maximum observed-time filter (local time). A date pins a bound to that day.
// A blank date makes the clock time apply on every day, e.g. "only show messages from 19:00 on".
public sealed record TimeFilter(DateTime? From, DateTime? To, TimeSpan? FromClock, TimeSpan? ToClock) {
 public static readonly TimeFilter Open = new(null, null, null, null);
 public bool Matches(DateTimeOffset time) {
  var local = ClockZone.Convert(time).DateTime;
  var clock = local.TimeOfDay;
  return (From == null || local >= From) && (To == null || local <= To)
      && (FromClock == null || clock >= FromClock) && (ToClock == null || clock <= ToClock);
 }
}

public static class TimeWindow {
 static readonly string[] Formats = [@"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss"];

 // Null means no limit on that side. "To 15:00" includes the whole minute (or second when typed).
 public static TimeSpan? Clock(string text, bool upper) {
  var clock = text.Trim();
  if (clock.Length == 0) return null;
  if (!TimeSpan.TryParseExact(clock, Formats, CultureInfo.InvariantCulture, out var t) || t < TimeSpan.Zero || t >= TimeSpan.FromDays(1))
   throw new FormatException("Enter a time as HH:mm or HH:mm:ss (24-hour clock).");
  if (upper) t += TimeSpan.FromTicks((clock.Count(c => c == ':') == 1 ? TimeSpan.TicksPerMinute : TimeSpan.TicksPerSecond) - 1);
  return t;
 }

 public static TimeFilter Build(DateTime? fromDay, string fromText, DateTime? toDay, string toText) {
  var fromClock = Clock(fromText, false);
  var toClock = Clock(toText, true);
  DateTime? from = null, to = null;
  if (fromDay != null) from = fromDay.Value.Date + (fromClock ?? TimeSpan.Zero);
  if (toDay != null) to = toDay.Value.Date + (toClock ?? (TimeSpan.FromDays(1) - TimeSpan.FromTicks(1)));
  if (from != null && to != null && from > to) throw new FormatException("From must be before To.");
  if (fromDay == null && toDay == null && fromClock != null && toClock != null && fromClock > toClock) throw new FormatException("From must be before To.");
  return new TimeFilter(from, to, fromDay == null ? fromClock : null, toDay == null ? toClock : null);
 }
}
