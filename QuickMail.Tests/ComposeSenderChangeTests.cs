// Changing the From account on a draft — issue #637.
//
// The store keys draft rows on (id, account, folder), so a sender change has to re-key the row. The
// half that kept regressing is the SERVER id, and it is the half that destroys data.
//
// AppendDraftAsync does not carry an account of its own: it resolves the Drafts folder of whatever
// account it is handed and, when given a replace id, does AddFlags(Deleted) + Expunge on that UID
// there. So a server id that belonged to account A, carried into a save under account B, expunges
// whatever happens to hold that UID in B's Drafts. UIDs are small per-folder integers, so this is an
// ordinary collision rather than an exotic one, and an expunge is not a move to Trash.
//
// A previous round fixed this and the fix did not survive: the assignment sat below a guard that the
// server-id case returns at, so only the local-row case was ever covered. Nothing pinned it, which
// is why it went unnoticed — hence this file.

using System;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class ComposeSenderChangeTests
{
    private static readonly Guid AccountA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid AccountB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static AccountModel Account(Guid id, string user) =>
        new() { Id = id, Username = user, AuthType = AuthType.OAuth2Google };

    [Fact]
    public async Task ChangingTheSenderAfterAnOnlineSave_DoesNotCarryTheServerIdIntoTheNewAccount()
    {
        using var store = new RealDraftStore();
        await store.SeedDraftsFolderAsync(AccountA);
        await store.SeedDraftsFolderAsync(AccountB);

        var mail = new RecordingMailService();
        var vm = new ComposeViewModel(
            new StubSmtpService(), new StubAccountService(), new StubCredentialService(),
            mail, store.Drafts, new StubTemplateService())
        {
            SenderAccount = Account(AccountA, "samuel@interfree.ca"),
            To = "someone@example.com",
            Subject = "Airport thoughts",
            Body = "Boarding soon.",
        };

        // Online, so the server leg succeeds and the draft now has a real id in A's Drafts.
        await vm.SaveDraftCommand.ExecuteAsync(null);
        Assert.Equal(AccountA, Assert.Single(mail.Appends).AccountId);

        vm.SenderAccount = Account(AccountB, "samuel@quicksilver.example");
        await vm.SaveDraftCommand.ExecuteAsync(null);

        var second = mail.Appends[1];
        Assert.Equal(AccountB, second.AccountId);
        // The whole point: no id from A's mailbox is handed to a save against B's.
        Assert.Null(second.ReplaceId);
    }

    [Fact]
    public async Task ChangingTheSenderOfAnOfflineDraft_LeavesNoRowBehindInTheOldAccount()
    {
        using var store = new RealDraftStore();
        await store.SeedDraftsFolderAsync(AccountA);
        await store.SeedDraftsFolderAsync(AccountB);

        var mail = new RecordingMailService { AppendDraftThrows = true };   // offline
        var vm = new ComposeViewModel(
            new StubSmtpService(), new StubAccountService(), new StubCredentialService(),
            mail, store.Drafts, new StubTemplateService())
        {
            SenderAccount = Account(AccountA, "samuel@interfree.ca"),
            To = "someone@example.com",
            Subject = "Airport thoughts",
            Body = "Boarding soon.",
        };

        await vm.SaveDraftCommand.ExecuteAsync(null);
        Assert.Single(await store.Store.LoadFolderSummariesAsync(AccountA, "Drafts"));

        vm.SenderAccount = Account(AccountB, "samuel@quicksilver.example");
        await vm.SaveDraftCommand.ExecuteAsync(null);

        // One draft, in one place. Leaving the old row behind meant it was still uploaded later,
        // into the mailbox the user had just moved the message away from.
        Assert.Empty(await store.Store.LoadFolderSummariesAsync(AccountA, "Drafts"));
        Assert.Single(await store.Store.LoadFolderSummariesAsync(AccountB, "Drafts"));
    }
}
