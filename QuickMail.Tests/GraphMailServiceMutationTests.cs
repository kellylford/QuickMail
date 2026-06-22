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
/// PR 6 — exercises the Graph mutation surface (move/copy/delete/trash, mark-read, drafts,
/// attachment download, folder CRUD) against a stub handler that records method, URL, and body.
/// </summary>
public class GraphMailServiceMutationTests : IDisposable
{
    // GraphMailService wraps a GraphClient/HttpClient; dispose every one created during a test
    // (CLAUDE.md IDisposable rule). xUnit disposes the test-class instance after each test.
    private readonly List<GraphMailService> _created = new();

    public void Dispose()
    {
        foreach (var svc in _created) svc.Dispose();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<string, (HttpStatusCode Status, string Json)> _respond;
        public List<(string Method, string Url, string Body)> Calls { get; } = new();

        public RecordingHandler(Func<string, (HttpStatusCode, string)> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method.Method, url, body));
            var (status, json) = _respond(url);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private const string MeJson = """{"id":"u1","userPrincipalName":"user@contoso.com"}""";

    // ConnectAsync probes /me and the five well-known folders; anything not explicitly stubbed
    // returns "{}" (a folder with a null id → skipped), which is harmless for mutation tests.
    private (GraphMailService Svc, RecordingHandler Handler) Make(Func<string, (HttpStatusCode, string)> respond)
    {
        var handler = new RecordingHandler(url => url.Contains("/me?") ? (HttpStatusCode.OK, MeJson) : respond(url));
        var svc = new GraphMailService(new StubOAuthService(), null, new HttpClient(handler));
        _created.Add(svc);
        return (svc, handler);
    }

    private static (HttpStatusCode, string) Ok(string json = "{}") => (HttpStatusCode.OK, json);

    private async Task<(GraphMailService Svc, RecordingHandler Handler, AccountModel Account)> ConnectedAsync(
        Func<string, (HttpStatusCode, string)>? respond = null)
    {
        var (svc, handler) = Make(respond ?? (_ => Ok()));
        var account = new AccountModel { Id = Guid.NewGuid(), Username = "old@contoso.com", BackendKind = BackendKind.MicrosoftGraph };
        await svc.ConnectAsync(account);
        return (svc, handler, account);
    }

    [Fact]
    public async Task MoveMessagesAsync_PostsMovePerMessage_WithDestination()
    {
        var (svc, h, acct) = await ConnectedAsync();

        await svc.MoveMessagesAsync(acct.Id, "inbox", new[] { "m1", "m2" }, "archive");

        var moves = h.Calls.Where(c => c.Method == "POST" && c.Url.Contains("/move")).ToList();
        Assert.Equal(2, moves.Count);
        Assert.Contains(moves, c => c.Url.Contains("/me/messages/m1/move"));
        Assert.Contains(moves, c => c.Url.Contains("/me/messages/m2/move"));
        Assert.All(moves, c => Assert.Contains("archive", c.Body));
    }

    [Fact]
    public async Task CopyMessagesAsync_PostsCopyPerMessage()
    {
        var (svc, h, acct) = await ConnectedAsync();

        await svc.CopyMessagesAsync(acct.Id, "inbox", new[] { "m1" }, "archive");

        var copy = Assert.Single(h.Calls, c => c.Method == "POST" && c.Url.Contains("/me/messages/m1/copy"));
        Assert.Contains("archive", copy.Body);
    }

    [Fact]
    public async Task MoveToTrashBatchAsync_MovesToDeletedItems()
    {
        var (svc, h, acct) = await ConnectedAsync();

        await svc.MoveToTrashBatchAsync(acct.Id, "inbox", new[] { "m1", "m2" });

        var moves = h.Calls.Where(c => c.Method == "POST" && c.Url.Contains("/move")).ToList();
        Assert.Equal(2, moves.Count);
        Assert.All(moves, c => Assert.Contains("deleteditems", c.Body));
    }

    [Fact]
    public async Task PermanentlyDeleteBatchAsync_DeletesEachMessage()
    {
        var (svc, h, acct) = await ConnectedAsync();

        await svc.PermanentlyDeleteBatchAsync(acct.Id, "deleteditems", new[] { "m1", "m2" });

        var deletes = h.Calls.Where(c => c.Method == "DELETE" && c.Url.Contains("/me/messages/")).ToList();
        Assert.Equal(2, deletes.Count);
        Assert.Contains(deletes, c => c.Url.EndsWith("/me/messages/m1"));
        Assert.Contains(deletes, c => c.Url.EndsWith("/me/messages/m2"));
    }

    [Fact]
    public async Task EmptyTrashAsync_DeletesEveryTrashMessage_AndReturnsCount()
    {
        var (svc, h, acct) = await ConnectedAsync(url =>
            url.Contains("/mailFolders/deleteditems/messages")
                ? Ok("""{"value":[{"id":"d1"},{"id":"d2"},{"id":"d3"}]}""")
                : Ok());

        var count = await svc.EmptyTrashAsync(acct.Id);

        Assert.Equal(3, count);
        Assert.Equal(3, h.Calls.Count(c => c.Method == "DELETE" && c.Url.Contains("/me/messages/")));
    }

