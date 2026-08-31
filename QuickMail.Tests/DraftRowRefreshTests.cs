// How the message list re-reads a draft row it has been told changed — issue #637.
//
// The compose window reports WHICH rows it touched; the list re-reads them from the store. These
// pin the three ways that went wrong the first time it was written, each of which looked fine in
// the Drafts folder and misbehaved everywhere else:
//
//   - adding a row rebuilt the whole Messages collection, which nulls the selection and sends focus
//     back to the first row — with a compose window auto-saving in the background, that happened on
//     every interval while the user was reading down the list;
//   - the stored preview was copied raw, so a refreshed draft was the one row in the folder that
//     read its body aloud for a user who has previews turned off;
//   - the outcome sentence was said even when no row had changed, so an auto-save in a background
//     window overwrote the status bar of a folder that had no such row in it.

using System;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class DraftRowRefreshTests
{
    private static readonly Guid AccountId = Guid.Parse("abababab-abab-abab-abab-abababababab");

    private static MailMessageSummary Row(string id, string subject = "Airport thoughts") => new()
    {
        MessageId = id, AccountId = AccountId, FolderName = "Drafts",
        Subject = subject, IsRead = true, IsPendingUpload = true,
        Date = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
        Preview = "Boarding soon, more than a couple of words of body text.",
    };

    private static MainViewModel Vm(StubLocalStoreService store, StubConfigService? config = null,
                                   params MailMessageSummary[] rows)
    {
        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            store, new StubOAuthService(), new StubSyncService(),
            config ?? new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
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
    public async Task AddingARow_KeepsTheCollectionAndTheUsersPlace()
    {
        var sitting = Row("41", "Something else");
        var store = new StubLocalStoreService();
        var vm = Vm(store, null, sitting);
        vm.SelectedMessage = sitting;
        var before = vm.Messages;

        // A draft saved while this folder is open.
        var arrived = Row("local-1");
        store.SeededSummaries[(AccountId, "Drafts")] = [arrived];

        await vm.RefreshDraftRowsAsync([new DraftRowKey(AccountId, "Drafts", "local-1")], null);

        // Same collection instance: replacing it is what nulls SelectedMessage and sends focus
        // back to row one.
        Assert.Same(before, vm.Messages);
        Assert.Same(sitting, vm.SelectedMessage);
        Assert.Contains(vm.Messages, m => m.MessageId == "local-1");
    }

    [Fact]
    public async Task ARefreshedRow_RespectsThePreviewSetting()
    {
        var config = new StubConfigService();
        var cfg = config.Load();
        cfg.PreviewLines = 0;              // previews off
        config.Save(cfg);

        var live = Row("local-1");
        var store = new StubLocalStoreService();
        store.SeededSummaries[(AccountId, "Drafts")] = [Row("local-1", "Airport thoughts, again")];
        var vm = Vm(store, config, live);

        await vm.RefreshDraftRowsAsync([new DraftRowKey(AccountId, "Drafts", "local-1")], null);

        Assert.Equal("Airport thoughts, again", live.Subject);
        // The stored row carries body text; a folder with previews off must not speak it.
        Assert.Equal(string.Empty, live.Preview);
    }

    [Fact]
    public async Task NothingChanged_SaysNothing()
    {
        // A compose window auto-saving in the background raises this for a row that is not in the
        // folder the user is reading. Saying "Draft uploaded." there describes nothing he can see.
        var store = new StubLocalStoreService();
        var vm = Vm(store, null, Row("41"));
        vm.StatusText = "Ready";

        await vm.RefreshDraftRowsAsync(
            [new DraftRowKey(Guid.NewGuid(), "Drafts", "local-99")], "Draft uploaded.");

        Assert.Equal("Ready", vm.StatusText);
    }

    [Fact]
    public async Task SomethingChanged_StillSaysSo()
    {
        var live = Row("local-1");
        var store = new StubLocalStoreService();       // no seeded row: the draft has gone
        var vm = Vm(store, null, live);

        await vm.RefreshDraftRowsAsync(
            [new DraftRowKey(AccountId, "Drafts", "local-1")], "Draft uploaded.");

        Assert.DoesNotContain(live, vm.Messages);
        Assert.Equal("Draft uploaded.", vm.StatusText);
    }

    [Fact]
    public async Task AnEditedSubject_ReachesTheRowAndItsSpokenName()
    {
        // Subject, Date and To are plain properties: assigning them raises nothing, and the row's
        // spoken name is a MultiBinding that only re-evaluates when a path notifies. Copying them
        // silently left the list speaking the previous subject while the model said otherwise.
        var live = Row("local-1");
        var store = new StubLocalStoreService();
        store.SeededSummaries[(AccountId, "Drafts")] = [Row("local-1", "Airport thoughts, again")];
        var vm = Vm(store, null, live);

        var raised = new System.Collections.Generic.List<string?>();
        live.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await vm.RefreshDraftRowsAsync([new DraftRowKey(AccountId, "Drafts", "local-1")], null);

        Assert.Equal("Airport thoughts, again", live.Subject);
        Assert.Contains(nameof(MailMessageSummary.Subject), raised);
    }

    [Fact]
    public async Task ADraftSavedWhileAnotherFolderIsOpen_DoesNotAppearInIt()
    {
        // The gate exists to stop a Drafts row being injected into the Inbox listing.
        var store = new StubLocalStoreService();
        store.SeededSummaries[(AccountId, "Drafts")] = [Row("local-1")];
        var vm = Vm(store, null, Row("41"));
        vm.SelectedFolder = new MailFolderModel
        {
            AccountId = AccountId, FullName = "INBOX", DisplayName = "Inbox",
            Kind = SpecialFolderKind.Inbox,
        };

        await vm.RefreshDraftRowsAsync([new DraftRowKey(AccountId, "Drafts", "local-1")], null);

        Assert.DoesNotContain(vm.Messages, m => m.MessageId == "local-1");
    }
}
