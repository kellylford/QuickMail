using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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

    [Fact]
    public void AutodiscoverOnPort143IsAcceptedAsStartTlsNotImplicitSsl()
    {
        var xml = AutodiscoverImap.Replace("<Port>993</Port>", "<Port>143</Port>", StringComparison.Ordinal);

        var parsed = AutoDiscoverService.ParseAutodiscover(xml, "contoso.com", new ProviderCatalog());

        Assert.NotNull(parsed);
        Assert.False(parsed!.ImapUseSsl);
        Assert.Equal(143, parsed.ImapPort);
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
        // Bounded: 2 endpoints x (1 + MaxRedirects) plus the single ISPDB chain — nowhere near unbounded.
        Assert.True(handler.Requests.Count < 20, $"unbounded redirect chase: {handler.Requests.Count} requests");
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

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private Func<HttpRequestMessage, HttpResponseMessage> _responder = _ => new(HttpStatusCode.NotFound);

        public List<HttpRequestMessage> Requests { get; } = [];

        public void Respond(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class StubConfigService(ConfigModel config) : IConfigService
    {
        public ConfigModel Load() => config;
        public void Save(ConfigModel c) { }
    }
}
