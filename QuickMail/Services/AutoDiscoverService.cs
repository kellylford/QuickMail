using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Three-tier settings discovery for an email address.
///
/// 1. The built-in provider table — offline, instant, nothing leaves the machine.
/// 2. Mozilla's autoconfig database (the one Thunderbird uses) — only the DOMAIN is sent.
/// 3. The domain's own Exchange Autodiscover endpoint — the full address is sent, to that domain's
///    own server, exactly as Outlook does.
///
/// Tiers 2 and 3 are skipped entirely when <see cref="ConfigModel.AutoDiscoverOnline"/> is off.
/// A failure at any tier falls through to the next; exhausting all three returns null rather than
/// throwing, because the caller's job is to surface "not found" and open manual entry.
/// </summary>
public sealed class AutoDiscoverService : IAutoDiscoverService, IDisposable
{
    private const string IspdbUrlPrefix = "https://autoconfig.thunderbird.net/v1.1/";

    /// <summary>Per-tier budget. Three tiers plus overhead stay inside <see cref="OverallTimeout"/>.</summary>
    private static readonly TimeSpan TierTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Ceiling for the whole call, so a slow tier cannot hold the dialog indefinitely.</summary>
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(12);

    private readonly IProviderCatalog _catalog;
    private readonly IConfigService _config;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    public AutoDiscoverService(IProviderCatalog catalog, IConfigService config)
        : this(catalog, config, CreateDefaultClient(), ownsHttpClient: true)
    {
    }

    /// <summary>Test seam: lets a test supply an <see cref="HttpMessageHandler"/>-backed client.</summary>
    internal AutoDiscoverService(IProviderCatalog catalog, IConfigService config, HttpClient http, bool ownsHttpClient)
    {
        _catalog = catalog;
        _config = config;
        _http = http;
        _ownsHttpClient = ownsHttpClient;
    }

    private static HttpClient CreateDefaultClient()
    {
        // AllowAutoRedirect is off deliberately: an automatic redirect could walk an HTTPS lookup
        // down to plain HTTP, which would leak the address and let anyone on the path dictate the
        // mail servers QuickMail is about to trust. Certificate validation is left at its default —
        // never disabled here, whatever the account's AcceptInvalidCert setting says.
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("QuickMail-Autoconfig/1.0");
        return client;
    }

    public async Task<DiscoveredSettings?> DiscoverAsync(string emailAddress, CancellationToken ct)
    {
        var domain = DomainOf(emailAddress);
        if (domain is null) return null;

        // Tier 1 — the built-in table. Always runs, even with online discovery disabled.
        var known = _catalog.MatchByEmail(emailAddress);
        if (known is not null)
        {
            LogService.Debug($"AutoDiscover: {domain} matched built-in provider '{known.Id}'.");
            return new DiscoveredSettings(
                known.ImapHost, known.ImapPort, known.ImapUseSsl,
                known.SmtpHost, known.SmtpPort, known.SmtpUseSsl,
                known.Id, known.DisplayName, DiscoverySource.LocalCatalog);
        }

        if (!_config.Load().AutoDiscoverOnline)
        {
            LogService.Debug($"AutoDiscover: {domain} unknown and online lookup is disabled.");
            return null;
        }

        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(OverallTimeout);

        // Tier 2 — Mozilla autoconfig. Domain only.
        var ispdb = await TryTierAsync(
            () => QueryIspdbAsync(domain, overall.Token), "ISPDB", domain).ConfigureAwait(false);
        if (ispdb is not null) return ispdb;

        // Tier 3 — the domain's own Exchange Autodiscover endpoint.
        var exchange = await TryTierAsync(
            () => QueryAutodiscoverAsync(emailAddress, domain, overall.Token), "Autodiscover", domain)
            .ConfigureAwait(false);
        if (exchange is not null) return exchange;

        LogService.Debug($"AutoDiscover: no settings found for {domain}.");
        return null;
    }

    /// <summary>
    /// Runs one tier, swallowing every failure mode it can produce (DNS, TLS, 404, timeout,
    /// malformed XML) so the next tier still gets its turn. The domain is logged; the address never is.
    /// </summary>
    private static async Task<DiscoveredSettings?> TryTierAsync(
        Func<Task<DiscoveredSettings?>> tier, string tierName, string domain)
    {
        try
        {
            var result = await tier().ConfigureAwait(false);
            if (result is not null)
                LogService.Debug($"AutoDiscover: {tierName} resolved {domain}.");
            return result;
        }
        catch (Exception ex)
        {
            // Includes OperationCanceledException from the per-tier / overall timeout: a tier that
            // times out is a tier that found nothing, and the next one should still run.
            LogService.Debug($"AutoDiscover: {tierName} failed for {domain}: {ex.GetType().Name}.");
            return null;
        }
    }

    // ── Tier 2: Mozilla autoconfig (ISPDB) ───────────────────────────────────────────

