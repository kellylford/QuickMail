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
/// <para>
/// Microsoft path: <c>GET /me/calendarView</c> over the user's PRIMARY calendar. calendarView
/// expands recurring series server-side, so each returned item is a concrete occurrence stored
/// as its own row (Uid = the occurrence's own Graph id) with
/// <see cref="CalendarEvent.RecurrenceRule"/> left null — otherwise the local
/// <c>RecurrenceExpander</c> would double-expand the series. The
/// <c>Prefer: outlook.timezone="UTC"</c> header normalizes event times to UTC so they can be
/// stored directly as UTC ticks. Paging and HTTP 429 Retry-After handling come from
/// <see cref="GraphClient"/> (the same client the Graph mail backend and contact sync use).
/// </para>
/// <para>
/// Google path: <c>GET calendars/primary/events?singleEvents=true</c> via
/// <see cref="GoogleCalendarClient"/> — the same server-expanded-occurrence model, keyed by
/// <see cref="AuthType.OAuth2Google"/> (a Gmail account is IMAP + Google OAuth, so the identity
/// provider, not the mail backend, is what makes calendar sync possible — mirrors contact sync).
/// Both providers store rows the same way: <c>is_graph=1</c> (meaning "server-synced calendar
/// row"), replace-slice per account. NOTE: the class/interface name predates the Google path;
/// renaming to CalendarSyncService is earmarked for the M4 two-way engine.
/// </para>
/// </remarks>
public sealed class GraphCalendarSyncService : IGraphCalendarSyncService
{
    // Sync window relative to now: a month of history plus a year ahead. Replace-slice per sync,
    // so the window simply rolls forward on each pass; no delta tokens in v1.
    internal static readonly TimeSpan WindowBack    = TimeSpan.FromDays(30);
    internal static readonly TimeSpan WindowForward = TimeSpan.FromDays(365);

    // Ask Graph for times in the machine's own zone (Windows zone id, the format Outlook uses).
    // Timed events still convert exactly to UTC ticks via ParseGraphDateTimeUtc (which honors the
    // returned zone). All-day events arrive as LOCAL midnights, so taking .Date yields the correct
    // calendar day — with a UTC Prefer header an all-day event authored east of Greenwich came back
    // as the previous day's UTC evening and displayed one day early.
    private static readonly IReadOnlyDictionary<string, string> UtcPreferHeader =
        new Dictionary<string, string> { ["Prefer"] = $"outlook.timezone=\"{TimeZoneInfo.Local.Id}\"" };

    private readonly IAccountService _accounts;
    private readonly ILocalStoreService _store;
    private readonly GraphClient _graph;
    private readonly GoogleCalendarClient? _google;

    public GraphCalendarSyncService(IAccountService accounts, ILocalStoreService store, GraphClient graph,
                                    GoogleCalendarClient? google = null)
    {
        _accounts = accounts;
        _store    = store;
        _graph    = graph;
        _google   = google;
    }

    /// <summary>Accounts on the Graph mail backend have a Microsoft calendar to pull.</summary>
    private static bool IsGraphEligible(AccountModel account) =>
        account.BackendKind == BackendKind.MicrosoftGraph || account.AuthType == AuthType.OAuth2Microsoft;

    /// <summary>
    /// Google-signed-in accounts have a Google calendar to pull (keyed by auth type, not backend:
    /// Gmail mail is IMAP). No-op when no Google client is wired or no such account exists.
    /// </summary>
    private bool IsGoogleEligible(AccountModel account)
        => _google != null && account.AuthType == AuthType.OAuth2Google && !IsGraphEligible(account);

