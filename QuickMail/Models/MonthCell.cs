using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace QuickMail.Models;

/// <summary>
/// One day cell in the calendar's Month grid. Immutable snapshot built by the ViewModel:
/// the date, that day's events, and the spoken/visual labels.
/// </summary>
public sealed class MonthCell
{
    public DateTime Date { get; }

    /// <summary>True when the cell belongs to the displayed month (not a leading/trailing pad day).</summary>
    public bool IsInMonth { get; }

    public IReadOnlyList<CalendarEvent> DayEvents { get; }

    public int EventCount => DayEvents.Count;

    /// <summary>Visual cell text, e.g. "9" or "9 •2" when the day has 2 events.</summary>
    public string CellText => EventCount == 0 ? Date.Day.ToString() : $"{Date.Day} •{EventCount}";

    /// <summary>
    /// Spoken name, e.g. "Tuesday June 9, 3 events" (adds "today" and the month for pad days).
    /// </summary>
    public string AccessibleName
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(Date.ToString("dddd MMMM d"));
            if (Date == DateTime.Today) sb.Append(", today");
            sb.Append(", ").Append(EventCount == 0 ? "no events"
                : $"{EventCount} event{(EventCount == 1 ? "" : "s")}");
            return sb.ToString();
        }
    }

    /// <summary>Details-pane text: the day's events, one per line.</summary>
    public string DayDetail
    {
        get
        {
            if (EventCount == 0)
                return Date.ToString("dddd, MMMM d, yyyy") + "\nNo events.";
            var sb = new StringBuilder(Date.ToString("dddd, MMMM d, yyyy"));
            foreach (var e in DayEvents.OrderBy(e => e.StartTimeTicks ?? long.MaxValue))
                sb.Append('\n').Append(e.DisplayLine);
            return sb.ToString();
        }
    }

    public MonthCell(DateTime date, int displayedMonth, IReadOnlyList<CalendarEvent> dayEvents)
    {
        Date = date.Date;
        IsInMonth = date.Month == displayedMonth;
        DayEvents = dayEvents;
    }

    /// <summary>
    /// Selector-bound items are announced via ToString (see the accessibility checklist) —
    /// must match <see cref="AccessibleName"/>.
    /// </summary>
    public override string ToString() => AccessibleName;
}
