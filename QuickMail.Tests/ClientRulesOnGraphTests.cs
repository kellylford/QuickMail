using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Verifies the second half of #333: QuickMail's <b>client-side</b> rules act on Microsoft 365
/// (Graph) accounts the same way they do on IMAP accounts.
/// <para>
/// This wires the <b>real</b> <see cref="MailServiceRouter"/> to two recording backends and drives
/// <see cref="RuleService"/> through it, so it exercises the actual production path — rule matching
/// is backend-agnostic, and the rule's action must dispatch to the backend that owns the account.
/// A regression here (e.g. constructing RuleService with the IMAP backend instead of the router)
/// would silently make rules a no-op for Graph accounts, which is exactly the bug this pins.
/// </para>
/// </summary>
public class ClientRulesOnGraphTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LocalStoreService _store;
    private readonly Guid _graphAccountId = Guid.NewGuid();
    private readonly Guid _imapAccountId = Guid.NewGuid();

    public ClientRulesOnGraphTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"qm-rules-graph-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new LocalStoreService(new ProfileContext(_tempDir));
        _store.Initialize();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // ── A backend that records the mutations rules perform ───────────────────────

    private sealed class RecordingMailService : IMailService
    {
        public List<(string Method, Guid AccountId, string Folder, string? Destination, List<string> Ids)> Actions { get; } = [];

        public Task MarkReadAsync(Guid a, string f, string id, CancellationToken ct = default)
        { Actions.Add(("MarkRead", a, f, null, [id])); return Task.CompletedTask; }

        public Task MoveMessagesAsync(Guid a, string f, IList<string> ids, string dest, CancellationToken ct = default)
        { Actions.Add(("Move", a, f, dest, [.. ids])); return Task.CompletedTask; }

        public Task MoveToTrashBatchAsync(Guid a, string f, IList<string> ids, CancellationToken ct = default)
        { Actions.Add(("Trash", a, f, null, [.. ids])); return Task.CompletedTask; }

        // ── Everything else: inert ───────────────────────────────────────────────
        public Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
        public bool IsConnected(Guid accountId) => true;
        public Task<List<MailFolderModel>> GetFoldersAsync(Guid a, CancellationToken ct = default) => Task.FromResult(new List<MailFolderModel>());
        public Task<List<MailMessageSummary>> GetMessageSummariesAsync(Guid a, string f, int max, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>());
        public Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(Guid a, string f, DateTime since, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>());
        public Task<List<MailMessageSummary>> GetMessagesSinceAsync(Guid a, string f, string sinceId, int count, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>());
        public Task<MailMessageDetail> GetMessageDetailAsync(Guid a, string f, string id, CancellationToken ct = default) => Task.FromResult(new MailMessageDetail());
        public Task<MailMessageDetail> PrefetchMessageDetailAsync(Guid a, string f, string id, CancellationToken ct = default) => Task.FromResult(new MailMessageDetail());
        public Task MarkReadBatchAsync(Guid a, string f, IList<string> ids, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetMessageFlaggedAsync(Guid a, string f, string id, bool flagged, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveToTrashAsync(Guid a, string f, string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task PermanentlyDeleteBatchAsync(Guid a, string f, IList<string> ids, CancellationToken ct = default) => Task.CompletedTask;
        public Task NoOpAsync(Guid a, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> CountTrashMessagesAsync(Guid a, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> EmptyTrashAsync(Guid a, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IList<string>> GetFolderMessageIdsAsync(Guid a, string f, CancellationToken ct = default) => Task.FromResult<IList<string>>([]);
        public Task<IReadOnlyDictionary<string, string>> FetchPreviewsAsync(Guid a, string f, IList<string> ids, int maxLines, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        public Task<int> PollAsync(Guid a, string f, CancellationToken ct = default) => Task.FromResult(0);
        public Task<(int Total, int Unread)> GetInboxStatusAsync(Guid a, CancellationToken ct = default) => Task.FromResult((0, 0));
        public Task<string?> FindDraftsFolderNameAsync(Guid a, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string> AppendDraftAsync(Guid a, ComposeModel draft, string? replaceId, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task AppendToSentAsync(Guid a, ComposeModel sent, CancellationToken ct = default) => Task.CompletedTask;
        public Task<byte[]> DownloadAttachmentAsync(Guid a, string f, string id, string part, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task CopyMessagesAsync(Guid a, string f, IList<string> ids, string dest, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateFolderAsync(Guid a, string? parent, string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteFolderAsync(Guid a, string f, CancellationToken ct = default) => Task.CompletedTask;
        public Task RenameFolderAsync(Guid a, string f, string newName, string? newParent, CancellationToken ct = default) => Task.CompletedTask;
        public Task CopyFolderAsync(Guid a, string f, string? destParent, CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    // ── Harness: the real router, an IMAP default backend, and a Graph backend ───

    private sealed record Harness(
        RuleService Rules, RecordingMailService Imap, RecordingMailService Graph);

    private Harness Build(params MailRule[] rules)
    {
        var imap = new RecordingMailService();
        var graph = new RecordingMailService();

        // First backend is the router's default (IMAP), matching App.xaml.cs.
        var router = new MailServiceRouter([imap, graph]);
        router.RegisterAccount(_graphAccountId, graph);
        router.RegisterAccount(_imapAccountId, imap);

        var svc = new RuleService(router, _store, _tempDir);
        svc.SaveRules([.. rules]);
        return new Harness(svc, imap, graph);
    }

    private static MailMessageSummary Message(Guid accountId, string subject, string id = "1") => new()
    {
        MessageId = id,
        AccountId = accountId,
        FolderName = "INBOX",
        From = "sender@example.com",
        To = "me@example.com",
        Subject = subject,
        Preview = "body text",
    };

    private MailRule MoveRule(Guid? accountId) => new()
    {
        Name = "File invoices",
        AccountId = accountId,
        UseSubjectCondition = true,
        SubjectContains = "invoice",
        Action = RuleAction.MoveToFolder,
        TargetFolder = "Archive",
    };

    // ── The verification ─────────────────────────────────────────────────────────

    [Fact]
    public async Task MoveRule_OnGraphAccount_DispatchesToTheGraphBackend()
    {
        var h = Build(MoveRule(_graphAccountId));
        var incoming = new List<MailMessageSummary> { Message(_graphAccountId, "Your invoice is ready") };

        var (matched, removed) = await h.Rules.ApplyRulesAsync(incoming, _graphAccountId, CancellationToken.None);

        var action = Assert.Single(h.Graph.Actions);
        Assert.Equal("Move", action.Method);
        Assert.Equal(_graphAccountId, action.AccountId);
        Assert.Equal("Archive", action.Destination);
        Assert.Equal(["1"], action.Ids);

        Assert.Empty(h.Imap.Actions);        // never leaked to the wrong backend
        Assert.Equal(1, matched);
        Assert.Single(removed);              // moved messages are pulled from the incoming batch
        Assert.Empty(incoming);
    }

    [Fact]
    public async Task MarkAsReadRule_OnGraphAccount_DispatchesToTheGraphBackend()
    {
        var rule = MoveRule(_graphAccountId);
        rule.Action = RuleAction.MarkAsRead;
        rule.TargetFolder = null;
        var h = Build(rule);

        await h.Rules.ApplyRulesAsync(
            [Message(_graphAccountId, "invoice attached")], _graphAccountId, CancellationToken.None);

        Assert.Equal("MarkRead", Assert.Single(h.Graph.Actions).Method);
        Assert.Empty(h.Imap.Actions);
    }

    [Fact]
    public async Task DeleteRule_OnGraphAccount_DispatchesToTheGraphBackend()
    {
        var rule = MoveRule(_graphAccountId);
        rule.Action = RuleAction.Delete;
        rule.TargetFolder = null;
        var h = Build(rule);

        await h.Rules.ApplyRulesAsync(
            [Message(_graphAccountId, "invoice attached")], _graphAccountId, CancellationToken.None);

        Assert.Equal("Trash", Assert.Single(h.Graph.Actions).Method);
        Assert.Empty(h.Imap.Actions);
    }

    [Fact]
    public async Task MoveRule_OnImapAccount_StillDispatchesToTheImapBackend()
    {
        // Control: proves the Graph results above are real routing, not "everything goes to Graph".
        var h = Build(MoveRule(_imapAccountId));

        await h.Rules.ApplyRulesAsync(
            [Message(_imapAccountId, "invoice attached")], _imapAccountId, CancellationToken.None);

        Assert.Equal("Move", Assert.Single(h.Imap.Actions).Method);
        Assert.Empty(h.Graph.Actions);
    }

    [Fact]
    public async Task AllAccountsRule_AlsoAppliesToAGraphAccount()
    {
        // Today an unscoped (AccountId == null) rule applies everywhere, Graph included. Pinned so
        // the D1 migration in #333 — which stops all-account rules reaching Graph — is a deliberate,
        // visible change rather than an accidental one.
        var h = Build(MoveRule(accountId: null));

        await h.Rules.ApplyRulesAsync(
            [Message(_graphAccountId, "invoice attached")], _graphAccountId, CancellationToken.None);

        Assert.Single(h.Graph.Actions);
    }

    [Fact]
    public async Task RuleScopedToAnotherAccount_DoesNotTouchTheGraphAccount()
    {
        var h = Build(MoveRule(_imapAccountId));

        await h.Rules.ApplyRulesAsync(
            [Message(_graphAccountId, "invoice attached")], _graphAccountId, CancellationToken.None);

        Assert.Empty(h.Graph.Actions);
        Assert.Empty(h.Imap.Actions);
    }

    [Fact]
    public async Task MarkAsUnread_IsLocalOnly_OnGraphToo()
    {
        // Documented gap: Mark as unread never reaches any server (no IMailService call at all), so
        // it's the one action with no server-rule equivalent. Pre-existing for IMAP; pinned here so
        // the limitation is visible rather than folklore.
        var rule = MoveRule(_graphAccountId);
        rule.Action = RuleAction.MarkAsUnread;
        rule.TargetFolder = null;
        var h = Build(rule);

        await h.Rules.ApplyRulesAsync(
            [Message(_graphAccountId, "invoice attached")], _graphAccountId, CancellationToken.None);

        Assert.Empty(h.Graph.Actions);
        Assert.Empty(h.Imap.Actions);
    }
}
