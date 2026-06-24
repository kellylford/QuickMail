using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Abstraction over where calendar events come from.
/// v1 has one implementation: <see cref="LocalCacheCalendarProvider"/> (harvests from the
/// local message cache). v2 will add CalDAV / Graph calendar providers without touching
/// the ViewModel or View.
/// </summary>
public interface ICalendarProvider
{
    /// <summary>Loads all events known to this provider, ordered by start time ascending.</summary>
    Task<List<CalendarEvent>> LoadEventsAsync(CancellationToken ct = default);

    /// <summary>Inserts or updates an event (upsert by Uid + AccountId).</summary>
    Task UpsertEventAsync(CalendarEvent evt, CancellationToken ct = default);

    /// <summary>Updates only the response status for an existing event.</summary>
    Task UpdateResponseStatusAsync(string uid, Guid accountId, CalendarResponseStatus status, CancellationToken ct = default);

    /// <summary>Deletes an event by Uid + AccountId.</summary>
    Task DeleteEventAsync(string uid, Guid accountId, CancellationToken ct = default);
}