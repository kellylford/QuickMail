using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace QuickMail.Services;

/// <summary>One discovered CardDAV addressbook collection: its absolute URL and display name.</summary>
public sealed record CardDavAddressbookInfo(string Url, string DisplayName);

/// <summary>
/// Minimal CardDAV client for generic contact sync (read-down v1, iCloud-first). Discovery and
/// vCard fetch only — the CardDAV sibling of <see cref="ICalDavCalendarClient"/>.
/// </summary>
public interface ICardDavContactClient
{
    /// <summary>
    /// Resolves the server's first addressbook collection:
    /// PROPFIND current-user-principal → addressbook-home-set → the home's child collections.
    /// Throws (with a presentable message) when any step fails.
    /// </summary>
    Task<CardDavAddressbookInfo> DiscoverAddressbookAsync(string serverUrl, string username, string password,
                                                          CancellationToken ct = default);

    /// <summary>
    /// REPORT addressbook-query against the collection, returning each resource's raw vCard body
    /// (one body per contact resource). A body may in principle carry several VCARDs.
    /// </summary>
    Task<List<string>> FetchVCardsAsync(string addressbookUrl, string username, string password,
                                        CancellationToken ct = default);
}

/// <summary>
/// Raw-HttpClient CardDAV client (no new NuGet dependency), mirroring
/// <see cref="CalDavCalendarClient"/>. Built against iCloud
/// (<c>https://contacts.icloud.com</c>) but intentionally generic — any RFC 6352 server speaks
/// the same discovery chain and addressbook-query REPORT.
/// </summary>
/// <remarks>
/// <para>
/// <b>Redirects are followed manually.</b> iCloud's discovery entry point 301-redirects to the
/// user's partition host (e.g. <c>p123-contacts.icloud.com</c>), and .NET strips the
/// Authorization header on cross-host auto-redirects — which would turn every discovery into a
/// silent 401. The own-constructed handler therefore disables auto-redirect and this class
/// re-issues the request (same method, body, and freshly attached Basic auth) to the Location
/// target, up to <see cref="MaxRedirects"/> hops.
/// </para>
/// <para>
/// XML parsing matches by element local name (namespace-prefix agnostic) but requires the
/// CardDAV namespace (<c>urn:ietf:params:xml:ns:carddav</c>) where it is semantically
/// load-bearing (the <c>addressbook</c> resourcetype), since <c>DAV:</c> also defines unrelated
/// names.
/// </para>
/// </remarks>
public sealed class CardDavContactClient : ICardDavContactClient, IDisposable
{
    private const int MaxRedirects = 5;
    internal const string CardDavNs = "urn:ietf:params:xml:ns:carddav";

    private static readonly HttpMethod PropfindMethod = new("PROPFIND");
    private static readonly HttpMethod ReportMethod   = new("REPORT");

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public CardDavContactClient(HttpClient? http = null)
    {
        // AllowAutoRedirect=false: redirects must be re-issued with auth re-attached (see remarks).
        _http     = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _ownsHttp = http is null;
    }

    // ── Identity helpers ─────────────────────────────────────────────────────────

