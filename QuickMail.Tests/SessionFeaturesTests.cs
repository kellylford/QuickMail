// Tests for features added in the 0.6.x and 0.7.x sessions:
//   • DateDisplay format (12-hour clock, M/d/yyyy for older dates)
//   • Preview suppression when PreviewLines = 0
//   • OnlineMode flag on MainViewModel
//   • IDLE new-mail detection triggering a targeted inbox sync
//   • Mode-transition invariants for MessageOpenMode (ReadingPane ↔ Tab ↔ Window)

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

// ── DateDisplay ──────────────────────────────────────────────────────────────

public class MailMessageSummaryDateDisplayTests
{
    private static MailMessageSummary MsgAt(int hour, int minute)
    {
        var now = DateTimeOffset.Now;
        return new MailMessageSummary
        {
            Date = new DateTimeOffset(now.Year, now.Month, now.Day, hour, minute, 0, now.Offset)
        };
    }

    [Fact]
    public void Today_Morning_ShowsHourMinuteA()
    {
        var msg = MsgAt(9, 30);
        Assert.Equal("9:30A", msg.DateDisplay);
    }

    [Fact]
    public void Today_Afternoon_ShowsHourMinuteP()
    {
        var msg = MsgAt(15, 45);
        Assert.Equal("3:45P", msg.DateDisplay);
    }

    [Fact]
    public void Today_Noon_ShowsP()
    {
        var msg = MsgAt(12, 0);
        Assert.Equal("12:00P", msg.DateDisplay);
    }

    [Fact]
    public void Today_Midnight_ShowsA()
    {
        var msg = MsgAt(0, 0);
        Assert.Equal("12:00A", msg.DateDisplay);
    }

    [Fact]
    public void OtherDate_ShowsMdyyyy()
    {
        var now = DateTimeOffset.Now;
        var msg = new MailMessageSummary
        {
            // One year ago — definitely not today.
            Date = new DateTimeOffset(now.Year - 1, 5, 1, 10, 0, 0, now.Offset)
        };
        Assert.Equal($"5/1/{now.Year - 1}", msg.DateDisplay);
    }
}

// ── Preview suppression ──────────────────────────────────────────────────────

public class PreviewSuppressionTests
{
    sealed class ZeroPreviewConfig : IConfigService
    {
        public ConfigModel Load() => new() { PreviewLines = 0 };
        public void Save(ConfigModel config) { }
    }

    sealed class PreviewStore : ILocalStoreService
    {
        private readonly List<MailMessageSummary> _messages;
        public PreviewStore(IEnumerable<MailMessageSummary> messages) => _messages = new(messages);

        public void Initialize() { }
        public Task UpsertSummariesAsync(IEnumerable<MailMessageSummary> summaries) => Task.CompletedTask;
        public Task<List<MailMessageSummary>> LoadAllSummariesAsync() => Task.FromResult(new List<MailMessageSummary>(_messages));
        public Task<List<MailMessageSummary>> LoadAllSummariesAsync(Guid accountId) => Task.FromResult(new List<MailMessageSummary>(_messages));
        public Task<List<MailMessageSummary>> LoadFolderSummariesAsync(Guid accountId, string folderName, int? limit = null) => Task.FromResult(new List<MailMessageSummary>(_messages));
        public Task DeleteSummariesAsync(Guid accountId, string folderName, IEnumerable<string> messageIds) => Task.CompletedTask;
        public Task DeleteAccountDataAsync(Guid accountId) => Task.CompletedTask;
        public Task UpdateIsReadAsync(Guid accountId, string folderName, string messageId, bool isRead) => Task.CompletedTask;
        public Task UpdateIsReadBatchAsync(IEnumerable<(Guid AccountId, string FolderName, string MessageId)> items, bool isRead) => Task.CompletedTask;
        public Task UpdatePreviewAsync(Guid accountId, string folderName, string messageId, string preview) => Task.CompletedTask;
        public Task UpdatePreviewsBatchAsync(Guid accountId, string folderName, IEnumerable<(string MessageId, string Preview)> updates) => Task.CompletedTask;
        public Task<bool> HasSummariesMissingRecipientsAsync() => Task.FromResult(false);
        public Task UpsertDetailAsync(MailMessageDetail detail) => Task.CompletedTask;
        public Task<MailMessageDetail?> LoadDetailAsync(Guid accountId, string folderName, string messageId) => Task.FromResult<MailMessageDetail?>(null);
        public Task<string> GetMaxMessageKeyAsync(Guid accountId, string folderName) => Task.FromResult("0");
        public Task<HashSet<string>> GetAllMessageIdsAsync(Guid accountId, string folderName) => Task.FromResult(new HashSet<string>());
        public Task<int> CountSummariesAsync(Guid accountId) => Task.FromResult(0);
        public Task<DateTimeOffset?> GetOldestMessageDateAsync(Guid accountId) => Task.FromResult<DateTimeOffset?>(null);
        public Task UpdateFlagIdAsync(Guid accountId, string folderName, string messageId, string? flagId) => Task.CompletedTask;
        public Task UpdateFlagIdBatchAsync(IEnumerable<(Guid AccountId, string FolderName, string MessageId)> items, string? flagId) => Task.CompletedTask;
    }

