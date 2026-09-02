using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The offline-bodies pass (#637): with the setting on, sync caches the bodies of recent Inbox mail
/// that has none, newest first, and nothing else — not older mail, not other folders, not accounts
/// that keep whole messages already, and not an account the app knows is unreachable. Real store on
/// a temp profile, so the query that drives it is the shipping SQL.
/// </summary>
public class SyncServiceOfflineBodiesTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LocalStoreService _store;
    private readonly StubConfigService _config = new();
    private readonly StubConnectivityService _connectivity = new();
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly MailFolderModel _inbox = new() { FullName = "INBOX", DisplayName = "Inbox", Kind = SpecialFolderKind.Inbox };
    private readonly MailFolderModel _projects = new() { FullName = "Projects", DisplayName = "Projects" };

    public SyncServiceOfflineBodiesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"qm-offline-bodies-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new LocalStoreService(new ProfileContext(_tempDir));
        _store.Initialize();
        _inbox.AccountId = _accountId;
        _projects.AccountId = _accountId;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Records every body fetch and can fail one the way a dead network does.</summary>
    private sealed class RecordingPrefetchMail : StubImapMailServiceBase
    {
        public List<string> Prefetched { get; } = [];
        public string? FailOn { get; set; }
        public List<MailMessageSummary> Arrivals { get; } = [];

        public override Task<MailMessageDetail> PrefetchMessageDetailAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        {
            if (messageId == FailOn) throw new SocketException((int)SocketError.HostUnreachable);
            Prefetched.Add(messageId);
            return Task.FromResult(new MailMessageDetail
            {
                MessageId = messageId, AccountId = accountId, FolderName = folderName,
                From = "a <a@example.com>", PlainTextBody = $"body {messageId}",
            });
        }

        public override Task<List<MailMessageSummary>> GetMessagesSinceAsync(Guid accountId, string folderName, string sinceMessageId, int initialCount, CancellationToken ct = default)
            => Task.FromResult(Arrivals.ToList());

        /// <summary>What the startup sync sees on the server; without it the id-diff pass reads an
        /// empty listing as "everything was deleted remotely" and purges the seeded rows.</summary>
        public List<MailMessageSummary> OnServer { get; } = [];

        public override Task<IReadOnlyList<(string Id, DateTimeOffset ReceivedUtc, bool IsRead)>> GetFolderMessageIdDatesAsync(Guid accountId, string folderName, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(string, DateTimeOffset, bool)>>(
                [.. OnServer.Where(m => m.FolderName == folderName).Select(m => (m.MessageId, m.Date, m.IsRead))]);

        public override Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(Guid accountId, string folderName, DateTime since, CancellationToken ct = default)
            => Task.FromResult(OnServer.Where(m => m.FolderName == folderName).ToList());
    }

    private AccountModel Account(BackendKind backend = BackendKind.ImapSmtp, bool shared = false) => new()
    {
        Id = _accountId, AccountName = "Test", Username = "t@example.com", ImapHost = "host", BackendKind = backend, IsShared = shared,
    };

    private MailMessageSummary Summary(string id, string folder, int daysAgo) => new()
    {
        MessageId = id, AccountId = _accountId, FolderName = folder, From = "a <a@example.com>",
        Subject = id, IsRead = true, Date = DateTimeOffset.UtcNow.AddDays(-daysAgo),
    };

    private async Task SeedAsync(params MailMessageSummary[] rows) => await _store.UpsertSummariesAsync(rows);

    private async Task SeedBodyAsync(string id, string folder)
        => await _store.UpsertDetailAsync(new MailMessageDetail { MessageId = id, AccountId = _accountId, FolderName = folder, PlainTextBody = "cached" });

    /// <summary>Every (downloaded, planned) a pass ended with.</summary>
    private readonly List<(int Downloaded, int Planned)> _completed = [];

    private (SyncService Sync, RecordingPrefetchMail Mail, List<(int Done, int Total)> Progress) Build(int offlineBodyDays, int syncDays = 30)
    {
        var cfg = _config.Load();
        cfg.OfflineBodyDays = offlineBodyDays;
        cfg.SyncDays = syncDays;
        _config.Save(cfg);
        var mail = new RecordingPrefetchMail();
        var sync = new SyncService(mail, _store, _config, new StubRuleService(), connectivity: _connectivity)
        {
            FetchPacing = TimeSpan.Zero,
        };
        var progress = new List<(int, int)>();
        sync.OfflineBodyProgressChanged += (d, t) => progress.Add((d, t));
        sync.OfflineBodyPassCompleted += (d, t) => _completed.Add((d, t));
        return (sync, mail, progress);
    }

    private Dictionary<Guid, List<MailFolderModel>> Folders() => new() { [_accountId] = [_inbox, _projects] };

    [Fact]
    public async Task OffFetchesNothing()
    {
        await SeedAsync(Summary("a", "INBOX", 1));
        var (sync, mail, progress) = Build(offlineBodyDays: 0);

        await sync.BackfillOfflineBodiesAsync([Account()], Folders(), CancellationToken.None);

        Assert.Empty(mail.Prefetched);
        Assert.Empty(progress);
    }

    [Fact]
    public async Task FetchesOnlyInWindowInboxMailWithoutABody_NewestFirst()
    {
        await SeedAsync(
            Summary("old", "INBOX", 20),          // outside a 7-day window
            Summary("cached", "INBOX", 2),        // has a body already
            Summary("older-new", "INBOX", 5),
            Summary("newest", "INBOX", 1),
            Summary("elsewhere", "Projects", 1)); // not an Inbox
        await SeedBodyAsync("cached", "INBOX");
        var (sync, mail, progress) = Build(offlineBodyDays: 7);

        await sync.BackfillOfflineBodiesAsync([Account()], Folders(), CancellationToken.None);

        Assert.Equal(["newest", "older-new"], mail.Prefetched);
        Assert.NotNull(await _store.LoadDetailAsync(_accountId, "INBOX", "newest"));
        Assert.Equal((0, 2), progress.First());
        // Intermediate progress never claims the pass is over; the completion event does, once.
        Assert.DoesNotContain((2, 2), progress);
        Assert.Equal([(2, 2)], _completed);
    }

    [Fact]
    public async Task TheStartupSyncRunsThePassBehindThePreviews()
    {
        // The wiring, not the pass: SyncAllAccountsAsync → post-sync trickle → bodies.
        var (sync, mail, _) = Build(offlineBodyDays: 7);
        mail.OnServer.Add(Summary("a", "INBOX", 1));

        await sync.SyncAllAccountsAsync([Account()], Folders(), CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_completed.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.Equal(["a"], mail.Prefetched);
        Assert.Equal([(1, 1)], _completed);
    }

    [Fact]
    public async Task APassStopsAtTheCapAndTheNextOnePicksUpTheRest()
    {
        var rows = Enumerable.Range(0, SyncService.MaxBodiesPerPass + 1)
            .Select(i => Summary($"m{i:D4}", "INBOX", 0)).ToArray();
        await SeedAsync(rows);
        var (sync, mail, _) = Build(offlineBodyDays: 7);

        await sync.BackfillOfflineBodiesAsync([Account()], Folders(), CancellationToken.None);
        Assert.Equal(SyncService.MaxBodiesPerPass, mail.Prefetched.Count);
        Assert.Equal([(SyncService.MaxBodiesPerPass, SyncService.MaxBodiesPerPass)], _completed);

        await sync.BackfillOfflineBodiesAsync([Account()], Folders(), CancellationToken.None);
        Assert.Equal(SyncService.MaxBodiesPerPass + 1, mail.Prefetched.Count);
        Assert.Equal(SyncService.MaxBodiesPerPass + 1, mail.Prefetched.Distinct().Count());
    }

    [Fact]
    public async Task OnlyOnePassRunsAtATime()
    {
        await SeedAsync(Summary("a", "INBOX", 1), Summary("b", "INBOX", 2));
        var (sync, mail, _) = Build(offlineBodyDays: 7);
        sync.FetchPacing = TimeSpan.FromMilliseconds(150);

        var first = sync.BackfillOfflineBodiesAsync([Account()], Folders(), CancellationToken.None);
        await Task.Delay(50);
        await sync.BackfillOfflineBodiesAsync([Account()], Folders(), CancellationToken.None);   // skipped: one already running
        await first;

        Assert.Equal(2, mail.Prefetched.Count);
        Assert.Equal(2, mail.Prefetched.Distinct().Count());
        Assert.Single(_completed);
    }

    [Fact]
    public async Task ASecondPassHasNothingLeftToDo()
    {
        await SeedAsync(Summary("a", "INBOX", 1));
        var (sync, mail, _) = Build(offlineBodyDays: 7);
        await sync.BackfillOfflineBodiesAsync([Account()], Folders(), CancellationToken.None);
        Assert.Single(mail.Prefetched);

        await sync.BackfillOfflineBodiesAsync([Account()], Folders(), CancellationToken.None);

        Assert.Single(mail.Prefetched);
    }

    [Theory]
    [InlineData(0, 30, 0)]
    [InlineData(30, 7, 7)]
    [InlineData(30, 0, 30)]
    [InlineData(7, 30, 7)]
    [InlineData(90, 90, 90)]
    public void TheWindowIsNeverWiderThanTheSyncRange(int offlineBodyDays, int syncDays, int expected)
    {
        var cfg = new ConfigModel { OfflineBodyDays = offlineBodyDays, SyncDays = syncDays };
        Assert.Equal(expected, cfg.EffectiveOfflineBodyDays);
    }

    [Fact]
    public async Task Pop3AndSharedAccountsAreSkipped()
    {
        await SeedAsync(Summary("a", "INBOX", 1));
        var (sync, mail, _) = Build(offlineBodyDays: 7);

        await sync.BackfillOfflineBodiesAsync([Account(BackendKind.Pop3Smtp)], Folders(), CancellationToken.None);
        await sync.BackfillOfflineBodiesAsync([Account(shared: true)], Folders(), CancellationToken.None);

        Assert.Empty(mail.Prefetched);
    }

    [Fact]
    public async Task AnAccountKnownOfflineIsSkipped()
    {
        await SeedAsync(Summary("a", "INBOX", 1));
        _connectivity.SetAccount(_accountId, false);
        var (sync, mail, _) = Build(offlineBodyDays: 7);

        await sync.BackfillOfflineBodiesAsync([Account()], Folders(), CancellationToken.None);

        Assert.Empty(mail.Prefetched);
    }

    [Fact]
    public async Task AConnectionFailureStopsTheAccountAndReportsIt()
    {
        await SeedAsync(Summary("first", "INBOX", 1), Summary("second", "INBOX", 2));
        var (sync, mail, progress) = Build(offlineBodyDays: 7);
        mail.FailOn = "first";

        await sync.BackfillOfflineBodiesAsync([Account()], Folders(), CancellationToken.None);

        Assert.Empty(mail.Prefetched);   // "second" was never tried
        Assert.Contains((_accountId, "offline-bodies", false), _connectivity.Notes);
        // The pass reports what it actually cached, so the view model says nothing rather than
        // "Downloaded 2 messages" after the server went away.
        Assert.Equal([(0, 2)], _completed);
        Assert.Equal([(0, 2)], progress);
    }

    [Fact]
    public async Task CancellationStopsThePass()
    {
        await SeedAsync(Summary("a", "INBOX", 1));
        var (sync, mail, _) = Build(offlineBodyDays: 7);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sync.BackfillOfflineBodiesAsync([Account()], Folders(), cts.Token));

        Assert.Empty(mail.Prefetched);
    }

    [Fact]
    public async Task AnInboxArrivalGetsItsBodyWithoutWaitingForASweep()
    {
        var (sync, mail, _) = Build(offlineBodyDays: 7);
        mail.Arrivals.Add(Summary("arrived", "INBOX", 0));

        await sync.SyncOneFolderAsync(Account(), _inbox, CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (mail.Prefetched.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.Equal(["arrived"], mail.Prefetched);
    }

    [Fact]
    public async Task AnArrivalWithTheSettingOffIsLeftAlone()
    {
        var (sync, mail, _) = Build(offlineBodyDays: 0);
        mail.Arrivals.Add(Summary("arrived", "INBOX", 0));
        await sync.SyncOneFolderAsync(Account(), _inbox, CancellationToken.None);
        await Task.Delay(200);
        Assert.Empty(mail.Prefetched);
    }

    [Fact]
    public async Task AnArrivalOutsideTheWindowIsLeftAlone()
    {
        // An IDLE fetch of the last 50 can carry mail older than the window (a folder that just
        // had a lot moved into it); only the in-window ones get bodies.
        var (sync, mail, _) = Build(offlineBodyDays: 7);
        mail.Arrivals.Add(Summary("old", "INBOX", 20));
        mail.Arrivals.Add(Summary("new", "INBOX", 1));
        await sync.SyncOneFolderAsync(Account(), _inbox, CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (mail.Prefetched.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        await Task.Delay(100);
        Assert.Equal(["new"], mail.Prefetched);
    }

    [Fact]
    public async Task TheSweepPathDoesNotHook_ThePassCoversIt()
    {
        // SyncFolderFullAsync (the periodic sweep) and the startup sync run the pass themselves;
        // hooking there would fetch the whole first window twice.
        var (sync, mail, _) = Build(offlineBodyDays: 7);
        mail.Arrivals.Add(Summary("swept", "INBOX", 1));
        await sync.SyncFolderFullAsync(Account(), _inbox, CancellationToken.None);
        await Task.Delay(200);
        Assert.Empty(mail.Prefetched);
    }

    [Fact]
    public async Task TheStoreQueryOrdersNewestFirstAndHonoursTheLimit()
    {
        await SeedAsync(Summary("d3", "INBOX", 3), Summary("d1", "INBOX", 1), Summary("d2", "INBOX", 2), Summary("d40", "INBOX", 40));
        await SeedBodyAsync("d2", "INBOX");

        var ids = await _store.GetMessageIdsMissingDetailAsync(_accountId, "INBOX", DateTimeOffset.UtcNow.AddDays(-7), limit: 10);
        Assert.Equal(["d1", "d3"], ids);

        var limited = await _store.GetMessageIdsMissingDetailAsync(_accountId, "INBOX", DateTimeOffset.UtcNow.AddDays(-7), limit: 1);
        Assert.Equal(["d1"], limited);
    }
}
