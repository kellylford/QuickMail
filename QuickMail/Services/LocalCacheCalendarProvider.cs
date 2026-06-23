using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Harvests calendar events from the local message cache. Scans MessageDetail rows
/// whose <c>calendar_ics</c> column is non-empty, re-parses each with
/// <see cref="IcsModel.Parse"/>, and upserts into the CalendarEvent table.
/// This is the v1 provider; v2 will add CalDAV / Graph providers behind the same interface.
/// </summary>
public sealed class LocalCacheCalendarProvider : ICalendarProvider
{
    private readonly ILocalStoreService _store;

    public LocalCacheCalendarProvider(ILocalStoreService store)
    {
        _store = store;
    }

    public Task<List<CalendarEvent>> LoadEventsAsync(CancellationToken ct = default)
        => _store.LoadCalendarEventsAsync();

    public Task UpsertEventAsync(CalendarEvent evt, CancellationToken ct = default)
        => _store.UpsertCalendarEventAsync(evt);

    public Task UpdateResponseStatusAsync(string uid, Guid accountId, CalendarResponseStatus status, CancellationToken ct = default)
        => _store.UpdateCalendarResponseStatusAsync(uid, accountId, status);

    public Task DeleteEventAsync(string uid, Guid accountId, CancellationToken ct = default)
        => _store.DeleteCalendarEventAsync(uid, accountId);

    /// <summary>
    /// Scans all cached MessageDetail rows with calendar_ics text, re-parses them,
    /// and upserts the resulting events into the CalendarEvent table. Existing
    /// response statuses are preserved (upsert does not overwrite response_status).
    /// Call this after a sync completes or on demand (F5).
    /// </summary>
    public async Task HarvestAsync(CancellationToken ct = default)
    {
        var rows = await _store.LoadAllCalendarIcsAsync();
        foreach (var (accountId, folder, messageId, icsText) in rows)
        {
            ct.ThrowIfCancellationRequested();
            IcsModel? model;
            try
            {
                model = IcsModel.Parse(icsText);
            }
            catch
            {
                // Malformed ICS in the cache — skip rather than crash the harvest.
                continue;
            }
            if (model == null || string.IsNullOrEmpty(model.Uid)) continue;

            // METHOD:CANCEL means the organizer cancelled the event. Mark it as
            // Cancelled so the calendar list can filter it out and announce it.
            var isCancel = string.Equals(model.Method, "CANCEL", StringComparison.OrdinalIgnoreCase);

            var evt = new CalendarEvent
            {
                Uid              = model.Uid,
                AccountId        = accountId,
                Summary          = model.Summary ?? string.Empty,
                Description      = model.Description ?? string.Empty,
                Location         = model.Location ?? string.Empty,
                Organizer        = model.Organizer ?? string.Empty,
                OrganizerName    = model.OrganizerName ?? string.Empty,
                StartTimeTicks   = model.StartTime?.ToUniversalTime().Ticks,
                EndTimeTicks     = model.EndTime?.ToUniversalTime().Ticks,
                Sequence         = model.Sequence,
                Method           = model.Method,
                SourceMessageId  = messageId,
                SourceFolder     = folder,
                ResponseStatus   = isCancel ? CalendarResponseStatus.Cancelled : CalendarResponseStatus.Pending,
            };
            await _store.UpsertCalendarEventAsync(evt);

            // The upsert's ON CONFLICT clause does not overwrite response_status
            // (by design, so re-harvesting doesn't clobber the user's reply). But a
            // cancellation must override any prior status, so update it explicitly.
            if (isCancel)
                await _store.UpdateCalendarResponseStatusAsync(model.Uid, accountId, CalendarResponseStatus.Cancelled);
        }
    }
}