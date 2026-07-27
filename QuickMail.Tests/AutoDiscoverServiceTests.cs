using System;
using System.Collections.Generic;
using System.Net;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Security;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Covers the three discovery tiers and — just as importantly — their failure behavior. A tier that
/// throws must fall through to the next, and exhausting every tier must return null rather than
/// propagate, because the dialog's job is to say "not found" and open manual entry.
/// </summary>
public class AutoDiscoverServiceTests
{
    private const string GmailIspdb = """
        <?xml version="1.0"?>
        <clientConfig version="1.1">
          <emailProvider id="example.edu">
            <displayName>Example University</displayName>
            <incomingServer type="imap">
              <hostname>mail.example.edu</hostname>
              <port>993</port>
              <socketType>SSL</socketType>
            </incomingServer>
            <outgoingServer type="smtp">
              <hostname>smtp.example.edu</hostname>
              <port>587</port>
              <socketType>STARTTLS</socketType>
            </outgoingServer>
          </emailProvider>
        </clientConfig>
        """;

    // ── Tier 1: the built-in catalog ─────────────────────────────────────────────────

    [Fact]
    public async Task KnownDomainResolvesFromTheCatalogWithoutAnyNetworkCall()
    {
        var handler = new RecordingHandler();
        var svc = Build(handler);

        var result = await svc.DiscoverAsync("kelly@gmail.com", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(DiscoverySource.LocalCatalog, result!.Source);
        Assert.Equal("imap.gmail.com", result.ImapHost);
        Assert.Equal("smtp.gmail.com", result.SmtpHost);
        Assert.Equal(ProviderCatalog.GmailId, result.ProviderId);
        Assert.Empty(handler.Requests); // the whole point of tier 1
    }

    [Fact]
    public async Task CatalogStillWorksWhenOnlineDiscoveryIsDisabled()
    {
        var handler = new RecordingHandler();
        var svc = Build(handler, autoDiscoverOnline: false);

        var result = await svc.DiscoverAsync("kelly@icloud.com", CancellationToken.None);

        Assert.Equal(DiscoverySource.LocalCatalog, result!.Source);
        Assert.Equal("imap.mail.me.com", result.ImapHost);
    }

    [Fact]
    public async Task OnlineDiscoveryDisabledSkipsTheNetworkEntirelyForAnUnknownDomain()
    {
        var handler = new RecordingHandler();
        handler.Respond(_ => Ok(GmailIspdb)); // would succeed if it were ever called
        var svc = Build(handler, autoDiscoverOnline: false);

        var result = await svc.DiscoverAsync("kelly@example.edu", CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(handler.Requests);
    }

    // ── Tier 2: Mozilla autoconfig ───────────────────────────────────────────────────

    [Fact]
    public async Task IspdbSuppliesHostsPortsAndSslModes()
    {
        var handler = new RecordingHandler();
        handler.Respond(req => req.RequestUri!.Host == "autoconfig.thunderbird.net" ? Ok(GmailIspdb) : NotFound());
        var svc = Build(handler);

        var result = await svc.DiscoverAsync("kelly@example.edu", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(DiscoverySource.Ispdb, result!.Source);
        Assert.Equal("mail.example.edu", result.ImapHost);
        Assert.Equal(993, result.ImapPort);
        Assert.True(result.ImapUseSsl);                 // socketType SSL
        Assert.Equal("smtp.example.edu", result.SmtpHost);
        Assert.Equal(587, result.SmtpPort);
        Assert.False(result.SmtpUseSsl);                // socketType STARTTLS
        Assert.Equal("Example University", result.DisplayName);
        Assert.Null(result.ProviderId);                 // maps to "Other" in the dialog
    }

    [Fact]
    public async Task IspdbSendsOnlyTheDomainNeverTheAddress()
    {
        var handler = new RecordingHandler();
        handler.Respond(_ => Ok(GmailIspdb));
        var svc = Build(handler);

        await svc.DiscoverAsync("kelly@example.edu", CancellationToken.None);

        var url = handler.Requests[0].RequestUri!.ToString();
        Assert.Equal("https://autoconfig.thunderbird.net/v1.1/example.edu", url);
        Assert.DoesNotContain("kelly", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IspdbImplicitSslOnBothLegsIsParsedAsSsl()
    {
        var xml = GmailIspdb.Replace("<socketType>STARTTLS</socketType>", "<socketType>SSL</socketType>",
            StringComparison.Ordinal);

        var parsed = AutoDiscoverService.ParseIspdb(xml, "example.edu");

        Assert.True(parsed!.ImapUseSsl);
        Assert.True(parsed.SmtpUseSsl);
    }

    [Fact]
    public void IspdbWithNoImapServerIsRejected()
    {
        // POP-only providers exist; half a configuration is worse than none, because the missing
        // half silently stays at defaults that do not work.
        var xml = GmailIspdb.Replace("type=\"imap\"", "type=\"pop3\"", StringComparison.Ordinal);

        Assert.Null(AutoDiscoverService.ParseIspdb(xml, "example.edu"));
    }

    [Fact]
    public void IspdbWithAMissingPortIsRejected()
    {
        var xml = GmailIspdb.Replace("<port>993</port>", string.Empty, StringComparison.Ordinal);

        Assert.Null(AutoDiscoverService.ParseIspdb(xml, "example.edu"));
    }

    // ── Refusing cleartext (the reason discovery can't be trusted blindly) ───────────

    // UseSsl=false maps to MailKit's StartTlsWhenAvailable, which connects in PLAINTEXT and
    // authenticates anyway when the server advertises no STARTTLS. So accepting socketType "plain"
    // from a discovery response would let whoever answers for a typosquatted domain name a server
    // that harvests the password in the clear — and the user would see only "Settings found",
    // because Advanced settings stays collapsed.
    [Theory]
    [InlineData("<socketType>SSL</socketType>", "<socketType>plain</socketType>")]     // outgoing plain
    [InlineData("<socketType>plain</socketType>", "<socketType>STARTTLS</socketType>")] // incoming plain
    [InlineData("<socketType>plain</socketType>", "<socketType>plain</socketType>")]
    public void IspdbCleartextIsRejected(string incoming, string outgoing)
    {
        var xml = GmailIspdb
            .Replace("<socketType>SSL</socketType>", incoming, StringComparison.Ordinal)
            .Replace("<socketType>STARTTLS</socketType>", outgoing, StringComparison.Ordinal);

        Assert.Null(AutoDiscoverService.ParseIspdb(xml, "example.edu"));
    }

    [Fact]
    public async Task ACleartextIspdbAnswerFallsThroughRatherThanBeingApplied()
    {
        var plain = GmailIspdb.Replace("<socketType>SSL</socketType>", "<socketType>plain</socketType>",
            StringComparison.Ordinal);
        var handler = new RecordingHandler();
        handler.Respond(req => req.RequestUri!.Host.Contains("thunderbird", StringComparison.Ordinal)
            ? Ok(plain)
            : NotFound());
        var svc = Build(handler);

        Assert.Null(await svc.DiscoverAsync("kelly@example.edu", CancellationToken.None));
    }

    [Fact]
    public void AutodiscoverWithSslOffIsRejected()
    {
        var xml = AutodiscoverImap.Replace("<SSL>on</SSL>", "<SSL>off</SSL>", StringComparison.Ordinal);

        Assert.Null(AutoDiscoverService.ParseAutodiscover(xml, "contoso.com", new ProviderCatalog()));
    }

    [Fact]
    public void AutodiscoverOnThePlaintextSmtpPortIsRejected()
    {
        // Port 25 with "SSL on" is the other way to end up on StartTlsWhenAvailable.
        var xml = AutodiscoverImap.Replace("<Port>587</Port>", "<Port>25</Port>", StringComparison.Ordinal);

        Assert.Null(AutoDiscoverService.ParseAutodiscover(xml, "contoso.com", new ProviderCatalog()));
    }

    // Replaces AutodiscoverOnPort143IsAcceptedAsStartTlsNotImplicitSsl, which asserted only that
    // port 143 came back with ImapUseSsl false — and that flag alone used to mean
    // StartTlsWhenAvailable, i.e. "connect in plaintext and authenticate anyway if the server
    // advertises no STARTTLS". Accepting the port was never the problem; accepting it without
    // requiring encryption was. The old test locked that in, so it is replaced rather than extended.
    [Fact]
    public void AutodiscoverOnPort143IsAcceptedButOnlyWithStartTlsRequired()
    {
        var xml = AutodiscoverImap.Replace("<Port>993</Port>", "<Port>143</Port>", StringComparison.Ordinal);

        var parsed = AutoDiscoverService.ParseAutodiscover(xml, "contoso.com", new ProviderCatalog());

        Assert.NotNull(parsed);
        Assert.False(parsed!.ImapUseSsl);      // 143 is STARTTLS, not implicit TLS
        Assert.Equal(143, parsed.ImapPort);
        Assert.True(parsed.RequireStartTls);   // …and the negotiation is mandatory, not opportunistic
        Assert.Equal(SecureSocketOptions.StartTls,
            MailSecurity.Select(parsed.ImapUseSsl, parsed.RequireStartTls));
    }

    // Port 587 is the same hole on the outgoing leg: Autodiscover says "SSL on", the port says
    // STARTTLS, and SmtpUseSsl false alone would have connected in the clear against a server that
    // offers no STARTTLS.
    [Fact]
    public void AutodiscoverSmtpOnPort587AlsoRequiresStartTls()
    {
        var parsed = AutoDiscoverService.ParseAutodiscover(AutodiscoverImap, "contoso.com", new ProviderCatalog());

        Assert.False(parsed!.SmtpUseSsl);
        Assert.True(parsed.RequireStartTls);
        Assert.Equal(SecureSocketOptions.StartTls,
            MailSecurity.Select(parsed.SmtpUseSsl, parsed.RequireStartTls));
    }

    // The ISPDB path reaches the same place from socketType STARTTLS.
    [Fact]
    public void IspdbStartTlsRequiresTheNegotiationRatherThanPreferringIt()
    {
        var parsed = AutoDiscoverService.ParseIspdb(GmailIspdb, "example.edu");

        Assert.False(parsed!.SmtpUseSsl);
        Assert.True(parsed.RequireStartTls);
    }

    [Fact]
    public async Task EverySourceOfDiscoveredSettingsRequiresEncryption()
    {
        // Whatever tier answers, the settings were chosen for the user rather than by them, so none
        // of them may fall back to plaintext.
        var catalog = await Build(new RecordingHandler()).DiscoverAsync("kelly@gmail.com", CancellationToken.None);
        Assert.True(catalog!.RequireStartTls);

        var ispdb = new RecordingHandler();
        ispdb.Respond(req => req.RequestUri!.Host == "autoconfig.thunderbird.net" ? Ok(GmailIspdb) : NotFound());
        Assert.True((await Build(ispdb).DiscoverAsync("kelly@example.edu", CancellationToken.None))!.RequireStartTls);

        var autodiscover = new RecordingHandler();
        autodiscover.Respond(req => req.RequestUri!.Host.Contains("thunderbird", StringComparison.Ordinal)
            ? NotFound() : Ok(AutodiscoverImap));
        Assert.True((await Build(autodiscover).DiscoverAsync("kelly@contoso.com", CancellationToken.None))!.RequireStartTls);

        var dns = DnsOnly(MxJson("contoso-com.mail.protection.outlook.com"));
        Assert.True((await Build(dns).DiscoverAsync("kelly@contoso.com", CancellationToken.None))!.RequireStartTls);
    }

    // ── Redirects ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnHttpsRedirectIsFollowed()
    {
        // Microsoft 365 custom domains commonly answer Autodiscover with a 302 to
        // autodiscover-s.outlook.com. Treating every 30x as "nothing here" made that path dead.
        var handler = new RecordingHandler();
        handler.Respond(req =>
        {
            if (req.RequestUri!.Host.Contains("thunderbird", StringComparison.Ordinal)) return NotFound();
            if (req.RequestUri.Host == "autodiscover.contoso.com")
            {
                var moved = new HttpResponseMessage(HttpStatusCode.Found);
                moved.Headers.Location = new Uri("https://autodiscover-s.outlook.com/autodiscover/autodiscover.xml");
                return moved;
            }
            return Ok(AutodiscoverOffice365);
        });
        var svc = Build(handler);

        var result = await svc.DiscoverAsync("kelly@contoso.com", CancellationToken.None);

        Assert.Equal(ProviderCatalog.MicrosoftId, result!.ProviderId);
        Assert.Contains(handler.Requests, r => r.RequestUri!.Host == "autodiscover-s.outlook.com");
    }

    [Fact]
    public async Task ARedirectToPlainHttpIsRefused()
    {
        // Following a downgrade would leak the address and let anyone on the path pick the servers.
        var handler = new RecordingHandler();
        handler.Respond(req =>
        {
            if (req.RequestUri!.Host.Contains("thunderbird", StringComparison.Ordinal)) return NotFound();
            var moved = new HttpResponseMessage(HttpStatusCode.Found);
            moved.Headers.Location = new Uri("http://autodiscover.contoso.com/autodiscover/autodiscover.xml");
            return moved;
        });
        var svc = Build(handler);

        Assert.Null(await svc.DiscoverAsync("kelly@contoso.com", CancellationToken.None));
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.Scheme == "http");
    }

    // ── Where a redirect is allowed to go ────────────────────────────────────────────

    /// <summary>
    /// 302s the Autodiscover endpoint to whatever host the test names, and answers everything else
    /// with the Office 365 document — so if a hop is followed, it visibly succeeds.
    /// </summary>
    private static RecordingHandler RedirectingAutodiscover(string location, HttpStatusCode status = HttpStatusCode.Found)
    {
        var handler = new RecordingHandler();
        handler.Respond(req =>
        {
            if (req.RequestUri!.Host.Contains("thunderbird", StringComparison.Ordinal)) return NotFound();
            if (req.RequestUri.Host == "cloudflare-dns.com") return Ok(NoAnswer);
            // The bare-domain endpoint answers nothing, so the redirect is the only route to a
            // result and "no result" means the hop really was refused.
            if (req.RequestUri.Host == "contoso.com") return NotFound();
            if (req.RequestUri.Host == "autodiscover.contoso.com")
            {
                var moved = new HttpResponseMessage(status);
                moved.Headers.Location = new Uri(location);
                return moved;
            }
            return Ok(AutodiscoverOffice365);
        });
        return handler;
    }

    [Fact]
    public async Task ARedirectToAnUnrelatedHostIsRefused()
    {
        // The response to an Autodiscover POST names the IMAP and SMTP hosts this account is about to
        // send its password to. A 302 that hands that decision to an arbitrary third party is not a
        // redirect, it is a handover, and the target's valid certificate says nothing about whether
        // it speaks for contoso.com.
        var handler = RedirectingAutodiscover("https://collector.example/autodiscover/autodiscover.xml");
        var svc = Build(handler);

        Assert.Null(await svc.DiscoverAsync("kelly@contoso.com", CancellationToken.None));
        Assert.DoesNotContain(handler.Sent, r => r.Uri.Host == "collector.example");
    }

    [Fact]
    public async Task TheAddressIsNeverSentToAHostOutsideTheQueriedDomain()
    {
        // The Autodiscover request body carries <EMailAddress>the user's full address</EMailAddress>,
        // and newRequest() rebuilds that body on every hop. Following the redirect POSTed it to the
        // redirect target.
        var handler = RedirectingAutodiscover("https://collector.example/autodiscover/autodiscover.xml");
        var svc = Build(handler);

        await svc.DiscoverAsync("kelly.private@contoso.com", CancellationToken.None);

        foreach (var sent in handler.Sent)
        {
            var isQueriedDomain = sent.Uri.Host == "contoso.com" || sent.Uri.Host.EndsWith(".contoso.com", StringComparison.Ordinal);
            if (isQueriedDomain) continue;

            Assert.DoesNotContain("kelly.private", sent.Uri.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("kelly.private", sent.Body ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task AFollowedRedirectDoesNotRepostTheAddress()
    {
        // The allowed Microsoft hop still must not carry the body onward: a 302 downgrades to GET,
        // exactly as browsers and HttpClientHandler do for a cross-origin 301/302/303.
        var handler = RedirectingAutodiscover("https://autodiscover-s.outlook.com/autodiscover/autodiscover.xml");
        var svc = Build(handler);

        await svc.DiscoverAsync("kelly.private@contoso.com", CancellationToken.None);

        var hop = Assert.Single(handler.Sent.Where(r => r.Uri.Host == "autodiscover-s.outlook.com"));
        Assert.Equal(HttpMethod.Get, hop.Method);
        Assert.True(string.IsNullOrEmpty(hop.Body), $"the redirect re-sent a body: {hop.Body}");
    }

    [Fact]
    public async Task ARedirectWithinTheQueriedDomainIsFollowed()
    {
        // A domain moving its own Autodiscover endpoint around inside itself is ordinary, and the
        // address is going to that same domain either way.
        var handler = RedirectingAutodiscover("https://mail.contoso.com/autodiscover/autodiscover.xml");
        var svc = Build(handler);

        var result = await svc.DiscoverAsync("kelly@contoso.com", CancellationToken.None);

        Assert.Equal(ProviderCatalog.MicrosoftId, result!.ProviderId);
        Assert.Contains(handler.Sent, r => r.Uri.Host == "mail.contoso.com");
    }

    [Fact]
    public async Task A307ToAnAllowedHostKeepsThePostAndItsBody()
    {
        // 307 means "same method, same body" by definition, and the host it means it for has already
        // been checked. Only 301/302/303 downgrade.
        var handler = RedirectingAutodiscover("https://mail.contoso.com/autodiscover/autodiscover.xml",
            HttpStatusCode.TemporaryRedirect);
        var svc = Build(handler);

        await svc.DiscoverAsync("kelly@contoso.com", CancellationToken.None);

        var hop = Assert.Single(handler.Sent.Where(r => r.Uri.Host == "mail.contoso.com"));
        Assert.Equal(HttpMethod.Post, hop.Method);
        Assert.Contains("kelly@contoso.com", hop.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIspdbRedirectOffMozillasHostIsRefused()
    {
        // This tier queries one fixed, known host. A hop off it is not part of the protocol, and the
        // document it would return decides the same servers Autodiscover's would.
        var handler = new RecordingHandler();
        handler.Respond(req =>
        {
            if (req.RequestUri!.Host != "autoconfig.thunderbird.net") return NotFound();
            var moved = new HttpResponseMessage(HttpStatusCode.Found);
            moved.Headers.Location = new Uri("https://mirror.example/v1.1/example.edu");
            return moved;
        });
        var svc = Build(handler);

        Assert.Null(await svc.DiscoverAsync("kelly@example.edu", CancellationToken.None));
        Assert.DoesNotContain(handler.Sent, r => r.Uri.Host == "mirror.example");
    }

    [Fact]
    public async Task ADnsOverHttpsRedirectOffTheResolverIsRefused()
    {
        // Same reasoning: whoever answers the MX query picks the provider.
        var handler = new RecordingHandler();
        handler.Respond(req =>
        {
            if (req.RequestUri!.Host != "cloudflare-dns.com") return NotFound();
            var moved = new HttpResponseMessage(HttpStatusCode.Found);
            moved.Headers.Location = new Uri("https://resolver.example/dns-query?name=contoso.com&type=15");
            return moved;
        });
        var svc = Build(handler);

        Assert.Null(await svc.DiscoverAsync("kelly@contoso.com", CancellationToken.None));
        Assert.DoesNotContain(handler.Sent, r => r.Uri.Host == "resolver.example");
    }

    [Fact]
    public async Task ARedirectLoopTerminates()
    {
        var handler = new RecordingHandler();
        handler.Respond(req =>
        {
            var moved = new HttpResponseMessage(HttpStatusCode.Found);
            moved.Headers.Location = new Uri("https://round.example/again");
            return moved;
        });
        var svc = Build(handler);

        Assert.Null(await svc.DiscoverAsync("kelly@contoso.com", CancellationToken.None));

        // Bounded, which is the point — a redirect loop must terminate rather than chase forever.
        // The ceiling is (number of request sites) x (1 + MaxRedirects): ISPDB, two Autodiscover
        // endpoints, and the MX and CNAME lookups, so 5 x 4.
        const int ceiling = 5 * 4;
        Assert.True(handler.Requests.Count <= ceiling,
            $"unbounded redirect chase: {handler.Requests.Count} requests (ceiling {ceiling})");
    }

    // ── Tier 3: Exchange Autodiscover ────────────────────────────────────────────────

    private const string AutodiscoverImap = """
        <?xml version="1.0"?>
        <Autodiscover xmlns:a="http://schemas.microsoft.com/exchange/autodiscover/responseschema/2006">
          <Response>
            <Account>
              <Protocol><Type>IMAP</Type><Server>mail.contoso.com</Server><Port>993</Port><SSL>on</SSL></Protocol>
              <Protocol><Type>SMTP</Type><Server>smtp.contoso.com</Server><Port>587</Port><SSL>on</SSL></Protocol>
            </Account>
          </Response>
        </Autodiscover>
        """;

    private const string AutodiscoverOffice365 = """
        <?xml version="1.0"?>
        <Autodiscover>
          <Response>
            <Account>
              <Protocol><Type>EXCH</Type><Server>abc.outlook.office365.com</Server></Protocol>
              <Protocol><Type>EXHTTP</Type><Server>outlook.office365.com</Server></Protocol>
            </Account>
          </Response>
        </Autodiscover>
        """;

    [Fact]
    public async Task AutodiscoverImapBlockIsUsedWhenIspdbHasNothing()
    {
        var handler = new RecordingHandler();
        handler.Respond(req => req.RequestUri!.Host.Contains("thunderbird", StringComparison.Ordinal)
            ? NotFound()
            : Ok(AutodiscoverImap));
        var svc = Build(handler);

        var result = await svc.DiscoverAsync("kelly@contoso.com", CancellationToken.None);

        Assert.Equal(DiscoverySource.ExchangeAutodiscover, result!.Source);
        Assert.Equal("mail.contoso.com", result.ImapHost);
        Assert.Equal("smtp.contoso.com", result.SmtpHost);
        Assert.Equal(587, result.SmtpPort);
        Assert.False(result.SmtpUseSsl); // port 587 means STARTTLS even though Autodiscover says "SSL on"
    }

    [Fact]
    public void AutodiscoverSmtpOnPort465IsImplicitSsl()
    {
        var xml = AutodiscoverImap.Replace("<Port>587</Port>", "<Port>465</Port>", StringComparison.Ordinal);

        var parsed = AutoDiscoverService.ParseAutodiscover(xml, "contoso.com", new ProviderCatalog());

        Assert.True(parsed!.SmtpUseSsl);
        Assert.Equal(465, parsed.SmtpPort);
    }

    [Fact]
    public void AutodiscoverOffice365ResolvesToTheBuiltInMicrosoftProvider()
    {
        var parsed = AutoDiscoverService.ParseAutodiscover(AutodiscoverOffice365, "contoso.com", new ProviderCatalog());

        Assert.NotNull(parsed);
        Assert.Equal(ProviderCatalog.MicrosoftId, parsed!.ProviderId);
        Assert.Equal("outlook.office365.com", parsed.ImapHost);
        Assert.Equal(DiscoverySource.ExchangeAutodiscover, parsed.Source);
    }

    [Fact]
    public void AutodiscoverForANonOffice365ExchangeWithNoImapIsRejected()
    {
        var xml = AutodiscoverOffice365.Replace("outlook.office365.com", "exch.internal.contoso.com",
            StringComparison.Ordinal);

        // An on-premises Exchange with no IMAP protocol block gives us nothing QuickMail can use.
        Assert.Null(AutoDiscoverService.ParseAutodiscover(xml, "contoso.com", new ProviderCatalog()));
    }

    [Fact]
    public async Task AutodiscoverTriesTheSecondEndpointWhenTheFirstFails()
    {
        var handler = new RecordingHandler();
        handler.Respond(req =>
        {
            if (req.RequestUri!.Host.Contains("thunderbird", StringComparison.Ordinal)) return NotFound();
            if (req.RequestUri.Host.StartsWith("autodiscover.", StringComparison.Ordinal))
                throw new HttpRequestException("no such host");
            return Ok(AutodiscoverImap);
        });
        var svc = Build(handler);

        var result = await svc.DiscoverAsync("kelly@contoso.com", CancellationToken.None);

        Assert.Equal("mail.contoso.com", result!.ImapHost);
        Assert.Contains(handler.Requests, r => r.RequestUri!.Host == "autodiscover.contoso.com");
        Assert.Contains(handler.Requests, r => r.RequestUri!.Host == "contoso.com");
    }

    // ── Tier 4: where DNS says the domain's mail is delivered ───────────────────────

    private static string Dns(int type, params string[] data) =>
        "{\"Status\":0,\"Answer\":[" +
        string.Join(",", data.Select(d => $"{{\"name\":\"x\",\"type\":{type},\"data\":\"{d}\"}}")) +
        "]}";

    private static string MxJson(params string[] hosts) =>
        Dns(15, hosts.Select(h => "10 " + h + ".").ToArray());

    private static string CnameJson(string target) => Dns(5, target + ".");

    private const string NoAnswer = """{"Status":3}""";

    /// <summary>Routes ISPDB and Autodiscover to misses, and DNS to whatever the test supplies.</summary>
    private static RecordingHandler DnsOnly(string? mx, string? cname = null)
    {
        var handler = new RecordingHandler();
        handler.Respond(req =>
        {
            var uri = req.RequestUri!;
            if (uri.Host != "cloudflare-dns.com") return NotFound();
            var query = uri.Query;
            if (query.Contains("type=15", StringComparison.Ordinal)) return Ok(mx ?? NoAnswer);
            if (query.Contains("type=5", StringComparison.Ordinal)) return Ok(cname ?? NoAnswer);
            return Ok(NoAnswer);
        });
        return handler;
    }

    [Fact]
    public async Task AnMxUnderMicrosoftsMailProtectionSuffixIdentifiesMicrosoft()
    {
        var svc = Build(DnsOnly(MxJson("contoso-com.mail.protection.outlook.com")));

        var result = await svc.DiscoverAsync("kelly@contoso.com", CancellationToken.None);

        Assert.Equal(DiscoverySource.DnsMailHost, result!.Source);
        Assert.Equal(ProviderCatalog.MicrosoftId, result.ProviderId);
    }

    // The regression that started this: a domain keeps its Microsoft tenant after its mail moves
    // away. Asking "does this domain have a tenant" said yes and filled in Microsoft's servers, so
    // the user's real password went to smtp-mail.outlook.com and came back 535. Asking where the mail
    // is actually delivered gets it right.
    [Theory]
    [InlineData("theideaplace.net")]          // mail delivered to the domain itself
    [InlineData("in1-smtp.messagingengine.com")]
    [InlineData("mx.some-host.example")]
    public async Task ADomainWhoseMailGoesElsewhereIsNotClaimedByMicrosoft(string mxHost)
    {
        var svc = Build(DnsOnly(MxJson(mxHost)));

        Assert.Null(await svc.DiscoverAsync("kelly@contoso.com", CancellationToken.None));
    }

    // Organisations commonly front Exchange Online with a filtering gateway, which replaces the MX
    // while the mailboxes stay in Microsoft 365. The autodiscover CNAME still gives it away.
    [Fact]
    public async Task AMicrosoftAutodiscoverCnameIdentifiesMicrosoftEvenBehindAGatewayMx()
    {
        var svc = Build(DnsOnly(MxJson("mx1.filtering-gateway.example"),
                                CnameJson("autodiscover.outlook.com")));

        var result = await svc.DiscoverAsync("kelly@contoso.com", CancellationToken.None);

        Assert.Equal(ProviderCatalog.MicrosoftId, result!.ProviderId);
    }

    [Theory]
    [InlineData("aspmx.l.google.com")]
    [InlineData("alt1.aspmx.l.google.com")]
    [InlineData("gmail-smtp-in.l.google.com")]
    public async Task AGoogleWorkspaceMxIdentifiesGmail(string mxHost)
    {
        var svc = Build(DnsOnly(MxJson(mxHost)));

        var result = await svc.DiscoverAsync("kelly@contoso.com", CancellationToken.None);

        Assert.Equal(ProviderCatalog.GmailId, result!.ProviderId);
        Assert.Equal("imap.gmail.com", result.ImapHost);
    }

    [Theory]
    [InlineData("not-really-mail.google.com")]
    [InlineData("mx.storage.google.com")]
    public async Task AnMxUnderGoogleComThatIsNotAGoogleMailHostIsNotClaimedAsGmail(string mxHost)
    {
        // The suffix list held a bare "google.com", so ANY host under the domain was read as
        // "this domain's mail is Gmail" — and Gmail's servers, plus its app-password requirement,
        // were filled in for a domain that never said so. Google Workspace delivery hosts are all
        // under l.google.com.
        var svc = Build(DnsOnly(MxJson(mxHost)));

        Assert.Null(await svc.DiscoverAsync("kelly@contoso.com", CancellationToken.None));
    }

    [Fact]
    public async Task NoMxAndNoCnameFindsNothing()
    {
        var svc = Build(DnsOnly(mx: null));

        Assert.Null(await svc.DiscoverAsync("kelly@contoso.com", CancellationToken.None));
    }

    [Fact]
    public async Task TheDnsTierSendsTheDomainButNotTheAddress()
    {
        var handler = DnsOnly(MxJson("contoso-com.mail.protection.outlook.com"));
        var svc = Build(handler);

        await svc.DiscoverAsync("kelly.private@contoso.com", CancellationToken.None);

        foreach (var url in handler.Requests.Select(r => r.RequestUri!.ToString()))
            Assert.DoesNotContain("kelly.private", url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(handler.Requests, r => r.RequestUri!.Query.Contains("contoso.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheDnsTierIsSkippedWhenOnlineDiscoveryIsOff()
    {
        var handler = DnsOnly(MxJson("contoso-com.mail.protection.outlook.com"));
        var svc = Build(handler, autoDiscoverOnline: false);

        Assert.Null(await svc.DiscoverAsync("kelly@contoso.com", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MalformedDnsJsonSurfacesAsNotFoundNotAsAThrow()
    {
        var handler = new RecordingHandler();
        handler.Respond(req => req.RequestUri!.Host == "cloudflare-dns.com" ? Ok("not json") : NotFound());
        var svc = Build(handler);

        Assert.Null(await svc.DiscoverAsync("kelly@contoso.com", CancellationToken.None));
    }

    [Theory]
    [InlineData(15, "10 mail.example.com.", "mail.example.com")]
    [InlineData(15, "0 example.com.", "example.com")]
    [InlineData(5, "autodiscover.outlook.com.", "autodiscover.outlook.com")]
    public void DnsAnswerDataIsReducedToABareHost(int type, string data, string expected)
    {
        var parsed = AutoDiscoverService.ParseDnsAnswers(Dns(type, data), type);

        Assert.Equal([expected], parsed);
    }

    [Fact]
    public void DnsAnswersOfADifferentTypeAreIgnored()
        => Assert.Empty(AutoDiscoverService.ParseDnsAnswers(Dns(1, "1.2.3.4"), DnsTypeMxForTest));

    private const int DnsTypeMxForTest = 15;

    // ── Failure behavior ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AllTiersFailingReturnsNullRatherThanThrowing()
    {
        var handler = new RecordingHandler();
        handler.Respond(_ => NotFound());
        var svc = Build(handler);

        Assert.Null(await svc.DiscoverAsync("kelly@nowhere.example", CancellationToken.None));
    }

    [Fact]
    public async Task ANetworkFailureFallsThroughInsteadOfPropagating()
    {
        var handler = new RecordingHandler();
        handler.Respond(_ => throw new HttpRequestException("offline"));
        var svc = Build(handler);

        // Exercises the offline case: the dialog must reach manual entry, not surface an exception.
        Assert.Null(await svc.DiscoverAsync("kelly@nowhere.example", CancellationToken.None));
    }

    [Fact]
    public async Task MalformedIspdbXmlFallsThroughToAutodiscover()
    {
        var handler = new RecordingHandler();
        handler.Respond(req => req.RequestUri!.Host.Contains("thunderbird", StringComparison.Ordinal)
            ? Ok("<clientConfig>this is not well formed")
            : Ok(AutodiscoverImap));
        var svc = Build(handler);

        var result = await svc.DiscoverAsync("kelly@contoso.com", CancellationToken.None);

        Assert.Equal(DiscoverySource.ExchangeAutodiscover, result!.Source);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("@nodomain.com")]
    [InlineData("trailing@")]
    [InlineData("kelly@nodot")]
    [InlineData("kelly@has space.com")]
    [InlineData("kelly@evil.com/../path")]
    public async Task MalformedAddressesNeverReachTheNetwork(string email)
    {
        var handler = new RecordingHandler();
        handler.Respond(_ => Ok(GmailIspdb));
        var svc = Build(handler);

        Assert.Null(await svc.DiscoverAsync(email, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AnAlreadyCancelledTokenDoesNotThrowOutOfDiscoverAsync()
    {
        var handler = new RecordingHandler();
        handler.Respond(_ => Ok(GmailIspdb));
        var svc = Build(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // The dialog cancels a stale lookup when the user keeps typing; that must not surface
        // as an unhandled exception on the async void handler path.
        Assert.Null(await svc.DiscoverAsync("kelly@example.edu", cts.Token));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────

    private static AutoDiscoverService Build(RecordingHandler handler, bool autoDiscoverOnline = true) =>
        new(new ProviderCatalog(),
            new StubConfigService(new ConfigModel { AutoDiscoverOnline = autoDiscoverOnline }),
            new HttpClient(handler),
            ownsHttpClient: true);

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    /// <summary>One request as it actually went out. Captured at send time because the content is
    /// disposed along with the request, so reading the body afterwards would throw.</summary>
    private sealed record SentRequest(Uri Uri, HttpMethod Method, string? Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private Func<HttpRequestMessage, HttpResponseMessage> _responder = _ => new(HttpStatusCode.NotFound);

        public List<HttpRequestMessage> Requests { get; } = [];

        /// <summary>Every request, with its method and body — what the redirect tests assert on.</summary>
        public List<SentRequest> Sent { get; } = [];

        public void Respond(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            Sent.Add(new SentRequest(
                request.RequestUri!, request.Method,
                request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult()));
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class StubConfigService(ConfigModel config) : IConfigService
    {
        public ConfigModel Load() => config;
        public void Save(ConfigModel c) { }
    }
}
