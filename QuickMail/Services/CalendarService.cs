using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Default <see cref="ICalendarService"/>. Delegates persistence to an
/// <see cref="ICalendarProvider"/> (v1: <see cref="LocalCacheCalendarProvider"/>).
/// Maintains an in-memory sorted list of events that the ViewModel binds to.
/// </summary>
public sealed class CalendarService : ICalendarService
{
    private readonly ICalendarProvider _provider;
    private readonly object _lock = new();
    private List<CalendarEvent> _events = [];

    public CalendarService(ICalendarProvider provider)
    {
        _provider = provider;
    }

    public IReadOnlyList<CalendarEvent> Events
    {
        get
        {
            lock (_lock) return _events.ToList();
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        // Harvest from the local cache (no-op for non-harvesting providers).
        if (_provider is LocalCacheCalendarProvider local)
            await local.HarvestAsync(ct);

        var loaded = await _provider.LoadEventsAsync(ct);
        lock (_lock)
        {
            _events = loaded;
        }
    }

    public async Task UpsertEventAsync(CalendarEvent evt, CancellationToken ct = default)
    {
        await _provider.UpsertEventAsync(evt, ct);
        var loaded = await _provider.LoadEventsAsync(ct);
        lock (_lock)
        {
            _events = loaded;
        }
    }

    public async Task SetResponseStatusAsync(string uid, Guid accountId, CalendarResponseStatus status, CancellationToken ct = default)
    {
        await _provider.UpdateResponseStatusAsync(uid, accountId, status, ct);
        lock (_lock)
        {
            var idx = _events.FindIndex(e => e.Uid == uid && e.AccountId == accountId);
            if (idx >= 0)
                _events[idx].ResponseStatus = status;
        }
    }

    public async Task DeleteEventAsync(string uid, Guid accountId, CancellationToken ct = default)
    {
        await _provider.DeleteEventAsync(uid, accountId, ct);
        lock (_lock)
        {
            _events.RemoveAll(e => e.Uid == uid && e.AccountId == accountId);
        }
    }
}