using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services.Graph;

namespace QuickMail.Services;

/// <inheritdoc cref="IGraphCalendarSyncService"/>
/// <remarks>
/// Uses <c>GET /me/calendarView</c> over the user's PRIMARY calendar. calendarView expands
/// recurring series server-side, so each returned item is a concrete occurrence stored as its
/// own row (Uid = the occurrence's own Graph id) with <see cref="CalendarEvent.RecurrenceRule"/>
/// left null — otherwise the local <c>RecurrenceExpander</c> would double-expand the series.
/// The <c>Prefer: outlook.timezone="UTC"</c> header normalizes event times to UTC so they can be
/// stored directly as UTC ticks. Paging and HTTP 429 Retry-After handling come from
/// <see cref="GraphClient"/> (the same client the Graph mail backend and contact sync use).
/// </remarks>
public sealed class GraphCalendarSyncService : IGraphCalendarSyncService
{
    // Sync window relative to now: a month of history plus a year ahead. Replace-slice per sync,
    // so the window simply rolls forward on each pass; no delta tokens in v1.
    internal static readonly TimeSpan WindowBack    = TimeSpan.FromDays(30);
    internal static readonly TimeSpan WindowForward = TimeSpan.FromDays(365);

    private static readonly IReadOnlyDictionary<string, string> UtcPreferHeader =
        new Dictionary<string, string> { ["Prefer"] = "outlook.timezone=\"UTC\"" };

    private readonly IAccountService _accounts;
    private readonly ILocalStoreService _store;
    private readonly GraphClient _graph;

    public GraphCalendarSyncService(IAccountService accounts, ILocalStoreService store, GraphClient graph)
    {
        _accounts = accounts;
        _store    = store;
        _graph    = graph;
    }

    /// <summary>Only accounts on the Graph mail backend have a Microsoft calendar to pull.</summary>
    private static bool IsEligible(AccountModel account) => account.BackendKind == BackendKind.MicrosoftGraph;

