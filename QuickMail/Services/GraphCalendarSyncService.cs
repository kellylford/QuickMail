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
/// <para>
/// iCloud CalDAV path (read-down v1, per-account #282): for each opted-in account whose IMAP host
/// is <c>imap.mail.me.com</c>, its calendar at caldav.icloud.com is discovered and fetched via
/// <see cref="CalDavCalendarClient"/> using the account's own app-specific password
/// (<see cref="ICredentialService.GetPassword"/>) — no separate credential. Rows are stored under
/// the real <see cref="AccountModel.Id"/>. Unlike Graph/Google there is no server-side series
/// expansion, so recurring masters keep their RRULE (+ EXDATE) and the local RecurrenceExpander
/// produces the occurrences.
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
    private readonly ICalDavCalendarClient? _calDav;
    private readonly ICredentialService? _credentials;

    // Resolved CalDAV calendar collections per account (from discovery), so each iCloud account's
    // calendars are found once and reused. A LIST now (multi-calendar): every VEVENT collection the
    // account exposes. Cleared for an account on fetch failure so stale collection URLs (e.g. a
    // calendar recreated server-side) heal on the next pass.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, List<CalDavCalendarInfo>> _calDavCalendarsByAccount = new();

    public GraphCalendarSyncService(IAccountService accounts, ILocalStoreService store, GraphClient graph,
                                    GoogleCalendarClient? google = null,
                                    ICalDavCalendarClient? calDav = null,
                                    ICredentialService? credentials = null)
    {
        _accounts    = accounts;
        _store       = store;
        _graph       = graph;
        _google      = google;
        _calDav      = calDav;
        _credentials = credentials;
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

    /// <summary>
    /// iCloud accounts (IMAP host <c>imap.mail.me.com</c>) have a CalDAV calendar at
    /// caldav.icloud.com, reached with the account's own app-specific password. No-op when the
    /// CalDAV client or credential service isn't wired (tests).
    /// </summary>
    private bool IsICloudCalendarEligible(AccountModel account)
        => _calDav != null && _credentials != null && account.SyncCalendar
           && account.ImapHost.Equals("imap.mail.me.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the account has a calendar QuickMail can pull, regardless of the opt-in flag.</summary>
    private bool HasCalendar(AccountModel account)
        => IsGraphEligible(account) || IsGoogleEligible(account) || IsICloudCalendarEligible(account);

    public async Task<GraphCalendarSyncResult> SyncAllAsync(CancellationToken ct = default)
    {
        int accountsSynced = 0, eventsFetched = 0;
        string? error = null;

        foreach (var account in _accounts.LoadAccounts())
        {
            // Per-account opt-in (#282): only accounts the user chose to sync (Manage Accounts /
            // Add Account checkbox), and only those with a calendar we can reach.
            if (!account.SyncCalendar || !HasCalendar(account)) continue;
            ct.ThrowIfCancellationRequested();

            try
            {
                var count = await SyncOneAccountAsync(account, ct);
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

    /// <summary>Dispatches one account to its provider (Microsoft / Google / iCloud CalDAV). May throw.</summary>
    private async Task<int> SyncOneAccountAsync(AccountModel account, CancellationToken ct)
    {
        if (IsGraphEligible(account))  return await SyncAccountAsync(account, ct);
        if (IsGoogleEligible(account)) return await SyncGoogleAccountAsync(account, ct);
        if (IsICloudCalendarEligible(account)) return await SyncICloudCalendarAsync(account, ct);
        return 0;
    }

    /// <summary>
    /// Syncs one account's calendar on demand (from a Manage Accounts / Add Account opt-in). Honors
    /// the opt-in flag and eligibility, and is best-effort — logs and swallows failures like the
    /// background pass, so a fire-and-forget caller never faults. Returns the event count.
    /// </summary>
    public async Task<int> SyncAccountCalendarAsync(AccountModel account, CancellationToken ct = default)
    {
        if (!account.SyncCalendar || !HasCalendar(account)) return 0;
        try
        {
            return await SyncOneAccountAsync(account, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Log($"CalendarSync (single) failed for {account.AccountLabel}", ex);
            return 0;
        }
    }

    /// <summary>Removes an account's synced calendar rows (called when the user turns its sync off).</summary>
    public async Task RemoveAccountCalendarAsync(Guid accountId, CancellationToken ct = default)
    {
        _calDavCalendarsByAccount.TryRemove(accountId, out _);
        await _store.ReplaceGraphCalendarEventsAsync(accountId, []);
    }

    private async Task<int> SyncAccountAsync(AccountModel account, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var startUtc = nowUtc - WindowBack;
        var endUtc   = nowUtc + WindowForward;
        var window =
              $"startDateTime={Uri.EscapeDataString(startUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))}"
            + $"&endDateTime={Uri.EscapeDataString(endUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))}"
            + "&$select=id,subject,bodyPreview,location,organizer,start,end,isAllDay,isOrganizer,responseStatus"
            + "&$top=100";

        // silentOnly: background sync must never launch an interactive sign-in (same reasoning as
        // contact sync). Consent comes from the account's normal sign-in: work/school accounts get
        // Calendars.ReadWrite via `.default` (declared on the app registration); personal accounts
        // acquire the explicit GraphCalendarScopes silently once granted.

        // Enumerate every calendar, then pull each one's calendarView, tagging rows with the
        // calendar they came from so Home / Work / Family show as separate selectable nodes.
        var calendars = await _graph.GetAllPagesAsync<GraphCalendar>(
            account, "/me/calendars?$select=id,name", OAuthService.GraphCalendarScopes, silentOnly: true, headers: null, ct);

        var union = new List<CalendarEvent>();
        foreach (var cal in calendars)
        {
            if (string.IsNullOrEmpty(cal.Id)) continue;
            var calName = string.IsNullOrWhiteSpace(cal.Name) ? "Calendar" : cal.Name.Trim();
            var path = $"/me/calendars/{Uri.EscapeDataString(cal.Id)}/calendarView?{window}";
            var items = await _graph.GetAllPagesAsync<GraphCalendarEvent>(
                account, path, OAuthService.GraphCalendarScopes, silentOnly: true, UtcPreferHeader, ct);
            union.AddRange(items
                .Where(e => !string.IsNullOrEmpty(e.Id))
                .Select(e => MapEvent(e, account.Id, cal.Id, calName)));
        }

        await _store.ReplaceGraphCalendarEventsAsync(account.Id, union);
        return union.Count;
    }

    // ── Google (read-down v1) ────────────────────────────────────────────────────

    private async Task<int> SyncGoogleAccountAsync(AccountModel account, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var timeMin = nowUtc - WindowBack;
        var timeMax = nowUtc + WindowForward;

        // Enumerate the account's calendars, then pull each one's events, tagging rows with the
        // calendar they came from so each shows as its own selectable node.
        var calendars = await _google!.GetCalendarListAsync(account.Username, ct);

        var union = new List<CalendarEvent>();
        foreach (var cal in calendars)
        {
            if (string.IsNullOrEmpty(cal.Id) || cal.Deleted) continue;
            var calName = string.IsNullOrWhiteSpace(cal.Summary) ? "Calendar" : cal.Summary.Trim();
            var items = await _google!.GetEventsAsync(account.Username, timeMin, timeMax, cal.Id, ct);
            union.AddRange(items
                .Where(e => !string.IsNullOrEmpty(e.Id))
                // Cancelled occurrences can appear despite the default filters; they are deletions.
                .Where(e => !string.Equals(e.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
                .Select(e => MapGoogleEvent(e, account.Id, cal.Id, calName)));
        }

        await _store.ReplaceGraphCalendarEventsAsync(account.Id, union);
        return union.Count;
    }

    /// <summary>
    /// Maps one Google Calendar occurrence to a local row. Stored exactly like a Graph row —
    /// is_graph=1 here means "server-synced calendar row", regardless of which provider it came
    /// from; the account id says whose calendar it is. <paramref name="calendarId"/>/
    /// <paramref name="calendarName"/> tag which of the account's calendars it belongs to (empty for
    /// the write-back path, which re-tags on the next full sync).
    /// </summary>
    internal static CalendarEvent MapGoogleEvent(GoogleCalendarEvent e, Guid accountId,
        string calendarId = "", string calendarName = "")
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
            CalendarId      = calendarId,
            CalendarName    = calendarName,
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

    // ── iCloud CalDAV (read-down v1, per-account) ────────────────────────────────

    private const string ICloudCalDavUrl = "https://caldav.icloud.com";

    /// <summary>
    /// Syncs one iCloud account's calendar over CalDAV, using the account's own app-specific
    /// password (the same one IMAP uses — no separate credential). Discovery result is cached per
    /// account and cleared on fetch failure so a recreated server collection heals next pass. Rows
    /// are stored under the real <c>account.Id</c> and replace-sliced like the Graph/Google paths.
    /// </summary>
    private async Task<int> SyncICloudCalendarAsync(AccountModel account, CancellationToken ct)
    {
        var password = ICloudPassword(account);

        if (!_calDavCalendarsByAccount.TryGetValue(account.Id, out var calendars))
        {
            calendars = await _calDav!.DiscoverCalendarsAsync(ICloudCalDavUrl, account.Username, password, ct);
            _calDavCalendarsByAccount[account.Id] = calendars;
        }

        var nowUtc = DateTime.UtcNow;
        var union = new List<CalendarEvent>();
        try
        {
            // Fetch each discovered calendar, tagging rows with the collection href / display name
            // so each calendar is its own selectable source.
            foreach (var cal in calendars)
            {
                var resources = await _calDav!.FetchEventIcsAsync(cal.Url, account.Username, password,
                                                                  nowUtc - WindowBack, nowUtc + WindowForward, ct);
                union.AddRange(MapCalDavEvents(resources, account.Id, cal.Url, cal.DisplayName));
            }
        }
        catch
        {
            _calDavCalendarsByAccount.TryRemove(account.Id, out _); // stale collections? re-discover next pass
            throw;
        }

        await _store.ReplaceGraphCalendarEventsAsync(account.Id, union);
        return union.Count;
    }

    /// <summary>
    /// Maps fetched calendar-data ICS bodies to local rows. Unlike Graph/Google there is NO
    /// server-side expansion of recurring series: the master VEVENT arrives with its RRULE, which
    /// is stored so the local <c>RecurrenceExpander</c> produces the occurrences, and EXDATE
    /// entries land in the exdates column so deleted single occurrences stay hidden.
    /// v1 limitations, by design: overridden instances (RECURRENCE-ID VEVENTs) are skipped — a
    /// rescheduled occurrence shows at its original series slot; and STATUS:CANCELLED events are
    /// dropped. Duplicate UIDs across bodies collapse to one row (last wins).
    /// </summary>
    internal static List<CalendarEvent> MapCalDavEvents(IEnumerable<(string Href, string Ics)> icsBodies, Guid accountId,
        string calendarId = "", string calendarName = "")
    {
        var byUid = new Dictionary<string, CalendarEvent>(StringComparer.Ordinal);
        foreach (var (href, body) in icsBodies)
        {
            // Resolve the resource href absolute against the calendar collection when the server
            // gave a relative one (the client normally resolves it already). This is the REAL
            // resource URL — Apple names iPhone/web-created resources randomly (≠ UID), so it is
            // what edit/delete must target.
            var resourceUrl = href;
            if (!string.IsNullOrEmpty(href) && !Uri.IsWellFormedUriString(href, UriKind.Absolute)
                && Uri.TryCreate(calendarId, UriKind.Absolute, out var baseUri)
                && Uri.TryCreate(baseUri, href, out var abs))
                resourceUrl = abs.ToString();

            foreach (var ics in IcsModel.ParseAllEvents(body))
            {
                if (string.Equals(ics.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(ics.RecurrenceId)) continue;
                var evt = MapCalDavEvent(ics, accountId, calendarId, calendarName, resourceUrl);
                byUid[evt.Uid] = evt;
            }
        }
        return byUid.Values.ToList();
    }

    /// <summary>Maps one parsed VEVENT to a local row (is_graph=1 = "server-synced"), tagged with its calendar and CalDAV resource href.</summary>
    internal static CalendarEvent MapCalDavEvent(IcsModel ics, Guid accountId,
        string calendarId = "", string calendarName = "", string resourceUrl = "")
    {
        long? startTicks, endTicks;
        if (ics.IsAllDay)
        {
            // ICS all-day: date values with an EXCLUSIVE DTEND. Re-anchor at LOCAL midnight /
            // 23:59:59 of the last day, matching Graph, Google, and locally-authored all-day rows.
            var startDay = ics.StartTime?.Date;
            var endDay   = ics.EndTime?.Date.AddDays(-1) ?? startDay;
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
            // IcsModel start/end are Kind-carrying (Utc for Z stamps, Local otherwise);
            // ToUniversalTime handles both.
            startTicks = ics.StartTime?.ToUniversalTime().Ticks;
            endTicks   = ics.EndTime?.ToUniversalTime().Ticks;
        }

        var uid = !string.IsNullOrWhiteSpace(ics.Uid)
            ? ics.Uid
            : CalDavCalendarClient.SyntheticUid($"{ics.Summary}|{startTicks}");

        var evt = new CalendarEvent
        {
            Uid             = uid,
            AccountId       = accountId,
            IsGraph         = true, // "server-synced row" — read-only in UI, replace-slice owned
            CalendarId      = calendarId,
            CalendarName    = calendarName,
            ResourceUrl     = resourceUrl,
            Summary         = ics.Summary?.Trim() ?? string.Empty,
            Description     = ics.Description?.Trim() ?? string.Empty,
            Location        = ics.Location?.Trim() ?? string.Empty,
            Organizer       = ics.Organizer?.Trim() ?? string.Empty,
            OrganizerName   = ics.OrganizerName?.Trim() ?? string.Empty,
            StartTimeTicks  = startTicks,
            EndTimeTicks    = endTicks,
            IsAllDay        = ics.IsAllDay,
            // The user's own calendar; CalDAV v1 does not resolve per-attendee RSVP state.
            ResponseStatus  = CalendarResponseStatus.Accepted,
            SourceMessageId = string.Empty, // not an invite email
            SourceFolder    = string.Empty,
            Sequence        = ics.Sequence,
            RecurrenceRule  = string.IsNullOrWhiteSpace(ics.RecurrenceRule) ? null : ics.RecurrenceRule,
        };
        foreach (var exDate in ics.ExDates)
            evt.AddExDate(exDate);
        return evt;
    }

    /// <summary>The account's app-specific password (the same one IMAP uses), or a clear error.</summary>
    private string ICloudPassword(AccountModel account)
    {
        var password = _credentials!.GetPassword(account.Id);
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException(
                $"no app-specific password saved for {account.Username} — re-enter it in Manage Accounts.");
        return password;
    }

    // ── iCloud CalDAV write (create / edit / delete, single events) ───────────────
    // A locally-authored appointment is serialized to a VCALENDAR (IcsModel.ExportEvent) and PUT to
    // its calendar collection; the row is stored is_graph so it shows immediately and survives the
    // next replace-slice sync (which re-fetches it from the server). v1 is last-write-wins — no
    // ETag / If-Match round-trip yet (a create still uses If-None-Match to avoid clobbering).

    private async Task<CalendarEvent> CreateICloudEventAsync(AccountModel account, CalendarEvent evt, CancellationToken ct)
    {
        var password = ICloudPassword(account);
        if (string.IsNullOrWhiteSpace(evt.CalendarId))
            throw new InvalidOperationException("No iCloud calendar was selected for the new appointment.");
        if (string.IsNullOrWhiteSpace(evt.Uid))
            evt.Uid = Guid.NewGuid().ToString("N") + "@quickmail";

        var ics = IcsModel.ExportEvent(evt, includeMethod: false); // RFC 4791: no METHOD on a stored CalDAV resource
        // A new resource: QuickMail chooses its name (after the UID). Record the URL it PUT to so an
        // immediate edit (before the next read-sync captures the server's real href) still targets it.
        var resourceUrl = CalDavCalendarClient.EventResourceUrl(evt.CalendarId, evt.Uid);
        await _calDav!.PutEventAsync(resourceUrl, ics, account.Username, password, ifNoneMatch: true, ct);

        evt.AccountId = account.Id;
        evt.IsGraph = true; // "server-synced row"
        evt.ResponseStatus = CalendarResponseStatus.Accepted;
        evt.ResourceUrl = resourceUrl;
        await _store.UpsertCalendarEventAsync(evt);
        LogService.Log($"CalendarSync: created iCloud event on {account.AccountLabel} ({evt.Uid}).");
        return evt;
    }

    private async Task<CalendarEvent> UpdateICloudEventAsync(AccountModel account, CalendarEvent evt, CancellationToken ct)
    {
        var password = ICloudPassword(account);
        if (string.IsNullOrWhiteSpace(evt.CalendarId))
            throw new InvalidOperationException("This iCloud appointment has no calendar to update.");

        var ics = IcsModel.ExportEvent(evt, includeMethod: false); // RFC 4791: no METHOD on a stored CalDAV resource
        // Edit the resource where it actually lives: the real href captured on read-sync when we have
        // one (Apple names iPhone/web-created resources randomly, ≠ UID), else the QuickMail-created
        // name (a row not yet re-synced). Using {collection}/{uid}.ics blindly 404s / duplicates.
        var resourceUrl = string.IsNullOrEmpty(evt.ResourceUrl)
            ? CalDavCalendarClient.EventResourceUrl(evt.CalendarId, evt.Uid)
            : evt.ResourceUrl;
        await _calDav!.PutEventAsync(resourceUrl, ics, account.Username, password, ifNoneMatch: false, ct);

        evt.AccountId = account.Id;
        evt.IsGraph = true;
        evt.ResourceUrl = resourceUrl;
        await _store.UpsertCalendarEventAsync(evt);
        LogService.Log($"CalendarSync: updated iCloud event on {account.AccountLabel} ({evt.Uid}).");
        return evt;
    }

    private async Task DeleteICloudEventAsync(AccountModel account, CalendarEvent evt, CancellationToken ct)
    {
        var password = ICloudPassword(account);
        if (string.IsNullOrWhiteSpace(evt.CalendarId))
            throw new InvalidOperationException("This iCloud appointment has no calendar to delete from.");

        // Delete the resource where it actually lives: the real href captured on read-sync when we
        // have one (Apple names iPhone/web-created resources randomly, ≠ UID), else the
        // QuickMail-created name (a row not yet re-synced). Blindly using {collection}/{uid}.ics 404s.
        var resourceUrl = string.IsNullOrEmpty(evt.ResourceUrl)
            ? CalDavCalendarClient.EventResourceUrl(evt.CalendarId, evt.Uid)
            : evt.ResourceUrl;
        await _calDav!.DeleteEventAsync(resourceUrl, account.Username, password, ct);
        await _store.DeleteCalendarEventAsync(evt.Uid, account.Id);
        LogService.Log($"CalendarSync: deleted iCloud event on {account.AccountLabel} ({evt.Uid}).");
    }

    // ── Create/edit/delete push (Microsoft + Google) ─────────────────────────────
    // Each public method dispatches on the account: Google-signed-in accounts push to the Google
    // Calendar API, everything else takes the Graph path. Both providers reject recurring events
    // BEFORE any network call (v1 pushes single events only) and store the SERVER's returned copy
    // with is_graph set, so the row shows immediately and survives the next replace-slice sync.

    public async Task<CalendarEvent> CreateEventAsync(AccountModel account, CalendarEvent evt, CancellationToken ct = default)
    {
        if (evt.IsRecurring)
            throw new NotSupportedException("v1 calendar push handles single events only.");
        // Dispatch order mirrors the read path (SyncOneAccountAsync): Graph → Google → iCloud.
        // Graph is the default body; the guarded providers keep Google before iCloud. Predicates are
        // mutually exclusive, so ordering is for consistency, not behavior.
        if (IsGoogleEligible(account))
            return await CreateGoogleEventAsync(account, evt, ct);
        if (IsICloudCalendarEligible(account))
            return await CreateICloudEventAsync(account, evt, ct);

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
        // Dispatch order mirrors the read path (SyncOneAccountAsync): Graph → Google → iCloud.
        if (IsGoogleEligible(account))
            return await UpdateGoogleEventAsync(account, evt, ct);
        if (IsICloudCalendarEligible(account))
            return await UpdateICloudEventAsync(account, evt, ct);

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
        // Dispatch order mirrors the read path (SyncOneAccountAsync): Graph → Google → iCloud.
        if (IsGoogleEligible(account))
        {
            await _google!.DeleteEventAsync(account.Username, evt.Uid, GoogleCalendarId(evt), ct);
            await _store.DeleteCalendarEventAsync(evt.Uid, account.Id);
            LogService.Log($"CalendarSync: deleted Google event on {account.AccountLabel} ({evt.Uid}).");
            return;
        }
        if (IsICloudCalendarEligible(account))
        {
            await DeleteICloudEventAsync(account, evt, ct);
            return;
        }

        await _graph.DeleteAsync(account, $"/me/events/{Uri.EscapeDataString(evt.Uid)}",
            OAuthService.GraphCalendarScopes, silentOnly: true, ct);
        await _store.DeleteCalendarEventAsync(evt.Uid, account.Id);
        LogService.Log($"GraphCalendarSync: deleted event on {account.AccountLabel} ({evt.Uid}).");
    }

    // ── Google write path ────────────────────────────────────────────────────────

    // Target the calendar the event actually lives on. A blank CalendarId (e.g. a brand-new
    // event whose save target is the account's default) falls back to "primary". Using this for
    // update/delete is the fix for events on secondary Google calendars, which 404 against primary.
    private static string GoogleCalendarId(CalendarEvent evt)
        => string.IsNullOrWhiteSpace(evt.CalendarId) ? "primary" : evt.CalendarId;

    private async Task<CalendarEvent> CreateGoogleEventAsync(AccountModel account, CalendarEvent evt, CancellationToken ct)
    {
        var calId = GoogleCalendarId(evt);
        var created = await _google!.CreateEventAsync(account.Username, BuildGoogleWriteBody(evt), calId, ct);
        var mapped = MapGoogleEvent(created, account.Id, calId, evt.CalendarName);
        await _store.UpsertCalendarEventAsync(mapped);
        LogService.Log($"CalendarSync: created Google event on {account.AccountLabel} ({mapped.Uid}).");
        return mapped;
    }

    private async Task<CalendarEvent> UpdateGoogleEventAsync(AccountModel account, CalendarEvent evt, CancellationToken ct)
    {
        var calId = GoogleCalendarId(evt);
        var updated = await _google!.UpdateEventAsync(account.Username, evt.Uid, BuildGoogleWriteBody(evt), calId, ct);
        var mapped = MapGoogleEvent(updated, account.Id, calId, evt.CalendarName);
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

    /// <summary>
    /// Maps one Graph calendarView occurrence to a local <see cref="CalendarEvent"/> row.
    /// <paramref name="calendarId"/>/<paramref name="calendarName"/> tag which of the account's
    /// calendars it belongs to (empty for the write-back path, which re-tags on the next full sync).
    /// </summary>
    internal static CalendarEvent MapEvent(GraphCalendarEvent e, Guid accountId,
        string calendarId = "", string calendarName = "")
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
            CalendarId      = calendarId,
            CalendarName    = calendarName,
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
