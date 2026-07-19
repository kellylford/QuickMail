using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Per-account iCloud CardDAV contact sync (read-down v1) through <see cref="ICloudContactSource"/>
/// and the raw <see cref="CardDavContactClient"/>: discovery (PROPFIND principal →
/// addressbook-home-set → first addressbook, with a cross-host redirect that must keep Basic auth),
/// addressbook-query REPORT parsing, vCard parsing, and the missing-password guard. Only accounts
/// whose IMAP host is <c>imap.mail.me.com</c> are eligible, using the account's own app-specific
/// password (<see cref="ICredentialService.GetPassword"/>). HTTP is stubbed with a queued
/// <see cref="HttpMessageHandler"/>, mirroring CalDavCalendarSyncTests.
/// </summary>
public class CardDavContactSyncTests
{
    private const string ServerUrl = "https://contacts.icloud.com";
    private const string Username  = "kelly@example.com";
    private static readonly Guid AccountId = Guid.NewGuid();

    // ── Test plumbing ────────────────────────────────────────────────────────────

    private sealed record RecordedRequest(string Method, string Url, string? Depth, string? Auth, string Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public List<RecordedRequest> Requests { get; } = [];

        public RecordingHandler(params HttpResponseMessage[] responses)
            => _responses = new Queue<HttpResponseMessage>(responses);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.ToString(),
                request.Headers.TryGetValues("Depth", out var d) ? d.First() : null,
                request.Headers.Authorization?.ToString(),
                request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(ct)));
            return _responses.Count > 0 ? _responses.Dequeue() : Xml(EmptyMultistatus);
        }
    }

    /// <summary>Returns saved passwords keyed by account id, mirroring the real per-account model.</summary>
    private sealed class StubCredentials : ICredentialService
    {
        private readonly Dictionary<Guid, string> _passwords = [];
        public StubCredentials(Guid? accountId = null, string? password = null)
        {
            if (accountId is { } id && password != null) _passwords[id] = password;
        }
        public void SavePassword(Guid accountId, string password) => _passwords[accountId] = password;
        public string? GetPassword(Guid accountId) => _passwords.TryGetValue(accountId, out var v) ? v : null;
        public void DeletePassword(Guid accountId) => _passwords.Remove(accountId);
        public void SaveSecret(string key, string value) { }
        public string? GetSecret(string key) => null;
        public void DeleteSecret(string key) { }
    }

    private static HttpResponseMessage Xml(string body, HttpStatusCode code = (HttpStatusCode)207)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };

    private static AccountModel ICloudAccount() => new()
    {
        Id = AccountId,
        AuthType = AuthType.Password,
        BackendKind = BackendKind.ImapSmtp,
        ImapHost = "imap.mail.me.com",
        Username = Username,
    };

    private static ICloudContactSource Source(RecordingHandler handler, ICredentialService? credentials = null)
        => new(new CardDavContactClient(new HttpClient(handler)),
               credentials ?? new StubCredentials(AccountId, "app-pass"));

    // ── XML fixtures ─────────────────────────────────────────────────────────────

    private const string EmptyMultistatus =
        """<?xml version="1.0"?><d:multistatus xmlns:d="DAV:"/>""";

    private const string PrincipalXml =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <d:multistatus xmlns:d="DAV:">
          <d:response>
            <d:href>/</d:href>
            <d:propstat>
              <d:prop>
                <d:current-user-principal><d:href>/123456/principal/</d:href></d:current-user-principal>
              </d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
        </d:multistatus>
        """;

    // iCloud reports the addressbook home on the user's partition host — an ABSOLUTE href.
    private const string HomeSetXml =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:carddav">
          <d:response>
            <d:href>/123456/principal/</d:href>
            <d:propstat>
              <d:prop>
                <c:addressbook-home-set><d:href>https://p42-contacts.icloud.com/123456/carddav/</d:href></c:addressbook-home-set>
              </d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
        </d:multistatus>
        """;

    // Home children: the home itself (plain collection — must be skipped), then the first addressbook.
    private const string CollectionsXml =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:carddav">
          <d:response>
            <d:href>/123456/carddav/</d:href>
            <d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop>
            <d:status>HTTP/1.1 200 OK</d:status></d:propstat>
          </d:response>
          <d:response>
            <d:href>/123456/carddav/home/</d:href>
            <d:propstat><d:prop>
              <d:resourcetype><d:collection/><c:addressbook/></d:resourcetype>
              <d:displayname>Contacts</d:displayname>
            </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
          </d:response>
        </d:multistatus>
        """;

    /// <summary>Wraps vCard bodies (XML-safe in these fixtures) in a REPORT multistatus.</summary>
    private static string ReportXml(params string[] vcards)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8"?>""");
        sb.Append("""<d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:carddav">""");
        var i = 0;
        foreach (var vcard in vcards)
        {
            sb.Append($"<d:response><d:href>/123456/carddav/home/{++i}.vcf</d:href>")
              .Append("<d:propstat><d:prop><d:getetag>\"etag\"</d:getetag><c:address-data>")
              .Append(vcard)
              .Append("</c:address-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>");
        }
        sb.Append("</d:multistatus>");
        return sb.ToString();
    }

    private static string Card(string uid, string fn, string email) => string.Join("\n",
        "BEGIN:VCARD",
        "VERSION:3.0",
        $"UID:{uid}",
        $"FN:{fn}",
        $"EMAIL;TYPE=INTERNET:{email}",
        "END:VCARD");

    private RecordingHandler FullSyncHandler(params string[] vcards)
        => new(Xml(PrincipalXml), Xml(HomeSetXml), Xml(CollectionsXml), Xml(ReportXml(vcards)));

    // ── Full pipeline: discovery + REPORT + mapping ──────────────────────────────

    [Fact]
    public async Task Fetch_FullPipeline_ReturnsContacts()
    {
        var handler = FullSyncHandler(
            Card("card-1", "John Doe", "john@icloud.test"),
            Card("card-2", "Jane Roe", "jane@icloud.test"));

        var contacts = await Source(handler).FetchAsync(ICloudAccount(), TestContext.Current.CancellationToken);

        Assert.Equal(2, contacts.Count);
        Assert.All(contacts, c => Assert.False(c.IsPriorRecipient));
        var john = contacts.Single(c => c.SourceId == "card-1");
        Assert.Equal("John Doe", john.DisplayName);
        Assert.Equal("john@icloud.test", john.EmailAddress);
    }

    [Fact]
    public async Task Fetch_Discovery_WalksPrincipalHomeAndCollections_WithAuthAndDepth()
    {
        var handler = FullSyncHandler(Card("card-1", "John Doe", "john@icloud.test"));

        await Source(handler).FetchAsync(ICloudAccount(), TestContext.Current.CancellationToken);

        Assert.Equal(4, handler.Requests.Count);
        var expectedAuth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:app-pass"));
        Assert.All(handler.Requests, r => Assert.Equal(expectedAuth, r.Auth));

        Assert.Equal(("PROPFIND", "https://contacts.icloud.com/", "0"),
                     (handler.Requests[0].Method, handler.Requests[0].Url, handler.Requests[0].Depth));
        Assert.Contains("current-user-principal", handler.Requests[0].Body);

        Assert.Equal(("PROPFIND", "https://contacts.icloud.com/123456/principal/", "0"),
                     (handler.Requests[1].Method, handler.Requests[1].Url, handler.Requests[1].Depth));
        Assert.Contains("addressbook-home-set", handler.Requests[1].Body);

        // The home-set href was ABSOLUTE on the partition host — must be honored.
        Assert.Equal(("PROPFIND", "https://p42-contacts.icloud.com/123456/carddav/", "1"),
                     (handler.Requests[2].Method, handler.Requests[2].Url, handler.Requests[2].Depth));

        var report = handler.Requests[3];
        Assert.Equal("REPORT", report.Method);
        Assert.Equal("https://p42-contacts.icloud.com/123456/carddav/home/", report.Url);
        Assert.Equal("1", report.Depth);
        Assert.Contains("addressbook-query", report.Body);
        Assert.Contains("address-data", report.Body);
    }

    [Fact]
    public async Task Fetch_SecondPass_ReusesDiscoveredAddressbook()
    {
        var handler = FullSyncHandler(Card("card-1", "John Doe", "john@icloud.test"));
        var source = Source(handler);
        var account = ICloudAccount();

        await source.FetchAsync(account, TestContext.Current.CancellationToken);
        // Enqueue only a REPORT response — discovery must be cached per account.
        handler.Requests.Clear();
        await source.FetchAsync(account, TestContext.Current.CancellationToken);

        Assert.Single(handler.Requests);
        Assert.Equal("REPORT", handler.Requests[0].Method);
    }

    // ── Failure isolation ────────────────────────────────────────────────────────

    [Fact]
    public async Task Fetch_HttpFailure_Throws()
    {
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("bad app password") });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Source(handler).FetchAsync(ICloudAccount(), TestContext.Current.CancellationToken));
        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task Fetch_MissingSavedPassword_Throws_WithoutAnyRequest()
    {
        var handler = FullSyncHandler(Card("card-1", "John Doe", "john@icloud.test"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Source(handler, new StubCredentials()).FetchAsync(ICloudAccount(), TestContext.Current.CancellationToken));

        Assert.Contains("Manage Accounts", ex.Message);
        Assert.Empty(handler.Requests);
    }

    // ── Redirect handling (iCloud partition hosts) ───────────────────────────────

    [Fact]
    public async Task Discovery_FollowsCrossHostRedirect_ReattachingBasicAuth()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
        redirect.Headers.Location = new Uri("https://p42-contacts.icloud.com/");
        var handler = new RecordingHandler(redirect, Xml(PrincipalXml), Xml(HomeSetXml), Xml(CollectionsXml));
        using var client = new CardDavContactClient(new HttpClient(handler));

        var info = await client.DiscoverAddressbookAsync(ServerUrl, Username, "app-pass",
                                                         TestContext.Current.CancellationToken);

        // 1 redirected principal hop + re-issue, then home-set, then collections.
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal("https://p42-contacts.icloud.com/", handler.Requests[1].Url);
        // .NET strips Authorization on cross-host auto-redirects; the manual re-issue must not.
        Assert.All(handler.Requests, r => Assert.StartsWith("Basic ", r.Auth));
        // Relative hrefs resolve against the host that ANSWERED (the redirect target).
        Assert.Equal("https://p42-contacts.icloud.com/123456/carddav/home/", info.Url);
        Assert.Equal("Contacts", info.DisplayName);
    }

    // ── Client parsing units ─────────────────────────────────────────────────────

    [Fact]
    public void ParseInnerHref_FindsNestedHref_RegardlessOfPrefix()
    {
        Assert.Equal("/123456/principal/",
            CardDavContactClient.ParseInnerHref(PrincipalXml, "current-user-principal"));
        Assert.Equal("https://p42-contacts.icloud.com/123456/carddav/",
            CardDavContactClient.ParseInnerHref(HomeSetXml, "addressbook-home-set"));
        Assert.Null(CardDavContactClient.ParseInnerHref(EmptyMultistatus, "current-user-principal"));
    }

    [Fact]
    public void ParseFirstAddressbook_SkipsPlainCollections()
    {
        var (href, name) = CardDavContactClient.ParseFirstAddressbook(CollectionsXml)!.Value;
        Assert.Equal("/123456/carddav/home/", href);
        Assert.Equal("Contacts", name);
    }

    [Fact]
    public void ParseFirstAddressbook_NoAddressbook_ReturnsNull()
    {
        Assert.Null(CardDavContactClient.ParseFirstAddressbook(EmptyMultistatus));
    }

    [Fact]
    public void ParseAddressData_ExtractsEveryVCardBody()
    {
        var bodies = CardDavContactClient.ParseAddressData(ReportXml(
            Card("card-1", "John Doe", "john@icloud.test"),
            Card("card-2", "Jane Roe", "jane@icloud.test")));
        Assert.Equal(2, bodies.Count);
        Assert.Contains("UID:card-1", bodies[0]);
        Assert.Contains("UID:card-2", bodies[1]);
    }

    // ── vCard parsing units ──────────────────────────────────────────────────────

    [Fact]
    public void ParseAll_ExtractsUidFnEmail()
    {
        var card = Assert.Single(VCardModel.ParseAll(Card("card-1", "John Doe", "john@icloud.test")));
        Assert.Equal("card-1", card.Uid);
        Assert.Equal("John Doe", card.DisplayName);
        Assert.Equal("john@icloud.test", card.Email);
    }

    [Fact]
    public void ParseAll_MultipleCardsInOneBody()
    {
        var body = Card("a", "Alice A", "alice@icloud.test") + "\n" + Card("b", "Bob B", "bob@icloud.test");
        var cards = VCardModel.ParseAll(body).ToList();
        Assert.Equal(2, cards.Count);
        Assert.Equal("alice@icloud.test", cards[0].Email);
        Assert.Equal("bob@icloud.test", cards[1].Email);
    }

    [Fact]
    public void ParseAll_FirstEmailWins_WhenSeveralPresent()
    {
        var body = string.Join("\n",
            "BEGIN:VCARD",
            "UID:multi",
            "FN:Multi Mail",
            "EMAIL;TYPE=HOME:first@icloud.test",
            "EMAIL;TYPE=WORK:second@icloud.test",
            "END:VCARD");
        Assert.Equal("first@icloud.test", Assert.Single(VCardModel.ParseAll(body)).Email);
    }

    [Fact]
    public void ParseAll_GroupedEmail_IsCaptured()
    {
        // Apple/iCloud emit custom-labelled emails as grouped properties (RFC 6350 §3.3):
        // "item1.EMAIL". The group prefix must be stripped or the email — and the whole card — is lost.
        var body = string.Join("\n",
            "BEGIN:VCARD",
            "UID:grouped-1",
            "FN:Grouped Person",
            "item1.EMAIL;type=INTERNET;type=pref:grouped@icloud.test",
            "item1.X-ABLabel:_$!<School>!$_",
            "END:VCARD");
        Assert.Equal("grouped@icloud.test", Assert.Single(VCardModel.ParseAll(body)).Email);
    }

    [Fact]
    public void ParseAll_MissingUid_GetsStableSyntheticId()
    {
        var body = string.Join("\n",
            "BEGIN:VCARD",
            "FN:No Uid",
            "EMAIL:nouid@icloud.test",
            "END:VCARD");

        var first  = Assert.Single(VCardModel.ParseAll(body));
        var second = Assert.Single(VCardModel.ParseAll(body));

        Assert.StartsWith("carddav-", first.Uid);
        Assert.Equal(first.Uid, second.Uid); // stable across passes → replace-slice keeps identity
    }

    [Fact]
    public void ParseAll_FallsBackToStructuredName_WhenFnAbsent()
    {
        var body = string.Join("\n",
            "BEGIN:VCARD",
            "UID:nname",
            "N:Doe;John;;;",
            "EMAIL:n@icloud.test",
            "END:VCARD");
        Assert.Equal("John Doe", Assert.Single(VCardModel.ParseAll(body)).DisplayName);
    }

    [Fact]
    public async Task Fetch_DropsCardsWithoutEmail()
    {
        var noEmail = string.Join("\n",
            "BEGIN:VCARD",
            "UID:no-email",
            "FN:No Email",
            "END:VCARD");
        var handler = FullSyncHandler(noEmail, Card("card-1", "John Doe", "john@icloud.test"));

        var contacts = await Source(handler).FetchAsync(ICloudAccount(), TestContext.Current.CancellationToken);

        // The e-mail-less card is dropped; only the usable one survives.
        Assert.Equal("card-1", Assert.Single(contacts).SourceId);
    }

    [Fact]
    public void ParseAll_MalformedInput_ReturnsEmpty()
    {
        Assert.Empty(VCardModel.ParseAll(""));
        Assert.Empty(VCardModel.ParseAll("not a vcard at all"));
    }
}