    public async Task<GraphCalendarSyncResult> SyncAllAsync(CancellationToken ct = default)
    {
        int accountsSynced = 0, eventsFetched = 0;
        string? error = null;

        foreach (var account in _accounts.LoadAccounts())
        {
            if (!IsEligible(account)) continue;
            ct.ThrowIfCancellationRequested();

            try
            {
                var count = await SyncAccountAsync(account, ct);
                accountsSynced++;
                eventsFetched += count;
                LogService.Log($"GraphCalendarSync: {account.AccountLabel} — {count} event(s) synced.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InteractiveSignInRequiredException ex)
            {
                // The calendar grant hasn't been consented (or lapsed). Sync never opens an
                // interactive sign-in window from the background — surface how to fix it instead.
                LogService.Log($"GraphCalendarSync: sign-in required for {account.AccountLabel} — {ex.Message}");
                error = $"calendar access needs a new sign-in for {account.AccountLabel}.";
            }
            catch (Exception ex)
            {
                // Best-effort: log and report, never throw — a calendar-sync failure must not
                // break the mail-sync caller (mirrors ContactSyncService).
                LogService.Log($"GraphCalendarSync failed for {account.AccountLabel}", ex);
                error = ex.Message;
            }
        }

        return accountsSynced == 0 && error is null
            ? GraphCalendarSyncResult.None
            : new GraphCalendarSyncResult(accountsSynced, eventsFetched, error);
    }

    private async Task<int> SyncAccountAsync(AccountModel account, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var startUtc = nowUtc - WindowBack;
        var endUtc   = nowUtc + WindowForward;

        var path = "/me/calendarView"
            + $"?startDateTime={Uri.EscapeDataString(startUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))}"
            + $"&endDateTime={Uri.EscapeDataString(endUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))}"
            + "&$select=id,subject,bodyPreview,location,organizer,start,end,isAllDay,isOrganizer,responseStatus"
            + "&$top=100";

        // silentOnly: background sync must never launch an interactive sign-in (same reasoning as
        // contact sync). Consent comes from the account's normal sign-in: work/school accounts get
        // Calendars.ReadWrite via `.default` (declared on the app registration); personal accounts
        // acquire the explicit GraphCalendarScopes silently once granted.
        var items = await _graph.GetAllPagesAsync<GraphCalendarEvent>(
            account, path, OAuthService.GraphCalendarScopes, silentOnly: true, UtcPreferHeader, ct);

        var mapped = items
            .Where(e => !string.IsNullOrEmpty(e.Id))
            .Select(e => MapEvent(e, account.Id))
            .ToList();

        await _store.ReplaceGraphCalendarEventsAsync(account.Id, mapped);
        return mapped.Count;
    }

    /// <summary>Maps one Graph calendarView occurrence to a local <see cref="CalendarEvent"/> row.</summary>
    internal static CalendarEvent MapEvent(GraphCalendarEvent e, Guid accountId)
    {
        long? startTicks, endTicks;
        if (e.IsAllDay)
        {
            // Graph all-day events span midnight-to-midnight in the returned zone with an
            // EXCLUSIVE end. Re-anchor the dates at LOCAL midnight (and an inclusive end of
            // 23:59:59 on the last day), matching how locally-authored all-day appointments are
            // stored, so the row displays on the correct calendar day in the user's zone.
            var startDay = ParseGraphDateTime(e.Start)?.Date;
            var endDayExclusive = ParseGraphDateTime(e.End)?.Date;
            var endDay = endDayExclusive?.AddDays(-1);
            if (endDay.HasValue && startDay.HasValue && endDay.Value < startDay.Value)
                endDay = startDay;
            startTicks = startDay.HasValue
                ? DateTime.SpecifyKind(startDay.Value, DateTimeKind.Local).ToUniversalTime().Ticks
                : null;
            endTicks = endDay.HasValue
                ? DateTime.SpecifyKind(endDay.Value.AddDays(1).AddSeconds(-1), DateTimeKind.Local).ToUniversalTime().Ticks
                : null;
        }
        else
        {
            startTicks = ToUtcTicks(e.Start);
            endTicks   = ToUtcTicks(e.End);
        }

        return new CalendarEvent
        {
            Uid             = e.Id,
            AccountId       = accountId,
            IsGraph         = true,
            Summary         = e.Subject?.Trim() ?? string.Empty,
            Description     = e.BodyPreview?.Trim() ?? string.Empty,
            Location        = e.Location?.DisplayName?.Trim() ?? string.Empty,
            Organizer       = e.Organizer?.EmailAddress?.Address?.Trim() ?? string.Empty,
            OrganizerName   = e.Organizer?.EmailAddress?.Name?.Trim() ?? string.Empty,
            StartTimeTicks  = startTicks,
            EndTimeTicks    = endTicks,
            IsAllDay        = e.IsAllDay,
            ResponseStatus  = MapResponseStatus(e.ResponseStatus?.Response, e.IsOrganizer),
            // Not an invite email — there is no source message to open.
            SourceMessageId = string.Empty,
            SourceFolder    = string.Empty,
            // calendarView already expanded the series server-side; leaving RecurrenceRule null
            // prevents the local RecurrenceExpander from expanding each occurrence again.
            RecurrenceRule  = null,
        };
    }

    /// <summary>
    /// Maps Graph's <c>responseStatus.response</c> to the local status. The organizer of an event
    /// never "responds", so organizer/none map to Accepted for the user's own events; a genuine
    /// unanswered invitation stays Pending.
    /// </summary>
    internal static CalendarResponseStatus MapResponseStatus(string? response, bool isOrganizer)
        => response?.ToLowerInvariant() switch
        {
            "accepted"            => CalendarResponseStatus.Accepted,
            "declined"            => CalendarResponseStatus.Declined,
            "tentativelyaccepted" => CalendarResponseStatus.Tentative,
            "organizer"           => CalendarResponseStatus.Accepted,
            _                     => isOrganizer ? CalendarResponseStatus.Accepted : CalendarResponseStatus.Pending,
        };

    /// <summary>Parses a Graph dateTimeTimeZone into a UTC DateTime, honoring its zone field.</summary>
    private static DateTime? ParseGraphDateTimeUtc(GraphDateTimeTimeZone? dtz)
    {
        var wall = ParseGraphDateTime(dtz);
        if (!wall.HasValue) return null;

        var zone = dtz!.TimeZone;
        if (string.IsNullOrWhiteSpace(zone) || zone.Equals("UTC", StringComparison.OrdinalIgnoreCase))
            return DateTime.SpecifyKind(wall.Value, DateTimeKind.Utc);

        // Defensive: the Prefer header makes this UTC, but honor another zone if one comes back.
        try
        {
            var tzi = TimeZoneInfo.FindSystemTimeZoneById(zone);
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(wall.Value, DateTimeKind.Unspecified), tzi);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateTime.SpecifyKind(wall.Value, DateTimeKind.Utc);
        }
        catch (InvalidTimeZoneException)
        {
            return DateTime.SpecifyKind(wall.Value, DateTimeKind.Utc);
        }
    }

    private static long? ToUtcTicks(GraphDateTimeTimeZone? dtz) => ParseGraphDateTimeUtc(dtz)?.Ticks;

    /// <summary>Parses just the wall-clock value (e.g. "2026-07-16T14:00:00.0000000").</summary>
    private static DateTime? ParseGraphDateTime(GraphDateTimeTimeZone? dtz)
        => dtz?.DateTime != null
           && DateTime.TryParse(dtz.DateTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt
            : null;
}
