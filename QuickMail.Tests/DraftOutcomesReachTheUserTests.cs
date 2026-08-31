// The four signals the user asked for, on channels that reach him — issue #637.
//
// He runs QuickMail with most custom announcements off, so a signal delivered only through
// AccessibilityHelper.Announce is, for him, silence. Each of these therefore has to land somewhere
// durable: the status bar, or a focusable field. These pin the durable half, which is the half that
// kept being left out.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class DraftOutcomesReachTheUserTests
{
    private static readonly Guid AccountId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private static MainViewModel Vm() => new(
        new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
        new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
        new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
        new StubRuleService(), new StubSmtpService());

    private static MailMessageSummary Row(string id, string? reason = null) => new()
    {
        MessageId = id, AccountId = AccountId, FolderName = "Drafts",
        Subject = $"Message {id}", IsRead = true,
        IsPendingUpload = id.StartsWith("local-", StringComparison.Ordinal),
        SendFailedReason = reason,
    };

    // ── Move and copy refuse BEFORE the folder picker ────────────────────────

    [Fact]
    public void AMoveIsRefusedBeforeThePickerOpens()
    {
        var vm = Vm();

        Assert.True(vm.RefuseIfAnyHeldOnlyHere([Row("local-1"), Row("41")], "move"));
        Assert.Contains("have not reached the server", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedDraftIsNotBlamedOnTheServer()
    {
        // The reason may be QuickMail's own — an unreadable saved copy — so "fix what the server
        // objected to" is both wrong and unfollowable.
        var vm = Vm();

        Assert.True(vm.RefuseIfAnyHeldOnlyHere(
            [Row("local-1", "Its saved copy on this computer could not be read, so there was nothing to upload.")],
            "move"));
        Assert.DoesNotContain("your server", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OrdinaryMessagesAreNotRefused()
    {
        var vm = Vm();
        Assert.False(vm.RefuseIfAnyHeldOnlyHere([Row("41"), Row("42")], "move"));
    }

    // ── Auto-save failing means the message is nowhere ───────────────────────

    [Fact]
    public async Task WhenTheLocalStoreRefusesTheWrite_TheComposeWindowSaysSoDurably()
    {
        // This catch now means the LOCAL store refused, so the message exists nowhere at all. It
        // was announced as Status and written to a TextBlock with no focus stop — nothing that
        // reaches a user with announcements off.
        var vm = new ComposeViewModel(
            new StubSmtpService(), new StubAccountService(), new StubCredentialService(),
            new RecordingMailService { AppendDraftThrows = true },
            new ThrowingDraftService(), new StubTemplateService())
        {
            SenderAccount = new AccountModel { Id = AccountId, Username = "samuel@interfree.ca" },
            To = "someone@example.com",
            Subject = "Airport thoughts",
            Body = "Boarding soon.",
        };

        await vm.AutoSaveAsync();

        // Durable and focusable, and it names the LOCAL write rather than claiming the message
        // exists nowhere -- an earlier save may well have put an older copy on disk, and
        // overstating that is the same fault as calling a busy database a lost message.
        Assert.Contains("could not write this message to your computer", vm.DeliveryNotice,
            StringComparison.Ordinal);
        Assert.DoesNotContain("nowhere", vm.DeliveryNotice, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ThrowingDraftService : ILocalDraftService
    {
        public Task<PendingDraftSave> SaveAsync(AccountModel account, ComposeModel draft, string folderName,
            string? previousMessageId, System.Threading.CancellationToken ct = default)
            => throw new InvalidOperationException("database is locked");
        public Task<string?> ResolveDraftsFolderNameAsync(Guid accountId) => Task.FromResult<string?>("Drafts");
        public Task<ComposeModel?> LoadAsync(Guid a, string f, string id, System.Threading.CancellationToken ct = default)
            => Task.FromResult<ComposeModel?>(null);
        public Task<string?> GetSupersededServerIdAsync(Guid a, string f, string id) => Task.FromResult<string?>(null);
        public Task MarkSendFailedAsync(Guid a, string f, string id, string reason) => Task.CompletedTask;
        public Task DiscardAsync(Guid a, string f, string id) => Task.CompletedTask;
        public Task<IReadOnlyList<MailMessageSummary>> GetPendingAsync(Guid a)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>([]);
        public Task<string> ReadDeliveryNoticeAsync(Guid a, string f, string id) => Task.FromResult(string.Empty);
    }
}

/// <summary>
/// The compose-to-list wiring itself, driven through real saves.
/// <para>A deletion probe showed that removing <c>composeVm.DraftRowDropped += …</c> from
/// MainWindow left every test green: the raise side and the handler side were each pinned, and
/// nothing joined them. The subscription lived in a Window, so only a window test could reach it --
/// and those cannot run here. <see cref="MainViewModel.AttachComposeViewModel"/> makes it reachable
/// without one, and these drive it by saving actual drafts rather than by raising the events.</para>
/// </summary>
public class ComposeToListWiringTests
{
    private static readonly Guid AccountId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    private static AccountModel Account() =>
        new() { Id = AccountId, Username = "samuel@interfree.ca", AuthType = AuthType.OAuth2Google };

    private static MainViewModel MainVm() => new(
        new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
        new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
        new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
        new StubRuleService(), new StubSmtpService())
    {
        Messages = new BatchObservableCollection<MailMessageSummary>(),
    };

    private static MailMessageSummary RowFor(string id, string? reason = null) => new()
    {
        MessageId = id, AccountId = AccountId, FolderName = "Drafts",
        Subject = "Airport thoughts", IsPendingUpload = true, IsRead = true,
        SendFailedReason = reason,
    };

    [Fact]
    public async Task SavingADraftAgain_ClearsTheRefusalOnTheRowThroughTheWiring()
    {
        using var store = new RealDraftStore();
        await store.SeedDraftsFolderAsync(AccountId);
        var main = MainVm();
        // Offline, so every save keeps the draft local and the row survives to be asserted on.
        var compose = new ComposeViewModel(
            new StubSmtpService(), new StubAccountService(), new StubCredentialService(),
            new RecordingMailService { AppendDraftThrows = true }, store.Drafts, new StubTemplateService())
        {
            SenderAccount = Account(),
            To = "someone@example.com",
            Subject = "Airport thoughts",
            Body = "Boarding soon.",
        };

        string? mintedId = null;
        compose.DraftStored += (_, _, id, _) => mintedId = id;
        main.AttachComposeViewModel(compose);

        await compose.SaveDraftCommand.ExecuteAsync(null);
        Assert.NotNull(mintedId);

        // The row the user is looking at, marked refused by an earlier sweep.
        main.Messages.Add(RowFor(mintedId!, "Your mail server refused it: over quota."));
        Assert.Equal("not uploaded", main.Messages[0].LocationLabel);

        compose.Body = "Boarding now.";
        await compose.SaveDraftCommand.ExecuteAsync(null);

        Assert.Null(main.Messages[0].SendFailedReason);
        Assert.Equal("not on server", main.Messages[0].LocationLabel);
    }

    [Fact]
    public async Task OnceTheUploadTakesIt_TheRowLeavesTheListThroughTheWiring()
    {
        using var store = new RealDraftStore();
        await store.SeedDraftsFolderAsync(AccountId);
        var main = MainVm();

        // Fails the server leg for the first subject and accepts the second, so one compose window
        // produces a local row and then uploads it -- the sequence that left a ghost row behind.
        var mail = new RecordingMailService
        {
            AppendDraftFailure = subject =>
                subject == "Airport thoughts" ? new InvalidOperationException("offline") : null,
        };
        var compose = new ComposeViewModel(
            new StubSmtpService(), new StubAccountService(), new StubCredentialService(),
            mail, store.Drafts, new StubTemplateService())
        {
            SenderAccount = Account(),
            To = "someone@example.com",
            Subject = "Airport thoughts",
            Body = "Boarding soon.",
        };

        string? mintedId = null;
        compose.DraftStored += (_, _, id, _) => mintedId = id;
        main.AttachComposeViewModel(compose);

        await compose.SaveDraftCommand.ExecuteAsync(null);
        Assert.NotNull(mintedId);
        main.Messages.Add(RowFor(mintedId!));

        // Now the connection is back.
        compose.Subject = "Airport thoughts, sent";
        await compose.SaveDraftCommand.ExecuteAsync(null);

        Assert.Empty(main.Messages);
    }
}

/// <summary>
/// Which REASON each raiser reports. A deletion probe showed the whole compose-side half of this
/// mechanism was unpinned: the sender re-key could report Uploaded, and both silent raisers could be
/// deleted outright, with 112 tests across 14 draft classes still green. The handler side was
/// pinned; the situation-to-reason mapping was not, and getting that wrong is what told a user
/// offline that a draft had reached a server (#637).
/// </summary>
public class DraftRowDropReasonTests
{
    private static readonly Guid AccountA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AccountB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static AccountModel Account(Guid id) =>
        new() { Id = id, Username = $"user-{id:N}@example.com", AuthType = AuthType.OAuth2Google };

    private static ComposeViewModel Vm(RealDraftStore store, IMailService mail, Guid sender) => new(
        new StubSmtpService(), new StubAccountService(), new StubCredentialService(),
        mail, store.Drafts, new StubTemplateService())
    {
        SenderAccount = Account(sender),
        To = "someone@example.com",
        Subject = "Airport thoughts",
        Body = "Boarding soon.",
    };

    [Fact]
    public async Task AnUploadReportsUploaded()
    {
        using var store = new RealDraftStore();
        await store.SeedDraftsFolderAsync(AccountA);
        var vm = Vm(store, new RecordingMailService(), AccountA);   // server leg succeeds

        var reasons = new List<DraftRowDropReason>();
        vm.DraftRowDropped += (_, _, _, r) => reasons.Add(r);

        await vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.Equal([DraftRowDropReason.Uploaded], reasons);
    }

    [Fact]
    public async Task ASenderChangeReportsAMove_NotAnUpload()
    {
        using var store = new RealDraftStore();
        await store.SeedDraftsFolderAsync(AccountA);
        await store.SeedDraftsFolderAsync(AccountB);
        // Offline throughout, so nothing can possibly have been uploaded.
        var vm = Vm(store, new RecordingMailService { AppendDraftThrows = true }, AccountA);

        var reasons = new List<DraftRowDropReason>();
        vm.DraftRowDropped += (_, _, _, r) => reasons.Add(r);

        await vm.SaveDraftCommand.ExecuteAsync(null);
        Assert.Empty(reasons);

        vm.SenderAccount = Account(AccountB);
        await vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.Equal([DraftRowDropReason.MovedToAnotherAccount], reasons);
    }

    [Fact]
    public async Task TheOldRowIsDroppedOnlyAfterTheReplacementExists()
    {
        // Delete-then-write meant a save that failed after the re-key left the draft in neither
        // account, with the row already gone from the list and the user told it had moved.
        using var store = new RealDraftStore();
        await store.SeedDraftsFolderAsync(AccountA);
        await store.SeedDraftsFolderAsync(AccountB);
        var vm = Vm(store, new RecordingMailService { AppendDraftThrows = true }, AccountA);

        await vm.SaveDraftCommand.ExecuteAsync(null);
        vm.SenderAccount = Account(AccountB);
        await vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.Empty(await store.Store.LoadFolderSummariesAsync(AccountA, "Drafts"));
        Assert.Single(await store.Store.LoadFolderSummariesAsync(AccountB, "Drafts"));
    }

    [Fact]
    public async Task DecliningToKeepADraftReportsADiscard()
    {
        using var store = new RealDraftStore();
        await store.SeedDraftsFolderAsync(AccountA);
        var vm = Vm(store, new RecordingMailService { AppendDraftThrows = true }, AccountA);

        var reasons = new List<DraftRowDropReason>();
        vm.DraftRowDropped += (_, _, _, r) => reasons.Add(r);

        await vm.SaveDraftCommand.ExecuteAsync(null);
        await vm.DiscardLocalCopyAsync();

        Assert.Equal([DraftRowDropReason.Discarded], reasons);
    }
}
