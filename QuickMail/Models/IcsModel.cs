using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace QuickMail.Models;

/// <summary>
/// Parsed representation of an ICS calendar invite (meeting request).
/// Extracted from text/calendar MIME parts in incoming messages.
/// </summary>
public class IcsModel
{
    public string? Organizer { get; set; }
    public string? OrganizerName { get; set; }
    public string? Summary { get; set; }
    public string? Description { get; set; }
    public string? Location { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? Uid { get; set; }
    public string? Sequence { get; set; }
    public string? Method { get; set; } // REQUEST, CANCEL, REPLY, etc.

    /// <summary>True when DTSTART was a date-only value (VALUE=DATE) — an all-day event.
    /// For all-day events <see cref="EndTime"/> keeps the ICS DTEND semantics: EXCLUSIVE
    /// (midnight of the day after the last day), per RFC 5545.</summary>
    public bool IsAllDay { get; set; }

    /// <summary>Raw RRULE value for a recurring series master (e.g. "FREQ=WEEKLY;BYDAY=TU"),
    /// or null for a one-off. Parse with <see cref="RecurrenceRule.Parse"/>.</summary>
    public string? RecurrenceRule { get; set; }

    /// <summary>Raw RECURRENCE-ID value when this VEVENT is an overridden instance of a
    /// recurring series (not the series master). Null for masters and one-offs.</summary>
    public string? RecurrenceId { get; set; }

    /// <summary>ICS STATUS value (CONFIRMED, TENTATIVE, CANCELLED), or null when absent.</summary>
    public string? Status { get; set; }

    /// <summary>Excluded occurrence starts (EXDATE), normalized to machine-local wall-clock —
    /// the same shape <see cref="CalendarEvent.AddExDate"/> expects. Empty for one-offs and
    /// untouched series.</summary>
    public List<DateTime> ExDates { get; } = [];

    /// <summary>
    /// Human-readable summary for screen reader announcement and display.
    /// </summary>
    public string DisplaySummary
    {
        get
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(Summary))
                sb.AppendLine($"Event: {Summary}");
            if (!string.IsNullOrWhiteSpace(OrganizerName))
                sb.AppendLine($"Organizer: {OrganizerName}");
            else if (!string.IsNullOrWhiteSpace(Organizer))
                sb.AppendLine($"Organizer: {Organizer}");
            if (StartTime.HasValue)
            {
                sb.Append($"When: {StartTime.Value.ToLocalTime():f}");
                if (EndTime.HasValue)
                    sb.Append($" - {EndTime.Value.ToLocalTime():t}");
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(Location))
                sb.AppendLine($"Location: {Location}");
            if (!string.IsNullOrWhiteSpace(Description))
            {
                sb.AppendLine();
                sb.AppendLine(Description);
            }
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// Brief one-line summary for the message list / status bar.
    /// </summary>
    public string BriefSummary
    {
        get
        {
            var title = Summary ?? "Calendar Event";
            if (StartTime.HasValue)
                return $"{title} — {StartTime.Value.ToLocalTime():f}";
            return title;
        }
    }

    /// <summary>
    /// Parses an ICS file from raw text content. Multi-VEVENT calendars yield the FIRST
    /// meaningful VEVENT (invite emails carry a single VEVENT, so this matters only for
    /// pathological input). Returns null if the content is not a valid meeting request.
    /// </summary>
    public static IcsModel? Parse(string icsContent)
    {
        var events = ParseAllEvents(icsContent);
        return events.Count > 0 ? events[0] : null;
    }

    /// <summary>
    /// Parses EVERY VEVENT in an ICS calendar body — the CalDAV sync path, where one
    /// calendar-data body can carry a recurring series master plus overridden instances
    /// (RECURRENCE-ID). Each VEVENT becomes its own model with the same per-property
    /// handling as <see cref="Parse"/> (TZID-aware date-times, escaping, folding), plus
    /// RRULE / EXDATE / RECURRENCE-ID / STATUS / all-day capture. The VCALENDAR-level
    /// METHOD is applied to every event that did not carry its own. Events with neither
    /// a start time nor a summary are dropped. Never throws; malformed input yields [].
    /// </summary>
    public static List<IcsModel> ParseAllEvents(string icsContent)
    {
        var result = new List<IcsModel>();
        if (string.IsNullOrWhiteSpace(icsContent)) return result;

        try
        {
            var lines = UnfoldLines(icsContent);
            string? calendarMethod = null;
            IcsModel? current = null;

            void FlushCurrent()
            {
                // Only keep meaningful events (same criterion Parse always used).
                if (current != null &&
                    (current.StartTime.HasValue || !string.IsNullOrWhiteSpace(current.Summary)))
                    result.Add(current);
                current = null;
            }

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                if (line.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    FlushCurrent(); // defensive: unterminated previous VEVENT
                    current = new IcsModel();
                    continue;
                }
                if (line.StartsWith("END:VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    FlushCurrent();
                    continue;
                }
                if (current == null)
                {
                    // METHOD is at the VCALENDAR level, not inside VEVENT
                    if (line.StartsWith("METHOD:", StringComparison.OrdinalIgnoreCase))
                        calendarMethod = line[7..];
                    continue;
                }

                ParseVEventLine(current, line);
            }
            FlushCurrent(); // defensive: truncated input missing END:VEVENT

            foreach (var evt in result)
                evt.Method ??= calendarMethod;

            return result;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Applies one unfolded content line to the VEVENT being built.</summary>
    private static void ParseVEventLine(IcsModel model, string line)
    {
        var colonIdx = line.IndexOf(':');
        if (colonIdx < 0) return;

        var prop = line[..colonIdx];
        var value = line[(colonIdx + 1)..];

        // Handle property parameters (e.g. "DTSTART;TZID=America/Chicago:20260101T120000")
        var semicolonIdx = prop.IndexOf(';');
        var propName = semicolonIdx >= 0 ? prop[..semicolonIdx] : prop;

        switch (propName.ToUpperInvariant())
        {
            case "ORGANIZER":
                model.OrganizerName = ExtractCnParam(prop);
                model.Organizer = ExtractMailtoValue(value);
                if (string.IsNullOrWhiteSpace(model.OrganizerName))
                    model.OrganizerName = model.Organizer;
                break;
            case "SUMMARY":
                model.Summary = UnescapeIcsText(value);
                break;
            case "DESCRIPTION":
                model.Description = UnescapeIcsText(value);
                break;
            case "LOCATION":
                model.Location = UnescapeIcsText(value);
                break;
            case "DTSTART":
                model.StartTime = ParseIcsDateTime(value, ExtractTzidParam(prop));
                model.IsAllDay = IsDateOnly(prop, value);
                break;
            case "DTEND":
                model.EndTime = ParseIcsDateTime(value, ExtractTzidParam(prop));
                break;
            case "UID":
                model.Uid = value;
                break;
            case "SEQUENCE":
                model.Sequence = value;
                break;
            case "METHOD":
                model.Method = value;
                break;
            case "RRULE":
                model.RecurrenceRule = value;
                break;
            case "RECURRENCE-ID":
                model.RecurrenceId = value;
                break;
            case "STATUS":
                model.Status = value.Trim();
                break;
            case "EXDATE":
                // EXDATE can carry multiple comma-separated values, each honoring the
                // property's TZID. Normalize to machine-local wall-clock, matching what
                // CalendarEvent.AddExDate stores and RecurrenceExpander compares against.
                var tzid = ExtractTzidParam(prop);
                foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parsed = ParseIcsDateTime(part.Trim(), tzid);
                    if (parsed.HasValue)
                        model.ExDates.Add(parsed.Value.Kind == DateTimeKind.Utc
                            ? parsed.Value.ToLocalTime()
                            : parsed.Value);
                }
                break;
        }
    }

    /// <summary>True when a DTSTART property is date-only: explicit VALUE=DATE parameter,
    /// or a bare 8-digit yyyyMMdd value.</summary>
    private static bool IsDateOnly(string propWithParams, string value)
    {
        foreach (var part in propWithParams.Split(';'))
            if (string.Equals(part.Trim(), "VALUE=DATE", StringComparison.OrdinalIgnoreCase))
                return true;
        return value.Trim().Length == 8;
    }

    /// <summary>
    /// Generates an ICS reply (accept/decline/tentative) for sending back to the organizer.
    /// </summary>
    public string GenerateReply(string attendeeEmail, string attendeeName, string partStat)
    {
        var now = DateTime.UtcNow;
        var sb = new StringBuilder();
        sb.AppendLine("BEGIN:VCALENDAR");
        sb.AppendLine("VERSION:2.0");
        sb.AppendLine("PRODID:-//QuickMail//EN");
        sb.AppendLine("METHOD:REPLY");
        sb.AppendLine("BEGIN:VEVENT");
        if (!string.IsNullOrWhiteSpace(Uid))
            sb.AppendLine($"UID:{Uid}");
        if (StartTime.HasValue)
            sb.AppendLine($"DTSTART:{StartTime.Value.ToUniversalTime():yyyyMMddTHHmmssZ}");
        if (EndTime.HasValue)
            sb.AppendLine($"DTEND:{EndTime.Value.ToUniversalTime():yyyyMMddTHHmmssZ}");
        if (!string.IsNullOrWhiteSpace(Summary))
            sb.AppendLine($"SUMMARY:{EscapeIcsText(Summary)}");
        if (!string.IsNullOrWhiteSpace(Organizer))
            sb.AppendLine($"ORGANIZER:{Organizer}");
        sb.AppendLine($"DTSTAMP:{now:yyyyMMddTHHmmssZ}");
        sb.AppendLine($"ATTENDEE;CN={EscapeIcsText(attendeeName)};PARTSTAT={partStat}:mailto:{attendeeEmail}");
        sb.AppendLine("END:VEVENT");
        sb.AppendLine("END:VCALENDAR");
        return sb.ToString();
    }

    /// <summary>
    /// Serializes a calendar event to a standalone .ics file body (METHOD:PUBLISH) suitable for
    /// saving to disk and importing into any calendar application. All-day events use DATE values;
    /// timed events use UTC; a recurrence rule is carried through as RRULE.
    /// </summary>
    public static string ExportEvent(CalendarEvent evt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BEGIN:VCALENDAR");
        sb.AppendLine("VERSION:2.0");
        sb.AppendLine("PRODID:-//QuickMail//EN");
        sb.AppendLine("METHOD:PUBLISH");
        sb.AppendLine("BEGIN:VEVENT");
        sb.AppendLine($"UID:{(string.IsNullOrWhiteSpace(evt.Uid) ? Guid.NewGuid().ToString("N") : evt.Uid)}");
        sb.AppendLine($"DTSTAMP:{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}");

        if (evt.StartTime.HasValue)
        {
            if (evt.IsAllDay)
            {
                // All-day: DATE values; DTEND is exclusive per RFC 5545, so day after the last day.
                var startDay = evt.StartTime.Value.Date;
                var endDayExclusive = (evt.EndTime ?? evt.StartTime.Value).Date.AddDays(1);
                sb.AppendLine($"DTSTART;VALUE=DATE:{startDay:yyyyMMdd}");
                sb.AppendLine($"DTEND;VALUE=DATE:{endDayExclusive:yyyyMMdd}");
            }
            else
            {
                sb.AppendLine($"DTSTART:{evt.StartTime.Value.ToUniversalTime():yyyyMMdd'T'HHmmss'Z'}");
                if (evt.EndTime.HasValue)
                    sb.AppendLine($"DTEND:{evt.EndTime.Value.ToUniversalTime():yyyyMMdd'T'HHmmss'Z'}");
            }
        }

        if (!string.IsNullOrWhiteSpace(evt.RecurrenceRule))
            sb.AppendLine($"RRULE:{evt.RecurrenceRule}");
        if (!string.IsNullOrWhiteSpace(evt.Summary))
            sb.AppendLine($"SUMMARY:{EscapeIcsText(evt.Summary)}");
        if (!string.IsNullOrWhiteSpace(evt.Location))
            sb.AppendLine($"LOCATION:{EscapeIcsText(evt.Location)}");
        if (!string.IsNullOrWhiteSpace(evt.Description))
            sb.AppendLine($"DESCRIPTION:{EscapeIcsText(evt.Description)}");

        sb.AppendLine("END:VEVENT");
        sb.AppendLine("END:VCALENDAR");
        return sb.ToString();
    }

    // ── Parsing helpers ────────────────────────────────────────────────────────

    /// <summary>ICS lines can be folded (continued on next line with a leading space/tab).</summary>
    private static List<string> UnfoldLines(string content)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length > 0 && (trimmed[0] == ' ' || trimmed[0] == '\t'))
            {
                // Continuation of previous line
                current.Append(trimmed[1..]);
            }
            else
            {
                if (current.Length > 0)
                    result.Add(current.ToString());
                current.Clear();
                current.Append(trimmed);
            }
        }
        if (current.Length > 0)
            result.Add(current.ToString());
        return result;
    }

    /// <summary>Extracts the CN parameter from a property like "ORGANIZER;CN=John Doe:mailto:john@example.com".</summary>
    private static string? ExtractCnParam(string prop)
    {
        var match = Regex.Match(prop, @"CN=""?([^"":;]+)""?", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Extracts the email address from a mailto: URI value.</summary>
    private static string? ExtractMailtoValue(string value)
    {
        return value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            ? value[7..]
            : value;
    }

    /// <summary>
    /// Parses an ICS datetime value, honoring a TZID parameter when present. A value ending in
    /// 'Z' is UTC. A value with a resolvable <paramref name="tzid"/> is interpreted in that zone
    /// and converted to machine-local time, so a 10:00 Eastern meeting displays correctly for a
    /// Pacific user. A value with no TZID (or an unresolvable one) falls back to the historical
    /// behavior: treated as machine-local time.
    /// </summary>
    private static DateTime? ParseIcsDateTime(string value, string? tzid = null)
    {
        // Formats: 20260101T120000Z, 20260101T120000, 20260101
        value = value.Trim();
        if (value.Length >= 15 && value[8] == 'T')
        {
            var isUtc = value.EndsWith('Z');
            var datePart = value[..8];
            var timePart = value.Substring(9, 6);

            if (isUtc)
            {
                if (DateTime.TryParseExact($"{datePart}{timePart}", "yyyyMMddHHmmss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var utc))
                    return utc;
                return null;
            }

            if (DateTime.TryParseExact($"{datePart}{timePart}", "yyyyMMddHHmmss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var naive))
            {
                // TZID present and resolvable: the naive time is wall-clock in that zone.
                // .NET 8 resolves both IANA ids ("America/New_York") and Windows ids
                // ("Eastern Standard Time") on Windows 10+ via ICU.
                if (!string.IsNullOrWhiteSpace(tzid)
                    && TimeZoneInfo.TryFindSystemTimeZoneById(tzid, out var zone))
                {
                    var utcTime = TimeZoneInfo.ConvertTimeToUtc(
                        DateTime.SpecifyKind(naive, DateTimeKind.Unspecified), zone);
                    return utcTime.ToLocalTime();
                }
                // No/unknown TZID: historical behavior — assume machine-local.
                return DateTime.SpecifyKind(naive, DateTimeKind.Local);
            }
        }
        if (value.Length >= 8 && DateTime.TryParseExact(value[..8], "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal,
                out var dateOnly))
            return dateOnly;
        return null;
    }

    /// <summary>
    /// Extracts the TZID parameter from a property name with parameters, e.g.
    /// "DTSTART;TZID=America/Chicago" or "DTSTART;VALUE=DATE-TIME;TZID=\"Europe/Paris\"".
    /// Returns null when absent. Quotes are stripped.
    /// </summary>
    internal static string? ExtractTzidParam(string propWithParams)
    {
        foreach (var part in propWithParams.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase))
            {
                var v = trimmed[5..].Trim().Trim('"');
                return v.Length > 0 ? v : null;
            }
        }
        return null;
    }

    private static string UnescapeIcsText(string text)
    {
        return text
            .Replace("\\n", "\n")
            .Replace("\\N", "\n")
            .Replace("\\,", ",")
            .Replace("\\;", ";")
            .Replace("\\\\", "\\");
    }

    private static string EscapeIcsText(string text)
    {
        return text
            .Replace("\\", "\\\\")
            .Replace(",", "\\,")
            .Replace(";", "\\;")
            .Replace("\r\n", "\\n")
            .Replace("\n", "\\n");
    }
}
