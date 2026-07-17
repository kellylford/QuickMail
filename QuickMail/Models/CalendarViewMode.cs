namespace QuickMail.Models;

/// <summary>
/// Which slice of the calendar the event list shows. All modes share the same accessible
/// master/detail list; the mode changes which events appear and how the period is labelled.
/// (Month grid is a separate future surface — see the full-calendar spec, M3.)
/// </summary>
public enum CalendarViewMode
{
    /// <summary>All upcoming appointments, unbounded (the original behaviour).</summary>
    Agenda,

    /// <summary>Appointments on a single reference date.</summary>
    Day,

    /// <summary>Appointments in the week containing the reference date.</summary>
    Week,

    /// <summary>A month at a glance: a 7-column day grid; Enter on a day drills into Day view.</summary>
    Month,
}