    [Fact]
    public async Task MarkReadBatchAsync_PatchesIsReadPerMessage()
    {
        var (svc, h, acct) = await ConnectedAsync();

        await svc.MarkReadBatchAsync(acct.Id, "inbox", new[] { "m1", "m2" });

        var patches = h.Calls.Where(c => c.Method == "PATCH" && c.Url.Contains("/me/messages/")).ToList();
        Assert.Equal(2, patches.Count);
        Assert.All(patches, c => Assert.Contains("isRead", c.Body));
    }

    [Fact]
    public async Task CountTrashMessagesAsync_ReadsDeletedItemsTotal()
    {
        var (svc, _, acct) = await ConnectedAsync(url =>
            url.Contains("/mailFolders/deleteditems?") ? Ok("""{"totalItemCount":7}""") : Ok());

        Assert.Equal(7, await svc.CountTrashMessagesAsync(acct.Id));
    }

    [Fact]
    public async Task CreateFolderAsync_TopLevel_PostsToMailFolders()
    {
        var (svc, h, acct) = await ConnectedAsync();

        await svc.CreateFolderAsync(acct.Id, null, "Projects");

        var post = Assert.Single(h.Calls, c => c.Method == "POST" && c.Url.EndsWith("/me/mailFolders"));
        Assert.Contains("Projects", post.Body);
    }

    [Fact]
    public async Task CreateFolderAsync_WithParent_PostsToChildFolders()
    {
        var (svc, h, acct) = await ConnectedAsync();

        await svc.CreateFolderAsync(acct.Id, "parent1", "Sub");

        Assert.Single(h.Calls, c => c.Method == "POST" && c.Url.Contains("/me/mailFolders/parent1/childFolders"));
    }

    [Fact]
    public async Task DeleteFolderAsync_DeletesTheFolder()
    {
        var (svc, h, acct) = await ConnectedAsync();

        await svc.DeleteFolderAsync(acct.Id, "f1");

        Assert.Single(h.Calls, c => c.Method == "DELETE" && c.Url.EndsWith("/me/mailFolders/f1"));
    }

    [Fact]
    public async Task RenameFolderAsync_PatchesNameThenMovesToNewParent()
    {
        var (svc, h, acct) = await ConnectedAsync();

        await svc.RenameFolderAsync(acct.Id, "f1", "Renamed", "parent2");

        var patch = Assert.Single(h.Calls, c => c.Method == "PATCH" && c.Url.EndsWith("/me/mailFolders/f1"));
        Assert.Contains("Renamed", patch.Body);
        var move = Assert.Single(h.Calls, c => c.Method == "POST" && c.Url.Contains("/me/mailFolders/f1/move"));
        Assert.Contains("parent2", move.Body);
    }

    [Fact]
    public async Task RenameFolderAsync_RenameOnly_PatchesNameWithoutMoving()
    {
        var (svc, h, acct) = await ConnectedAsync();

        await svc.RenameFolderAsync(acct.Id, "f1", "Renamed", newParentFolderName: null);

        Assert.Single(h.Calls, c => c.Method == "PATCH" && c.Url.EndsWith("/me/mailFolders/f1"));
        Assert.DoesNotContain(h.Calls, c => c.Url.Contains("/move"));
    }

    [Fact]
    public async Task CopyFolderAsync_PostsCopyToDestination()
    {
        var (svc, h, acct) = await ConnectedAsync();

        await svc.CopyFolderAsync(acct.Id, "f1", "dest");

        var copy = Assert.Single(h.Calls, c => c.Method == "POST" && c.Url.Contains("/me/mailFolders/f1/copy"));
        Assert.Contains("dest", copy.Body);
    }

    [Fact]
    public async Task DownloadAttachmentAsync_GetsAttachmentValueBytes()
    {
        var (svc, _, acct) = await ConnectedAsync(url =>
            url.Contains("/attachments/a1/$value") ? Ok("FILEDATA") : Ok());

        var bytes = await svc.DownloadAttachmentAsync(acct.Id, "inbox", "m1", "a1");

        Assert.Equal("FILEDATA", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task AppendDraftAsync_PostsMimeToMessages_AndReturnsNewId()
    {
        var (svc, h, acct) = await ConnectedAsync(url =>
            url.EndsWith("/me/messages") ? Ok("""{"id":"draft1"}""") : Ok());

        var id = await svc.AppendDraftAsync(acct.Id,
            new ComposeModel { To = "a@b.com", Subject = "s", Body = "hi" }, replaceMessageId: null);

        Assert.Equal("draft1", id);
        var post = Assert.Single(h.Calls, c => c.Method == "POST" && c.Url.EndsWith("/me/messages"));
        Assert.NotEmpty(post.Body); // base64 MIME
    }

    [Fact]
    public async Task AppendDraftAsync_DeletesOldDraft_WhenReplacing()
    {
        var (svc, h, acct) = await ConnectedAsync(url =>
            url.EndsWith("/me/messages") ? Ok("""{"id":"draft2"}""") : Ok());

        await svc.AppendDraftAsync(acct.Id,
            new ComposeModel { To = "a@b.com", Subject = "s", Body = "hi" }, replaceMessageId: "old1");

        Assert.Single(h.Calls, c => c.Method == "DELETE" && c.Url.EndsWith("/me/messages/old1"));
        Assert.Single(h.Calls, c => c.Method == "POST" && c.Url.EndsWith("/me/messages"));
    }
}
