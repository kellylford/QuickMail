// Tests for the "All Archive" virtual folder — issue #452.
//
// All Archive is the fifth folder-scoped aggregate, alongside All Inboxes / Drafts / Sent / Trash.
// It differs from those four in one important way: the four match folders on SpecialFolderKind,
// while All Archive resolves each account's archive destination the same way Move to Archive does,
// so a per-account override (AccountModel.ArchiveFolderFullName) pointing at an ordinary folder is
// honoured. These tests pin that behaviour, the tree/list placement, and the live-arrival filter.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class AllArchiveVirtualFolderTests
{
    // Folder + message source for a fixed set of accounts. Folders are keyed by account so each
    // account can have a different archive shape (server-flagged, override, or none at all).
    private sealed class FolderedMailService : IMailService
    {
        private readonly Dictionary<Guid, List<MailFolderModel>> _folders;
        private readonly Dictionary<(Guid, string), List<MailMessageSummary>> _messages;

        public FolderedMailService(
            Dictionary<Guid, List<MailFolderModel>> folders,
            Dictionary<(Guid, string), List<MailMessageSummary>> messages)
        {
            _folders  = folders;
            _messages = messages;
        }

        public Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default) =>
            Task.FromResult(_folders.TryGetValue(accountId, out var f) ? new List<MailFolderModel>(f) : []);

        public Task<List<MailMessageSummary>> GetMessageSummariesAsync(
            Guid accountId, string folderName, int maxMessages, CancellationToken ct = default) =>
            Task.FromResult(_messages.TryGetValue((accountId, folderName), out var m)
                ? new List<MailMessageSummary>(m) : []);

        public Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(Guid accountId, string folderName, DateTime since, CancellationToken ct = default)
            => GetMessageSummariesAsync(accountId, folderName, int.MaxValue, ct);

        public Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default) => Task.CompletedTask;
        public bool IsConnected(Guid accountId) => true;
        public Task DisconnectAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<MailMessageSummary>> GetMessagesSinceAsync(Guid accountId, string folderName, string sinceMessageId, int initialCount, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>());
        public Task<MailMessageDetail> GetMessageDetailAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => Task.FromResult(new MailMessageDetail());
        public Task<MailMessageDetail> PrefetchMessageDetailAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => Task.FromResult(new MailMessageDetail());
        public Task MarkReadAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkReadBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetMessageFlaggedAsync(Guid accountId, string folderName, string messageId, bool flagged, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveToTrashAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveToTrashBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default) => Task.CompletedTask;
        public Task PermanentlyDeleteBatchAsync(Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default) => Task.CompletedTask;
        public Task NoOpAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> CountTrashMessagesAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> EmptyTrashAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IList<string>> GetFolderMessageIdsAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.FromResult<IList<string>>(Array.Empty<string>());
        public Task<IReadOnlyList<(string Id, DateTimeOffset ReceivedUtc, bool IsRead)>> GetFolderMessageIdDatesAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(string, DateTimeOffset, bool)>>([]);
        public Task<IReadOnlyDictionary<string, string>> FetchPreviewsAsync(Guid accountId, string folderName, IList<string> messageIds, int maxLines, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        public Task<int> PollAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.FromResult(0);
        public Task<(int Total, int Unread)> GetInboxStatusAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult((0, 0));
        public Task<string?> FindDraftsFolderNameAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string> AppendDraftAsync(Guid accountId, ComposeModel draft, string? replaceMessageId, CancellationToken ct = default) => Task.FromResult("0");
        public Task AppendToSentAsync(Guid accountId, ComposeModel sent, CancellationToken ct = default) => Task.CompletedTask;
        public Task<byte[]> DownloadAttachmentAsync(Guid accountId, string folderName, string messageId, string partSpecifier, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task CopyMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveMessagesAsync(Guid accountId, string folderName, IList<string> messageIds, string destinationFolder, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateFolderAsync(Guid accountId, string? parentFolderName, string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteFolderAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.CompletedTask;
        public Task RenameFolderAsync(Guid accountId, string folderName, string newName, string? newParentFolderName, CancellationToken ct = default) => Task.CompletedTask;
        public Task CopyFolderAsync(Guid accountId, string folderName, string? destinationParentName, CancellationToken ct = default) => Task.CompletedTask;
        public void StartWatchers(IReadOnlyList<AccountModel> accounts, CancellationToken ct = default) { }
        public void StopWatchers() { }
        public void Dispose() { }
    }

    // Sync service whose FolderSynced event the test can raise on demand, to exercise the
    // live-arrival branch of OnFolderSynced.
    private sealed class RaisableSyncService : ISyncService
    {
#pragma warning disable CS0067 // not raised by this fake
        public event Action<IReadOnlyList<MailMessageSummary>>? MessagesRemoved;
        public event Action<IReadOnlyList<MailMessageSummary>>? FolderReadStatesReconciled;
        public event Action<int>? RulesApplied;
        public event Action<int, int>? SyncProgressChanged;
#pragma warning restore CS0067
        public event Action<IReadOnlyList<MailMessageSummary>>? FolderSynced;

        public void RaiseFolderSynced(params MailMessageSummary[] messages) => FolderSynced?.Invoke(messages);

        public Task SyncAllAccountsAsync(IEnumerable<AccountModel> accounts,
            IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<MailMessageSummary>> SyncOneFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>(Array.Empty<MailMessageSummary>());
        public Task<IReadOnlyList<MailMessageSummary>> SyncOneFolderOnlineAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>(Array.Empty<MailMessageSummary>());
        public Task<IReadOnlyList<MailMessageSummary>> SyncFolderFullAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>(Array.Empty<MailMessageSummary>());
        public Task<int> ReconcileFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct) => Task.FromResult(0);
        public DateTimeOffset? LastSyncedUtc(Guid accountId) => null;
        public void SeedRebuildBaseline(IEnumerable<Guid> accountIds) { }
    }

    private static readonly Guid AccountA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AccountB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static AccountModel Account(Guid id, string label, string? archiveOverride = null) => new()
    {
        Id                     = id,
        AccountName            = label,
        Username               = label.ToLowerInvariant() + "@example.com",
        AuthType               = AuthType.OAuth2Microsoft,
        ArchiveFolderFullName  = archiveOverride,
    };

    private static MailFolderModel Folder(Guid accountId, string fullName, SpecialFolderKind kind) => new()
    {
        AccountId   = accountId,
        FullName    = fullName,
        DisplayName = fullName,
        Kind        = kind,
    };

    private static MailMessageSummary Msg(Guid accountId, string folder, string id, int daysAgo) => new()
    {
        MessageId  = id,
        AccountId  = accountId,
        FolderName = folder,
        From       = "someone@example.com",
        Subject    = $"Subject {id}",
        Date       = DateTimeOffset.Now.AddDays(-daysAgo),
    };

    private static async Task<(MainViewModel Vm, RaisableSyncService Sync)> MakeVmAsync(
        IEnumerable<AccountModel> accounts,
        Dictionary<Guid, List<MailFolderModel>> folders,
        Dictionary<(Guid, string), List<MailMessageSummary>> messages)
    {
        var imap = new FolderedMailService(folders, messages);
        var sync = new RaisableSyncService();
        var vm = new MainViewModel(
            imap, new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), sync,
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService());

        foreach (var a in accounts) vm.Accounts.Add(a);
        await vm.ConnectAllAccountsAsync();
        return (vm, sync);
    }

    private static Task SelectFolder(MainViewModel vm, MailFolderModel folder)
        => vm.SelectFolderCommand.ExecuteAsync(folder);

    // ── Placement in the folder list and tree ────────────────────────────────

    [Fact]
    public async Task Folders_ContainsTheAllArchiveSentinel()
    {
        var (vm, _) = await MakeVmAsync(
            [Account(AccountA, "Work")],
            new() { [AccountA] = [Folder(AccountA, "Archive", SpecialFolderKind.Archive)] },
            []);

        Assert.Contains(vm.Folders, f =>
            f.FullName == MainViewModel.AllArchiveFolder.FullName);
    }

    [Fact]
    public async Task FolderTree_AllMailGroup_ListsAllArchiveBetweenSentAndTrash()
    {
        var (vm, _) = await MakeVmAsync(
            [Account(AccountA, "Work")],
            new() { [AccountA] = [Folder(AccountA, "Archive", SpecialFolderKind.Archive)] },
            []);

        var group = vm.FolderTree.FirstOrDefault(n => n.IsHeader && n.Label == "All Mail");
        Assert.NotNull(group);
        var labels = group!.Children.Select(c => c.Label).ToList();

        Assert.Equal(
            new[] { "All Mail", "All Inboxes", "All Drafts", "All Sent", "All Archive", "All Trash", "All Flagged" },
            labels);
    }

    // ── Fetch ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SelectAllArchive_MergesEveryAccountsArchiveFolder_NewestFirst()
    {
        var (vm, _) = await MakeVmAsync(
            [Account(AccountA, "Work"), Account(AccountB, "Home")],
            new()
            {
                [AccountA] = [Folder(AccountA, "INBOX", SpecialFolderKind.Inbox),
                              Folder(AccountA, "Archive", SpecialFolderKind.Archive)],
                [AccountB] = [Folder(AccountB, "INBOX", SpecialFolderKind.Inbox),
                              Folder(AccountB, "Archief", SpecialFolderKind.Archive)],
            },
            new()
            {
                [(AccountA, "Archive")] = [Msg(AccountA, "Archive", "a-old", 5)],
                [(AccountB, "Archief")] = [Msg(AccountB, "Archief", "b-new", 1)],
                [(AccountA, "INBOX")]   = [Msg(AccountA, "INBOX", "inbox", 0)],
            });

        await SelectFolder(vm, MainViewModel.AllArchiveFolder);

        Assert.Equal(new[] { "b-new", "a-old" }, vm.Messages.Select(m => m.MessageId).ToArray());
    }

    [Fact]
    public async Task SelectAllArchive_HonoursPerAccountOverride_OverServerFlaggedFolder()
    {
        // Account B archives to an ordinary "Keep" folder rather than its server Archive folder —
        // the aggregate must show where Move to Archive actually writes.
        var (vm, _) = await MakeVmAsync(
            [Account(AccountA, "Work"), Account(AccountB, "Home", archiveOverride: "Keep")],
            new()
            {
                [AccountA] = [Folder(AccountA, "Archive", SpecialFolderKind.Archive)],
                [AccountB] = [Folder(AccountB, "Archive", SpecialFolderKind.Archive),
                              Folder(AccountB, "Keep",    SpecialFolderKind.None)],
            },
            new()
            {
                [(AccountA, "Archive")] = [Msg(AccountA, "Archive", "a", 2)],
                [(AccountB, "Archive")] = [Msg(AccountB, "Archive", "b-server", 1)],
                [(AccountB, "Keep")]    = [Msg(AccountB, "Keep",    "b-keep",   0)],
            });

        await SelectFolder(vm, MainViewModel.AllArchiveFolder);

        Assert.Equal(new[] { "b-keep", "a" }, vm.Messages.Select(m => m.MessageId).ToArray());
    }

    [Fact]
    public async Task SelectAllArchive_AccountWithNoArchiveFolder_ContributesNothing()
    {
        var (vm, _) = await MakeVmAsync(
            [Account(AccountA, "Work"), Account(AccountB, "Home")],
            new()
            {
                [AccountA] = [Folder(AccountA, "Archive", SpecialFolderKind.Archive)],
                [AccountB] = [Folder(AccountB, "INBOX", SpecialFolderKind.Inbox)],
            },
            new()
            {
                [(AccountA, "Archive")] = [Msg(AccountA, "Archive", "a", 1)],
                [(AccountB, "INBOX")]   = [Msg(AccountB, "INBOX",   "b", 0)],
            });

        await SelectFolder(vm, MainViewModel.AllArchiveFolder);

        Assert.Equal(new[] { "a" }, vm.Messages.Select(m => m.MessageId).ToArray());
        Assert.Equal("1 message in All Archive.", vm.StatusText);
    }

    [Fact]
    public async Task SelectAllArchive_NoArchiveFoldersAnywhere_SaysSoRatherThanFailing()
    {
        var (vm, _) = await MakeVmAsync(
            [Account(AccountA, "Work")],
            new() { [AccountA] = [Folder(AccountA, "INBOX", SpecialFolderKind.Inbox)] },
            []);

        await SelectFolder(vm, MainViewModel.AllArchiveFolder);

        Assert.Empty(vm.Messages);
        Assert.Equal("No messages in All Archive.", vm.StatusText);
    }

    // Regression guard for the refactor that generalised FetchVirtualFolderAsync from a
    // SpecialFolderKind to a sentinel name: the four kind-scoped aggregates must be unaffected.
    [Fact]
    public async Task SelectAllSent_StillMatchesOnKind()
    {
        var (vm, _) = await MakeVmAsync(
            [Account(AccountA, "Work")],
            new()
            {
                [AccountA] = [Folder(AccountA, "Sent", SpecialFolderKind.Sent),
                              Folder(AccountA, "Archive", SpecialFolderKind.Archive)],
            },
            new()
            {
                [(AccountA, "Sent")]    = [Msg(AccountA, "Sent",    "sent", 1)],
                [(AccountA, "Archive")] = [Msg(AccountA, "Archive", "arch", 0)],
            });

        await SelectFolder(vm, MainViewModel.AllSentFolder);

        Assert.Equal(new[] { "sent" }, vm.Messages.Select(m => m.MessageId).ToArray());
        Assert.Equal("1 message in All Sent.", vm.StatusText);
    }

    // The status line is spoken, so "1 messages" is a defect rather than a cosmetic one. Pinning
    // both arms here because the count text is shared by all five folder-scoped aggregates.
    [Fact]
    public async Task SelectAllArchive_TwoMessages_UsesThePluralNoun()
    {
        var (vm, _) = await MakeVmAsync(
            [Account(AccountA, "Work")],
            new() { [AccountA] = [Folder(AccountA, "Archive", SpecialFolderKind.Archive)] },
            new()
            {
                [(AccountA, "Archive")] = [Msg(AccountA, "Archive", "a", 2),
                                           Msg(AccountA, "Archive", "b", 1)],
            });

        await SelectFolder(vm, MainViewModel.AllArchiveFolder);

        Assert.Equal("2 messages in All Archive.", vm.StatusText);
    }

    // Every folder-scoped aggregate must have its own name in the status line — the display-name
    // lookup used to fall back to "All Archive" for anything it did not recognise.
    [Theory]
    [InlineData("AllInboxes", "INBOX",  SpecialFolderKind.Inbox,  "All Inboxes")]
    [InlineData("AllDrafts",  "Drafts", SpecialFolderKind.Drafts, "All Drafts")]
    [InlineData("AllTrash",   "Trash",  SpecialFolderKind.Trash,  "All Trash")]
    public async Task SelectFolderScopedAggregate_StatusNamesThatAggregate(
        string keySuffix, string folderName, SpecialFolderKind kind, string expectedName)
    {
        var (vm, _) = await MakeVmAsync(
            [Account(AccountA, "Work")],
            new() { [AccountA] = [Folder(AccountA, folderName, kind)] },
            new() { [(AccountA, folderName)] = [Msg(AccountA, folderName, "m", 0)] });

        var sentinel = vm.Folders.First(f => f.FullName == "\x00" + keySuffix);
        await SelectFolder(vm, sentinel);

        Assert.Equal($"1 message in {expectedName}.", vm.StatusText);
    }

    // ── Changing an account's archive destination while All Archive is open ──

    [Fact]
    public async Task SetArchiveFolder_WhileViewingAllArchive_ReloadsAgainstTheNewDestination()
    {
        var (vm, _) = await MakeVmAsync(
            [Account(AccountA, "Work")],
            new()
            {
                [AccountA] = [Folder(AccountA, "Archive", SpecialFolderKind.Archive),
                              Folder(AccountA, "Keep",    SpecialFolderKind.None)],
            },
            new()
            {
                [(AccountA, "Archive")] = [Msg(AccountA, "Archive", "server", 1)],
                [(AccountA, "Keep")]    = [Msg(AccountA, "Keep",    "keep",   0)],
            });

        await SelectFolder(vm, MainViewModel.AllArchiveFolder);
        Assert.Equal(new[] { "server" }, vm.Messages.Select(m => m.MessageId).ToArray());

        await vm.SetArchiveFolderAsync(AccountA, "Keep");

        Assert.Equal(new[] { "keep" }, vm.Messages.Select(m => m.MessageId).ToArray());
    }

    [Fact]
    public async Task SetArchiveFolder_WhileViewingAnotherFolder_DoesNotDisturbTheList()
    {
        var (vm, _) = await MakeVmAsync(
            [Account(AccountA, "Work")],
            new()
            {
                [AccountA] = [Folder(AccountA, "Sent",    SpecialFolderKind.Sent),
                              Folder(AccountA, "Archive", SpecialFolderKind.Archive),
                              Folder(AccountA, "Keep",    SpecialFolderKind.None)],
            },
            new()
            {
                [(AccountA, "Sent")]    = [Msg(AccountA, "Sent",    "sent", 0)],
                [(AccountA, "Archive")] = [Msg(AccountA, "Archive", "arch", 0)],
            });

        await SelectFolder(vm, MainViewModel.AllSentFolder);
        await vm.SetArchiveFolderAsync(AccountA, "Keep");

        Assert.Equal(new[] { "sent" }, vm.Messages.Select(m => m.MessageId).ToArray());
        Assert.Equal(MainViewModel.AllSentFolder.FullName, vm.SelectedFolder?.FullName);
    }

    // ── Live arrivals ────────────────────────────────────────────────────────

    [Fact]
    public async Task FolderSynced_WhileViewingAllArchive_AcceptsOnlyArchiveDestinationMessages()
    {
        var (vm, sync) = await MakeVmAsync(
            [Account(AccountA, "Work", archiveOverride: "Keep")],
            new()
            {
                [AccountA] = [Folder(AccountA, "INBOX",   SpecialFolderKind.Inbox),
                              Folder(AccountA, "Archive", SpecialFolderKind.Archive),
                              Folder(AccountA, "Keep",    SpecialFolderKind.None)],
            },
            []);

        await SelectFolder(vm, MainViewModel.AllArchiveFolder);

        sync.RaiseFolderSynced(
            Msg(AccountA, "Keep",    "keep",  0),
            Msg(AccountA, "INBOX",   "inbox", 0),
            Msg(AccountA, "Archive", "arch",  0));

        Assert.Equal(new[] { "keep" }, vm.Messages.Select(m => m.MessageId).ToArray());
    }
}