    public async Task<GraphCalendarSyncResult> SyncAllAsync(CancellationToken ct = default)
    {
        int accountsSynced = 0, eventsFetched = 0;
        string? error = null;

        foreach (var account in _accounts.LoadAccounts())
        {
            var graphEligible  = IsGraphEligible(account);
            var googleEligible = IsGoogleEligible(account);
            if (!graphEligible && !googleEligible) continue;
            ct.ThrowIfCancellationRequested();

            try
            {
                var count = graphEligible
                    ? await SyncAccountAsync(account, ct)
                    : await SyncGoogleAccountAsync(account, ct);
                accountsSynced++;
                eventsFetched += count;
                LogService.Log($"CalendarSync: {account.AccountLabel} — {count} event(s) synced.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InteractiveSignInRequiredException ex)
            {
                // The calendar grant hasn't been consented (or lapsed). Sync never opens an
                // interactive sign-in window from the background — surface how to fix it instead.
                LogService.Log($"CalendarSync: sign-in required for {account.AccountLabel} — {ex.Message}");
                error = $"calendar access needs a new sign-in for {account.AccountLabel}.";
            }
            catch (Exception ex)
            {
                // Best-effort: log and report, never throw — a calendar-sync failure must not
                // break the mail-sync caller (mirrors ContactSyncService). A Google account whose
                // refresh token predates the calendar consent lands here as a 403 until the user
                // re-signs-in interactively.
                LogService.Log($"CalendarSync failed for {account.AccountLabel}", ex);
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

    // ── Google (read-down v1) ────────────────────────────────────────────────────

    private async Task<int> SyncGoogleAccountAsync(AccountModel account, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var items = await _google!.GetPrimaryEventsAsync(
            account.Username, nowUtc - WindowBack, nowUtc + WindowForward, ct);

        var mapped = items
            .Where(e => !string.IsNullOrEmpty(e.Id))
            // Cancelled occurrences can appear despite the default filters; they are deletions.
            .Where(e => !string.Equals(e.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
            .Select(e => MapGoogleEvent(e, account.Id))
            .ToList();

        await _store.ReplaceGraphCalendarEventsAsync(account.Id, mapped);
        return mapped.Count;
    }

    /// <summary>
    /// Maps one Google Calendar occurrence to a local row. Stored exactly like a Graph row —
    /// is_graph=1 here means "server-synced calendar row", regardless of which provider it came
    /// from; the account id says whose calendar it is.
    /// </summary>
    internal static CalendarEvent MapGoogleEvent(GoogleCalendarEvent e, Guid accountId)
    {
        long? startTicks, endTicks;
        var allDay = e.Start?.Date != null; // all-day events carry date-only start/end

        if (allDay)
        {
            // Google all-day: date-only strings with an EXCLUSIVE end date. Re-anchor at LOCAL
            // midnight / 23:59:59 of the last day, matching Graph and locally-authored all-day rows.
            var startDay = ParseDateOnly(e.Start?.Date);
            var endDay   = ParseDateOnly(e.End?.Date)?.AddDays(-1);
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
            startTicks = ParseRfc3339UtcTicks(e.Start?.DateTime);
            endTicks   = ParseRfc3339UtcTicks(e.End?.DateTime);
        }

        return new CalendarEvent
        {
            Uid             = e.Id,
            AccountId       = accountId,
            IsGraph         = true, // "server-synced row" — see remark above
            Summary         = e.Summary?.Trim() ?? string.Empty,
            Description     = e.Description?.Trim() ?? string.Empty,
            Location        = e.Location?.Trim() ?? string.Empty,
            Organizer       = e.Organizer?.Email?.Trim() ?? string.Empty,
            OrganizerName   = e.Organizer?.DisplayName?.Trim() ?? string.Empty,
            StartTimeTicks  = startTicks,
            EndTimeTicks    = endTicks,
            IsAllDay        = allDay,
            ResponseStatus  = MapGoogleResponseStatus(e),
            SourceMessageId = string.Empty, // not an invite email
            SourceFolder    = string.Empty,
            RecurrenceRule  = null, // singleEvents=true already expanded any series server-side
        };
    }

    /// <summary>
    /// The user's own response: the attendee entry with <c>self=true</c>. Events with no
    /// attendee list (or no self entry) are the user's own calendar entries — Accepted.
    /// </summary>
    internal static CalendarResponseStatus MapGoogleResponseStatus(GoogleCalendarEvent e)
    {
        var self = e.Attendees?.FirstOrDefault(a => a.Self);
        return self?.ResponseStatus?.ToLowerInvariant() switch
        {
            "accepted"    => CalendarResponseStatus.Accepted,
            "declined"    => CalendarResponseStatus.Declined,
            "tentative"   => CalendarResponseStatus.Tentative,
            "needsaction" => CalendarResponseStatus.Pending,
            _             => CalendarResponseStatus.Accepted,
        };
    }

    private static DateTime? ParseDateOnly(string? date)
        => DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                  DateTimeStyles.None, out var d)
            ? d.Date
            : null;

    /// <summary>Parses an RFC3339 stamp (offset-carrying) to UTC ticks.</summary>
    private static long? ParseRfc3339UtcTicks(string? stamp)
        => DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
            ? dto.UtcTicks
            : null;

    // ── Create/edit/delete push (Microsoft + Google) ─────────────────────────────
    // Each public method dispatches on the account: Google-signed-in accounts push to the Google
    // Calendar API, everything else takes the Graph path. Both providers reject recurring events
    // BEFORE any network call (v1 pushes single events only) and store the SERVER's returned copy
    // with is_graph set, so the row shows immediately and survives the next replace-slice sync.

    public async Task<CalendarEvent> CreateEventAsync(AccountModel account, CalendarEvent evt, CancellationToken ct = default)
    {
        if (evt.IsRecurring)
            throw new NotSupportedException("v1 calendar push handles single events only.");
        if (IsGoogleEligible(account))
            return await CreateGoogleEventAsync(account, evt, ct);

        var body = BuildCreateBody(evt);

        // Same auth posture as SyncAccountAsync: calendar scopes, never interactive from here.
        // Prefer UTC so the response times come back ready to store as UTC ticks.
        var created = await _graph.PostReadAsync<GraphCalendarEvent>(
            account, "/me/events", body, OAuthService.GraphCalendarScopes, silentOnly: true, UtcPreferHeader, ct)
            ?? throw new InvalidOperationException("Graph returned no event body for the created event.");

        // Store the SERVER's copy (Uid = the Graph id), flagged is_graph — it shows up
        // immediately, and because it now exists on the server it survives (is re-fetched by)
        // the next replace-slice sync.
        var mapped = MapEvent(created, account.Id);
        await _store.UpsertCalendarEventAsync(mapped);
        LogService.Log($"GraphCalendarSync: created event on {account.AccountLabel} ({mapped.Uid}).");
        return mapped;
    }

    public async Task<CalendarEvent> UpdateEventAsync(AccountModel account, CalendarEvent evt, CancellationToken ct = default)
    {
        if (evt.IsRecurring)
            throw new NotSupportedException("v1 calendar push handles single events only.");
        if (IsGoogleEligible(account))
            return await UpdateGoogleEventAsync(account, evt, ct);

        var body = BuildCreateBody(evt); // PATCH accepts the same shape; omitted fields are unchanged
        var updated = await _graph.PatchReadAsync<GraphCalendarEvent>(
            account, $"/me/events/{Uri.EscapeDataString(evt.Uid)}", body,
            OAuthService.GraphCalendarScopes, silentOnly: true, UtcPreferHeader, ct)
            ?? throw new InvalidOperationException("Graph returned no event body for the updated event.");

        var mapped = MapEvent(updated, account.Id);
        await _store.UpsertCalendarEventAsync(mapped);
        LogService.Log($"GraphCalendarSync: updated event on {account.AccountLabel} ({mapped.Uid}).");
        return mapped;
    }

    public async Task DeleteEventAsync(AccountModel account, CalendarEvent evt, CancellationToken ct = default)
    {
        if (IsGoogleEligible(account))
        {
            await _google!.DeleteEventAsync(account.Username, evt.Uid, ct);
            await _store.DeleteCalendarEventAsync(evt.Uid, account.Id);
            LogService.Log($"CalendarSync: deleted Google event on {account.AccountLabel} ({evt.Uid}).");
            return;
        }

        await _graph.DeleteAsync(account, $"/me/events/{Uri.EscapeDataString(evt.Uid)}",
            OAuthService.GraphCalendarScopes, silentOnly: true, ct);
        await _store.DeleteCalendarEventAsync(evt.Uid, account.Id);
        LogService.Log($"GraphCalendarSync: deleted event on {account.AccountLabel} ({evt.Uid}).");
    }

    // ── Google write path ────────────────────────────────────────────────────────

    private async Task<CalendarEvent> CreateGoogleEventAsync(AccountModel account, CalendarEvent evt, CancellationToken ct)
    {
        var created = await _google!.CreateEventAsync(account.Username, BuildGoogleWriteBody(evt), ct);
        var mapped = MapGoogleEvent(created, account.Id);
        await _store.UpsertCalendarEventAsync(mapped);
        LogService.Log($"CalendarSync: created Google event on {account.AccountLabel} ({mapped.Uid}).");
        return mapped;
    }

    private async Task<CalendarEvent> UpdateGoogleEventAsync(AccountModel account, CalendarEvent evt, CancellationToken ct)
    {
        var updated = await _google!.UpdateEventAsync(account.Username, evt.Uid, BuildGoogleWriteBody(evt), ct);
        var mapped = MapGoogleEvent(updated, account.Id);
        await _store.UpsertCalendarEventAsync(mapped);
        LogService.Log($"CalendarSync: updated Google event on {account.AccountLabel} ({mapped.Uid}).");
        return mapped;
    }

    /// <summary>
    /// Builds the Google create/patch body for a local event about to be pushed — the Google
    /// counterpart of <see cref="BuildCreateBody"/>. Timed events send RFC3339 UTC stamps;
    /// all-day events send date-only boundaries with Google's EXCLUSIVE end date (local rows
    /// store all-day as local midnight .. 23:59:59 of the last day, so end = the day AFTER the
    /// last day). Blank description/location are omitted, mirroring the Graph body.
    /// </summary>
    internal static GoogleEventWriteBody BuildGoogleWriteBody(CalendarEvent evt)
    {
        GoogleEventTime start, end;
        if (evt.IsAllDay)
        {
            var startDay = (evt.StartTime ?? DateTime.Today).Date;
            var lastDay  = (evt.EndTime ?? startDay).Date;
            if (lastDay < startDay) lastDay = startDay;
            start = new GoogleEventTime { Date = startDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
            end   = new GoogleEventTime { Date = lastDay.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        }
        else
        {
            var startTicks = evt.StartTimeTicks ?? DateTime.UtcNow.Ticks;
            var endTicks   = evt.EndTimeTicks ?? startTicks + TimeSpan.FromMinutes(30).Ticks;
            start = new GoogleEventTime { DateTime = Rfc3339Utc(startTicks) };
            end   = new GoogleEventTime { DateTime = Rfc3339Utc(endTicks) };
        }

        return new GoogleEventWriteBody
        {
            Summary     = evt.Summary,
            Description = string.IsNullOrWhiteSpace(evt.Description) ? null : evt.Description,
            Location    = string.IsNullOrWhiteSpace(evt.Location) ? null : evt.Location,
            Start       = start,
            End         = end,
        };
    }

    private static string Rfc3339Utc(long utcTicks)
        => new DateTime(utcTicks, DateTimeKind.Utc)
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Builds the <c>POST /me/events</c> body for a local event about to be pushed.</summary>
    internal static GraphCreateEventBody BuildCreateBody(CalendarEvent evt)
    {
        GraphDateTimeTimeZone start, end;
        if (evt.IsAllDay)
        {
            // Graph all-day events must span midnight-to-midnight with an EXCLUSIVE end. Local
            // all-day rows are stored as local midnight .. 23:59:59 of the last day, so send the
            // local calendar dates as date boundaries and end = the day AFTER the last day.
            var startDay = (evt.StartTime ?? DateTime.Today).Date;
            var lastDay  = (evt.EndTime ?? startDay).Date;
            if (lastDay < startDay) lastDay = startDay;
            start = DateBoundary(startDay);
            end   = DateBoundary(lastDay.AddDays(1));
        }
        else
        {
            start = UtcStamp(evt.StartTimeTicks ?? DateTime.UtcNow.Ticks);
            end   = UtcStamp(evt.EndTimeTicks ?? (evt.StartTimeTicks ?? DateTime.UtcNow.Ticks) + TimeSpan.FromMinutes(30).Ticks);
        }

        return new GraphCreateEventBody
        {
            Subject  = evt.Summary,
            Body     = string.IsNullOrWhiteSpace(evt.Description)
                        ? null
                        : new GraphItemBody { ContentType = "text", Content = evt.Description },
            Start    = start,
            End      = end,
            Location = string.IsNullOrWhiteSpace(evt.Location)
                        ? null
                        : new GraphLocation { DisplayName = evt.Location },
            IsAllDay = evt.IsAllDay,
        };
    }

    private static GraphDateTimeTimeZone UtcStamp(long utcTicks) => new()
    {
        DateTime = new DateTime(utcTicks, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        TimeZone = "UTC",
    };

    /// <summary>An all-day boundary: midnight of the given calendar date (zone label UTC — all-day
    /// events are date-anchored, so the date is what matters).</summary>
    private static GraphDateTimeTimeZone DateBoundary(DateTime day) => new()
    {
        DateTime = day.ToString("yyyy-MM-dd'T'00:00:00", CultureInfo.InvariantCulture),
        TimeZone = "UTC",
    };

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
