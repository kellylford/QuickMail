// Deleting a selection that mixes local and server messages — issue #637.
//
// Local-first drafts made this ordinary. A draft written offline lives only in SQLite under a
// "local-…" id no server ever issued, and MergeLocalOnlyMessagesAsync deliberately merges those
// rows into the server's Drafts listing so the user sees one folder rather than two. Select all in
// Drafts and you are very likely holding both kinds at once.
//
// The backend cannot be handed a local id: ImapMailService parses ids as UIDs, so uint.Parse throws
// on the "local-" prefix. Guarding with All(IsLocal) meant one offline draft in the selection sent
// the WHOLE batch to the server, threw, and left nothing deleted — not the server messages, not the
// local one — while the rows had already been removed from the view optimistically. The user was
// told the delete "may not have completed" and everything came back on the next sync.
//
// Move, copy and archive refuse a mixed selection outright, which is why they never had this bug.
// Delete cannot refuse: it is what the user presses to be rid of either kind.

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

public class DeleteMixedLocalAndServerTests
{
    private static readonly Guid AccountId = Guid.Parse("2e2e2e2e-2e2e-2e2e-2e2e-2e2e2e2e2e2e");

    /// <summary>Refuses a local id exactly as the real IMAP backend does, and records the rest.</summary>
    private sealed class UidOnlyMailService : StubImapMailServiceBase
    {
        public List<string> Trashed { get; } = [];

        public override Task MoveToTrashBatchAsync(Guid accountId, string folderName,
            IList<string> messageIds, CancellationToken ct = default)
        {
            foreach (var id in messageIds)
            {
                // What ImapMailService.ToUid's uint.Parse does to "local-abc".
                if (!uint.TryParse(id, out _))
                    throw new FormatException($"The input string '{id}' was not in a correct format.");
                Trashed.Add(id);
            }
            return Task.CompletedTask;
        }
    }

    private static MailMessageSummary Row(string id) => new()
    {
        MessageId  = id,
        AccountId  = AccountId,
        FolderName = "Drafts",
        Subject    = $"Message {id}",
        IsRead     = true,
    };

    private static (MainViewModel vm, StubLocalStoreService store, UidOnlyMailService mail) MakeVm()
    {
        var store = new StubLocalStoreService();
        var mail  = new UidOnlyMailService();
        var vm = new MainViewModel(
            mail, new StubAccountService(), new StubCredentialService(),
            store, new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService());
        // Deleting a draft held only here cannot be undone, so the VM asks first and fails
        // closed. Tests that are not about the prompt answer yes.
        vm.ConfirmLocalDraftDelete = _ => true;
        vm.SelectedFolder = new MailFolderModel
        {
            AccountId = AccountId, FullName = "Drafts", DisplayName = "Drafts",
            Kind = SpecialFolderKind.Drafts,
        };
        return (vm, store, mail);
    }

    [Fact]
    public async Task AMixedSelection_DeletesTheLocalRowsRatherThanHandingThemToTheServer()
    {
        var (vm, store, mail) = MakeVm();
        var local = Row("local-abc");
        var server = Row("41");
        store.SeededSummaries[(AccountId, "Drafts")] = [local, server];
        vm.Messages = new Helpers.BatchObservableCollection<MailMessageSummary>([local, server]);

        await vm.DeleteMessagesAsync([local, server]);

        // The server message really was trashed, and the local one was never offered to a backend
        // that cannot parse its id.
        Assert.Equal(["41"], mail.Trashed);

        // The local row is gone from the store. It was never handed to the backend, so the whole
        // batch no longer throws before anything happens — which is what left BOTH messages behind
        // while the list had already dropped them.
        Assert.DoesNotContain(await store.LoadFolderSummariesAsync(AccountId, "Drafts"),
            m => m.MessageId == "local-abc");
        Assert.DoesNotContain("Delete may not have completed", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAllLocalSelection_StillNeverReachesTheServer()
    {
        var (vm, store, mail) = MakeVm();
        var a = Row("local-a");
        var b = Row("local-b");
        store.SeededSummaries[(AccountId, "Drafts")] = [a, b];
        vm.Messages = new Helpers.BatchObservableCollection<MailMessageSummary>([a, b]);

        await vm.DeleteMessagesAsync([a, b]);

        Assert.Empty(mail.Trashed);
        Assert.Empty(await store.LoadFolderSummariesAsync(AccountId, "Drafts"));
        Assert.DoesNotContain("Delete may not have completed", vm.StatusText, StringComparison.Ordinal);
    }
    [Fact]
    public async Task DeletingADraftHeldOnlyHere_AsksFirst()
    {
        var (vm, store, _) = MakeVm();
        var local = Row("local-abc");
        store.SeededSummaries[(AccountId, "Drafts")] = [local];
        vm.Messages = new Helpers.BatchObservableCollection<MailMessageSummary>([local]);
        string? asked = null;
        vm.ConfirmLocalDraftDelete = m => { asked = m; return false; };

        await vm.DeleteMessagesAsync([local]);

        // Every other Delete in the app means "moved to Trash, still there if you want it back".
        // This one destroys the only copy that has ever existed, and used to say the same words.
        Assert.NotNull(asked);
        Assert.Contains("cannot be undone", asked, StringComparison.Ordinal);
        Assert.Single(await store.LoadFolderSummariesAsync(AccountId, "Drafts"));
    }

    [Fact]
    public async Task DeletingOnlyServerMessages_DoesNotAsk()
    {
        var (vm, store, _) = MakeVm();
        var server = Row("41");
        store.SeededSummaries[(AccountId, "Drafts")] = [server];
        vm.Messages = new Helpers.BatchObservableCollection<MailMessageSummary>([server]);
        var asked = false;
        vm.ConfirmLocalDraftDelete = _ => { asked = true; return true; };

        await vm.DeleteMessagesAsync([server]);

        // Those go to Trash and can be got back; a prompt there would be noise on the common path.
        Assert.False(asked);
    }

    [Fact]
    public async Task WithNoConfirmationWired_ADraftHeldOnlyHereIsNotDeleted()
    {
        var (vm, store, _) = MakeVm();
        var local = Row("local-abc");
        store.SeededSummaries[(AccountId, "Drafts")] = [local];
        vm.Messages = new Helpers.BatchObservableCollection<MailMessageSummary>([local]);
        vm.ConfirmLocalDraftDelete = null;

        await vm.DeleteMessagesAsync([local]);

        // Fails closed, like the account-removal confirmation it mirrors: with no way to get a
        // yes, nothing irreversible happens.
        Assert.Single(await store.LoadFolderSummariesAsync(AccountId, "Drafts"));
    }
}
