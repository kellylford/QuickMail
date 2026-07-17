using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace QuickMail.Models;

public enum RecurrenceFrequency { Daily, Weekly, Monthly, Yearly }

/// <summary>
/// A parsed subset of an iCalendar RRULE: FREQ, INTERVAL, BYDAY (weekly), and one end condition
/// (COUNT or UNTIL). Persisted as the RRULE string in <see cref="CalendarEvent.RecurrenceRule"/>.
/// This is intentionally a practical subset — enough for daily/weekly/monthly/yearly repeats with
/// an optional weekly day set and an end — not full RFC 5545.
/// </summary>
public sealed class RecurrenceRule
{
    public RecurrenceFrequency Frequency { get; set; } = RecurrenceFrequency.Weekly;

    /// <summary>Every N periods. 1 = every day/week/month/year.</summary>
    public int Interval { get; set; } = 1;

    /// <summary>Weekly only: the days the event repeats on. Empty = use the start date's weekday.</summary>
    public List<DayOfWeek> ByDay { get; set; } = [];

    /// <summary>Total number of occurrences (including the first). Null = no count limit.</summary>
    public int? Count { get; set; }

    /// <summary>Last date an occurrence may fall on (inclusive, local date). Null = no until limit.</summary>
    public DateTime? Until { get; set; }

    /// <summary>True when the series never ends (no COUNT, no UNTIL).</summary>
    public bool IsInfinite => Count is null && Until is null;

    private static readonly Dictionary<DayOfWeek, string> DayCodes = new()
    {
        [DayOfWeek.Sunday] = "SU", [DayOfWeek.Monday] = "MO", [DayOfWeek.Tuesday] = "TU",
        [DayOfWeek.Wednesday] = "WE", [DayOfWeek.Thursday] = "TH", [DayOfWeek.Friday] = "FR",
        [DayOfWeek.Saturday] = "SA",
    };
    private static readonly Dictionary<string, DayOfWeek> DayFromCode =
        DayCodes.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>Serializes to an iCalendar RRULE value string (no "RRULE:" prefix).</summary>
    public string ToRRule()
    {
        var sb = new StringBuilder();
        sb.Append("FREQ=").Append(Frequency.ToString().ToUpperInvariant());
        if (Interval > 1) sb.Append(";INTERVAL=").Append(Interval);
        if (Frequency == RecurrenceFrequency.Weekly && ByDay.Count > 0)
            sb.Append(";BYDAY=").Append(string.Join(",", ByDay.Distinct().OrderBy(d => (int)d).Select(d => DayCodes[d])));
        if (Count is int c) sb.Append(";COUNT=").Append(c);
        else if (Until is DateTime u) sb.Append(";UNTIL=").Append(u.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>Parses an RRULE value string. Returns null if it has no recognizable FREQ.</summary>
    public static RecurrenceRule? Parse(string? rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule)) return null;
        var text = rrule.Trim();
        if (text.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase)) text = text[6..];

        var parts = text.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parts)
        {
            var eq = p.IndexOf('=');
            if (eq > 0) map[p[..eq].Trim()] = p[(eq + 1)..].Trim();
        }
        if (!map.TryGetValue("FREQ", out var freqStr)) return null;

        var rule = new RecurrenceRule();
        rule.Frequency = freqStr.ToUpperInvariant() switch
        {
            "DAILY" => RecurrenceFrequency.Daily,
            "WEEKLY" => RecurrenceFrequency.Weekly,
            "MONTHLY" => RecurrenceFrequency.Monthly,
            "YEARLY" => RecurrenceFrequency.Yearly,
            _ => RecurrenceFrequency.Weekly,
        };
        if (map.TryGetValue("INTERVAL", out var iv) && int.TryParse(iv, out var interval) && interval > 0)
            rule.Interval = interval;
        if (map.TryGetValue("BYDAY", out var byday))
            foreach (var code in byday.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (DayFromCode.TryGetValue(code.Trim().ToUpperInvariant(), out var dow))
                    rule.ByDay.Add(dow);
        if (map.TryGetValue("COUNT", out var cnt) && int.TryParse(cnt, out var count) && count > 0)
            rule.Count = count;
        if (map.TryGetValue("UNTIL", out var until))
        {
            var formats = new[] { "yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss", "yyyyMMdd" };
            if (DateTime.TryParseExact(until, formats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var u))
                rule.Until = u.ToLocalTime();
        }
        return rule;
    }

    /// <summary>Short human description, e.g. "Weekly on Tuesday" or "Every 2 weeks".</summary>
    public string Describe()
    {
        var unit = Frequency switch
        {
            RecurrenceFrequency.Daily => "day",
            RecurrenceFrequency.Weekly => "week",
            RecurrenceFrequency.Monthly => "month",
            _ => "year",
        };
        var head = Interval == 1 ? $"{Frequency}" : $"Every {Interval} {unit}s";
        if (Frequency == RecurrenceFrequency.Weekly && ByDay.Count > 0)
            head += " on " + string.Join(", ", ByDay.OrderBy(d => (int)d)
                .Select(d => CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(d)));
        if (Count is int c) head += $", {c} times";
        else if (Until is DateTime u) head += $", until {u:MMM d, yyyy}";
        return head;
    }
}
