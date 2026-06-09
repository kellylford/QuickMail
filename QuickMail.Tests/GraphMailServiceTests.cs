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

        await svc.ConnectAsync(account);

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

        var (svc, _) = Make(url =>
            url.Contains("/me?") ? (HttpStatusCode.OK, MeJson)
            : url.Contains("skiptoken=P2") ? (HttpStatusCode.OK, page2)
            : (HttpStatusCode.OK, page1));

        var account = GraphAccount();
        await svc.ConnectAsync(account);
        var folders = await svc.GetFoldersAsync(account.Id);

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
        await svc.ConnectAsync(account);
        var list = await svc.GetMessageSummariesAsync(account.Id, "inbox", 50);

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
        await svc.ConnectAsync(account);
        var d = await svc.GetMessageDetailAsync(account.Id, "inbox", "m1");

        Assert.Equal("m1", d.MessageId);
        Assert.Equal("<p>Hi</p>", d.HtmlBody);
        Assert.Equal(string.Empty, d.PlainTextBody);
        Assert.Equal("<abc@x>", d.InternetMessageId);
        var att = Assert.Single(d.Attachments); // inline excluded
        Assert.Equal("doc.pdf", att.FileName);
        Assert.Equal("a1", att.PartSpecifier);  // Graph attachment id
        Assert.Equal(1234, att.FileSize);
    }

    [Fact]
    public async Task GetMessagesSinceDateAsync_AddsReceivedDateTimeFilter()
    {
        var (svc, handler) = Make(url => url.Contains("/me?") ? (HttpStatusCode.OK, MeJson) : (HttpStatusCode.OK, """{"value":[]}"""));

        var account = GraphAccount();
        await svc.ConnectAsync(account);
        await svc.GetMessagesSinceDateAsync(account.Id, "inbox", DateTime.UtcNow.AddDays(-7));

        Assert.Contains(handler.Requests, u => Uri.UnescapeDataString(u).Contains("filter=receivedDateTime ge"));
    }

    [Fact]
    public void Mutation_Throws_NotImplemented_InPR4()
    {
        var (svc, _) = Make(url => (HttpStatusCode.OK, "{}"));
        // These throw synchronously (expression-bodied `=> throw ...`), so the discard keeps the
        // lambda an Action rather than a Func<Task>.
        Assert.Throws<NotImplementedException>(() => { _ = svc.MoveToTrashAsync(Guid.NewGuid(), "inbox", "m1"); });
        Assert.Throws<NotImplementedException>(() => { _ = svc.AppendToSentAsync(Guid.NewGuid(), new ComposeModel()); });
        Assert.Throws<NotImplementedException>(() => { _ = svc.CreateFolderAsync(Guid.NewGuid(), null, "New"); });
    }

    [Fact]
    public async Task PerAccountCall_BeforeConnect_Throws()
    {
        var (svc, _) = Make(url => (HttpStatusCode.OK, "{}"));
        // No ConnectAsync first → the account isn't registered, so token resolution can't proceed.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.GetFoldersAsync(Guid.NewGuid()));
    }
}
