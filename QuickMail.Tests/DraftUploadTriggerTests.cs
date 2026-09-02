// When the drafts actually go up, and what a blocked account is told — issue #637.
//
// The compose window promises "It will go to the server when you are back online." That was not
// true: the upload pass had exactly one caller, reached only from the main window's Loaded event,
// so the promise meant "the next time you restart QuickMail". These pin the trigger, the guard
// against two passes racing over one queue, and the fact that a blocked account is met rather than
// written to a status bar the next sweep overwrites.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class DraftUploadTriggerTests
{
    private static readonly Guid AccountId = Guid.Parse("7a7a7a7a-7a7a-7a7a-7a7a-7a7a7a7a7a7a");

    private sealed class OneAccount : IAccountService
    {
        public List<AccountModel> LoadAccounts() =>
            [new AccountModel { Id = AccountId, AccountName = "Work", Username = "me@example.com",
                               AuthType = AuthType.OAuth2Google }];   // no password to look up
        public void SaveAccounts(List<AccountModel> a) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    private static MailFolderModel Folder(string full, SpecialFolderKind kind) =>
        new() { AccountId = AccountId, FullName = full, DisplayName = full, Kind = kind };

    private static (MainViewModel Vm, CountingSync Sync) Vm(bool onlineMode = false)
    {
        var store = new StubLocalStoreService();
        store.SeededFolders[AccountId] =
        [
            Folder("INBOX", SpecialFolderKind.Inbox),
            Folder("Drafts", SpecialFolderKind.Drafts),
        ];
        var sync = new CountingSync();
        var vm = new MainViewModel(
            new StubImapMailService(), new OneAccount(), new StubCredentialService(),
            store, new StubOAuthService(), sync, new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(),
            new StubSmtpService(), onlineMode, uiDispatcher: new StubUiDispatcher());
        vm.LoadAccountList();
        return (vm, sync);
    }

    [Fact]
    public async Task WhenAnAccountConnects_ItsWaitingDraftsAreSentAtOnce()
    {
        // "When you are back online" has to mean the moment the account is reachable, not the next
        // launch. SetCachedFolders is the one place an account becomes connected.
        var (vm, sync) = Vm();

        await vm.InitialLoadAsync();
        await vm.StartBackgroundSyncAsync();
        await sync.Settled();

        Assert.Contains(AccountId, sync.UploadedFor);
    }

    [Fact]
    public async Task AnOrdinaryFolderRefresh_DoesNotQueueAnotherPass()
    {
        // Only the transition into "connected" counts. Firing on every folder write would put a
        // second pass over the same queue on every refresh.
        var (vm, sync) = Vm();
        await vm.InitialLoadAsync();
        await vm.StartBackgroundSyncAsync();
        await sync.Settled();
        var afterFirst = sync.UploadedFor.Count;

        await vm.StartBackgroundSyncAsync();
        await sync.Settled();

        Assert.Equal(afterFirst, sync.UploadedFor.Count);
    }

    [Fact]
    public async Task InOnlineMode_NothingIsQueued()
    {
        // No local store, so nothing is being held and there is nothing to send.
        var (vm, sync) = Vm(onlineMode: true);

        await vm.InitialLoadAsync();
        await sync.Settled();

        Assert.Empty(sync.UploadedFor);
    }

    [Fact]
    public async Task TwoPassesForOneAccount_DoNotOverlap()
    {
        // The sweep and a reconnect can both ask. Two passes reading the same queue would each see
        // the same rows and append them twice.
        var drafts = new SlowDrafts();
        var sync = new SyncService(
            new RecordingMailService(), new StubLocalStoreService(), new StubConfigService(),
            new StubRuleService(), ui: new StubUiDispatcher(), probeMode: false, localDrafts: drafts);
        var account = new AccountModel { Id = AccountId, Username = "me@example.com" };

        var both = await Task.WhenAll(
            sync.UploadPendingDraftsAsync(account, TestContext.Current.CancellationToken),
            sync.UploadPendingDraftsAsync(account, TestContext.Current.CancellationToken));

        Assert.Equal(1, drafts.MaxConcurrent);
        Assert.Equal(1, both.Sum());          // the second pass finds the queue already emptied
    }

    /// <summary>Counts passes per account and lets a test wait for the fire-and-forget ones.</summary>
    private sealed class CountingSync : ISyncService
    {
#pragma warning disable CS0067 // not what this file is about
        public event Action<IReadOnlyList<MailMessageSummary>>? FolderSynced;
        public event Action<IReadOnlyList<MailMessageSummary>>? MessagesRemoved;
        public event Action<IReadOnlyList<MailMessageSummary>>? DraftUploadsRefused;
        public event Action<AccountModel, string>? DraftUploadsBlocked;
        public event Action<int>? DraftsUploaded;
        public event Action<IReadOnlyList<MailMessageSummary>>? FolderReadStatesReconciled;
        public event Action<int>? RulesApplied;
        public event Action<int, int>? SyncProgressChanged;
#pragma warning restore CS0067

        private readonly List<Task> _inFlight = [];
        public List<Guid> UploadedFor { get; } = [];

        public Task<int> UploadPendingDraftsAsync(AccountModel account, CancellationToken ct)
        {
            lock (UploadedFor) UploadedFor.Add(account.Id);
            return Task.FromResult(0);
        }

        /// <summary>The view model starts these on the thread pool, so give them a turn to land.</summary>
        public async Task Settled()
        {
            for (var i = 0; i < 20 && UploadedFor.Count == 0; i++) await Task.Delay(10);
            await Task.WhenAll(_inFlight);
        }

        public Task SyncAllAccountsAsync(IEnumerable<AccountModel> accounts,
            IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders, CancellationToken ct)
            => Task.CompletedTask;
        public Task<IReadOnlyList<MailMessageSummary>> SyncOneFolderAsync(AccountModel a, MailFolderModel f, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>([]);
        public Task<IReadOnlyList<MailMessageSummary>> SyncOneFolderOnlineAsync(AccountModel a, MailFolderModel f, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>([]);
        public Task<int> ReconcileFolderAsync(AccountModel a, MailFolderModel f, CancellationToken ct) => Task.FromResult(0);
        public Task<IReadOnlyList<MailMessageSummary>> SyncFolderFullAsync(AccountModel a, MailFolderModel f, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>([]);
        public void SeedRebuildBaseline(IEnumerable<Guid> accountIds) { }
        public DateTimeOffset? LastSyncedUtc(Guid accountId) => null;
    }

    /// <summary>A draft store slow enough for two passes to overlap if nothing stops them.</summary>
    private sealed class SlowDrafts : ILocalDraftService
    {
        private readonly List<MailMessageSummary> _pending =
        [
            new() { MessageId = "local-1", AccountId = AccountId, FolderName = "Drafts", IsPendingUpload = true },
        ];
        private int _live;

        public int MaxConcurrent { get; private set; }

        public async Task<IReadOnlyList<MailMessageSummary>> GetPendingAsync(Guid accountId)
        {
            var live = Interlocked.Increment(ref _live);
            MaxConcurrent = Math.Max(MaxConcurrent, live);
            await Task.Delay(40);
            Interlocked.Decrement(ref _live);
            lock (_pending) return [.. _pending];
        }

        public Task DiscardAsync(Guid a, string f, string id)
        {
            lock (_pending) _pending.RemoveAll(p => p.MessageId == id);
            return Task.CompletedTask;
        }

        public Task<ComposeModel?> LoadAsync(Guid a, string f, string id, CancellationToken ct = default)
            => Task.FromResult<ComposeModel?>(new ComposeModel { AccountId = a, Subject = id });
        public Task<string?> GetSupersededServerIdAsync(Guid a, string f, string id) => Task.FromResult<string?>(null);
        public Task MarkSendFailedAsync(Guid a, string f, string id, string reason) => Task.CompletedTask;
        public Task<PendingDraftSave> SaveAsync(AccountModel account, ComposeModel draft, string folderName,
            string? previousMessageId, CancellationToken ct = default)
            => throw new NotSupportedException("the upload pass never saves");
        public Task<string?> ResolveDraftsFolderNameAsync(Guid accountId) => Task.FromResult<string?>("Drafts");
        public Task<string> ReadDeliveryNoticeAsync(Guid a, string f, string id) => Task.FromResult(string.Empty);
    }
}
