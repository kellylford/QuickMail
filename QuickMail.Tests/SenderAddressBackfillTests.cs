// The one-time sender-address backfill (#803): rows cached before from_email existed get it filled in place.
// Nothing is cleared — POP3's cache is the only copy of its mail — so these pin that it only ever fills what
// is missing, splits only Graph rows, and asks the server only for IMAP rows that hold a display name alone.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public class SenderAddressBackfillRuleTests
{
    [Fact]
    public void AGraphRow_IsSplitIntoNameAndAddress()
        => Assert.Equal(new SenderAddressBackfill.Result("Alice", "alice@x.com"),
            SenderAddressBackfill.For("Alice <alice@x.com>", null, isGraphRow: true));

    [Fact]
    public void AGraphRow_KeepsAnUnquotedCommaInItsName()
        // AsHeaderString never quoted, so a header parser would read "Ford" and "Kelly" as two addresses.
        => Assert.Equal(new SenderAddressBackfill.Result("Ford, Kelly", "kelly@example.com"),
            SenderAddressBackfill.For("Ford, Kelly <kelly@example.com>", null, isGraphRow: true));

    [Fact]
    public void AGraphRowWithNoName_KeepsItsText_AndTakesItAsTheAddress()
        => Assert.Equal(new SenderAddressBackfill.Result(null, "alice@x.com"),
            SenderAddressBackfill.For("alice@x.com", null, isGraphRow: true));

    [Fact]
    public void AnImapNameWithAngleBrackets_IsNeverSplit()
        // Only Graph wrote "Name <address>" into from_disp; anywhere else it is the sender's own display name.
        => Assert.Null(SenderAddressBackfill.For("Team <Sales>", null, isGraphRow: false));

    [Fact]
    public void AnImapRow_TakesItsAddressFromTheCachedBody()
        => Assert.Equal(new SenderAddressBackfill.Result(null, "kelly@example.com"),
            SenderAddressBackfill.For("Ford, Kelly", "\"Ford, Kelly\" <kelly@example.com>", isGraphRow: false));

    [Fact]
    public void AnImapRowWithNoName_IsAlreadyItsAddress()
        => Assert.Equal(new SenderAddressBackfill.Result(null, "news@company.com"),
            SenderAddressBackfill.For("news@company.com", null, isGraphRow: false));

    [Theory]
    [InlineData("Some Company")]   // a name alone, no body cached: only the server knows
    [InlineData("")]
    [InlineData("   ")]
    public void NothingOnDisk_SaysNothing(string fromDisp)
        => Assert.Null(SenderAddressBackfill.For(fromDisp, null, isGraphRow: false));
}

/// <summary>The store half, on a real database: the shipping SQL fills only what is missing.</summary>
public class SenderAddressBackfillStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"qm-sender-backfill-{Guid.NewGuid():N}");
    private readonly LocalStoreService _store;
    private readonly Guid _graph = Guid.NewGuid();
    private readonly Guid _imap = Guid.NewGuid();

    public SenderAddressBackfillStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new LocalStoreService(new ProfileContext(_dir));
        _store.Initialize();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private MailMessageSummary Row(Guid account, string id, string from, string fromEmail = "") => new()
    {
        MessageId = id, AccountId = account, FolderName = "INBOX", From = from, FromEmail = fromEmail,
        Subject = id, Date = DateTimeOffset.UtcNow,
    };

    private async Task<MailMessageSummary> Load(Guid account, string id)
        => (await _store.LoadFolderSummariesAsync(account, "INBOX")).Single(m => m.MessageId == id);

    [Fact]
    public async Task FillsWhatIsOnDisk_AndLeavesTheRestForTheServer()
    {
        await _store.UpsertSummariesAsync([
            Row(_graph, "g1", "Alice <alice@x.com>"),
            Row(_imap,  "i1", "Ford, Kelly"),
            Row(_imap,  "i2", "news@company.com"),
            Row(_imap,  "i3", "Some Company"),
            Row(_imap,  "i4", "Already Done", "done@x.com"),
        ]);
        await _store.UpsertDetailAsync(new MailMessageDetail
        {
            MessageId = "i1", AccountId = _imap, FolderName = "INBOX", From = "\"Ford, Kelly\" <kelly@example.com>",
        });

        var filled = await _store.BackfillSenderEmailsLocallyAsync([_graph]);

        Assert.Equal(3, filled);
        var g1 = await Load(_graph, "g1");
        Assert.Equal(("Alice", "alice@x.com"), (g1.From, g1.FromEmail));          // split: #801 for older rows
        Assert.Equal(("Ford, Kelly", "kelly@example.com"), ((await Load(_imap, "i1")).From, (await Load(_imap, "i1")).FromEmail));
        Assert.Equal("news@company.com", (await Load(_imap, "i2")).FromEmail);
        Assert.Equal("", (await Load(_imap, "i3")).FromEmail);                    // only the server knows
        Assert.Equal("done@x.com", (await Load(_imap, "i4")).FromEmail);          // untouched

        var forServer = await _store.GetMessageIdsMissingSenderEmailAsync(_imap);
        Assert.Equal(["i3"], forServer["INBOX"]);
    }

    [Fact]
    public async Task RunningItAgain_ChangesNothing()
    {
        await _store.UpsertSummariesAsync([Row(_graph, "g1", "Alice <alice@x.com>")]);
        Assert.Equal(1, await _store.BackfillSenderEmailsLocallyAsync([_graph]));

        Assert.Equal(0, await _store.BackfillSenderEmailsLocallyAsync([_graph]));
        Assert.Equal("Alice", (await Load(_graph, "g1")).From);
    }

    [Fact]
    public async Task AnImapNameLookingLikeAGraphHeader_IsNotSplit()
    {
        await _store.UpsertSummariesAsync([Row(_imap, "i1", "Bob <bob@x.com>")]);

        await _store.BackfillSenderEmailsLocallyAsync([_graph]);

        Assert.Equal("Bob <bob@x.com>", (await Load(_imap, "i1")).From);
    }

    [Fact]
    public async Task ServerAddresses_FillOnlyRowsStillMissingOne()
    {
        await _store.UpsertSummariesAsync([Row(_imap, "i1", "Some Company"), Row(_imap, "i2", "Kept", "kept@x.com")]);

        await _store.UpdateSenderEmailsBatchAsync(_imap, "INBOX", [("i1", "news@company.com"), ("i2", "other@x.com")]);

        Assert.Equal("news@company.com", (await Load(_imap, "i1")).FromEmail);
        Assert.Equal("kept@x.com", (await Load(_imap, "i2")).FromEmail);
    }
}

