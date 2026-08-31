// Round ten's findings — issue #637.
//
// Each of these pins something a reviewer found by reading code that had just been written to fix
// the round before it. They are grouped because they share one cause: the refresh path mutates a
// list other people are standing in, and every shortcut taken to make that cheap has turned out to
// cost something that only shows up on the second save, in another folder, or in another window.

using System;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class DraftRowRefreshOrderingTests
{
    private static readonly Guid AccountId = Guid.Parse("cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd");
    private static readonly DateTimeOffset Noon =
        new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    private static MailMessageSummary Row(string id, DateTimeOffset when, string subject = "Draft") => new()
    {
        MessageId = id, AccountId = AccountId, FolderName = "Drafts",
        Subject = subject, IsRead = true, IsPendingUpload = true, Date = when,
    };

    private static MainViewModel Vm(StubLocalStoreService store, params MailMessageSummary[] rows)
    {
        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            store, new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService())
        {
            Messages = new BatchObservableCollection<MailMessageSummary>(rows),
        };
        vm.SelectedFolder = new MailFolderModel
        {
            AccountId = AccountId, FullName = "Drafts", DisplayName = "Drafts",
            Kind = SpecialFolderKind.Drafts,
        };
        return vm;
    }

    [Fact]
    public async Task ARowReSavedOnce_DoesNotStopLaterDraftsFromAppearing()
    {
        // The first attempt at protecting the sort order latched a flag on any in-place date change
        // and refused every later insert until the list was rebuilt. Since an auto-save changes the
        // date, one tick meant no draft appeared live in that folder again — the exact defect this
        // path exists to fix, and silently, because the suppressed insert also suppressed the
        // outcome sentence.
        var existing = Row("local-1", Noon);
        var store = new StubLocalStoreService();
        var vm = Vm(store, existing);

        // First: an in-place re-save that moves the row's date.
        store.SeededSummaries[(AccountId, "Drafts")] = [Row("local-1", Noon.AddHours(1))];
        await vm.RefreshDraftRowsAsync([new DraftRowKey(AccountId, "Drafts", "local-1")], null);

        // Then: a brand new draft saved into the same open folder.
        store.SeededSummaries[(AccountId, "Drafts")] =
            [Row("local-1", Noon.AddHours(1)), Row("local-2", Noon.AddHours(2), "Second")];
        await vm.RefreshDraftRowsAsync([new DraftRowKey(AccountId, "Drafts", "local-2")], null);

        Assert.Contains(vm.Messages, m => m.MessageId == "local-2");
    }

    [Fact]
    public async Task ANewRowIsPlacedAboveTheFirstOlderRow_EvenWhenTheListIsNoLongerSorted()
    {
        // Re-saving a draft updates its date in place and deliberately does not move the row, so the
        // list can be out of newest-first order. NEITHER a scan nor a binary search can be
        // "correct" against a disordered list -- the guarantee is only that the scan is
        // deterministic and bounded: the row lands immediately above the first row older than it.
        // A binary search over the same list can land anywhere, which is why this path stopped
        // using one.
        var newer = Row("local-2", Noon, "Newer");
        var older = Row("local-1", Noon.AddHours(-2), "Older");
        var store = new StubLocalStoreService();
        var vm = Vm(store, newer, older);          // correctly ordered to begin with

        // Disorder it the way an auto-save does: the row at the BOTTOM becomes the newest, and
        // deliberately stays where it is.
        store.SeededSummaries[(AccountId, "Drafts")] = [Row("local-1", Noon.AddHours(3), "Older")];
        await vm.RefreshDraftRowsAsync([new DraftRowKey(AccountId, "Drafts", "local-1")], null);
        Assert.Equal(["local-2", "local-1"], vm.Messages.Select(m => m.MessageId));

        // A new draft, newer than the row at the top and older than the one below it.
        store.SeededSummaries[(AccountId, "Drafts")] =
            [Row("local-1", Noon.AddHours(3), "Older"), Row("local-3", Noon.AddHours(1), "Middle")];
        await vm.RefreshDraftRowsAsync([new DraftRowKey(AccountId, "Drafts", "local-3")], null);

        // Above the first row older than it, and nothing already in the list moved.
        Assert.Equal(["local-3", "local-2", "local-1"], vm.Messages.Select(m => m.MessageId));
    }

    [Fact]
    public async Task AFolderChangedDuringTheStoreRead_LeavesTheNewFolderAlone()
    {
        // The race, not a guard in front of it. RefreshDraftRowsAsync decides whether a row
        // belongs, awaits the store, then mutates the list -- so a folder change landing IN that
        // await must not drop a Drafts row into whatever folder the user just opened. What makes
        // that safe is that IsViewingDraftFolder is itself asked AFTER the read returns, so it sees
        // the new folder. This pins that ordering: move the check earlier and it fails.
        var store = new SwitchesFolderMidRead { Row = Row("local-1", Noon) };
        var vm = Vm(store);
        store.Switch = () => vm.SelectedFolder = new MailFolderModel
        {
            AccountId = AccountId, FullName = "INBOX", DisplayName = "Inbox",
            Kind = SpecialFolderKind.Inbox,
        };

        await vm.RefreshDraftRowsAsync([new DraftRowKey(AccountId, "Drafts", "local-1")], null);

        Assert.DoesNotContain(vm.Messages, m => m.MessageId == "local-1");
    }

    private sealed class SwitchesFolderMidRead : StubLocalStoreService
    {
        public MailMessageSummary? Row { get; set; }
        public Action? Switch { get; set; }

        public override Task<MailMessageSummary?> LoadSummaryAsync(Guid a, string f, string id)
        {
            Switch?.Invoke();          // the user opens another folder while the read is in flight
            return Task.FromResult(Row);
        }
    }
}
