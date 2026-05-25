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
    /// Parses an ICS file from raw text content.
    /// Returns null if the content is not a valid meeting request.
    /// </summary>
    public static IcsModel? Parse(string icsContent)
    {
        if (string.IsNullOrWhiteSpace(icsContent)) return null;

        try
        {
            var model = new IcsModel();
            var lines = UnfoldLines(icsContent);

            bool inVevent = false;
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                // METHOD is at the VCALENDAR level, not inside VEVENT
                if (!inVevent && line.StartsWith("METHOD:", StringComparison.OrdinalIgnoreCase))
                {
                    model.Method = line[7..];
                    continue;
                }

                if (line.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    inVevent = true;
                    continue;
                }
                if (line.StartsWith("END:VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    inVevent = false;
                    continue;
                }
                if (!inVevent) continue;

                var colonIdx = line.IndexOf(':');
                if (colonIdx < 0) continue;

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
                        model.StartTime = ParseIcsDateTime(value);
                        break;
                    case "DTEND":
                        model.EndTime = ParseIcsDateTime(value);
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
                }
            }

            // Only return if we found a meaningful event
            if (model.StartTime.HasValue || !string.IsNullOrWhiteSpace(model.Summary))
                return model;

            return null;
        }
        catch
        {
            return null;
        }
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

    private static DateTime? ParseIcsDateTime(string value)
    {
        // Formats: 20260101T120000Z, 20260101T120000, 20260101
        value = value.Trim();
        if (value.Length >= 15 && value[8] == 'T')
        {
            var isUtc = value.EndsWith("Z");
            var datePart = value[..8];
            var timePart = value.Substring(9, 6);
            if (DateTime.TryParseExact($"{datePart}{timePart}",
                    isUtc ? "yyyyMMddHHmmss" : "yyyyMMddHHmmss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    isUtc ? System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal
                          : System.Globalization.DateTimeStyles.AssumeLocal,
                    out var dt))
                return dt;
        }
        if (value.Length >= 8 && DateTime.TryParseExact(value[..8], "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal,
                out var dateOnly))
            return dateOnly;
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
