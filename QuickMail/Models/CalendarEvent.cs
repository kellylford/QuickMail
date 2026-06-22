using System;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QuickMail.Models;

/// <summary>
/// A calendar event harvested from a cached message that contained a
/// <c>text/calendar</c> MIME part. One row per unique (Uid, AccountId).
/// Persisted in the <c>CalendarEvent</c> SQLite table.
/// </summary>
public partial class CalendarEvent : ObservableObject
{
    /// <summary>ICS VEVENT UID — the stable identity of the event across updates.</summary>
    public string Uid { get; set; } = string.Empty;

    /// <summary>Account that received the invite. Events from all accounts merge into one calendar.</summary>
    public Guid AccountId { get; set; }

    public string Summary { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Organizer { get; set; } = string.Empty;
    public string OrganizerName { get; set; } = string.Empty;

    /// <summary>Event start (UTC ticks). Null if the ICS had no DTSTART.</summary>
    public long? StartTimeTicks { get; set; }

    /// <summary>Event end (UTC ticks). Null if the ICS had no DTEND.</summary>
    public long? EndTimeTicks { get; set; }

    /// <summary>ICS SEQUENCE number — bumped by the organizer on updates.</summary>
    public string? Sequence { get; set; }

    /// <summary>ICS METHOD (REQUEST, CANCEL, REPLY, etc.).</summary>
    public string? Method { get; set; }

    /// <summary>MessageId (per-folder storage key) of the source invite email.</summary>
    public string SourceMessageId { get; set; } = string.Empty;

    /// <summary>Folder name of the source invite email.</summary>
    public string SourceFolder { get; set; } = string.Empty;

    [ObservableProperty]
    private CalendarResponseStatus _responseStatus = CalendarResponseStatus.Pending;

    /// <summary>Start time as a nullable DateTime (local), for display and sorting.</summary>
    public DateTime? StartTime => StartTimeTicks.HasValue
        ? new DateTime(StartTimeTicks.Value, DateTimeKind.Utc).ToLocalTime()
        : null;

    /// <summary>End time as a nullable DateTime (local), for display.</summary>
    public DateTime? EndTime => EndTimeTicks.HasValue
        ? new DateTime(EndTimeTicks.Value, DateTimeKind.Utc).ToLocalTime()
        : null;

    /// <summary>
    /// Human-readable, screen-reader-friendly one-line summary for the calendar list.
    /// Example: "Team standup, today 10:00 to 10:30, Accepted. Location: Zoom."
    /// </summary>
    public string DisplayLine
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(string.IsNullOrWhiteSpace(Summary) ? "Calendar event" : Summary);

            if (StartTime.HasValue)
            {
                var now = DateTime.Now;
                var start = StartTime.Value;
                var dayWord = start.Date == now.Date ? "today"
                    : start.Date == now.Date.AddDays(1) ? "tomorrow"
                    : start.Date == now.Date.AddDays(-1) ? "yesterday"
                    : null;
                var datePart = dayWord ?? start.ToString("ddd, MMM d");
                sb.Append(", ").Append(datePart).Append(' ').Append(start.ToString("t"));
                if (EndTime.HasValue && EndTime.Value.TimeOfDay != start.TimeOfDay)
                    sb.Append(" to ").Append(EndTime.Value.ToString("t"));
            }

            sb.Append(", ").Append(ResponseStatus.ToString());
            if (!string.IsNullOrWhiteSpace(Location))
                sb.Append(". Location: ").Append(Location);
            if (!string.IsNullOrWhiteSpace(OrganizerName))
                sb.Append(". Organizer: ").Append(OrganizerName);
            else if (!string.IsNullOrWhiteSpace(Organizer))
                sb.Append(". Organizer: ").Append(Organizer);

            return sb.ToString();
        }
    }
}