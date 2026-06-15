using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class MainViewModelFlagTests
{
    private static MailMessageSummary FlaggedMsg(string id = "1") => new()
    {
        MessageId  = id,
        AccountId  = Guid.NewGuid(),
        FolderName = "INBOX",
        FlagId     = FlagDefinition.BuiltInFlagId.ToString(),
    };

    private static MailMessageSummary UnflaggedMsg(string id = "1") => new()
    {
        MessageId  = id,
        AccountId  = Guid.NewGuid(),
        FolderName = "INBOX",
    };

    private static MainViewModel MakeVm(IFlagService flagService, IEnumerable<MailMessageSummary>? messages = null)
    {
        ILocalStoreService store = messages != null
            ? new FilterableStoreForFlags(messages)
            : new StubLocalStoreService();
        return new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            store, new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService(),
            flagService: flagService);
    }

    [Fact]
    public async Task FlaggedFilter_ShowsOnlyFlaggedMessages()
    {
        var flagged   = FlaggedMsg("1");
        var unflagged = UnflaggedMsg("2");
        var vm = MakeVm(new StubFlagService(), new[] { flagged, unflagged });
        await vm.InitialLoadAsync();

        await vm.SetFilterCommand.ExecuteAsync("flagged");

        Assert.Single(vm.Messages);
        Assert.Equal("1", vm.Messages[0].MessageId);
    }

    [Fact]
    public async Task FlaggedFilter_AllUnflagged_ShowsEmpty()
    {
        var msgs = new[] { UnflaggedMsg("1"), UnflaggedMsg("2") };
        var vm = MakeVm(new StubFlagService(), msgs);
        await vm.InitialLoadAsync();

        await vm.SetFilterCommand.ExecuteAsync("flagged");

        Assert.Empty(vm.Messages);
    }

    [Fact]
    public async Task IsFilterFlagged_TrueWhenFlaggedFilterActive()
    {
        var vm = MakeVm(new StubFlagService());
        await vm.InitialLoadAsync();

        await vm.SetFilterCommand.ExecuteAsync("flagged");

        Assert.True(vm.IsFilterFlagged);
    }

    [Fact]
    public async Task IsFilterFlagged_FalseWhenOtherFilterActive()
    {
        var vm = MakeVm(new StubFlagService());
        await vm.InitialLoadAsync();

        await vm.SetFilterCommand.ExecuteAsync("unread");

        Assert.False(vm.IsFilterFlagged);
    }

    // ── Phase 5: in-memory flag reconciliation on FolderSynced ───────────────

    private sealed class FireableSyncService : ISyncService
    {
        public event Action<IReadOnlyList<MailMessageSummary>>? FolderSynced;
#pragma warning disable CS0067
        public event Action<IReadOnlyList<MailMessageSummary>>? MessagesRemoved;
        public event Action<int>? RulesApplied;
        public event Action<int, int>? SyncProgressChanged;
#pragma warning restore CS0067
        public void Fire(IReadOnlyList<MailMessageSummary> messages) => FolderSynced?.Invoke(messages);
        public Task SyncAllAccountsAsync(IEnumerable<AccountModel> accounts, IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders, CancellationToken ct) => Task.CompletedTask;
        public Task SyncOneFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct) => Task.CompletedTask;
        public Task SyncOneFolderOnlineAsync(AccountModel account, MailFolderModel folder, CancellationToken ct) => Task.CompletedTask;
        public DateTimeOffset? LastSyncedUtc(Guid accountId) => null;
    }

    [Fact]
    public async Task OnFolderSynced_ExternallyUnflaggedMessage_ClearsFlagInMemory()
    {
        // Arrange: load a flagged message into the VM via the initial FolderSynced fire.
        var accountId  = Guid.NewGuid();
        var syncService = new FireableSyncService();
        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), syncService,
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService(), flagService: new StubFlagService());

        await vm.InitialLoadAsync();

        // Point the selected folder at a real folder so FolderSynced processes messages.
        vm.SelectedFolder = new MailFolderModel
        {
            FullName  = "INBOX",
            AccountId = accountId,
            IsHeader  = false,
        };

        // Fire 1: message arrives server-flagged → added to _rawMessages with FlagId set.
        var flaggedIncoming = new MailMessageSummary
        {
            MessageId       = "msg1",
            AccountId       = accountId,
            FolderName      = "INBOX",
            IsServerFlagged = true,
        };
        syncService.Fire([flaggedIncoming]);

        var inMemory = vm.LoadedMessages.FirstOrDefault(m => m.MessageId == "msg1");
        Assert.NotNull(inMemory);
        Assert.NotNull(inMemory!.FlagId);

        // Fire 2: same message, server now reports not-flagged (externally cleared).
        var unflaggedSync = new MailMessageSummary
        {
            MessageId       = "msg1",
            AccountId       = accountId,
            FolderName      = "INBOX",
            IsServerFlagged = false,
        };
        syncService.Fire([unflaggedSync]);

        // The in-memory flag must have been cleared.
        Assert.Null(inMemory.FlagId);
    }

    [Fact]
    public async Task OnFolderSynced_LocalNamedFlag_PreservedWhenServerFlagged()
    {
        // Arrange: message in memory with a user-assigned named flag (not the built-in default).
        var accountId   = Guid.NewGuid();
        var customFlagId = Guid.NewGuid().ToString();
        var syncService = new FireableSyncService();
        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), syncService,
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService(), flagService: new StubFlagService());

        await vm.InitialLoadAsync();

        vm.SelectedFolder = new MailFolderModel
        {
            FullName  = "INBOX",
            AccountId = accountId,
            IsHeader  = false,
        };

        // Fire 1: message arrives and we simulate a named flag being applied locally.
        var incoming = new MailMessageSummary
        {
            MessageId       = "msg2",
            AccountId       = accountId,
            FolderName      = "INBOX",
            IsServerFlagged = true,
            FlagId          = customFlagId,
        };
        syncService.Fire([incoming]);

        var inMemory = vm.LoadedMessages.FirstOrDefault(m => m.MessageId == "msg2");
        Assert.NotNull(inMemory);
        Assert.Equal(customFlagId, inMemory!.FlagId);

        // Fire 2: server still flagged — named flag must NOT be overwritten with built-in default.
        var syncAgain = new MailMessageSummary
        {
            MessageId       = "msg2",
            AccountId       = accountId,
            FolderName      = "INBOX",
            IsServerFlagged = true,
        };
        syncService.Fire([syncAgain]);

        Assert.Equal(customFlagId, inMemory.FlagId);
    }
}

/// <summary>Local store stub that serves a fixed set of messages for flag filter tests.</summary>
sealed class FilterableStoreForFlags : ILocalStoreService
{
    private readonly List<MailMessageSummary> _messages;

    public FilterableStoreForFlags(IEnumerable<MailMessageSummary> messages)
        => _messages = new List<MailMessageSummary>(messages);

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