    /// <summary>Deterministic fallback id for a vCard that (against RFC 6350) carries no UID.</summary>
    internal static string SyntheticUid(string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return "carddav-" + Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    // ── Discovery ────────────────────────────────────────────────────────────────

    private const string PrincipalBody =
        """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:current-user-principal/></d:prop></d:propfind>""";

    private const string HomeSetBody =
        """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:carddav"><d:prop><c:addressbook-home-set/></d:prop></d:propfind>""";

    private const string CollectionsBody =
        """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:carddav"><d:prop><d:resourcetype/><d:displayname/></d:prop></d:propfind>""";

    public async Task<CardDavAddressbookInfo> DiscoverAddressbookAsync(string serverUrl, string username, string password,
                                                                       CancellationToken ct = default)
    {
        var baseUri = NormalizeUri(serverUrl);

        var (principalXml, principalReqUri) =
            await SendAsync(PropfindMethod, baseUri, PrincipalBody, depth: "0", username, password, ct);
        var principalHref = ParseInnerHref(principalXml, "current-user-principal")
            ?? throw new InvalidOperationException("The server did not report an account principal — check the server address.");

        var (homeXml, homeReqUri) =
            await SendAsync(PropfindMethod, new Uri(principalReqUri, principalHref), HomeSetBody, depth: "0", username, password, ct);
        var homeHref = ParseInnerHref(homeXml, "addressbook-home-set")
            ?? throw new InvalidOperationException("The server did not report an address-book home for this account.");

        var (collectionsXml, collectionsReqUri) =
            await SendAsync(PropfindMethod, new Uri(homeReqUri, homeHref), CollectionsBody, depth: "1", username, password, ct);
        var (addressbookHref, addressbookName) = ParseFirstAddressbook(collectionsXml)
            ?? throw new InvalidOperationException("No address book was found for this account.");

        return new CardDavAddressbookInfo(new Uri(collectionsReqUri, addressbookHref).ToString(), addressbookName);
    }

    // ── vCard fetch ──────────────────────────────────────────────────────────────

    private const string AddressbookQueryBody =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <c:addressbook-query xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:carddav">
          <d:prop><d:getetag/><c:address-data/></d:prop>
          <c:filter/>
        </c:addressbook-query>
        """;

    public async Task<List<string>> FetchVCardsAsync(string addressbookUrl, string username, string password,
                                                     CancellationToken ct = default)
    {
        var (xml, _) = await SendAsync(ReportMethod, NormalizeUri(addressbookUrl), AddressbookQueryBody, depth: "1", username, password, ct);
        return ParseAddressData(xml);
    }

    // ── Multistatus XML parsing (internal for tests) ─────────────────────────────

    /// <summary>The d:href nested inside the named property (e.g. current-user-principal,
    /// addressbook-home-set), or null. Matches by local name so any prefix works.</summary>
    internal static string? ParseInnerHref(string xml, string propLocalName)
    {
        var doc = XDocument.Parse(xml);
        var prop = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName.Equals(propLocalName, StringComparison.OrdinalIgnoreCase));
        var href = prop?.Descendants().FirstOrDefault(e => e.Name.LocalName == "href")?.Value.Trim();
        return string.IsNullOrEmpty(href) ? null : href;
    }

    /// <summary>
    /// The first response in a Depth-1 collections multistatus whose resourcetype marks a CardDAV
    /// addressbook collection. Requires the carddav namespace on the "addressbook" resourcetype,
    /// since that name is only meaningful there; skips plain collections and non-addressbook
    /// children.
    /// </summary>
    internal static (string Href, string DisplayName)? ParseFirstAddressbook(string xml)
    {
        var doc = XDocument.Parse(xml);
        foreach (var response in doc.Descendants().Where(e => e.Name.LocalName == "response"))
        {
            var href = response.Elements().FirstOrDefault(e => e.Name.LocalName == "href")?.Value.Trim();
            if (string.IsNullOrEmpty(href)) continue;

            var isAddressbook = response.Descendants()
                .Where(e => e.Name.LocalName == "resourcetype")
                .Any(rt => rt.Elements().Any(c => c.Name.LocalName == "addressbook" &&
                                                  c.Name.NamespaceName == CardDavNs));
            if (!isAddressbook) continue;

            var name = response.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "displayname")?.Value.Trim();
            return (href, string.IsNullOrEmpty(name) ? "Contacts" : name);
        }
        return null;
    }

    /// <summary>Every non-empty address-data vCard body in a REPORT multistatus.</summary>
    internal static List<string> ParseAddressData(string xml)
    {
        var doc = XDocument.Parse(xml);
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "address-data")
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
                    $"CardDAV request failed ({(int)resp.StatusCode} {resp.StatusCode}): {Truncate(errBody, 300)}");
            }

            return (await resp.Content.ReadAsStringAsync(ct), uri);
        }

        throw new HttpRequestException("CardDAV request failed: too many redirects.");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
