using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace QuickMail.Services;

/// <summary>One discovered CalDAV calendar collection: its absolute URL and display name.</summary>
public sealed record CalDavCalendarInfo(string Url, string DisplayName);

/// <summary>
/// Minimal CalDAV client for generic calendar sync (read-down v1, iCloud-first). Discovery and
/// event fetch only — see <see cref="CalDavCalendarClient"/> for the wire details.
/// </summary>
public interface ICalDavCalendarClient
{
    /// <summary>
    /// Resolves the server's first VEVENT-capable calendar collection:
    /// PROPFIND current-user-principal → calendar-home-set → the home's child collections.
    /// Throws (with a presentable message) when any step fails — this doubles as the
    /// Settings "Test" validation.
    /// </summary>
    Task<CalDavCalendarInfo> DiscoverCalendarAsync(string serverUrl, string username, string password,
                                                   CancellationToken ct = default);

    /// <summary>
    /// REPORT calendar-query for VEVENTs intersecting the UTC window, returning each
    /// response's raw calendar-data ICS body (one body per event resource; a body may hold
    /// several VEVENTs — a recurring master plus overridden instances).
    /// </summary>
    Task<List<string>> FetchEventIcsAsync(string calendarUrl, string username, string password,
                                          DateTime startUtc, DateTime endUtc, CancellationToken ct = default);
}

/// <summary>
/// Raw-HttpClient CalDAV client (no new NuGet dependency, mirroring
/// <see cref="GoogleCalendarClient"/>'s shape) with Basic auth per request. Built against
/// iCloud (<c>https://caldav.icloud.com</c>) but intentionally generic — Fastmail, Nextcloud,
/// and other RFC 4791 servers speak the same three-step discovery and calendar-query REPORT.
/// </summary>
/// <remarks>
/// <para>
/// <b>Redirects are followed manually.</b> iCloud's discovery entry point 301-redirects to the
/// user's partition host (e.g. <c>p123-caldav.icloud.com</c>), and .NET strips the
/// Authorization header on cross-host auto-redirects — which would turn every discovery into a
/// silent 401. The own-constructed handler therefore disables auto-redirect and this class
/// re-issues the request (same method, body, and freshly attached Basic auth) to the Location
/// target, up to <see cref="MaxRedirects"/> hops.
/// </para>
/// <para>
/// XML parsing matches by element local name (namespace-prefix agnostic) but requires the
/// CalDAV namespace (<c>urn:ietf:params:xml:ns:caldav</c>) where it is semantically load-bearing
/// (the <c>calendar</c> resourcetype), since <c>DAV:</c> also defines unrelated names.
/// </para>
/// </remarks>
public sealed class CalDavCalendarClient : ICalDavCalendarClient, IDisposable
{
    private const int MaxRedirects = 5;
    internal const string CalDavNs = "urn:ietf:params:xml:ns:caldav";

    private static readonly HttpMethod PropfindMethod = new("PROPFIND");
    private static readonly HttpMethod ReportMethod   = new("REPORT");

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public CalDavCalendarClient(HttpClient? http = null)
    {
        // AllowAutoRedirect=false: redirects must be re-issued with auth re-attached (see remarks).
        _http     = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _ownsHttp = http is null;
    }

    // ── Identity helpers ─────────────────────────────────────────────────────────

    /// <summary>Windows Credential Manager key for a CalDAV source's app-specific password.</summary>
    public static string SecretKeyFor(string username) => $"QuickMail/CalDAV/{username.Trim()}";

