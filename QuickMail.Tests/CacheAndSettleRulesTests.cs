using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// What a view does with mail it has just fetched from the server (#712): cache it where it always did, then have the
/// sync settle client rules for it off the UI thread, so a message a rule moves is in the list only briefly rather than
/// until the next background check.
/// </summary>
public class CacheAndSettleRulesTests
{
    private sealed class RecordingSync(List<string> log) : ISyncService
    {
#pragma warning disable CS0067
        public event Action<IReadOnlyList<MailMessageSummary>>? FolderSynced;
        public event Action<IReadOnlyList<MailMessageSummary>>? MessagesRemoved;
        public event Action<IReadOnlyList<MailMessageSummary>>? FolderReadStatesReconciled;
        public event Action<int>? RulesApplied;
        public event Action<int, int>? SyncProgressChanged;
        public event Action<int, int>? OfflineBodyProgressChanged;
        public event Action<int, int>? OfflineBodyPassCompleted;
#pragma warning restore CS0067

        /// <summary>A folder whose rules pass fails, as it would with the store unavailable.</summary>
        public string? FailFor { get; init; }

        public Task ApplyPendingRulesAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
        {
            log.Add("settle " + folder.FullName);
            if (folder.FullName == FailFor) throw new InvalidOperationException("the store is unavailable");
            return Task.CompletedTask;
        }

        public Task SyncAllAccountsAsync(IEnumerable<AccountModel> accounts,
            IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<MailMessageSummary>> SyncOneFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>([]);
        public Task<IReadOnlyList<MailMessageSummary>> SyncOneFolderOnlineAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>([]);
        public Task<IReadOnlyList<MailMessageSummary>> SyncFolderFullAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>([]);
        public Task<int> ReconcileFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct) => Task.FromResult(0);
        public void SeedRebuildBaseline(IEnumerable<Guid> accountIds) { }
        public Task BackfillOfflineBodiesAsync(IEnumerable<AccountModel> accounts,
            IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders, CancellationToken ct) => Task.CompletedTask;
        public DateTimeOffset? LastSyncedUtc(Guid accountId) => null;
    }

    private static MainViewModel MakeVm(List<string> log, RecordingSync? sync = null) => new(
        new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
        new StubLocalStoreService(), new StubOAuthService(), sync ?? new RecordingSync(log), new StubConfigService(),
        new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService());

    private static List<(AccountModel Account, MailFolderModel Folder)> InboxAndArchive()
    {
        var account = new AccountModel { Id = Guid.NewGuid(), AccountName = "Home" };
        return
        [
            (account, new MailFolderModel { AccountId = account.Id, FullName = "INBOX", Kind = SpecialFolderKind.Inbox }),
            (account, new MailFolderModel { AccountId = account.Id, FullName = "Archive" }),
        ];
    }

    [Fact]
    public async Task RulesAreSettledOnlyOnceTheWriteHasLanded()
    {
        // Settling before the mail is in the store would find nothing waiting, and the rules would wait for the next sync.
        var log = new List<string>();
        var vm = MakeVm(log);
        var write = new TaskCompletionSource();

        var settling = vm.SettleRulesAfterCachingAsync(write.Task, InboxAndArchive());
        Assert.Empty(log);

        write.SetResult();
        await settling;
        Assert.Equal(["settle INBOX", "settle Archive"], log);
    }

    [Fact]
    public async Task AWriteThatFailed_SettlesNothing()
    {
        // The failed write is logged where it was made. With nothing cached, there is nothing waiting to settle.
        var log = new List<string>();
        var vm = MakeVm(log);

        await vm.SettleRulesAfterCachingAsync(Task.FromException(new IOException("disk full")), InboxAndArchive());

        Assert.Empty(log);
    }

    [Fact]
    public async Task OneFolderFailing_DoesNotKeepTheOthersWaiting()
    {
        // The failing folder's mail stays waiting for the next sync; the rest is settled now, as it would have been.
        var log = new List<string>();
        var vm = MakeVm(log, new RecordingSync(log) { FailFor = "INBOX" });

        await vm.SettleRulesAfterCachingAsync(Task.CompletedTask, InboxAndArchive());

        Assert.Equal(["settle INBOX", "settle Archive"], log);
    }
}
