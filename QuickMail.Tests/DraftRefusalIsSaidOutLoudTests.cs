// The upload pass reporting what it did — issue #637.
//
// A successful upload got a status line; a refusal got a row label and nothing else. A row label is
// durable, but a screen reader does not re-speak a row the user is not sitting on, so the good news
// was reported and the bad news was silent. That asymmetry is the defect: the sweep runs in the
// background, and the user has no reason to go looking.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class DraftRefusalIsSaidOutLoudTests
{
    private static readonly Guid AccountId = Guid.Parse("3c3c3c3c-3c3c-3c3c-3c3c-3c3c3c3c3c3c");

    private static MailMessageSummary Row(string id, string? reason = null) => new()
    {
        MessageId = id, AccountId = AccountId, FolderName = "Drafts",
        Subject = $"Draft {id}", IsRead = true, IsPendingUpload = true, SendFailedReason = reason,
    };

    private static (MainViewModel Vm, RaisingSyncService Sync) Vm(params MailMessageSummary[] rows)
    {
        var sync = new RaisingSyncService();
        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), sync,
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService(),
            uiDispatcher: new StubUiDispatcher())
        {
            Messages = new BatchObservableCollection<MailMessageSummary>(rows),
        };
        return (vm, sync);
    }

    [Fact]
    public void ARefusedUpload_IsSaidAndNotOnlyWrittenOntoTheRow()
    {
        var live = Row("local-1");
        var (vm, sync) = Vm(live);

        // Captured DURING the notification. SetStatus resets the category on the way out, because
        // the View reads it synchronously from PropertyChanged; asserting the field afterwards
        // measures the reset, not the announcement.
        AnnouncementCategory? spoken = null;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.StatusText) && vm.StatusText.Length > 0)
                spoken = vm.StatusAnnouncementCategory;
        };

        sync.RaiseRefused([Row("local-1", "Your mail server refused it: over quota.")]);

        Assert.Equal("Your mail server refused it: over quota.", live.SendFailedReason);
        Assert.Contains("could not be uploaded", vm.StatusText, StringComparison.Ordinal);
        // Result, not Status: this is an outcome for the user's mail, and Status is the category a
        // user who turns announcements off turns off first.
        Assert.Equal(AnnouncementCategory.Result, spoken);
    }

    [Fact]
    public void SeveralRefusals_AreCounted()
    {
        var (vm, sync) = Vm(Row("local-1"), Row("local-2"));

        sync.RaiseRefused([Row("local-1", "over quota"), Row("local-2", "over quota")]);

        Assert.Contains("2 drafts could not be uploaded", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void NoRefusals_SayNothing()
    {
        var (vm, sync) = Vm(Row("local-1"));
        vm.StatusText = "Ready";

        sync.RaiseRefused([]);

        Assert.Equal("Ready", vm.StatusText);
    }

    private sealed class RaisingSyncService : ISyncService
    {
#pragma warning disable CS0067 // the rest of the interface is not what this file is about
        public event Action<IReadOnlyList<MailMessageSummary>>? FolderSynced;
        public event Action<IReadOnlyList<MailMessageSummary>>? MessagesRemoved;
        public event Action<int>? DraftsUploaded;
        public event Action<IReadOnlyList<MailMessageSummary>>? FolderReadStatesReconciled;
        public event Action<int>? RulesApplied;
        public event Action<int, int>? SyncProgressChanged;
#pragma warning restore CS0067
        public event Action<IReadOnlyList<MailMessageSummary>>? DraftUploadsRefused;

        public void RaiseRefused(IReadOnlyList<MailMessageSummary> refused)
            => DraftUploadsRefused?.Invoke(refused);

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
}