    private static readonly MailMessageSummary[] MessagesWithPreviews =
    [
        new() { MessageId = "1", Preview = "First preview text",  Date = DateTimeOffset.Now },
        new() { MessageId = "2", Preview = "Second preview text", Date = DateTimeOffset.Now.AddMinutes(-1) },
        new() { MessageId = "3", Preview = "Third preview text",  Date = DateTimeOffset.Now.AddMinutes(-2) },
    ];

    [Fact]
    public async Task InitialLoad_PreviewLinesZero_ClearsAllPreviews()
    {
        var store = new PreviewStore(MessagesWithPreviews);
        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            store, new StubOAuthService(), new StubSyncService(), new ZeroPreviewConfig(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService());

        await vm.InitialLoadAsync();

        Assert.All(vm.Messages, m => Assert.Equal(string.Empty, m.Preview));
    }

    [Fact]
    public async Task InitialLoad_PreviewLinesNonZero_PreservesPreview()
    {
        // Default StubConfigService returns PreviewLines = 3 (from ConfigModel defaults).
        var store = new PreviewStore(MessagesWithPreviews);
        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            store, new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService());

        await vm.InitialLoadAsync();

        Assert.All(vm.Messages, m => Assert.NotEqual(string.Empty, m.Preview));
    }


}

// ── Online mode ──────────────────────────────────────────────────────────────

public class OnlineModeTests
{
    private static MainViewModel MakeVm(bool onlineMode = false) =>
        new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService(), onlineMode: onlineMode);

    [Fact]
    public void OnlineMode_DefaultIsFalse()
    {
        var vm = MakeVm();
        Assert.False(vm.OnlineMode);
    }

    [Fact]
    public void OnlineMode_TrueWhenPassedToConstructor()
    {
        var vm = MakeVm(onlineMode: true);
        Assert.True(vm.OnlineMode);
    }

    [Fact]
    public async Task InitialLoad_OnlineMode_LeavesMessagesEmpty()
    {
        // In online mode, InitialLoadAsync must not touch the local store.
        var vm = MakeVm(onlineMode: true);
        await vm.InitialLoadAsync();
        Assert.Empty(vm.Messages);
    }
}

// ── IDLE new-mail detection ──────────────────────────────────────────────────

public class IdleNewMailTests
{
    sealed class FireableMailService : IMailService
    {
        private readonly Guid _accountId;
        public FireableMailService(Guid accountId) => _accountId = accountId;

        public event Action<Guid>? InboxNewMailDetected;
#pragma warning disable CS0067 // not raised by this fake
        public event Action<Guid, bool>? AccountReachabilityChanged;
#pragma warning restore CS0067
        public void FireNewMail() => InboxNewMailDetected?.Invoke(_accountId);

        // Return one INBOX folder so ConnectAllAccountsAsync populates _cachedFolders.
        public Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default) =>
            Task.FromResult(new List<MailFolderModel>
            {
                new() { FullName = "INBOX", DisplayName = "Inbox", AccountId = accountId, Kind = SpecialFolderKind.Inbox }
            });

