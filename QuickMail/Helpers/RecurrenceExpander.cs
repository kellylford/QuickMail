using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;

namespace QuickMail.Helpers;

/// <summary>
/// Expands a recurring event's master start + <see cref="RecurrenceRule"/> into occurrence start
/// times. Works entirely in local wall-clock time (AddDays/AddMonths/AddYears preserve the time of
/// day across DST — a 9:00 weekly meeting stays 9:00), and bounds work with a safety cap.
/// </summary>
public static class RecurrenceExpander
{
    // ~10 years of daily occurrences — a backstop against runaway/infinite series.
    private const int DefaultSafetyCap = 3660;

    /// <summary>
    /// Returns occurrence start times (local) that fall within [windowStart, windowEnd), honoring
    /// COUNT and UNTIL. Occurrences are produced in chronological order.
    /// </summary>
    public static List<DateTime> Expand(DateTime start, RecurrenceRule rule,
                                        DateTime windowStart, DateTime windowEnd,
                                        int safetyCap = DefaultSafetyCap)
    {
        var result = new List<DateTime>();
        var count = 0;
        foreach (var occ in Occurrences(start, rule, safetyCap))
        {
            if (rule.Count is int max && count >= max) break;
            if (rule.Until is DateTime until && occ.Date > until.Date) break;
            count++;
            if (occ >= windowEnd) break;          // occurrences increase monotonically
            if (occ >= windowStart) result.Add(occ);
        }
        return result;
    }

    /// <summary>Lazily yields occurrence start times from <paramref name="start"/> in order.</summary>
    private static IEnumerable<DateTime> Occurrences(DateTime start, RecurrenceRule rule, int safetyCap)
    {
        var emitted = 0;

        if (rule.Frequency == RecurrenceFrequency.Weekly && rule.ByDay.Count > 0)
        {
            var days = rule.ByDay.Distinct().OrderBy(d => (int)d).ToList();
            var timeOfDay = start.TimeOfDay;
            var weekAnchor = start.Date.AddDays(-(int)start.DayOfWeek); // Sunday of the start's week
            var week = 0;
            while (emitted < safetyCap)
            {
                var baseDay = weekAnchor.AddDays(7 * rule.Interval * week);
                foreach (var d in days)
                {
                    var occ = baseDay.AddDays((int)d) + timeOfDay;
                    if (occ < start) continue; // days earlier in the first week than the actual start
                    yield return occ;
                    if (++emitted >= safetyCap) yield break;
                }
                week++;
            }
        }
        else
        {
            var cursor = start;
            while (emitted < safetyCap)
            {
                yield return cursor;
                emitted++;
                cursor = rule.Frequency switch
                {
                    RecurrenceFrequency.Daily => cursor.AddDays(rule.Interval),
                    RecurrenceFrequency.Weekly => cursor.AddDays(7 * rule.Interval),
                    RecurrenceFrequency.Monthly => cursor.AddMonths(rule.Interval),
                    RecurrenceFrequency.Yearly => cursor.AddYears(rule.Interval),
                    _ => cursor.AddDays(rule.Interval),
                };
            }
        }
    }
}
