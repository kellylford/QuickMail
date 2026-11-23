using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Send and Receive (#874): F9 sends what is waiting in the Outbox, then checks every connected
/// account's Inbox and says how many new messages arrived.
/// </summary>
public class SendReceiveTests
{
    private static readonly Guid Work = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Home = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static AccountModel Account(Guid id, string label) => new()
    {
        Id = id, AccountName = label, Username = label.ToLowerInvariant() + "@example.com", AuthType = AuthType.OAuth2Microsoft,
    };

    private static MailFolderModel Inbox(Guid account) =>
        new() { AccountId = account, FullName = "INBOX", DisplayName = "Inbox", Kind = SpecialFolderKind.Inbox };

    private static List<MailMessageSummary> Arrivals(Guid account, int n) =>
        Enumerable.Range(1, n).Select(i => new MailMessageSummary
        {
            MessageId = $"{account:N}-{i}", AccountId = account, FolderName = "INBOX", Subject = $"New {i}", Date = DateTimeOffset.UtcNow,
        }).ToList();

    private sealed class Fixture
    {
        public StubOutboxService Outbox { get; } = new();
        public StubSyncService Sync { get; } = new();
        public StubConnectivityService Connectivity { get; } = new();
        public CommandRegistry Registry { get; } = new();
        public MainViewModel Vm { get; }
        public List<(string Text, AnnouncementCategory Category)> Said { get; } = [];

        public Fixture(bool onlineMode = false)
        {
            var folders = new Dictionary<Guid, List<MailFolderModel>>
            {
                [Work] = [Inbox(Work), new MailFolderModel { AccountId = Work, FullName = "Archive", DisplayName = "Archive" }],
                [Home] = [Inbox(Home)],
            };
            Vm = new MainViewModel(
                new FolderedMailService(folders, []), new StubAccountService(), new StubCredentialService(),
                new StubLocalStoreService(), new StubOAuthService(), Sync,
                new StubConfigService(), Registry, new StubViewService(),
                new StubRuleService(), new StubSmtpService(),
                onlineMode: onlineMode,
                outboxService: Outbox,
                connectivity: Connectivity);
            Vm.Accounts.Add(Account(Work, "Work"));
            Vm.Accounts.Add(Account(Home, "Home"));
            Vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.StatusText))
                    Said.Add((Vm.StatusText, Vm.StatusAnnouncementCategory));
            };
        }

        public async Task ReadyAsync()
        {
            await Vm.ConnectAllAccountsAsync();
            lock (Sync.FolderSyncs) Sync.FolderSyncs.Clear();
            Outbox.Flushes.Clear();
            Said.Clear();
        }

        public Task PressF9Async() => Vm.SendAndReceiveCommand.ExecuteAsync(null);
    }

    [Fact]
    public void IsRegisteredOnF9InTheMailCategory()
    {
        var f = new Fixture();
        var cmd = f.Registry.FindById("mail.sendReceive");

        Assert.NotNull(cmd);
        Assert.Equal("Mail", cmd.Category);
        Assert.Equal("Send and Receive", cmd.Title);
        Assert.Equal(Key.F9, cmd.DefaultKey);
        Assert.Equal(ModifierKeys.None, cmd.DefaultModifiers);
        Assert.Same(cmd, f.Registry.FindByGesture(Key.F9, ModifierKeys.None));
        // The refresh command keeps its id and key.
        Assert.Equal(Key.F5, f.Registry.FindById("mail.refresh")?.DefaultKey);
    }

    [Fact]
    public async Task ChecksEveryInbox_AndSaysThereIsNothingNew()
    {
        var f = new Fixture();
        await f.ReadyAsync();

        await f.PressF9Async();

        Assert.Equal(
            [("full", Home, "INBOX"), ("full", Work, "INBOX")],
            f.Sync.FolderSyncs.OrderBy(s => s.AccountId));
        Assert.Equal(("No new messages.", AnnouncementCategory.Result), f.Said[^1]);
    }

    [Fact]
    public async Task AnEmptyOutbox_IsNotDrainedOrMentioned()
    {
        var f = new Fixture();
        await f.ReadyAsync();

        await f.PressF9Async();

        Assert.Empty(f.Outbox.Flushes);
        Assert.DoesNotContain(f.Said, s => s.Text.Contains("Outbox", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WaitingMail_IsSentFirst_WithAForcedDrain()
    {
        var f = new Fixture();
        f.Outbox.Items.Add(new OutboxItem
        {
            Id = OutboxItem.NewId(), AccountId = Work, Kind = OutboxKind.Send, Subject = "Lunch", To = "a@example.com",
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        await f.ReadyAsync();
        var syncsWhenFlushed = -1;
        f.Outbox.NextFlushResult = new OutboxFlushResult(1, 0, 0, 0);
        f.Outbox.RaiseFlushCompletedDuringFlush = true;
        f.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.StatusText) && f.Vm.StatusText.StartsWith("Outbox:", StringComparison.Ordinal))
                syncsWhenFlushed = f.Sync.FolderSyncs.Count;
        };

        await f.PressF9Async();

        Assert.Equal([true], f.Outbox.Flushes);
        Assert.Equal(0, syncsWhenFlushed);   // sent before anything was received
        Assert.Contains(("Outbox: 1 message sent.", AnnouncementCategory.Result), f.Said);
        Assert.Equal("No new messages.", f.Said[^1].Text);
    }

    [Fact]
    public async Task CountsNewMailAcrossAccounts()
    {
        var f = new Fixture();
        await f.ReadyAsync();
        f.Sync.FullSyncArrivals = (account, _) => Arrivals(account.Id, account.Id == Work ? 2 : 1);

        await f.PressF9Async();

        Assert.Equal(("3 new messages.", AnnouncementCategory.Result), f.Said[^1]);
    }

    [Fact]
    public async Task AnAccountThatCannotBeReached_IsNamed()
    {
        var f = new Fixture();
        await f.ReadyAsync();
        f.Sync.FullSyncArrivals = (account, _) =>
            account.Id == Home ? throw new InvalidOperationException("no route") : Arrivals(account.Id, 1);

        await f.PressF9Async();

        Assert.Equal("1 new message. Could not check Home.", f.Said[^1].Text);
    }

    [Fact]
    public async Task Offline_SaysSoWithoutAskingTheServer()
    {
        var f = new Fixture();
        await f.ReadyAsync();
        f.Connectivity.IsOnline = false;

        await f.PressF9Async();

        Assert.Empty(f.Sync.FolderSyncs);
        Assert.Equal(AnnouncementCategory.Result, f.Said[^1].Category);
        Assert.StartsWith("You're offline.", f.Said[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlineMode_UsesTheOnlineFetch_AndGivesNoCount()
    {
        var f = new Fixture(onlineMode: true);
        await f.ReadyAsync();

        await f.PressF9Async();

        Assert.All(f.Sync.FolderSyncs, s => Assert.Equal("online", s.Kind));
        Assert.Equal(2, f.Sync.FolderSyncs.Count);
        Assert.Equal("Checked for new mail.", f.Said[^1].Text);
    }

    [Fact]
    public async Task WithNoAccounts_SaysSo()
    {
        var f = new Fixture();
        f.Vm.Accounts.Clear();

        await f.PressF9Async();

        Assert.Equal(("There are no accounts to send or receive mail for.", AnnouncementCategory.Result), f.Said[^1]);
    }

    [Theory]
    [InlineData(2, 0, "", "No new messages.")]
    [InlineData(2, 1, "", "1 new message.")]
    [InlineData(2, 5, "", "5 new messages.")]
    [InlineData(1, 0, "Home", "No new messages. Could not check Home.")]
    [InlineData(1, 0, "Home,Shared", "No new messages. Could not check Home, Shared.")]
    [InlineData(0, 0, "Home", "Could not check Home for new mail.")]
    [InlineData(0, 0, "Home,Work", "Could not check any account for new mail.")]
    public void SummaryWording(int checkedCount, int newCount, string unreachable, string expected)
    {
        string[] names = unreachable.Length == 0 ? [] : unreachable.Split(',');
        Assert.Equal(expected, MainViewModel.SendReceiveSummary(checkedCount, newCount, names));
    }

    [Fact]
    public void SummaryWording_OnlineMode() =>
        Assert.Equal("Checked for new mail.", MainViewModel.SendReceiveSummary(2, null, []));
}