    private async Task<DiscoveredSettings?> QueryIspdbAsync(string domain, CancellationToken ct)
    {
        using var tier = CancellationTokenSource.CreateLinkedTokenSource(ct);
        tier.CancelAfter(TierTimeout);

        var url = IspdbUrlPrefix + Uri.EscapeDataString(domain);
        using var response = await _http.GetAsync(url, tier.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        var xml = await response.Content.ReadAsStringAsync(tier.Token).ConfigureAwait(false);
        return ParseIspdb(xml, domain);
    }

    /// <summary>
    /// Parses a Thunderbird autoconfig document. Internal so the parser is testable without HTTP.
    /// Returns null unless BOTH an IMAP incoming server and an SMTP outgoing server are present —
    /// half a configuration would silently leave the other half at defaults that do not work.
    /// </summary>
    internal static DiscoveredSettings? ParseIspdb(string xml, string domain)
    {
        var doc = XDocument.Parse(xml);
        var provider = doc.Descendants("emailProvider").FirstOrDefault();
        if (provider is null) return null;

        var incoming = provider.Elements("incomingServer")
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("type"), "imap", StringComparison.OrdinalIgnoreCase));
        var outgoing = provider.Elements("outgoingServer")
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("type"), "smtp", StringComparison.OrdinalIgnoreCase));
        if (incoming is null || outgoing is null) return null;

        var imapHost = (string?)incoming.Element("hostname");
        var smtpHost = (string?)outgoing.Element("hostname");
        if (string.IsNullOrWhiteSpace(imapHost) || string.IsNullOrWhiteSpace(smtpHost)) return null;

        if (!TryParsePort(incoming.Element("port"), out var imapPort)) return null;
        if (!TryParsePort(outgoing.Element("port"), out var smtpPort)) return null;

        var displayName = (string?)provider.Element("displayName");

        return new DiscoveredSettings(
            imapHost.Trim(), imapPort, IsImplicitSsl((string?)incoming.Element("socketType")),
            smtpHost.Trim(), smtpPort, IsImplicitSsl((string?)outgoing.Element("socketType")),
            ProviderId: null,
            DisplayName: string.IsNullOrWhiteSpace(displayName) ? domain : displayName.Trim(),
            DiscoverySource.Ispdb);
    }

    /// <summary>
    /// Maps an autoconfig socketType to QuickMail's boolean. "SSL" is implicit TLS on connect;
    /// "STARTTLS" and "plain" are not. Anything unrecognized is treated as not-implicit, matching
    /// AccountModel's SMTP default of STARTTLS on 587.
    /// </summary>
    private static bool IsImplicitSsl(string? socketType) =>
        string.Equals(socketType?.Trim(), "SSL", StringComparison.OrdinalIgnoreCase);

    private static bool TryParsePort(XElement? element, out int port)
    {
        port = 0;
        var raw = ((string?)element)?.Trim();
        return !string.IsNullOrEmpty(raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
            && port is > 0 and <= 65535;
    }

    // ── Tier 3: Exchange Autodiscover ────────────────────────────────────────────────

    private async Task<DiscoveredSettings?> QueryAutodiscoverAsync(string email, string domain, CancellationToken ct)
    {
        // The two endpoints Outlook tries, in the same order. HTTPS only — there is no HTTP fallback
        // by design, and redirects are not followed (see CreateDefaultClient).
        string[] endpoints =
        [
            $"https://autodiscover.{domain}/autodiscover/autodiscover.xml",
            $"https://{domain}/autodiscover/autodiscover.xml",
        ];

        foreach (var endpoint in endpoints)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var tier = CancellationTokenSource.CreateLinkedTokenSource(ct);
                tier.CancelAfter(TierTimeout);

                using var content = new StringContent(BuildAutodiscoverRequest(email), Encoding.UTF8, "text/xml");
                using var response = await _http.PostAsync(endpoint, content, tier.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;

                var xml = await response.Content.ReadAsStringAsync(tier.Token).ConfigureAwait(false);
                var parsed = ParseAutodiscover(xml, domain, _catalog);
                if (parsed is not null) return parsed;
            }
            catch (Exception ex)
            {
                // One endpoint being unreachable is the normal case — most domains publish only the
                // autodiscover.<domain> host. Try the next before giving up on the tier.
                LogService.Debug($"AutoDiscover: endpoint for {domain} failed: {ex.GetType().Name}.");
            }
        }

        return null;
    }

    private static string BuildAutodiscoverRequest(string email) =>
        $"""
         <?xml version="1.0" encoding="utf-8"?>
         <Autodiscover xmlns="http://schemas.microsoft.com/exchange/autodiscover/outlook/requestschema/2006">
           <Request>
             <EMailAddress>{new XText(email)}</EMailAddress>
             <AcceptableResponseSchema>http://schemas.microsoft.com/exchange/autodiscover/outlook/responseschema/2006a</AcceptableResponseSchema>
           </Request>
         </Autodiscover>
         """;

    /// <summary>
    /// Parses an Exchange Autodiscover response. Internal so the parser is testable without HTTP.
    ///
    /// A Protocol block of Type IMAP carries usable hosts directly. A Microsoft 365 domain instead
    /// returns EXCH/EXHTTP blocks pointing at Office 365 — those carry no IMAP host, so the built-in
    /// Microsoft entry supplies the settings. Element names are matched without their namespace,
    /// because the response schema namespace varies between Exchange versions.
    /// </summary>
    internal static DiscoveredSettings? ParseAutodiscover(string xml, string domain, IProviderCatalog catalog)
    {
        var doc = XDocument.Parse(xml);
        var protocols = doc.Descendants().Where(e => e.Name.LocalName == "Protocol").ToList();
        if (protocols.Count == 0) return null;

        string? LocalValue(XElement parent, string name) =>
            parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim();

        var imap = protocols.FirstOrDefault(p =>
            string.Equals(LocalValue(p, "Type"), "IMAP", StringComparison.OrdinalIgnoreCase));
        var smtp = protocols.FirstOrDefault(p =>
            string.Equals(LocalValue(p, "Type"), "SMTP", StringComparison.OrdinalIgnoreCase));

        if (imap is not null && smtp is not null)
        {
            var imapHost = LocalValue(imap, "Server");
            var smtpHost = LocalValue(smtp, "Server");
            if (!string.IsNullOrWhiteSpace(imapHost) && !string.IsNullOrWhiteSpace(smtpHost))
            {
                return new DiscoveredSettings(
                    imapHost, ParsePortOrDefault(LocalValue(imap, "Port"), 993), IsSsl(LocalValue(imap, "SSL"), true),
                    smtpHost, ParsePortOrDefault(LocalValue(smtp, "Port"), 587), ImplicitSmtpSsl(LocalValue(smtp, "Port")),
                    ProviderId: null,
                    DisplayName: domain,
                    DiscoverySource.ExchangeAutodiscover);
            }
        }

        // Microsoft 365: no IMAP block, but an EXCH/EXHTTP block whose server sits on an Office 365
        // host. Hand back the built-in Microsoft entry — including its ProviderId, so the dialog
        // selects that provider and offers Microsoft sign-in rather than a password box.
        var isOffice365 = protocols.Any(p =>
        {
            var type = LocalValue(p, "Type");
            if (!string.Equals(type, "EXCH", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(type, "EXHTTP", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(type, "EXPR", StringComparison.OrdinalIgnoreCase))
                return false;

            var server = LocalValue(p, "Server") ?? string.Empty;
            var host = LocalValue(p, "ASUrl") ?? LocalValue(p, "EwsUrl") ?? string.Empty;
            return server.Contains("outlook.com", StringComparison.OrdinalIgnoreCase)
                || server.Contains("outlook.office365.com", StringComparison.OrdinalIgnoreCase)
                || host.Contains("outlook.office365.com", StringComparison.OrdinalIgnoreCase);
        });

        if (!isOffice365) return null;

        var microsoft = catalog.ById(ProviderCatalog.MicrosoftId);
        if (microsoft is null) return null;

        return new DiscoveredSettings(
            microsoft.ImapHost, microsoft.ImapPort, microsoft.ImapUseSsl,
            microsoft.SmtpHost, microsoft.SmtpPort, microsoft.SmtpUseSsl,
            microsoft.Id, microsoft.DisplayName, DiscoverySource.ExchangeAutodiscover);
    }

    private static int ParsePortOrDefault(string? raw, int fallback) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
            ? port
            : fallback;

    private static bool IsSsl(string? raw, bool fallback) => raw?.Trim().ToUpperInvariant() switch
    {
        "ON" or "TRUE" => true,
        "OFF" or "FALSE" => false,
        _ => fallback,
    };

    /// <summary>
    /// Autodiscover reports "SSL on" for both implicit TLS and STARTTLS, so it cannot distinguish
    /// them. Port does: 465 is implicit SSL on connect, 587 and 25 are STARTTLS.
    /// </summary>
    private static bool ImplicitSmtpSsl(string? rawPort) => ParsePortOrDefault(rawPort, 587) == 465;

    // ── Shared helpers ───────────────────────────────────────────────────────────────

    /// <summary>The domain part of an email address, or null when the address is unusable.</summary>
    internal static string? DomainOf(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var at = email.LastIndexOf('@');
        if (at <= 0 || at == email.Length - 1) return null;

        var domain = email[(at + 1)..].Trim();
        // A domain with whitespace, a slash, or no dot is not something to build a URL from.
        if (domain.Length == 0 || !domain.Contains('.') ||
            domain.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '?' or '#' or '@' or ':'))
            return null;

        return domain;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttpClient) _http.Dispose();
    }
}
