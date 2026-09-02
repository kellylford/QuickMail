using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The Outbox virtual folder (#637): where mail written offline waits, and the one place the user
/// can see, reopen, remove or push it. These pin its placement in the tree, what its rows say, and
/// that Enter and Delete on it go to the queue rather than to any server.
/// </summary>
public class MainViewModelOutboxTests
{
    private static readonly Guid Work = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Home = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static AccountModel Account(Guid id, string label) => new()
    {
        Id = id, AccountName = label, Username = label.ToLowerInvariant() + "@example.com", AuthType = AuthType.OAuth2Microsoft,
    };

    private static OutboxItem Row(Guid account, OutboxKind kind, string subject, int minutesAgo, OutboxState state = OutboxState.Pending) => new()
    {
        Id = OutboxItem.NewId(), AccountId = account, Kind = kind, State = state, Subject = subject,
        To = "to@example.com", CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
    };

    private sealed class Fixture
    {
        public StubOutboxService Outbox { get; } = new();
        public CommandRegistry Registry { get; } = new();
        public MainViewModel Vm { get; }

        public Fixture(bool onlineMode = false, bool withOutbox = true)
        {
            var folders = new Dictionary<Guid, List<MailFolderModel>>
            {
                [Work] = [new MailFolderModel { AccountId = Work, FullName = "INBOX", DisplayName = "Inbox", Kind = SpecialFolderKind.Inbox }],
            };
            Vm = new MainViewModel(
                new FolderedMailService(folders, []), new StubAccountService(), new StubCredentialService(),
                new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
                new StubConfigService(), Registry, new StubViewService(),
                new StubRuleService(), new StubSmtpService(),
                onlineMode: onlineMode,
                outboxService: withOutbox ? Outbox : null);
            Vm.Accounts.Add(Account(Work, "Work"));
            Vm.Accounts.Add(Account(Home, "Home"));
        }

        public async Task ConnectAsync() => await Vm.ConnectAllAccountsAsync();

        public Task SelectOutboxAsync() => Vm.SelectFolderCommand.ExecuteAsync(MainViewModel.OutboxFolder);
    }

    // ── Placement ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheOutboxIsTheLastChildOfTheAllMailGroup()
    {
        var f = new Fixture();
        await f.ConnectAsync();

        var group = f.Vm.FolderTree.First(n => n.IsHeader && n.Label == "All Mail");
        Assert.Equal("Outbox", group.Children.Last().Label);
        Assert.Contains(f.Vm.Folders, x => x.FullName == MainViewModel.OutboxFolder.FullName);
    }

    [Fact]
    public async Task OnlineModeHasNoOutbox()
    {
        var f = new Fixture(onlineMode: true);
        await f.ConnectAsync();

        var group = f.Vm.FolderTree.First(n => n.IsHeader && n.Label == "All Mail");
        Assert.DoesNotContain(group.Children, c => c.Label == "Outbox");
        Assert.DoesNotContain(f.Vm.Folders, x => x.FullName == MainViewModel.OutboxFolder.FullName);
    }

    [Fact]
    public async Task NoOutboxServiceMeansNoOutboxFolder()
    {
        var f = new Fixture(withOutbox: false);
        await f.ConnectAsync();

        var group = f.Vm.FolderTree.First(n => n.IsHeader && n.Label == "All Mail");
        Assert.DoesNotContain(group.Children, c => c.Label == "Outbox");
    }

    [Fact]
    public void TheOutboxIsNeverAStartupFolderOrASavedViewTarget()
    {
        Assert.DoesNotContain(MainViewModel.AllVirtualFolders, v => v.FullName == MainViewModel.OutboxFolder.FullName);
        Assert.Equal('\0', MainViewModel.OutboxFolder.FullName[0]);
        Assert.Equal(SpecialFolderKind.Outbox, MainViewModel.OutboxFolder.Kind);
    }

    [Fact]
    public async Task TheTreeNodeCountsWaitingItems()
    {
        var f = new Fixture();
        f.Outbox.Items.Add(Row(Work, OutboxKind.Send, "one", 1));
        f.Outbox.Items.Add(Row(Home, OutboxKind.Draft, "two", 2));
        await f.ConnectAsync();

        var node = f.Vm.FolderTree.First(n => n.IsHeader && n.Label == "All Mail").Children.Last();
        Assert.Equal("Outbox, 2 waiting", node.AutomationName);

        f.Outbox.Items.Clear();
        f.Outbox.RaiseChanged();
        Assert.Equal("Outbox", node.AutomationName);
    }

    // ── Listing ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SelectingTheOutboxListsTheQueueNewestFirstWithTheStateLeadingTheSubject()
    {
        var f = new Fixture();
        f.Outbox.Items.Add(Row(Work, OutboxKind.Send, "Older", 10));
        f.Outbox.Items.Add(Row(Home, OutboxKind.Draft, "Newer", 1));
        f.Outbox.Items.Add(Row(Work, OutboxKind.Send, "Bounced", 5, OutboxState.Failed));
        f.Outbox.Items[2].LastError = "550 no such user";
        await f.ConnectAsync();

        await f.SelectOutboxAsync();

        Assert.True(f.Vm.IsSelectedFolderOutbox);
        Assert.Equal(
            ["Waiting to upload draft: Newer", "Failed: 550 no such user: Bounced", "Waiting to send: Older"],
            f.Vm.Messages.Select(m => m.Subject));
        Assert.Equal(["Home", "Work", "Work"], f.Vm.Messages.Select(m => m.From));
        Assert.All(f.Vm.Messages, m => Assert.Equal(MainViewModel.OutboxFolder.FullName, m.FolderName));
        Assert.All(f.Vm.Messages, m => Assert.True(m.IsRead));
        Assert.Equal("3 items in Outbox.", f.Vm.StatusText);
        Assert.False(f.Vm.IsBusy);
    }

    [Fact]
    public async Task AnEmptyOutboxSaysSo()
    {
        var f = new Fixture();
        await f.ConnectAsync();

        await f.SelectOutboxAsync();

        Assert.Empty(f.Vm.Messages);
        Assert.Equal("Outbox is empty.", f.Vm.StatusText);
    }

    // ── Open ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnterOnARowReopensItInComposeWithItsOutboxId()
    {
        var f = new Fixture();
        var row = Row(Work, OutboxKind.Send, "Lunch", 1);
        f.Outbox.Items.Add(row);
        f.Outbox.Composes[row.Id] = new ComposeModel { AccountId = Work, Subject = "Lunch", Bcc = "secret@example.com", Mode = ComposeMode.Markdown };
        await f.ConnectAsync();
        await f.SelectOutboxAsync();
        ComposeModel? opened = null;
        f.Vm.ComposeRequested += m => opened = m;

        f.Vm.SelectedMessage = f.Vm.Messages[0];
        await f.Vm.OpenOutboxItemCommand.ExecuteAsync(null);

        Assert.NotNull(opened);
        Assert.Equal(row.Id, opened.OutboxId);
        Assert.Equal("secret@example.com", opened.Bcc);
        Assert.Equal(ComposeMode.Markdown, opened.Mode);
    }

    [Fact]
    public async Task EnterOnAVanishedRowSaysSoAndRelists()
    {
        var f = new Fixture();
        var row = Row(Work, OutboxKind.Send, "Lunch", 1);
        f.Outbox.Items.Add(row);
        await f.ConnectAsync();
        await f.SelectOutboxAsync();
        f.Vm.SelectedMessage = f.Vm.Messages[0];
        f.Outbox.Items.Clear();   // sent by a drain in the meantime; no compose stored

        await f.Vm.OpenOutboxItemCommand.ExecuteAsync(null);

        Assert.Equal("Outbox is empty.", f.Vm.StatusText);
        Assert.Empty(f.Vm.Messages);
    }

    // ── Delete ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteRemovesFromTheQueueAfterAsking()
    {
        var f = new Fixture();
        f.Outbox.Items.Add(Row(Work, OutboxKind.Send, "Keep", 3));
        f.Outbox.Items.Add(Row(Work, OutboxKind.Send, "Drop", 1));
        await f.ConnectAsync();
        await f.SelectOutboxAsync();
        var asked = new List<(string Prompt, string Title)>();
        f.Vm.ConfirmationRequested = (prompt, title) => { asked.Add((prompt, title)); return true; };
        var drop = f.Vm.Messages.First(m => m.Subject.EndsWith("Drop", StringComparison.Ordinal));

        await f.Vm.DeleteMessagesAsync([drop]);

        var ask = Assert.Single(asked);
        Assert.Equal("Remove from Outbox", ask.Title);
        Assert.Contains("has not been sent", ask.Prompt, StringComparison.Ordinal);
        Assert.Equal([drop.MessageId], f.Outbox.Removed);
        Assert.Equal("Waiting to send: Keep", Assert.Single(f.Vm.Messages).Subject);
        Assert.Equal("1 Outbox item removed.", f.Vm.StatusText);
    }

    [Fact]
    public async Task DecliningTheQuestionRemovesNothing()
    {
        var f = new Fixture();
        f.Outbox.Items.Add(Row(Work, OutboxKind.Send, "Keep", 1));
        await f.ConnectAsync();
        await f.SelectOutboxAsync();
        f.Vm.ConfirmationRequested = (_, _) => false;

        await f.Vm.DeleteMessagesAsync([f.Vm.Messages[0]]);

        Assert.Empty(f.Outbox.Removed);
        Assert.Single(f.Vm.Messages);
    }

    // ── Send Outbox Now and drain summaries ──────────────────────────────────────

    [Fact]
    public async Task SendOutboxNowIsRegisteredWithNoDefaultKeyAndForcesADrain()
    {
        var f = new Fixture();
        await f.ConnectAsync();
        var cmd = f.Registry.FindById("mail.sendOutboxNow");
        Assert.NotNull(cmd);
        Assert.Equal("Mail", cmd.Category);
        Assert.Equal(System.Windows.Input.Key.None, cmd.DefaultKey);

        await f.Vm.SendOutboxNowCommand.ExecuteAsync(null);

        Assert.Equal([true], f.Outbox.Flushes);
        Assert.Equal("Outbox is empty.", f.Vm.StatusText);
    }

    [Fact]
    public async Task ADrainIsAnnouncedOnceAsAWhole()
    {
        var f = new Fixture();
        await f.ConnectAsync();

        f.Outbox.RaiseFlushCompleted(new OutboxFlushResult(2, 1, 0, 0));

        Assert.Equal("Outbox: 2 messages sent, 1 draft uploaded.", f.Vm.StatusText);
    }

    [Theory]
    [InlineData(1, 0, 0, "Outbox: 1 message sent.")]
    [InlineData(0, 1, 0, "Outbox: 1 draft uploaded.")]
    [InlineData(2, 3, 0, "Outbox: 2 messages sent, 3 drafts uploaded.")]
    [InlineData(1, 0, 1, "Outbox: 1 message sent. 1 failed. See the Outbox folder.")]
    [InlineData(0, 0, 2, "Outbox: 2 failed. See the Outbox folder.")]
    public void FlushSummaryWording(int sent, int drafts, int failed, string expected)
    {
        Assert.Equal(expected, MainViewModel.SummariseFlush(new OutboxFlushResult(sent, drafts, failed, 0)));
    }

    [Fact]
    public void DisposeUnsubscribesFromTheQueue()
    {
        var f = new Fixture();
        f.Vm.Dispose();
        var before = f.Vm.StatusText;

        f.Outbox.RaiseFlushCompleted(new OutboxFlushResult(1, 0, 0, 0));

        Assert.Equal(before, f.Vm.StatusText);
    }
}
