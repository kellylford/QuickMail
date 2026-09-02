using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Imap;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// What the user sees and hears when the app is offline (#637): cached mail with offline wording
/// instead of raw socket errors, no "Loading…" that never resolves, one "Offline" announcement and
/// one "Back online.", and the two cache-then-server paths falling through to the server when the
/// store itself throws (the two-scope fetch pattern in docs/ARCHITECTURE.md).
/// </summary>
public class MainViewModelOfflineStatusTests
{
    private static readonly Guid Work = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static MailFolderModel Folder(string name, SpecialFolderKind kind = SpecialFolderKind.None) => new()
    {
        AccountId = Work, FullName = name, DisplayName = name, Kind = kind,
    };

    private static MailMessageSummary Msg(string folder, string id) => new()
    {
        MessageId = id, AccountId = Work, FolderName = folder, From = "someone@example.com",
        Subject = $"Subject {id}", Date = DateTimeOffset.Now,
    };

    /// <summary>A mail service whose every server call fails the way a dead network does.</summary>
    private sealed class UnreachableMail : StubImapMailServiceBase
    {
        public Exception Failure { get; set; } = new SocketException((int)SocketError.HostUnreachable);
        public int DetailCalls;
        public MailMessageDetail? DetailToReturn { get; set; }

        public override Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(Guid accountId, string folderName, DateTime since, CancellationToken ct = default)
            => Task.FromException<List<MailMessageSummary>>(Failure);
        public override Task<MailMessageDetail> GetMessageDetailAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref DetailCalls);
            return DetailToReturn != null ? Task.FromResult(DetailToReturn) : Task.FromException<MailMessageDetail>(Failure);
        }
    }

    /// <summary>A store whose detail read throws, as every call does in --online mode.</summary>
    private sealed class DetailThrowingStore : StubLocalStoreService
    {
        public override Task<MailMessageDetail?> LoadDetailAsync(Guid accountId, string folderName, string messageId)
            => throw new InvalidOperationException("no such table: MessageDetail");
    }

    private sealed class Fixture
    {
        public StubConnectivityService Connectivity { get; } = new();
        public UnreachableMail Mail { get; } = new();
        public StubLocalStoreService Store { get; }
        public MainViewModel Vm { get; }
        public StatusAnnouncementRecorder Status { get; }
        public AccountModel Account { get; } = new()
        {
            Id = Work, AccountName = "Work", Username = "work@example.com", AuthType = AuthType.OAuth2Microsoft,
        };

        public Fixture(StubLocalStoreService? store = null, bool online = false)
        {
            Store = store ?? new StubLocalStoreService();
            Store.SeededFolders[Work] = [Folder("INBOX", SpecialFolderKind.Inbox), Folder("Projects")];
            Connectivity.IsOnline = online;
            Vm = new MainViewModel(
                Mail, new StubAccountService(), new StubCredentialService(),
                Store, new StubOAuthService(), new StubSyncService(),
                new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
                new StubRuleService(), new StubSmtpService(),
                connectivity: Connectivity);
            Vm.Accounts.Add(Account);
            Status = StatusAnnouncementRecorder.Watch(Vm);
        }

        public Task SelectAsync(string folder)
            => Vm.SelectFolderCommand.ExecuteAsync(Store.SeededFolders[Work].First(f => f.FullName == folder));
    }

    // ── Folders ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OfflineFolderWithCachedMailShowsTheCacheAndNeverAsksTheServer()
    {
        var f = new Fixture();
        f.Store.SeededSummaries[(Work, "Projects")] = [Msg("Projects", "p1")];
        await f.Vm.InitialLoadAsync();

        await f.SelectAsync("Projects");

        Assert.Single(f.Vm.Messages);
        Assert.Equal("Offline — showing 1 cached message.", f.Vm.StatusText);
        Assert.False(f.Vm.IsBusy);
    }

    [Fact]
    public async Task OfflineFolderWithNothingCachedSaysSoInsteadOfLoadingForever()
    {
        var f = new Fixture();
        await f.Vm.InitialLoadAsync();

        await f.SelectAsync("Projects");

        Assert.Empty(f.Vm.Messages);
        Assert.Equal("Offline — no cached messages in Projects.", f.Vm.StatusText);
        Assert.False(f.Vm.IsBusy);
    }

    [Fact]
    public async Task OnlineButUnreachableFolderLoadReportsOfflineWordingAndFeedsTheService()
    {
        var f = new Fixture(online: true);
        f.Store.SeededSummaries[(Work, "Projects")] = [Msg("Projects", "p1"), Msg("Projects", "p2")];
        await f.Vm.InitialLoadAsync();

        await f.SelectAsync("Projects");
        // The server refresh is fire-and-forget; give its (synchronous) failure a moment to land.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!f.Vm.StatusText.StartsWith("Offline", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.Equal("Offline — showing 2 cached messages.", f.Vm.StatusText);
        Assert.Contains((Work, "folder-load-failed", false), f.Connectivity.Notes);
        Assert.False(f.Vm.IsBusy);
    }

    [Fact]
    public async Task AServerThatAnsweredIsStillAnError()
    {
        var f = new Fixture(online: true);
        f.Mail.Failure = new ImapCommandException(ImapCommandResponse.No, "NO [ALERT] denied");
        await f.Vm.InitialLoadAsync();

        await f.SelectAsync("Projects");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!f.Vm.StatusText.StartsWith("Failed", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.StartsWith("Failed to load messages:", f.Vm.StatusText, StringComparison.Ordinal);
        Assert.Contains((Work, "folder-load-failed", true), f.Connectivity.Notes);
    }

    // ── Messages ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnUncachedMessageIsNotAvailableOffline()
    {
        var f = new Fixture();
        f.Store.SeededSummaries[(Work, "Projects")] = [Msg("Projects", "p1")];
        await f.Vm.InitialLoadAsync();
        await f.SelectAsync("Projects");

        await f.Vm.SelectMessageCommand.ExecuteAsync(f.Vm.Messages[0]);

        Assert.Equal("This message is not available offline.", f.Vm.StatusText);
        Assert.Null(f.Vm.MessageDetail);
        Assert.False(f.Vm.IsBusy);
        Assert.Contains((Work, "message-load-failed", false), f.Connectivity.Notes);
    }

    [Fact]
    public async Task ACachedMessageOpensOffline()
    {
        var f = new Fixture();
        f.Store.SeededSummaries[(Work, "Projects")] = [Msg("Projects", "p1")];
        f.Store.SeededDetail = new MailMessageDetail { MessageId = "p1", AccountId = Work, FolderName = "Projects", From = "a <a@example.com>", PlainTextBody = "hi" };
        await f.Vm.InitialLoadAsync();
        await f.SelectAsync("Projects");

        await f.Vm.SelectMessageCommand.ExecuteAsync(f.Vm.Messages[0]);

        Assert.NotNull(f.Vm.MessageDetail);
        Assert.Equal(0, f.Mail.DetailCalls);
    }

    [Fact]
    public async Task AStoreThatThrowsStillFallsThroughToTheServer()
    {
        // The two-scope fetch pattern: LoadDetailAsync throwing (--online mode) used to skip the
        // server fallback entirely because both calls sat in one try.
        var f = new Fixture(store: new DetailThrowingStore(), online: true);
        f.Store.SeededSummaries[(Work, "Projects")] = [Msg("Projects", "p1")];
        f.Mail.DetailToReturn = new MailMessageDetail { MessageId = "p1", AccountId = Work, FolderName = "Projects", From = "a <a@example.com>", PlainTextBody = "from server" };
        await f.Vm.InitialLoadAsync();
        await f.SelectAsync("Projects");

        await f.Vm.SelectMessageCommand.ExecuteAsync(f.Vm.Messages[0]);

        Assert.NotNull(f.Vm.MessageDetail);
        Assert.Equal("from server", f.Vm.MessageDetail.PlainTextBody);
        Assert.Equal(1, f.Mail.DetailCalls);
        Assert.Contains((Work, "message-loaded", true), f.Connectivity.Notes);
    }

    [Fact]
    public async Task ReplyOnAnUncachedMessageSaysWhyNothingOpened()
    {
        var f = new Fixture();
        f.Store.SeededSummaries[(Work, "Projects")] = [Msg("Projects", "p1")];
        await f.Vm.InitialLoadAsync();
        await f.SelectAsync("Projects");
        f.Vm.SelectedMessage = f.Vm.Messages[0];
        var opened = 0;
        f.Vm.ComposeRequested += _ => opened++;

        await f.Vm.ReplyCommand.ExecuteAsync(null);

        Assert.Equal(0, opened);
        Assert.Equal("This message is not available offline.", f.Vm.StatusText);
    }

    // ── Announcements and the status label ───────────────────────────────────────

    [Fact]
    public async Task LaunchingOfflineSaysSoOnceBeforeAnythingElse()
    {
        var f = new Fixture();
        f.Store.SeededSummaries[(Work, "INBOX")] = [Msg("INBOX", "i1"), Msg("INBOX", "i2")];

        await f.Vm.InitialLoadAsync();

        Assert.Equal("Offline", f.Vm.ConnectionStatusText);
        Assert.Equal("2 messages (cached — offline)", f.Vm.StatusText);
        Assert.Contains(("Offline. Showing cached messages.", AnnouncementCategory.Status), f.Status.Announced);
        Assert.Equal(1, f.Status.Announced.Count(a => a.Text.StartsWith("Offline.", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task GoingOfflineAndBackIsAnnouncedOnceEachWay()
    {
        var f = new Fixture(online: true);
        await f.Vm.InitialLoadAsync();
        Assert.DoesNotContain(f.Status.Announced, a => a.Text.StartsWith("Offline", StringComparison.Ordinal));

        f.Connectivity.RaiseOnlineChanged(false);
        f.Connectivity.RaiseOnlineChanged(false);
        f.Connectivity.RaiseOnlineChanged(true);

        var transitions = f.Status.Announced.Where(a => a.Text is "Offline. Showing cached messages." or "Back online.").ToList();
        Assert.Equal([("Offline. Showing cached messages.", AnnouncementCategory.Status), ("Back online.", AnnouncementCategory.Status)], transitions);
    }

    [Fact]
    public async Task ComingOnlineFirstAnnouncesNothing()
    {
        var f = new Fixture(online: true);
        await f.Vm.InitialLoadAsync();

        f.Connectivity.RaiseOnlineChanged(true);

        Assert.DoesNotContain(f.Status.Announced, a => a.Text == "Back online.");
    }

    [Theory]
    [InlineData(MainViewModel.ConnectionPhase.Connecting, true, 0, 2, "Connecting…")]
    [InlineData(MainViewModel.ConnectionPhase.Syncing, true, 2, 2, "Syncing…")]
    [InlineData(MainViewModel.ConnectionPhase.Idle, true, 0, 0, "No accounts")]
    [InlineData(MainViewModel.ConnectionPhase.Idle, false, 2, 2, "Offline")]
    [InlineData(MainViewModel.ConnectionPhase.Idle, true, 0, 2, "Offline")]
    [InlineData(MainViewModel.ConnectionPhase.Idle, true, 1, 2, "1 account connected")]
    [InlineData(MainViewModel.ConnectionPhase.Idle, true, 2, 3, "2 accounts connected")]
    public void TheConnectionLabelIsDerived(MainViewModel.ConnectionPhase phase, bool online, int connected, int accounts, string expected)
    {
        Assert.Equal(expected, MainViewModel.ConnectionStatusFor(phase, online, connected, accounts));
    }
}