/// <summary>The server half: fetches only for IMAP rows still missing an address, and reports when it is done.</summary>
public class SenderAddressBackfillSyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"qm-sender-sync-{Guid.NewGuid():N}");
    private readonly LocalStoreService _store;
    private readonly StubConnectivityService _connectivity = new();

    public SenderAddressBackfillSyncTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new LocalStoreService(new ProfileContext(_dir));
        _store.Initialize();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Answers FetchSenderEmailsAsync from a fixed table, and can fail the way a dead network does.</summary>
    private sealed class EnvelopeMail : StubImapMailServiceBase, IMailService
    {
        public Dictionary<string, string> OnServer { get; } = [];
        public List<(string Folder, List<string> Ids)> Fetches { get; } = [];
        public bool Unreachable { get; set; }

        public Task<IReadOnlyDictionary<string, string>> FetchSenderEmailsAsync(
            Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
        {
            if (Unreachable) throw new SocketException((int)SocketError.HostUnreachable);
            Fetches.Add((folderName, messageIds.ToList()));
            return Task.FromResult<IReadOnlyDictionary<string, string>>(
                messageIds.Where(OnServer.ContainsKey).ToDictionary(id => id, id => OnServer[id]));
        }
    }

    private static AccountModel Account(BackendKind backend = BackendKind.ImapSmtp) => new()
    {
        Id = Guid.NewGuid(), AccountName = "Test", Username = "t@example.com", ImapHost = "host", BackendKind = backend,
    };

    private (SyncService Sync, EnvelopeMail Mail) Build()
    {
        var mail = new EnvelopeMail();
        var sync = new SyncService(mail, _store, new StubConfigService(), new StubRuleService(), connectivity: _connectivity)
        {
            SenderEmailPacing = TimeSpan.Zero,
        };
        return (sync, mail);
    }

    private Task Seed(AccountModel account, string id, string from, string fromEmail = "", string folder = "INBOX")
        => _store.UpsertSummariesAsync([new MailMessageSummary
        {
            MessageId = id, AccountId = account.Id, FolderName = folder, From = from, FromEmail = fromEmail,
            Subject = id, Date = DateTimeOffset.UtcNow,
        }]);

    [Fact]
    public async Task FetchesOnlyRowsMissingAnAddress_AndFillsThem()
    {
        var imap = Account();
        await Seed(imap, "1", "Some Company");
        await Seed(imap, "2", "Done", "done@x.com");
        var (sync, mail) = Build();
        mail.OnServer["1"] = "news@company.com";
        var completed = 0;
        sync.RequestSenderEmailBackfill(() => completed++);

        await sync.BackfillSenderEmailsFromServerAsync([imap], CancellationToken.None);

        var (_, ids) = Assert.Single(mail.Fetches);
        Assert.Equal(["1"], ids);
        Assert.Equal("news@company.com", (await _store.LoadFolderSummariesAsync(imap.Id, "INBOX")).Single(m => m.MessageId == "1").FromEmail);
        Assert.Equal(1, completed);
    }

    [Fact]
    public async Task RunsOncePerRequest()
    {
        var imap = Account();
        await Seed(imap, "1", "Some Company");
        var (sync, mail) = Build();
        sync.RequestSenderEmailBackfill(() => { });

        await sync.BackfillSenderEmailsFromServerAsync([imap], CancellationToken.None);
        await sync.BackfillSenderEmailsFromServerAsync([imap], CancellationToken.None);   // a later sweep's trickle

        Assert.Single(mail.Fetches);
    }

    [Fact]
    public async Task NeverAsksTheServerForGraphOrPop3Rows()
    {
        var graph = Account(BackendKind.MicrosoftGraph);
        var pop3 = Account(BackendKind.Pop3Smtp);
        await Seed(graph, "g", "Some Company");
        await Seed(pop3, "p", "Some Company");
        var (sync, mail) = Build();
        var completed = 0;
        sync.RequestSenderEmailBackfill(() => completed++);

        await sync.BackfillSenderEmailsFromServerAsync([graph, pop3], CancellationToken.None);

        Assert.Empty(mail.Fetches);
        Assert.Equal(1, completed);
    }

    [Fact]
    public async Task AnUnreachableServer_LeavesItForNextLaunch()
    {
        var imap = Account();
        await Seed(imap, "1", "Some Company");
        var (sync, mail) = Build();
        mail.Unreachable = true;
        var completed = 0;
        sync.RequestSenderEmailBackfill(() => completed++);

        await sync.BackfillSenderEmailsFromServerAsync([imap], CancellationToken.None);

        Assert.Equal(0, completed);
        Assert.Equal("", (await _store.LoadFolderSummariesAsync(imap.Id, "INBOX")).Single().FromEmail);
    }

    [Fact]
    public async Task AnAccountKnownOffline_IsNotAsked_AndLeavesItForNextLaunch()
    {
        var imap = Account();
        await Seed(imap, "1", "Some Company");
        _connectivity.NoteAccountUnreachable(imap.Id, "test");
        var (sync, mail) = Build();
        var completed = 0;
        sync.RequestSenderEmailBackfill(() => completed++);

        await sync.BackfillSenderEmailsFromServerAsync([imap], CancellationToken.None);

        Assert.Empty(mail.Fetches);
        Assert.Equal(0, completed);
    }

    [Fact]
    public async Task WithoutARequest_NothingIsFetched()
    {
        var imap = Account();
        await Seed(imap, "1", "Some Company");
        var (sync, mail) = Build();

        await sync.BackfillSenderEmailsFromServerAsync([imap], CancellationToken.None);

        Assert.Empty(mail.Fetches);
    }

    [Fact]
    public async Task ABigCache_IsFetchedInBatches_WithProgressAndOneCompletion()
    {
        // It is real traffic the user did not ask for, so it is never invisible: progress after each batch,
        // one completion at the end.
        var imap = Account();
        var total = SyncService.SenderEmailFetchBatch * 2 + 200;
        await _store.UpsertSummariesAsync([.. Enumerable.Range(1, total).Select(i => new MailMessageSummary
        {
            MessageId = i.ToString(System.Globalization.CultureInfo.InvariantCulture), AccountId = imap.Id,
            FolderName = "INBOX", From = "Some Company", Subject = "s", Date = DateTimeOffset.UtcNow.AddMinutes(-i),
        })]);
        var (sync, mail) = Build();
        for (var i = 1; i <= total; i++) mail.OnServer[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = $"n{i}@x.com";
        var progress = new List<(int, int)>();
        var completed = new List<(int, int)>();
        sync.SenderEmailBackfillProgressChanged += (d, t) => progress.Add((d, t));
        sync.SenderEmailBackfillCompleted += (f, p) => completed.Add((f, p));
        sync.RequestSenderEmailBackfill(() => { });

        await sync.BackfillSenderEmailsFromServerAsync([imap], CancellationToken.None);

        Assert.Equal(3, mail.Fetches.Count);
        Assert.All(mail.Fetches, f => Assert.True(f.Ids.Count <= SyncService.SenderEmailFetchBatch));
        Assert.Equal([(500, total), (1000, total)], progress);
        Assert.Equal([(total, total)], completed);
    }

    [Fact]
    public async Task NothingToFetch_SaysNothing()
    {
        var imap = Account();
        await Seed(imap, "1", "Done", "done@x.com");
        var (sync, _) = Build();
        var said = 0;
        sync.SenderEmailBackfillProgressChanged += (_, _) => said++;
        sync.SenderEmailBackfillCompleted += (_, _) => said++;
        var completed = 0;
        sync.RequestSenderEmailBackfill(() => completed++);

        await sync.BackfillSenderEmailsFromServerAsync([imap], CancellationToken.None);

        Assert.Equal(0, said);
        Assert.Equal(1, completed);   // still done, so the marker is written
    }
}

public class SenderAddressBackfillWordingTests
{
    [Fact]
    public void Progress_ReadsAsACount()
        => Assert.Equal(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, "Updating sender addresses: {0:N0} of {1:N0}", 2000, 8000),
            QuickMail.ViewModels.MainViewModel.DescribeSenderEmailBackfill(2000, 8000));

    [Fact]
    public void Finishing_ShowsAResultOnlyWhenSomethingWasFilled()
    {
        Assert.Null(QuickMail.ViewModels.MainViewModel.SenderEmailBackfillFinished(0));
        Assert.Equal("Updated the sender address of 1 message.", QuickMail.ViewModels.MainViewModel.SenderEmailBackfillFinished(1));
        Assert.Equal(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, "Updated sender addresses for {0:N0} messages.", 1234),
            QuickMail.ViewModels.MainViewModel.SenderEmailBackfillFinished(1234));
    }
}
