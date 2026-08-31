// Refusals the user has to actually meet — issue #637.
//
// Three commands refuse while a draft has not reached the server, and all three reported it only on
// the status bar: a channel that is silence for a user running with custom announcements off, and
// which the next sync overwrites regardless. A Move that opens no folder picker and says nothing is
// indistinguishable from a key that does not work. The user chose a dialog for both.
//
// The save path had the same shape in reverse: two early returns refused the save without writing
// the durable notice, so the window then refused to close with nothing said and no focus move —
// Escape appearing to do nothing, for ever.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class DraftRefusalIsMetTests
{
    private static readonly Guid AccountId = Guid.Parse("bcbcbcbc-bcbc-bcbc-bcbc-bcbcbcbcbcbc");

    private static MailMessageSummary Draft(string id = "local-1", string? reason = null) => new()
    {
        MessageId = id, AccountId = AccountId, FolderName = "Drafts",
        Subject = "Airport thoughts", IsRead = true, IsPendingUpload = true,
        SendFailedReason = reason,
    };

    private static MainViewModel Vm()
        => new(new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
               new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
               new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
               new StubRuleService(), new StubSmtpService());

    [Theory]
    [InlineData("move")]
    [InlineData("copy")]
    [InlineData("archive")]
    public void ARefusedCommand_IsShownAndNotJustLogged(string verb)
    {
        var vm = Vm();
        string? shown = null;
        vm.ShowRefusalRequested = m => shown = m;

        Assert.True(vm.RefuseIfAnyHeldOnlyHere([Draft()], verb));

        Assert.NotNull(shown);
        Assert.Contains(verb, shown, StringComparison.Ordinal);
        // The status line still carries it, for anyone who does have announcements on.
        Assert.Equal(shown, vm.StatusText);
    }

    [Fact]
    public void AnOrdinaryMessage_IsNotRefusedAndNothingIsShown()
    {
        var vm = Vm();
        var shown = false;
        vm.ShowRefusalRequested = _ => shown = true;

        Assert.False(vm.RefuseIfAnyHeldOnlyHere([Draft("41")], "move"));
        Assert.False(shown);
    }

    [Fact]
    public void ARefusedDraft_IsNotToldToWaitForAnUploadThatWillNeverHappen()
    {
        var vm = Vm();
        string? shown = null;
        vm.ShowRefusalRequested = m => shown = m;

        vm.RefuseIfAnyHeldOnlyHere([Draft(reason: "Your mail server refused it: over quota.")], "move");

        Assert.NotNull(shown);
        Assert.DoesNotContain("once it has been uploaded", shown, StringComparison.Ordinal);
    }

    // ── The save path's silent refusals ──────────────────────────────────────

    private static ComposeViewModel Compose(ILocalDraftService drafts) => new(
        new StubSmtpService(), new StubAccountService(), new StubCredentialService(),
        new RecordingMailService { AppendDraftThrows = true }, drafts, new StubTemplateService());

    [Fact]
    public async Task SavingWithNoSenderAccount_LeavesAReasonAndAsksForFocus()
    {
        // The window will refuse to close after this, so a status line alone left Escape doing
        // nothing, silently, for ever.
        using var store = new RealDraftStore();
        var vm = Compose(store.Drafts);
        vm.Subject = "Airport thoughts";
        vm.Body = "Boarding soon.";
        var refused = 0;
        vm.SaveRefused += () => refused++;

        await vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.False(vm.LastSaveKeptTheMessage);
        Assert.Contains("no sender account", vm.DeliveryNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, refused);
    }

    [Fact]
    public async Task SavingWithTooManyAttachments_LeavesAReasonAndAsksForFocus()
    {
        using var store = new RealDraftStore();
        await store.SeedDraftsFolderAsync(AccountId);
        var vm = Compose(store.Drafts);
        vm.SenderAccount = new AccountModel
        {
            Id = AccountId, Username = "samuel@interfree.ca", AuthType = AuthType.OAuth2Google,
        };
        vm.Subject = "Airport thoughts";
        vm.Attachments.Add(new AttachmentModel { FileName = "big.bin", FileSize = 26_000_000 });
        var refused = 0;
        vm.SaveRefused += () => refused++;

        await vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.False(vm.LastSaveKeptTheMessage);
        Assert.Contains("25 MB", vm.DeliveryNotice, StringComparison.Ordinal);
        Assert.Equal(1, refused);
    }

    [Fact]
    public async Task ACancelledAutoSave_IsNotReportedAsTheStoreRefusingTheWrite()
    {
        // The window cancels the auto-save token on its way into the close handler, before the user
        // has answered the prompt. A tick landing in that window is not a failure, and saying so put
        // "your latest changes are not saved" into the durable field while the store was healthy.
        using var store = new RealDraftStore();
        await store.SeedDraftsFolderAsync(AccountId);
        var vm = Compose(store.Drafts);
        vm.SenderAccount = new AccountModel
        {
            Id = AccountId, Username = "samuel@interfree.ca", AuthType = AuthType.OAuth2Google,
        };
        vm.To = "someone@example.com";
        vm.Subject = "Airport thoughts";
        vm.Body = "Boarding soon.";

        vm.CancelAutoSave();
        await vm.AutoSaveAsync();

        Assert.Equal(string.Empty, vm.DeliveryNotice);
    }

    [Fact]
    public async Task ResumingAutoSave_ClearsANoticeTheCancelledTicksLeft()
    {
        using var store = new RealDraftStore();
        await store.SeedDraftsFolderAsync(AccountId);
        var vm = Compose(store.Drafts);
        vm.SenderAccount = new AccountModel
        {
            Id = AccountId, Username = "samuel@interfree.ca", AuthType = AuthType.OAuth2Google,
        };
        vm.Subject = "Airport thoughts";

        vm.DeliveryNotice = "Auto-save could not write this message to your computer, so your "
                          + "latest changes are not saved. Keep this window open and try Save Draft.";
        vm.CancelAutoSave();
        vm.ResumeAutoSave();

        Assert.Equal(string.Empty, vm.DeliveryNotice);
    }

    [Fact]
    public async Task ResumingAutoSave_LeavesAServerRefusalAlone()
    {
        // The two share one field, and the server's reason is the one the user is meant to act on.
        using var store = new RealDraftStore();
        await store.SeedDraftsFolderAsync(AccountId);
        var vm = Compose(store.Drafts);
        vm.DeliveryNotice = "This draft was not uploaded. Your mail server refused it: over quota.";
        vm.CancelAutoSave();
        vm.ResumeAutoSave();

        Assert.Contains("over quota", vm.DeliveryNotice, StringComparison.Ordinal);
    }
}
