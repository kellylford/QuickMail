// Answering "No" to the save-on-close prompt — issue #637.
//
// Local-first saving changed what "No" has to mean. Auto-save has very likely already written the
// message to disk by the time the window closes, so "no, do not save this" now needs QuickMail to
// go and remove that copy — otherwise the sweep uploads to the user's mailbox a message they
// explicitly declined to keep.
//
// The trap is that the same keystroke reaches two completely different situations. A window that
// STARTED a message owns what it wrote and may drop it. A window that OPENED an existing draft owns
// nothing: "no, do not save these changes" is not "delete the draft I opened", and treating them
// alike destroyed the draft, its attachments and its stored bytes with no prompt and no copy in
// Trash. These run against a real store for the reason in RealDraftStore.

using System;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class ComposeDiscardOnCloseTests
{
    private static readonly Guid AccountId = Guid.Parse("1d1d1d1d-1d1d-1d1d-1d1d-1d1d1d1d1d1d");

    private static AccountModel Account() => new()
    {
        Id = AccountId,
        Username = "samuel@interfree.ca",
        AuthType = AuthType.OAuth2Google,
    };

    /// <summary>Offline, so the local copy is the only copy and survives to be asserted on.</summary>
    private static ComposeViewModel MakeVm(RealDraftStore store) => new(
        new StubSmtpService(), new StubAccountService(), new StubCredentialService(),
        new RecordingMailService { AppendDraftThrows = true }, store.Drafts, new StubTemplateService());

    private static void Ready(ComposeViewModel vm)
    {
        vm.SenderAccount = Account();
        vm.To = "someone@example.com";
        vm.Subject = "Airport thoughts";
        vm.Body = "Boarding soon.";
    }

    [Fact]
    public async Task AMessageThisWindowStarted_IsDroppedWhenTheUserDeclinesToSaveIt()
    {
        using var store = new RealDraftStore();
        await store.SeedDraftsFolderAsync(AccountId);
        var vm = MakeVm(store);
        Ready(vm);

        // What auto-save does a few seconds into typing.
        await vm.SaveDraftCommand.ExecuteAsync(null);
        Assert.Single(await store.Store.LoadFolderSummariesAsync(AccountId, "Drafts"));

        await vm.DiscardLocalCopyAsync();

        // Declining to keep a message means it is not kept — and not quietly uploaded later by the
        // sweep, which is what made this necessary at all.
        Assert.Empty(await store.Store.LoadFolderSummariesAsync(AccountId, "Drafts"));
    }

    [Fact]
    public async Task ADraftThisWindowOnlyOPENED_SurvivesTheUserDecliningToSaveTheirEdits()
    {
        using var store = new RealDraftStore();
        await store.SeedDraftsFolderAsync(AccountId);

        // An offline draft that already exists — written by an earlier window, or an earlier run.
        var existing = await store.Drafts.SaveAsync(
            Account(), new ComposeModel { To = "someone@example.com", Subject = "Airport thoughts", Body = "Boarding soon." },
            "Drafts", null);

        var vm = MakeVm(store);
        vm.Seed(new ComposeModel
        {
            AccountId       = AccountId,
            DraftMessageId  = existing.MessageId,
            DraftFolderName = "Drafts",
            To = "someone@example.com", Subject = "Airport thoughts", Body = "Boarding soon.",
        });
        vm.SenderAccount = Account();
        vm.Body = "Boarding soon. One more thing.";   // the edit the user is about to decline

        await vm.DiscardLocalCopyAsync();

        // The draft the user opened is still there. Discarding it destroyed a message the user had
        // written earlier, with its attachments, because they answered a question about their
        // unsaved CHANGES — no prompt, no Trash copy, nothing to recover from.
        var kept = Assert.Single(await store.Store.LoadFolderSummariesAsync(AccountId, "Drafts"));
        Assert.Equal(existing.MessageId, kept.MessageId);
    }

    [Fact]
    public async Task ADraftOpenedThenSentFromADifferentAccount_IsStillNotThisWindowsToDrop()
    {
        using var store = new RealDraftStore();
        var otherId = Guid.Parse("1e1e1e1e-1e1e-1e1e-1e1e-1e1e1e1e1e1e");
        await store.SeedDraftsFolderAsync(AccountId);
        await store.SeedDraftsFolderAsync(otherId);
        var existing = await store.Drafts.SaveAsync(
            Account(), new ComposeModel { To = "a@example.com", Subject = "Kept", Body = "Body." },
            "Drafts", null);

        var vm = MakeVm(store);
        vm.Seed(new ComposeModel
        {
            AccountId       = AccountId,
            DraftMessageId  = existing.MessageId,
            DraftFolderName = "Drafts",
            To = "a@example.com", Subject = "Kept", Body = "Body.",
        });
        vm.SenderAccount = Account();

        // The user changes the From account, and auto-save ticks. The re-key deletes the old row
        // and nulls the id, so "did a row exist before this save?" reads FALSE — which promoted an
        // opened draft to this window's own creation and put it right back in reach of the
        // discard, by the one route the flag exists to block.
        vm.SenderAccount = new AccountModel
        {
            Id = otherId, Username = "other@example.com", AuthType = AuthType.OAuth2Google,
        };
        vm.Body = "Body. Edited.";
        await vm.SaveDraftCommand.ExecuteAsync(null);

        await vm.DiscardLocalCopyAsync();

        // The message the user opened still exists somewhere they can get it back from.
        var all = (await store.Store.LoadFolderSummariesAsync(AccountId, "Drafts")).Count
                + (await store.Store.LoadFolderSummariesAsync(otherId, "Drafts")).Count;
        Assert.Equal(1, all);
    }

    [Fact]
    public async Task ADraftOpenedThenSavedAgain_IsStillNotThisWindowsToDrop()
    {
        using var store = new RealDraftStore();
        await store.SeedDraftsFolderAsync(AccountId);
        var existing = await store.Drafts.SaveAsync(
            Account(), new ComposeModel { To = "a@example.com", Subject = "Kept", Body = "Body." },
            "Drafts", null);

        var vm = MakeVm(store);
        vm.Seed(new ComposeModel
        {
            AccountId       = AccountId,
            DraftMessageId  = existing.MessageId,
            DraftFolderName = "Drafts",
            To = "a@example.com", Subject = "Kept", Body = "Body.",
        });
        vm.SenderAccount = Account();
        vm.Body = "Body. Edited.";

        // Auto-save ticks while the window is open. Reading "did a row exist before this save?"
        // from a local variable that is null on entry to every call made the SECOND save look like
        // this window's own creation, which put the draft right back in reach of the discard.
        await vm.SaveDraftCommand.ExecuteAsync(null);
        await vm.DiscardLocalCopyAsync();

        Assert.Single(await store.Store.LoadFolderSummariesAsync(AccountId, "Drafts"));
    }
}
