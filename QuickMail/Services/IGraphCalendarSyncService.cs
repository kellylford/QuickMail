using System;
using System.Threading;
using System.Threading.Tasks;

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
/// Pulls each Microsoft (Graph backend) account's primary calendar into the local CalendarEvent
/// table — read-down only (full-calendar spec M4, v1). Best-effort: a failing account is logged
/// and reported in the result, never thrown, so calendar sync can never break a mail-sync caller.
/// </summary>
public interface IGraphCalendarSyncService
{
    /// <summary>
    /// Syncs every eligible account's primary calendar (window: -30 days to +365 days) using
    /// replace-slice semantics — the account's previous Graph-sourced rows are replaced wholesale,
    /// so events deleted on the server disappear locally.
    /// </summary>
    Task<GraphCalendarSyncResult> SyncAllAsync(CancellationToken ct = default);
}
