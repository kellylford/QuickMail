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

public class GraphMailServiceTests
{
    /// <summary>Routes each request to a canned JSON response by URL, recording the requested URLs.</summary>
    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<string, (HttpStatusCode Status, string Json)> _respond;
        public List<string> Requests { get; } = new();

        public StubHttpHandler(Func<string, (HttpStatusCode, string)> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            var (status, json) = _respond(url);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private const string MeJson = """{"id":"u1","userPrincipalName":"user@contoso.com"}""";

    private static (GraphMailService Svc, StubHttpHandler Handler) Make(Func<string, (HttpStatusCode, string)> respond)
    {
        var handler = new StubHttpHandler(respond);
        var svc = new GraphMailService(new StubOAuthService(), null, new HttpClient(handler));
        return (svc, handler);
    }

    private static AccountModel GraphAccount() => new()
    {
        Id = Guid.NewGuid(),
        Username = "old@contoso.com",
        BackendKind = BackendKind.MicrosoftGraph,
    };

    [Fact]
    public async Task ConnectAsync_UpdatesUsernameFromMe()
    {
        var (svc, _) = Make(url => (HttpStatusCode.OK, MeJson));
        var account = GraphAccount();

        await svc.ConnectAsync(account, ct: TestContext.Current.CancellationToken);

        Assert.Equal("user@contoso.com", account.Username);
    }

    [Fact]
    public async Task GetFoldersAsync_MapsFolders_FollowsNextLink_AndFlagsSpecial()
    {
        const string page1 = """
            {"value":[
              {"id":"f1","displayName":"Inbox","totalItemCount":10,"unreadItemCount":3},
              {"id":"f2","displayName":"Sent Items","totalItemCount":5,"unreadItemCount":0}
            ],"@odata.nextLink":"https://graph.microsoft.com/v1.0/me/mailFolders?$skiptoken=P2"}
            """;
        const string page2 = """{"value":[{"id":"f3","displayName":"Archive","totalItemCount":2,"unreadItemCount":1}]}""";

        // ConnectAsync resolves the well-known folder IDs; feed it the real ones (inbox->f1,
        // sentitems->f2) so special-folder classification runs through the ID map, not the
        // display-name fallback. The other three well-known lookups fall through to the list
        // JSON (parsed as a single folder with a null id) and are skipped — harmless. Localized-
        // name / ID-regression coverage lives in GetFoldersAsync_DetectsSpecialFoldersById_*.
        var (svc, _) = Make(url =>
            url.Contains("/mailFolders/inbox")       ? (HttpStatusCode.OK, """{"id":"f1"}""")
            : url.Contains("/mailFolders/sentitems") ? (HttpStatusCode.OK, """{"id":"f2"}""")
            : url.Contains("/me?")                   ? (HttpStatusCode.OK, MeJson)
            : url.Contains("skiptoken=P2")           ? (HttpStatusCode.OK, page2)
            : (HttpStatusCode.OK, page1));

        var account = GraphAccount();
        await svc.ConnectAsync(account, ct: TestContext.Current.CancellationToken);
        var folders = await svc.GetFoldersAsync(account.Id, TestContext.Current.CancellationToken);

        Assert.Equal(3, folders.Count); // nextLink page followed
        var inbox = folders.Single(f => f.FullName == "f1");
        Assert.Equal(SpecialFolderKind.Inbox, inbox.Kind);
        Assert.False(inbox.ExcludeFromAllMail);
        Assert.Equal(3, inbox.UnreadCount);

        var sent = folders.Single(f => f.FullName == "f2");
        Assert.Equal(SpecialFolderKind.Sent, sent.Kind);
        Assert.True(sent.ExcludeFromAllMail);

        var archive = folders.Single(f => f.FullName == "f3");
        Assert.Equal(SpecialFolderKind.None, archive.Kind);
        Assert.False(archive.ExcludeFromAllMail);
    }

    [Fact]
    public async Task GetFoldersAsync_DetectsSpecialFoldersById_EvenWithLocalizedNames()
    {
        // German display names; detection must come from the well-known folder IDs resolved at connect.
        const string listJson = """
            {"value":[
              {"id":"F-INBOX","displayName":"Posteingang","totalItemCount":5,"unreadItemCount":2},
              {"id":"F-SENT","displayName":"Gesendete Elemente","totalItemCount":3,"unreadItemCount":0},
              {"id":"F-OTHER","displayName":"Projekte","totalItemCount":1,"unreadItemCount":1}
            ]}
            """;
        var (svc, _) = Make(url =>
              url.Contains("/mailFolders/inbox")        ? (HttpStatusCode.OK, """{"id":"F-INBOX"}""")
            : url.Contains("/mailFolders/sentitems")    ? (HttpStatusCode.OK, """{"id":"F-SENT"}""")
            : url.Contains("/mailFolders/drafts")       ? (HttpStatusCode.OK, """{"id":"F-DRAFTS"}""")
            : url.Contains("/mailFolders/deleteditems") ? (HttpStatusCode.OK, """{"id":"F-TRASH"}""")
            : url.Contains("/mailFolders/junkemail")    ? (HttpStatusCode.OK, """{"id":"F-JUNK"}""")
            : url.Contains("/me/mailFolders?")          ? (HttpStatusCode.OK, listJson)
            : (HttpStatusCode.OK, MeJson));

        var account = GraphAccount();
        await svc.ConnectAsync(account, ct: TestContext.Current.CancellationToken);
        var folders = await svc.GetFoldersAsync(account.Id, TestContext.Current.CancellationToken);

        var inbox = folders.Single(f => f.FullName == "F-INBOX");
        Assert.Equal(SpecialFolderKind.Inbox, inbox.Kind);  // by ID, despite "Posteingang"
        Assert.False(inbox.ExcludeFromAllMail);

        var sent = folders.Single(f => f.FullName == "F-SENT");
        Assert.Equal(SpecialFolderKind.Sent, sent.Kind);    // by ID, despite "Gesendete Elemente"
        Assert.True(sent.ExcludeFromAllMail);

        Assert.Equal(SpecialFolderKind.None, folders.Single(f => f.FullName == "F-OTHER").Kind);
    }

    [Fact]
    public async Task GetFoldersAsync_DescendsIntoChildFolders_AndSetsParentId()
    {
        // /me/mailFolders returns only top-level folders; nested folders must be fetched by
        // walking each folder's /childFolders endpoint for as long as childFolderCount > 0.
        const string top = """
            {"value":[
              {"id":"f1","displayName":"Inbox","parentFolderId":"root","childFolderCount":1,"totalItemCount":10,"unreadItemCount":0},
              {"id":"f2","displayName":"Archive","parentFolderId":"root","childFolderCount":0,"totalItemCount":0,"unreadItemCount":0}
            ]}
            """;
        const string f1Children = """
            {"value":[{"id":"c1","displayName":"Projects","parentFolderId":"f1","childFolderCount":1,"totalItemCount":4,"unreadItemCount":1}]}
            """;
        const string c1Children = """
            {"value":[{"id":"g1","displayName":"2026","parentFolderId":"c1","childFolderCount":0,"totalItemCount":2,"unreadItemCount":0}]}
            """;

        var (svc, handler) = Make(url =>
              url.Contains("/me?")                         ? (HttpStatusCode.OK, MeJson)
            : url.Contains("/mailFolders/f1/childFolders") ? (HttpStatusCode.OK, f1Children)
            : url.Contains("/mailFolders/c1/childFolders") ? (HttpStatusCode.OK, c1Children)
            : url.Contains("/me/mailFolders?")             ? (HttpStatusCode.OK, top)
            : (HttpStatusCode.OK, """{"value":[]}"""));    // well-known lookups resolve to nothing

        var account = GraphAccount();
        await svc.ConnectAsync(account, ct: TestContext.Current.CancellationToken);
        var folders = await svc.GetFoldersAsync(account.Id, TestContext.Current.CancellationToken);

        // Top-level f1, f2 plus nested c1 (under f1) and g1 (under c1).
        Assert.Equal(4, folders.Count);
        Assert.Equal("f1", folders.Single(f => f.FullName == "c1").ParentId);
        Assert.Equal("c1", folders.Single(f => f.FullName == "g1").ParentId);
        // f2 had no children, so no descent request was made for it.
        Assert.DoesNotContain(handler.Requests, r => r.Contains("/mailFolders/f2/childFolders"));
    }

    [Fact]
    public async Task GetMessageSummariesAsync_MapsFields()
    {
        const string msgs = """
            {"value":[{
              "id":"m1","subject":"Hello",
              "from":{"emailAddress":{"name":"Alice","address":"alice@x.com"}},
              "toRecipients":[{"emailAddress":{"address":"me@x.com"}}],
              "receivedDateTime":"2024-01-02T03:04:05Z","isRead":false,
              "bodyPreview":"hi there","hasAttachments":true
            }]}
            """;
        var (svc, _) = Make(url => url.Contains("/me?") ? (HttpStatusCode.OK, MeJson) : (HttpStatusCode.OK, msgs));

        var account = GraphAccount();
        await svc.ConnectAsync(account, ct: TestContext.Current.CancellationToken);
        var list = await svc.GetMessageSummariesAsync(account.Id, "inbox", 50, TestContext.Current.CancellationToken);

        var m = Assert.Single(list);
        Assert.Equal("m1", m.MessageId);
        Assert.Equal("Alice <alice@x.com>", m.From);
        Assert.Equal("me@x.com", m.To);
        Assert.Equal("Hello", m.Subject);
        Assert.False(m.IsRead);
        Assert.Equal("hi there", m.Preview);
        Assert.True(m.HasAttachments);
    }

    [Fact]
    public async Task GetMessageDetailAsync_MapsBodyAndExcludesInlineAttachments()
    {
        const string detail = """
            {"id":"m1","subject":"Hello",
             "body":{"contentType":"html","content":"<p>Hi</p>"},
             "from":{"emailAddress":{"address":"alice@x.com"}},
             "toRecipients":[{"emailAddress":{"address":"me@x.com"}}],
             "ccRecipients":[],"internetMessageId":"<abc@x>",
             "receivedDateTime":"2024-01-02T03:04:05Z","isRead":true,"hasAttachments":true,
             "attachments":[
               {"id":"a1","name":"doc.pdf","contentType":"application/pdf","size":1234,"isInline":false},
               {"id":"a2","name":"inline.png","contentType":"image/png","size":50,"isInline":true}
             ]}
            """;
        var (svc, _) = Make(url => url.Contains("/me?") ? (HttpStatusCode.OK, MeJson) : (HttpStatusCode.OK, detail));

        var account = GraphAccount();
        await svc.ConnectAsync(account, ct: TestContext.Current.CancellationToken);
        var d = await svc.GetMessageDetailAsync(account.Id, "inbox", "m1", TestContext.Current.CancellationToken);

        Assert.Equal("m1", d.MessageId);
        Assert.Equal("<p>Hi</p>", d.HtmlBody);
        Assert.Equal(string.Empty, d.PlainTextBody);
        Assert.Equal("<abc@x>", d.InternetMessageId);
        var att = Assert.Single(d.Attachments); // inline excluded
        Assert.Equal("doc.pdf", att.FileName);
        Assert.Equal("a1", att.PartSpecifier);  // Graph attachment id
        Assert.Equal(1234, att.FileSize);
    }

    // A raw RFC 822 meeting request whose text/calendar part carries the invite. CRLF line endings
    // so MimeKit and the ICS parser see well-formed content regardless of the test file's encoding.
    private static string MeetingRequestMime() => string.Join("\r\n", new[]
    {
        "From: Org <org@contoso.com>",
        "To: me@contoso.com",
        "Subject: Lunch",
        "MIME-Version: 1.0",
        "Content-Type: multipart/alternative; boundary=\"b\"",
        "",
        "--b",
        "Content-Type: text/plain; charset=utf-8",
        "",
        "You're invited to lunch.",
        "--b",
        "Content-Type: text/calendar; method=REQUEST; charset=utf-8",
        "",
        "BEGIN:VCALENDAR",
        "VERSION:2.0",
        "METHOD:REQUEST",
        "BEGIN:VEVENT",
        "UID:lunch-1",
        "SUMMARY:Lunch",
        "DTSTART:20260722T170000Z",
        "DTEND:20260722T180000Z",
        "ORGANIZER;CN=Org:mailto:org@contoso.com",
        "END:VEVENT",
        "END:VCALENDAR",
        "--b--",
        "",
    });

    [Fact]
    public async Task GetMessageDetailAsync_MeetingRequest_ExtractsCalendarInviteFromRawMime()
    {
        // A meeting-request message is a derived type; Graph annotates it with @odata.type. The
        // message JSON has no text/calendar part, so the service must fetch the raw MIME ($value)
        // and extract the ICS — lighting up the reading-pane Accept/Decline card. Issue #332.
        const string detail = """
            {"id":"m1","@odata.type":"#microsoft.graph.eventMessageRequest","subject":"Lunch",
             "body":{"contentType":"html","content":"<p>invite</p>"},
             "from":{"emailAddress":{"address":"org@contoso.com"}},
             "toRecipients":[{"emailAddress":{"address":"me@contoso.com"}}],
             "ccRecipients":[],"receivedDateTime":"2026-07-22T00:00:00Z","isRead":false,"hasAttachments":false}
            """;
        var (svc, handler) = Make(url =>
              url.Contains("/me?")   ? (HttpStatusCode.OK, MeJson)
            : url.Contains("$value") ? (HttpStatusCode.OK, MeetingRequestMime())
            : (HttpStatusCode.OK, detail));

        var account = GraphAccount();
        await svc.ConnectAsync(account, ct: TestContext.Current.CancellationToken);
        var d = await svc.GetMessageDetailAsync(account.Id, "inbox", "m1", TestContext.Current.CancellationToken);

        Assert.Contains(handler.Requests, u => u.Contains("/messages/m1/$value"));
        Assert.NotNull(d.CalendarInvite);
        Assert.Equal("lunch-1", d.CalendarInvite!.Uid);
        Assert.Equal("Lunch", d.CalendarInvite.Summary);
        Assert.Equal("REQUEST", d.CalendarInvite.Method);
        Assert.Contains("BEGIN:VCALENDAR", d.CalendarIcs); // raw ICS persisted so the invite survives caching
    }

    [Fact]
    public async Task GetMessageDetailAsync_OrdinaryMessage_DoesNotFetchRawMime()
    {
        // No @odata.type annotation → ordinary mail → no $value round-trip and no invite card.
        const string detail = """
            {"id":"m1","subject":"Hello",
             "body":{"contentType":"html","content":"<p>Hi</p>"},
             "from":{"emailAddress":{"address":"alice@x.com"}},
             "toRecipients":[{"emailAddress":{"address":"me@x.com"}}],
             "ccRecipients":[],"receivedDateTime":"2024-01-02T03:04:05Z","isRead":true,"hasAttachments":false}
            """;
        var (svc, handler) = Make(url => url.Contains("/me?") ? (HttpStatusCode.OK, MeJson) : (HttpStatusCode.OK, detail));

        var account = GraphAccount();
        await svc.ConnectAsync(account, ct: TestContext.Current.CancellationToken);
        var d = await svc.GetMessageDetailAsync(account.Id, "inbox", "m1", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(handler.Requests, u => u.Contains("$value"));
        Assert.Null(d.CalendarInvite);
        Assert.Equal(string.Empty, d.CalendarIcs);
    }

    [Fact]
    public async Task GetMessageDetailAsync_MeetingMessageWithoutCalendarPart_OpensWithoutInvite()
    {
        // A meeting-typed message whose raw MIME carries no text/calendar part (e.g. a malformed or
        // partial invite, or an .ics delivered only as an application/ics attachment). The $value
        // fetch still happens, but extraction finds nothing and must fall through cleanly: the
        // message opens with no invite card rather than throwing. Guards the calendar == null branch.
        const string detail = """
            {"id":"m1","@odata.type":"#microsoft.graph.eventMessageRequest","subject":"Lunch",
             "body":{"contentType":"html","content":"<p>invite</p>"},
             "from":{"emailAddress":{"address":"org@contoso.com"}},
             "toRecipients":[{"emailAddress":{"address":"me@contoso.com"}}],
             "ccRecipients":[],"receivedDateTime":"2026-07-22T00:00:00Z","isRead":false,"hasAttachments":false}
            """;
        var noCalendarMime = string.Join("\r\n", new[]
        {
            "From: Org <org@contoso.com>",
            "To: me@contoso.com",
            "Subject: Lunch",
            "MIME-Version: 1.0",
            "Content-Type: text/plain; charset=utf-8",
            "",
            "You're invited to lunch, but the calendar part is missing.",
            "",
        });
        var (svc, handler) = Make(url =>
              url.Contains("/me?")   ? (HttpStatusCode.OK, MeJson)
            : url.Contains("$value") ? (HttpStatusCode.OK, noCalendarMime)
            : (HttpStatusCode.OK, detail));

        var account = GraphAccount();
        await svc.ConnectAsync(account, ct: TestContext.Current.CancellationToken);
        var d = await svc.GetMessageDetailAsync(account.Id, "inbox", "m1", TestContext.Current.CancellationToken);

        Assert.Contains(handler.Requests, u => u.Contains("/messages/m1/$value")); // meeting type still triggers the fetch
        Assert.Null(d.CalendarInvite);                                             // …but no invite is surfaced
        Assert.Equal(string.Empty, d.CalendarIcs);
    }

    [Fact]
    public async Task GetMessagesSinceDateAsync_AddsReceivedDateTimeFilter()
    {
        var (svc, handler) = Make(url => url.Contains("/me?") ? (HttpStatusCode.OK, MeJson) : (HttpStatusCode.OK, """{"value":[]}"""));

        var account = GraphAccount();
        await svc.ConnectAsync(account, ct: TestContext.Current.CancellationToken);
        await svc.GetMessagesSinceDateAsync(account.Id, "inbox", DateTime.UtcNow.AddDays(-7), TestContext.Current.CancellationToken);

        Assert.Contains(handler.Requests, u => Uri.UnescapeDataString(u).Contains("filter=receivedDateTime ge"));
    }

    [Fact]
    public void AppendToSentAsync_IsNoOp_ForGraph()
    {
        // Graph /sendMail auto-saves to Sent, so the post-send append is a no-op (not a throw).
        var (svc, _) = Make(url => (HttpStatusCode.OK, "{}"));
        Assert.True(svc.AppendToSentAsync(Guid.NewGuid(), new ComposeModel(), TestContext.Current.CancellationToken).IsCompletedSuccessfully);
    }

    [Fact]
    public void GraphMailService_HasNoLocalStoreDependency_SoOnlineModeWorks()
    {
        // --online mode leaves the SQLite schema uncreated; any ILocalStoreService call would throw.
        // GraphMailService must read straight from Graph, so it must not take a local store at all.
        var ctorParamTypes = typeof(GraphMailService).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType);
        Assert.DoesNotContain(typeof(ILocalStoreService), ctorParamTypes);
    }

    [Fact]
    public async Task PerAccountCall_BeforeConnect_Throws()
    {
        var (svc, _) = Make(url => (HttpStatusCode.OK, "{}"));
        // No ConnectAsync first → the account isn't registered, so token resolution can't proceed.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.GetFoldersAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }
}
