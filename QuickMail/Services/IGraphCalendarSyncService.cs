using System;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Outcome of a Graph calendar sync pass. <paramref name="Error"/> is a user-presentable message
/// for the last account that failed (each failure is also logged), or null when everything
/// succeeded. <see cref="None"/> is the "nothing eligible / nothing done" result.
/// </summary>
public sealed record GraphCalendarSyncResult(int AccountsSynced, int EventsFetched, string? Error)
{
    public static readonly GraphCalendarSyncResult None = new(0, 0, null);
}

/// <summary>
/// Pulls each server-backed account's primary calendar into the local CalendarEvent table —
/// read-down only: Microsoft (Graph backend) accounts via Graph calendarView, and Google-signed-in
/// accounts via the Google Calendar REST API (the name predates the Google path; see the
/// implementation remarks). Best-effort: a failing account is logged and reported in the result,
/// never thrown, so calendar sync can never break a mail-sync caller.
/// </summary>
public interface IGraphCalendarSyncService
{
    /// <summary>
    /// Syncs every eligible account's primary calendar (window: -30 days to +365 days) using
    /// replace-slice semantics — the account's previous Graph-sourced rows are replaced wholesale,
    /// so events deleted on the server disappear locally.
    /// </summary>
    Task<GraphCalendarSyncResult> SyncAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates a single (non-repeating) event on the account's primary Microsoft calendar
    /// (<c>POST /me/events</c>) and upserts the server's copy into the local store with
    /// <c>is_graph</c> set, so it appears immediately and survives the next replace-slice sync.
    /// Unlike <see cref="SyncAllAsync"/>, this THROWS on failure — the caller decides the
    /// fallback (e.g. save locally instead). Returns the locally-stored row (Uid = the
    /// server-assigned event id).
    /// </summary>
    Task<CalendarEvent> CreateEventAsync(AccountModel account, CalendarEvent evt, CancellationToken ct = default);
}