    /// <summary>
    /// The synthetic, stable AccountId for a CalDAV source's rows in the CalendarEvent table.
    /// CalDAV sources are not QuickMail accounts, but every calendar row needs an account id, so
    /// one is derived DETERMINISTICALLY from the source's identity: the first 16 bytes of
    /// SHA-256 over <c>"caldav|{url}|{username}"</c> (both trimmed and lower-cased, trailing
    /// slash dropped from the URL). The same Settings values always map to the same id — the
    /// calendar-tree filter, replace-slice deletes, and previously synced rows all keep lining
    /// up across restarts — while different sources cannot collide with each other, with real
    /// account GUIDs (random v4), or with the local sentinel (<see cref="System.Guid.Empty"/>).
    /// </summary>
    public static Guid AccountIdFor(string url, string username)
    {
        var key = $"caldav|{url.Trim().TrimEnd('/').ToLowerInvariant()}|{username.Trim().ToLowerInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>Deterministic fallback Uid for a VEVENT that (against RFC 5545) carries no UID.</summary>
    internal static string SyntheticUid(string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return "caldav-" + Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    // ── Discovery ────────────────────────────────────────────────────────────────

    private const string PrincipalBody =
        """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:current-user-principal/></d:prop></d:propfind>""";

    private const string HomeSetBody =
        """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:prop><c:calendar-home-set/></d:prop></d:propfind>""";

    private const string CollectionsBody =
        """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:prop><d:resourcetype/><d:displayname/><c:supported-calendar-component-set/></d:prop></d:propfind>""";

    public async Task<CalDavCalendarInfo> DiscoverCalendarAsync(string serverUrl, string username, string password,
                                                                CancellationToken ct = default)
    {
        var baseUri = NormalizeUri(serverUrl);

        var (principalXml, principalReqUri) =
            await SendAsync(PropfindMethod, baseUri, PrincipalBody, depth: "0", username, password, ct);
        var principalHref = ParseInnerHref(principalXml, "current-user-principal")
            ?? throw new InvalidOperationException("The server did not report an account principal — check the server address.");

        var (homeXml, homeReqUri) =
            await SendAsync(PropfindMethod, new Uri(principalReqUri, principalHref), HomeSetBody, depth: "0", username, password, ct);
        var homeHref = ParseInnerHref(homeXml, "calendar-home-set")
            ?? throw new InvalidOperationException("The server did not report a calendar home for this account.");

        var (collectionsXml, collectionsReqUri) =
            await SendAsync(PropfindMethod, new Uri(homeReqUri, homeHref), CollectionsBody, depth: "1", username, password, ct);
        var (calendarHref, calendarName) = ParseFirstVEventCalendar(collectionsXml)
            ?? throw new InvalidOperationException("No event calendar was found for this account.");

        return new CalDavCalendarInfo(new Uri(collectionsReqUri, calendarHref).ToString(), calendarName);
    }

    // ── Event fetch ──────────────────────────────────────────────────────────────

    public async Task<List<string>> FetchEventIcsAsync(string calendarUrl, string username, string password,
                                                       DateTime startUtc, DateTime endUtc, CancellationToken ct = default)
    {
        var body =
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <c:calendar-query xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:prop><d:getetag/><c:calendar-data/></d:prop>
              <c:filter>
                <c:comp-filter name="VCALENDAR">
                  <c:comp-filter name="VEVENT">
                    <c:time-range start="{Stamp(startUtc)}" end="{Stamp(endUtc)}"/>
                  </c:comp-filter>
                </c:comp-filter>
              </c:filter>
            </c:calendar-query>
            """;

        var (xml, _) = await SendAsync(ReportMethod, NormalizeUri(calendarUrl), body, depth: "1", username, password, ct);
        return ParseCalendarData(xml);
    }

    private static string Stamp(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc)
            .ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    // ── Multistatus XML parsing (internal for tests) ─────────────────────────────

    /// <summary>The d:href nested inside the named property (e.g. current-user-principal,
    /// calendar-home-set), or null. Matches by local name so any prefix works.</summary>
    internal static string? ParseInnerHref(string xml, string propLocalName)
    {
        var doc = XDocument.Parse(xml);
        var prop = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName.Equals(propLocalName, StringComparison.OrdinalIgnoreCase));
        var href = prop?.Descendants().FirstOrDefault(e => e.Name.LocalName == "href")?.Value.Trim();
        return string.IsNullOrEmpty(href) ? null : href;
    }

    /// <summary>
    /// The first response in a Depth-1 collections multistatus whose resourcetype marks a CalDAV
    /// calendar collection AND whose supported-calendar-component-set includes VEVENT (a missing
    /// component set counts as supporting everything). Skips VTODO-only collections (e.g.
    /// Reminders lists) and non-calendar children (inbox/outbox/notifications).
    /// </summary>
    internal static (string Href, string DisplayName)? ParseFirstVEventCalendar(string xml)
    {
        var doc = XDocument.Parse(xml);
        foreach (var response in doc.Descendants().Where(e => e.Name.LocalName == "response"))
        {
            var href = response.Elements().FirstOrDefault(e => e.Name.LocalName == "href")?.Value.Trim();
            if (string.IsNullOrEmpty(href)) continue;

            // Must be a CalDAV calendar collection — require the caldav namespace here, since
            // "calendar" is only meaningful as urn:ietf:params:xml:ns:caldav's resourcetype.
            var isCalendar = response.Descendants()
                .Where(e => e.Name.LocalName == "resourcetype")
                .Any(rt => rt.Elements().Any(c => c.Name.LocalName == "calendar" &&
                                                  c.Name.NamespaceName == CalDavNs));
            if (!isCalendar) continue;

            var componentSets = response.Descendants()
                .Where(e => e.Name.LocalName == "supported-calendar-component-set")
                .ToList();
            var supportsVEvent = componentSets.Count == 0 || componentSets.Any(set =>
                set.Elements().Any(c => c.Name.LocalName == "comp" &&
                    string.Equals((string?)c.Attribute("name"), "VEVENT", StringComparison.OrdinalIgnoreCase)));
            if (!supportsVEvent) continue;

            var name = response.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "displayname")?.Value.Trim();
            return (href, string.IsNullOrEmpty(name) ? "Calendar" : name);
        }
        return null;
    }

    /// <summary>Every non-empty calendar-data ICS body in a REPORT multistatus.</summary>
    internal static List<string> ParseCalendarData(string xml)
    {
        var doc = XDocument.Parse(xml);
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "calendar-data")
            .Select(e => e.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();
    }

    // ── HTTP plumbing ────────────────────────────────────────────────────────────

    private static Uri NormalizeUri(string url)
    {
        var trimmed = url.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "https://" + trimmed;
        return new Uri(trimmed);
    }

    /// <summary>
    /// Sends one WebDAV request with Basic auth, following redirects manually (auth re-attached
    /// on each hop — see class remarks). Returns the response body and the FINAL request URI so
    /// relative hrefs in the response resolve against the host that actually answered.
    /// </summary>
    private async Task<(string Body, Uri FinalUri)> SendAsync(HttpMethod method, Uri uri, string body,
        string depth, string username, string password, CancellationToken ct)
    {
        var auth = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var req = new HttpRequestMessage(method, uri);
            req.Headers.Authorization = auth;
            req.Headers.TryAddWithoutValidation("Depth", depth);
            req.Content = new StringContent(body, Encoding.UTF8, "application/xml");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            if ((int)resp.StatusCode is 301 or 302 or 307 or 308 && resp.Headers.Location != null)
            {
                uri = resp.Headers.Location.IsAbsoluteUri
                    ? resp.Headers.Location
                    : new Uri(uri, resp.Headers.Location);
                continue;
            }

            if (!resp.IsSuccessStatusCode) // 207 Multi-Status is a success code
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException(
                    $"CalDAV request failed ({(int)resp.StatusCode} {resp.StatusCode}): {Truncate(errBody, 300)}");
            }

            return (await resp.Content.ReadAsStringAsync(ct), uri);
        }

        throw new HttpRequestException("CalDAV request failed: too many redirects.");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
