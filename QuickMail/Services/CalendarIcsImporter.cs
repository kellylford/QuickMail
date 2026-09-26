using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>What one .ics import would store, and how it went. <see cref="Events"/> is empty when
/// the file held nothing usable.</summary>
public sealed record CalendarImportResult(
    IReadOnlyList<CalendarEvent> Events,
    int Added,
    int Updated,
    int Skipped)
{
    /// <summary>True when the file held no event that could be imported.</summary>
    public bool IsEmpty => Events.Count == 0;
}

/// <summary>
/// Turns the text of a .ics file into Local Calendar rows (issue #762). Pure — no store, no UI —
/// so the rules are testable: the caller stores <see cref="CalendarImportResult.Events"/> in one
/// batch.
/// <list type="bullet">
/// <item>Every row is a Local Calendar appointment: user-created and editable.</item>
/// <item>An event whose UID is already in the Local Calendar is updated, not duplicated, so
/// importing the same file twice is safe. A VEVENT with no UID gets a stable synthetic one for
/// the same reason.</item>
/// <item>STATUS:CANCELLED events are skipped and counted.</item>
/// <item>A repeating series keeps its RRULE and EXDATEs. An overridden instance (RECURRENCE-ID)
/// becomes what "edit this occurrence" makes: an EXDATE on the master plus a standalone
/// appointment, so the moved meeting lands on its new day. A cancelled instance becomes an EXDATE
/// only.</item>
/// <item>METHOD is ignored: a METHOD:REQUEST file is a plain import and nothing is sent.</item>
/// </list>
/// </summary>
public static class CalendarIcsImporter
{
    public static CalendarImportResult Plan(string icsText, IEnumerable<CalendarEvent> existingEvents)
    {
        var existingLocal = new Dictionary<string, CalendarEvent>(StringComparer.Ordinal);
        foreach (var e in existingEvents)
            if (e.AccountId == CalendarEvent.LocalAccountId && !string.IsNullOrEmpty(e.Uid))
                existingLocal[e.Uid] = e;

        var parsed = IcsModel.ParseAllEvents(icsText);
        var byUid = new Dictionary<string, CalendarEvent>(StringComparer.Ordinal);
        var skipped = 0;

        // Pass 1: series masters and one-offs.
        foreach (var ics in parsed.Where(p => string.IsNullOrEmpty(p.RecurrenceId)))
        {
            if (IsCancelled(ics)) { skipped++; continue; }
            if (!ics.StartTime.HasValue) { skipped++; continue; }

            var evt = ics.ToCalendarEvent(CalendarEvent.LocalAccountId);
            if (string.IsNullOrWhiteSpace(evt.Uid))
                evt.Uid = CalDavCalendarClient.SyntheticUid($"import|{ics.Summary}|{evt.StartTimeTicks}");
            evt.Method = null;

            // Re-import of a series: keep occurrences the user already removed or moved here, so
            // they do not come back alongside their detached copies.
            if (existingLocal.TryGetValue(evt.Uid, out var prior) && evt.IsRecurring)
                foreach (var d in prior.GetExDates())
                    AddExDateOnce(evt, d);

            byUid[evt.Uid] = evt;
        }

        // Pass 2: overridden instances of a series.
        foreach (var ics in parsed.Where(p => !string.IsNullOrEmpty(p.RecurrenceId)))
        {
            var masterUid = ics.Uid?.Trim() ?? string.Empty;
            if (!ics.RecurrenceIdTime.HasValue || masterUid.Length == 0) { skipped++; continue; }

            var master = byUid.GetValueOrDefault(masterUid);
            if (master == null && existingLocal.TryGetValue(masterUid, out var stored) && stored.IsRecurring)
            {
                master = stored;
                byUid[masterUid] = master; // re-store it with the new EXDATE
            }
            if (master != null && master.IsRecurring)
                AddExDateOnce(master, ics.RecurrenceIdTime.Value);

            if (IsCancelled(ics))
            {
                // A cancelled occurrence is just a hole in the series. Without a master there is
                // nothing to punch it in, which is a skip.
                if (master == null) skipped++;
                continue;
            }
            if (!ics.StartTime.HasValue) { skipped++; continue; }

            var standalone = ics.ToCalendarEvent(CalendarEvent.LocalAccountId);
            standalone.Uid = masterUid + "#" +
                ics.RecurrenceIdTime.Value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
            standalone.RecurrenceRule = null;
            standalone.ExDates = null;
            standalone.Method = null;
            byUid[standalone.Uid] = standalone;
        }

        var events = byUid.Values.ToList();
        var updated = events.Count(e => existingLocal.ContainsKey(e.Uid));
        // A master touched only to gain an EXDATE is an update of something already there; it is
        // not news to the user, but it is still counted so a re-import reads "Updated N".
        return new CalendarImportResult(events, events.Count - updated, updated, skipped);
    }

    private static bool IsCancelled(IcsModel ics)
        => string.Equals(ics.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase);

    private static void AddExDateOnce(CalendarEvent evt, DateTime occurrenceStartLocal)
    {
        if (evt.GetExDates().Contains(occurrenceStartLocal)) return;
        evt.AddExDate(occurrenceStartLocal);
    }
}