        public Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<MailMessageSummary>> GetMessageSummariesAsync(Guid accountId, string folderName, int maxMessages, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>());
        public Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(Guid accountId, string folderName, DateTime since, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>());
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
        public void StartIdleWatchers(IReadOnlyList<AccountModel> accounts, CancellationToken ct = default) { }
        public void StopIdleWatchers() { }
        public void Dispose() { }
    }

    sealed class SpySyncService : ISyncService
    {
        public readonly TaskCompletionSource<(AccountModel Account, MailFolderModel Folder)> SyncOneFolderCalled = new();
        public readonly TaskCompletionSource<(AccountModel Account, MailFolderModel Folder)> SyncOneFolderOnlineCalled = new();

#pragma warning disable CS0067
        public event Action<IReadOnlyList<MailMessageSummary>>? FolderSynced;
        public event Action<IReadOnlyList<MailMessageSummary>>? MessagesRemoved;
        public event Action<int>? RulesApplied;
        public event Action<int, int>? SyncProgressChanged;
#pragma warning restore CS0067

        public Task SyncAllAccountsAsync(IEnumerable<AccountModel> accounts,
            IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders, CancellationToken ct)
            => Task.CompletedTask;

        public Task SyncOneFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
        {
            SyncOneFolderCalled.TrySetResult((account, folder));
            return Task.CompletedTask;
        }

        public Task SyncOneFolderOnlineAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
        {
            SyncOneFolderOnlineCalled.TrySetResult((account, folder));
            return Task.CompletedTask;
        }

        public DateTimeOffset? LastSyncedUtc(Guid accountId) => null;
    }

    [Fact]
    public async Task NewMailDetected_TriggersSyncForMatchingInbox()
    {
        var accountId = Guid.NewGuid();
        var imap = new FireableMailService(accountId);
        var sync = new SpySyncService();
        var account = new AccountModel
        {
            Id = accountId,
            AuthType = AuthType.OAuth2Microsoft,
            Username = "test@example.com",
            ImapHost = "imap.example.com",
            ImapPort = 993
        };

        var vm = new MainViewModel(
            imap, new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), sync,
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService());

        // Seed the account and connect so _cachedFolders gets the INBOX entry.
        vm.Accounts.Add(account);
        await vm.ConnectAllAccountsAsync();

        // Fire the IDLE event — OnInboxNewMailDetected runs SyncOneFolderAsync via Task.Run.
        imap.FireNewMail();

        var result = await sync.SyncOneFolderCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(accountId, result.Account.Id);
        Assert.Equal(SpecialFolderKind.Inbox, result.Folder.Kind);
    }

    [Fact]
    public async Task NewMailDetected_OnlineMode_CallsSyncOneFolderOnline()
    {
        var accountId = Guid.NewGuid();
        var imap = new FireableMailService(accountId);
        var sync = new SpySyncService();
        var account = new AccountModel
        {
            Id = accountId,
            AuthType = AuthType.OAuth2Microsoft,
            Username = "test@example.com"
        };

        var vm = new MainViewModel(
            imap, new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), sync,
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService(), onlineMode: true);

        vm.Accounts.Add(account);
        await vm.ConnectAllAccountsAsync();

        imap.FireNewMail();

        // In online mode the handler must call SyncOneFolderOnlineAsync (not SyncOneFolderAsync).
        var result = await sync.SyncOneFolderOnlineCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(accountId, result.Account.Id);
        Assert.Equal(SpecialFolderKind.Inbox, result.Folder.Kind);
        Assert.False(sync.SyncOneFolderCalled.Task.IsCompleted);
    }
}

// ── MessageOpenMode transition invariants ────────────────────────────────────
// These tests catch the class of bug where switching modes leaves stale reading-
// pane state visible. The invariant: when MessageOpenMode != ReadingPane,
// IsMessageOpen must be false. ApplySettings is responsible for enforcing it.

public class MessageOpenModeTransitionTests
{
    private static MainViewModel MakeVm() =>
        new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService());

    private static ConfigModel CfgWith(MessageOpenMode mode) =>
        new ConfigModel { Windowing = new WindowingPreferences { MessageOpenMode = mode } };

    [Fact]
    public void ApplySettings_ReadingPaneToWindow_ClearsIsMessageOpen()
    {
        var vm = MakeVm();
        vm.IsMessageOpen = true;
        vm.MessageDetail = new MailMessageDetail();

        vm.ApplySettings(CfgWith(MessageOpenMode.Window));

        Assert.False(vm.IsMessageOpen);
        Assert.Null(vm.MessageDetail);
    }

    [Fact]
    public void ApplySettings_ReadingPaneToTab_ClearsIsMessageOpen()
    {
        var vm = MakeVm();
        vm.IsMessageOpen = true;
        vm.MessageDetail = new MailMessageDetail();

        vm.ApplySettings(CfgWith(MessageOpenMode.Tab));

        Assert.False(vm.IsMessageOpen);
        Assert.Null(vm.MessageDetail);
    }

    [Fact]
    public void ApplySettings_SameMode_DoesNotClearIsMessageOpen()
    {
        var vm = MakeVm();
        vm.IsMessageOpen = true;

        // Applying ReadingPane when already in ReadingPane: not a transition, should not clear.
        vm.ApplySettings(CfgWith(MessageOpenMode.ReadingPane));

        Assert.True(vm.IsMessageOpen);
    }

    [Fact]
    public void ApplySettings_WindowToReadingPane_DoesNotClearIsMessageOpen()
    {
        var vm = MakeVm();
        vm.ApplySettings(CfgWith(MessageOpenMode.Window));

        // Now switch back; IsMessageOpen was already false (never set to true in Window mode).
        // Verify the transition does not touch IsMessageOpen (it was already false).
        vm.IsMessageOpen = false;
        vm.ApplySettings(CfgWith(MessageOpenMode.ReadingPane));

        Assert.False(vm.IsMessageOpen); // still false — not set by the transition
    }
}
