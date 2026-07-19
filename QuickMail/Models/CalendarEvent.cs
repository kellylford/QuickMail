using System;
using System.Collections.Generic;
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
    /// <summary>
    /// Sentinel account id for locally-authored events (not harvested from an email invite).
    /// Real accounts always have a non-empty <see cref="Guid"/>, so <see cref="Guid.Empty"/> is
    /// a safe "local calendar" marker that needs no schema change.
    /// </summary>
    public static readonly Guid LocalAccountId = Guid.Empty;

    /// <summary>ICS VEVENT UID — the stable identity of the event across updates.</summary>
    public string Uid { get; set; } = string.Empty;

    /// <summary>Account that received the invite. Events from all accounts merge into one calendar.</summary>
    public Guid AccountId { get; set; }

    /// <summary>
    /// True when the user created this event locally in QuickMail (vs. harvested from an email
    /// invite). Locally-authored events are editable and deletable; invite-sourced events are not.
    /// </summary>
    public bool IsUserCreated => AccountId == LocalAccountId;

    /// <summary>True for an all-day appointment (no meaningful start/end time of day).</summary>
    public bool IsAllDay { get; set; }

    /// <summary>
    /// True when this row is a SERVER-SYNCED calendar row — pulled from the account's online
    /// calendar (Microsoft via Graph, or Google via the Calendar API) by
    /// <see cref="Services.GraphCalendarSyncService"/> — rather than harvested from an email
    /// invite or authored locally. The column name <c>is_graph</c> predates the Google path but
    /// means exactly this for both providers. Server-synced rows are read-only in the UI (the
    /// server is the source of truth; v1 sync is read-down only) and are replaced wholesale on
    /// each sync, so the invite harvest and orphan cleanup must never touch them.
    /// </summary>
    public bool IsGraph { get; set; }

    /// <summary>
    /// Provider identifier of the specific calendar this server-synced row belongs to (Graph calendar
    /// id, Google calendarList id, or CalDAV collection href). Empty for locally-authored and
    /// invite-harvested rows, which have no distinct server calendar. Lets one account's calendars
    /// (Home, Work, Family) show as separate selectable tree nodes.
    /// </summary>
    public string CalendarId { get; set; } = string.Empty;

    /// <summary>Human-readable name of the calendar in <see cref="CalendarId"/> (the tree node label). Empty when untagged.</summary>
    public string CalendarName { get; set; } = string.Empty;

    /// <summary>
    /// iCalendar RRULE string for a repeating appointment (e.g. "FREQ=WEEKLY;BYDAY=TU"), or null/empty
    /// for a one-off. Parse with <see cref="Models.RecurrenceRule.Parse"/>.
    /// </summary>
    public string? RecurrenceRule { get; set; }

    /// <summary>True when this event repeats.</summary>
    public bool IsRecurring => !string.IsNullOrWhiteSpace(RecurrenceRule);

    /// <summary>
    /// Excluded occurrence starts for a recurring master, serialized as comma-separated local
    /// "yyyyMMddTHHmmss" values. Occurrences at these starts are skipped ("delete/detach just
    /// this one"). Null/empty for one-offs and untouched series.
    /// </summary>
    public string? ExDates { get; set; }

    /// <summary>Parses <see cref="ExDates"/> into local DateTimes (invalid entries skipped).</summary>
    public IReadOnlyList<DateTime> GetExDates()
    {
        if (string.IsNullOrWhiteSpace(ExDates)) return Array.Empty<DateTime>();
        var list = new List<DateTime>();
        foreach (var part in ExDates.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (DateTime.TryParseExact(part.Trim(), "yyyyMMdd'T'HHmmss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt))
                list.Add(dt);
        return list;
    }

    /// <summary>Appends an occurrence start (local) to <see cref="ExDates"/>.</summary>
    public void AddExDate(DateTime occurrenceStartLocal)
    {
        var token = occurrenceStartLocal.ToString("yyyyMMdd'T'HHmmss",
            System.Globalization.CultureInfo.InvariantCulture);
        ExDates = string.IsNullOrWhiteSpace(ExDates) ? token : ExDates + "," + token;
    }

    /// <summary>
    /// For an expanded occurrence of a recurring series, the occurrence's own start (local); null for
    /// a master or a one-off. Set by the ViewModel when expanding; not persisted.
    /// </summary>
    public DateTime? OccurrenceStart { get; set; }

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
    [NotifyPropertyChangedFor(nameof(DisplayLine))]
    private CalendarResponseStatus _responseStatus = CalendarResponseStatus.Pending;

    /// <summary>True when the organizer cancelled the event (ICS METHOD:CANCEL).</summary>
    public bool IsCancelled => ResponseStatus == CalendarResponseStatus.Cancelled;

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
                if (IsAllDay)
                {
                    sb.Append(", ").Append(datePart).Append(" all day");
                }
                else
                {
                    sb.Append(", ").Append(datePart).Append(' ').Append(start.ToString("t"));
                    if (EndTime.HasValue && EndTime.Value.TimeOfDay != start.TimeOfDay)
                        sb.Append(" to ").Append(EndTime.Value.ToString("t"));
                }
            }

            // Local appointments are the user's own; a "Pending/Accepted" RSVP word is
            // meaningless for them, so only invite-sourced events announce a response status.
            if (!IsUserCreated)
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

    /// <summary>
    /// "Fields and data" variant of <see cref="DisplayLine"/> — each value prefixed with its field
    /// name ("Subject …, when …, status …"). Used for the row's accessible name when the
    /// CalendarListShowFieldLabels setting is on.
    /// </summary>
    public string LabeledLine
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append("Subject ").Append(string.IsNullOrWhiteSpace(Summary) ? "no title" : Summary);
            sb.Append(", when ").Append(WhenText);
            if (!IsUserCreated)
                sb.Append(", status ").Append(ResponseStatus.ToString());
            if (!string.IsNullOrWhiteSpace(Location))
                sb.Append(", location ").Append(Location);
            var org = !string.IsNullOrWhiteSpace(OrganizerName) ? OrganizerName
                    : !string.IsNullOrWhiteSpace(Organizer) ? Organizer : null;
            if (!IsUserCreated && org != null)
                sb.Append(", organizer ").Append(org);
            return sb.ToString();
        }
    }

    /// <summary>
    /// The composed accessible name for the row, stamped by <see cref="ViewModels.CalendarViewModel"/>
    /// per the CalendarListShowFieldLabels setting (labeled vs data-only). Falls back to
    /// <see cref="DisplayLine"/> when not stamped, so the row is never announced empty.
    /// </summary>
    private string _accessibleName = string.Empty;
    public string AccessibleName
    {
        get => string.IsNullOrEmpty(_accessibleName) ? DisplayLine : _accessibleName;
        set => _accessibleName = value;
    }

    /// <summary>Column text for the "When" grid column — a friendly date/time range.</summary>
    public string WhenText
    {
        get
        {
            if (!StartTime.HasValue) return "No date";
            var start = StartTime.Value;
            var now = DateTime.Now;
            var dayWord = start.Date == now.Date ? "Today"
                : start.Date == now.Date.AddDays(1) ? "Tomorrow"
                : start.Date == now.Date.AddDays(-1) ? "Yesterday"
                : start.ToString("ddd, MMM d");
            if (IsAllDay)
                return dayWord + " (all day)";
            var sb = new StringBuilder();
            sb.Append(dayWord).Append(' ').Append(start.ToString("t"));
            if (EndTime.HasValue && EndTime.Value.TimeOfDay != start.TimeOfDay)
                sb.Append('–').Append(EndTime.Value.ToString("t"));
            return sb.ToString();
        }
    }

    /// <summary>Column text for the "Status" grid column. Local events show "Appointment".</summary>
    public string StatusText => IsUserCreated ? "Appointment" : ResponseStatus.ToString();

    /// <summary>
    /// Multi-line detail block shown in the master/detail Details pane, one field per line.
    /// Read by both sighted users and, verbatim, by a screen reader on the read-only text box.
    /// </summary>
    public string DetailText
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(string.IsNullOrWhiteSpace(Summary) ? "(no title)" : Summary).Append('\n');

            if (StartTime.HasValue && IsAllDay)
            {
                sb.Append("When: ").Append(StartTime.Value.ToString("dddd, MMMM d, yyyy"))
                  .Append(" (all day)");
                if (EndTime.HasValue && EndTime.Value.Date > StartTime.Value.Date)
                    sb.Append(" to ").Append(EndTime.Value.ToString("dddd, MMMM d, yyyy"));
                sb.Append('\n');
            }
            else if (StartTime.HasValue)
            {
                sb.Append("When: ").Append(StartTime.Value.ToString("dddd, MMMM d, yyyy"))
                  .Append(" at ").Append(StartTime.Value.ToString("t"));
                if (EndTime.HasValue)
                    sb.Append(" to ").Append(
                        EndTime.Value.Date == StartTime.Value.Date
                            ? EndTime.Value.ToString("t")
                            : EndTime.Value.ToString("dddd, MMMM d, yyyy 'at' t"));
                sb.Append('\n');
            }
            else
            {
                sb.Append("When: no date set\n");
            }

            if (IsRecurring)
            {
                var r = global::QuickMail.Models.RecurrenceRule.Parse(RecurrenceRule);
                if (r != null) sb.Append("Repeats: ").Append(r.Describe()).Append('\n');
            }

            if (!string.IsNullOrWhiteSpace(Location))
                sb.Append("Location: ").Append(Location).Append('\n');

            if (!IsUserCreated)
            {
                sb.Append("Status: ").Append(ResponseStatus.ToString()).Append('\n');
                var org = !string.IsNullOrWhiteSpace(OrganizerName) ? OrganizerName
                        : !string.IsNullOrWhiteSpace(Organizer) ? Organizer : null;
                if (org != null)
                    sb.Append("Organizer: ").Append(org).Append('\n');
            }

            if (!string.IsNullOrWhiteSpace(Description))
                sb.Append('\n').Append(Description).Append('\n');

            return sb.ToString().TrimEnd('\n');
        }
    }
}