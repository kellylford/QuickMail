// The compose window and the message list, end to end, over one real SQLite store — issue #637.
//
// These replaced tests that pinned two events which each DESCRIBED what had happened: one said
// "stored", one said "the row is gone, for this reason". Every defect they produced was the same
// shape — the description and the store disagreed. The list no longer takes anyone's word for it:
// the compose window says which rows it touched and the list re-reads them. So these assert what
// the row says afterwards, rather than what was announced about it.
//
// The wiring is exercised through MainViewModel.AttachComposeViewModel rather than by calling the
// handler, because a deletion probe showed that removing the subscription left the whole suite
// green — the raise side and the handler side were each pinned and nothing joined them.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class ComposeToListWiringTests
{
    private static readonly Guid AccountA = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private static readonly Guid AccountB = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

    private static AccountModel Account(Guid id) =>
        new() { Id = id, Username = $"user-{id:N}@example.com", AuthType = AuthType.OAuth2Google };

    /// <summary>A main view model reading the SAME store the compose window writes to.</summary>
    private static MainViewModel MainVm(RealDraftStore store, params MailMessageSummary[] rows)
    {
        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            store.Store, new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService())
        {
            Messages = new BatchObservableCollection<MailMessageSummary>(rows),
        };
        // A row can only be added back to a view that should be showing it.
        vm.SelectedFolder = MainViewModel.AllDraftsFolder;
        return vm;
    }

    private static ComposeViewModel Compose(RealDraftStore store, IMailService mail, Guid sender) => new(
        new StubSmtpService(), new StubAccountService(), new StubCredentialService(),
        mail, store.Drafts, new StubTemplateService())
    {
        SenderAccount = Account(sender),
        To = "someone@example.com",
        Subject = "Airport thoughts",
        Body = "Boarding soon.",
    };

    private static MailMessageSummary Row(Guid account, string id, string? reason = null) => new()
    {
        MessageId = id, AccountId = account, FolderName = "Drafts",
        Subject = "Airport thoughts", IsPendingUpload = true, IsRead = true,
        SendFailedReason = reason,
    };

    /// <summary>Saves once so the store mints an id, and reports which id that was.</summary>
    private static async Task<string> SeedDraftAsync(ComposeViewModel compose)
    {
        var seen = new List<IReadOnlyList<DraftRowKey>>();
        void Capture(IReadOnlyList<DraftRowKey> keys, string? _) => seen.Add(keys);
        compose.DraftRowsChanged += Capture;
        await compose.SaveDraftCommand.ExecuteAsync(null);
        compose.DraftRowsChanged -= Capture;
        return seen[0][0].MessageId;
    }

    [Fact]
    public async Task SavingARefusedDraftAgain_TakesTheRefusalOffTheRow()
    {
        using var store = new RealDraftStore();
        await store.SeedDraftsFolderAsync(AccountA);
        var compose = Compose(store, new RecordingMailService { AppendDraftThrows = true }, AccountA);
        var id = await SeedDraftAsync(compose);

        // The row the user is looking at, marked refused by an earlier sweep.
        var live = Row(AccountA, id, "Your mail server refused it: over quota.");
        var main = MainVm(store, live);
        main.AttachComposeViewModel(compose);
        Assert.Equal("not uploaded", live.LocationLabel);

        compose.Body = "Boarding now.";
        await compose.SaveDraftCommand.ExecuteAsync(null);
        await main.LastDraftRefresh;

        // Re-read from the store, which cleared the reason when it took the save.
        Assert.Null(live.SendFailedReason);
        Assert.Equal("not on server", live.LocationLabel);
    }

    [Fact]
    public async Task OnceTheUploadTakesIt_TheRowLeavesTheListAndIsAccountedFor()
    {
        using var store = new RealDraftStore();
        await store.SeedDraftsFolderAsync(AccountA);
        // Refuses the server leg for the first subject and accepts the second, so one window
        // produces a local row and then uploads it — the sequence that left a ghost row behind.
        var mail = new RecordingMailService
        {
            AppendDraftFailure = subject =>
                subject == "Airport thoughts" ? new InvalidOperationException("offline") : null,
        };
        var compose = Compose(store, mail, AccountA);
        var id = await SeedDraftAsync(compose);

        var live = Row(AccountA, id);
        var main = MainVm(store, live);
        main.AttachComposeViewModel(compose);

        compose.Subject = "Airport thoughts, sent";
        await compose.SaveDraftCommand.ExecuteAsync(null);
        await main.LastDraftRefresh;

        Assert.DoesNotContain(live, main.Messages);
        Assert.Equal("Draft uploaded.", main.StatusText);
    }

    [Fact]
    public async Task ChangingTheSender_MovesTheRowRatherThanLosingIt()
    {
        // The old row goes AND the new one arrives. Raising only the old key is why a re-keyed
        // draft used to disappear from one account without appearing under the other.
        using var store = new RealDraftStore();
        await store.SeedDraftsFolderAsync(AccountA);
        await store.SeedDraftsFolderAsync(AccountB);
        var compose = Compose(store, new RecordingMailService { AppendDraftThrows = true }, AccountA);
        var id = await SeedDraftAsync(compose);

        var live = Row(AccountA, id);
        var main = MainVm(store, live);
        main.AttachComposeViewModel(compose);

        compose.SenderAccount = Account(AccountB);
        await compose.SaveDraftCommand.ExecuteAsync(null);
        await main.LastDraftRefresh;

        Assert.DoesNotContain(live, main.Messages);
        var moved = Assert.Single(main.Messages);
        Assert.Equal(AccountB, moved.AccountId);
        Assert.Equal("Draft moved to another account.", main.StatusText);
    }

    [Fact]
    public async Task DecliningToKeepADraft_TakesItsRowWithIt_AndSaysNothing()
    {
        using var store = new RealDraftStore();
        await store.SeedDraftsFolderAsync(AccountA);
        var compose = Compose(store, new RecordingMailService { AppendDraftThrows = true }, AccountA);
        var id = await SeedDraftAsync(compose);

        var live = Row(AccountA, id);
        var main = MainVm(store, live);
        main.AttachComposeViewModel(compose);
        main.StatusText = "Ready";

        await compose.DiscardLocalCopyAsync();
        await main.LastDraftRefresh;

        Assert.DoesNotContain(live, main.Messages);
        // The row going IS what the user asked for, so there is nothing to report.
        Assert.Equal("Ready", main.StatusText);
    }

    [Fact]
    public async Task AStoreThatWillNotAnswer_LeavesTheRowAlone()
    {
        // A store that throws is not evidence the row has gone, and removing it on that basis would
        // hide a draft that still exists.
        using var store = new RealDraftStore();
        await store.SeedDraftsFolderAsync(AccountA);
        var compose = Compose(store, new RecordingMailService { AppendDraftThrows = true }, AccountA);
        var id = await SeedDraftAsync(compose);

        var live = Row(AccountA, id);
        var main = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new ThrowingSummaryStore(), new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService())
        {
            Messages = new BatchObservableCollection<MailMessageSummary>([live]),
        };

        await main.RefreshDraftRowsAsync([new DraftRowKey(AccountA, "Drafts", id)], null);

        Assert.Contains(live, main.Messages);
    }

    private sealed class ThrowingSummaryStore : StubLocalStoreService
    {
        public override Task<MailMessageSummary?> LoadSummaryAsync(Guid a, string f, string id)
            => throw new InvalidOperationException("database is locked");
    }
}
