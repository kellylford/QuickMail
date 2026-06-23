using System;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Application-level calendar operations: refresh (harvest), load events with
/// filtering/sorting, and update response status. Delegates persistence to
/// <see cref="ICalendarProvider"/>.
/// </summary>
public interface ICalendarService
{
    /// <summary>Re-harvests events from the provider and reloads the in-memory list.</summary>
    Task RefreshAsync(CancellationToken ct = default);

    /// <summary>All events, sorted by start time ascending (nulls last).</summary>
    IReadOnlyList<CalendarEvent> Events { get; }

    /// <summary>
    /// Inserts or updates a single event (upsert by Uid + AccountId) and refreshes
    /// the in-memory list. Used when an invite reply creates or updates an event
    /// immediately, without waiting for the next harvest.
    /// </summary>
    Task UpsertEventAsync(CalendarEvent evt, CancellationToken ct = default);

    /// <summary>Updates the response status for an event and persists it.</summary>
    Task SetResponseStatusAsync(string uid, Guid accountId, CalendarResponseStatus status, CancellationToken ct = default);
}