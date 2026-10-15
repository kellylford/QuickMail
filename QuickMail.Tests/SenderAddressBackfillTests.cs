// The one-time sender-address backfill (#803): rows cached before from_email existed get it filled in place.
// Nothing is cleared — POP3's cache is the only copy of its mail — so these pin that it only ever fills what
// is missing, splits only Graph rows, never trusts an IMAP display text to be an address, and asks the server only
// for IMAP rows with no cached body.

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
    public void AnImapDisplayTextThatLooksLikeAnAddress_IsLeftToTheServer()
        // Review of #806: on IMAP, "boss@company.com" in from_disp may be a forger's display name in front of
        // another address. Taking it as the address would show the forged one in Properties, and from_email = ''
        // guards every later write, so the server fetch could never correct it.
        => Assert.Null(SenderAddressBackfill.For("boss@company.com", null, isGraphRow: false));

    [Fact]
    public void AnImapRowWithAForgedName_TakesTheRealAddressFromItsCachedBody()
        => Assert.Equal(new SenderAddressBackfill.Result(null, "attacker@evil.example"),
            SenderAddressBackfill.For("boss@company.com", "\"boss@company.com\" <attacker@evil.example>", isGraphRow: false));

    [Fact]
    public void AGraphRowWithAnAddressShapedName_TakesTheRealAddress()
        // Graph wrote "Name <address>" whenever the name differed from the address, so a forged name arrives with the
        // real address beside it.
        => Assert.Equal(new SenderAddressBackfill.Result("boss@company.com", "attacker@evil.example"),
            SenderAddressBackfill.For("boss@company.com <attacker@evil.example>", null, isGraphRow: true));

    [Fact]
    public void AGraphRowWithAFakeAddressInsideItsName_TakesTheRealAddressAtTheEnd()
        // Split at the LAST " <": the real address is the one Graph appended.
        => Assert.Equal(new SenderAddressBackfill.Result("Tom <ceo@bank.example>", "attacker@evil.example"),
            SenderAddressBackfill.For("Tom <ceo@bank.example> <attacker@evil.example>", null, isGraphRow: true));

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
            Row(_graph, "g2", "news@company.com"),
            Row(_imap,  "i1", "Ford, Kelly"),
            Row(_imap,  "i2", "boss@company.com"),
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
        Assert.Equal("news@company.com", (await Load(_graph, "g2")).FromEmail);   // a Graph row with no name
        Assert.Equal(("Ford, Kelly", "kelly@example.com"), ((await Load(_imap, "i1")).From, (await Load(_imap, "i1")).FromEmail));
        Assert.Equal("", (await Load(_imap, "i2")).FromEmail);                    // may be a forged name: server
        Assert.Equal("", (await Load(_imap, "i3")).FromEmail);                    // only the server knows
        Assert.Equal("done@x.com", (await Load(_imap, "i4")).FromEmail);          // untouched

        var forServer = await _store.GetMessageIdsMissingSenderEmailAsync(_imap);
        Assert.Equal(["i2", "i3"], forServer["INBOX"].Order());
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
        /// <summary>A folder whose fetch throws the given exception, the way a real server can fail it.</summary>
        public Dictionary<string, Exception> FailFolder { get; } = [];

        public Task<IReadOnlyDictionary<string, string>> FetchSenderEmailsAsync(
            Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
        {
            if (Unreachable) throw new SocketException((int)SocketError.HostUnreachable);
            if (FailFolder.TryGetValue(folderName, out var failure)) throw failure;
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
        sync.RequestSenderEmailBackfill([imap.Id], () => completed++);

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
        sync.RequestSenderEmailBackfill([imap.Id], () => { });

        await sync.BackfillSenderEmailsFromServerAsync([imap], CancellationToken.None);
        await sync.BackfillSenderEmailsFromServerAsync([imap], CancellationToken.None);   // a second call, no new request

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
        sync.RequestSenderEmailBackfill([], () => completed++);   // what App asks for: the IMAP accounts, none here

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
        sync.RequestSenderEmailBackfill([imap.Id], () => completed++);

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
        sync.RequestSenderEmailBackfill([imap.Id], () => completed++);

        await sync.BackfillSenderEmailsFromServerAsync([imap], CancellationToken.None);

        Assert.Empty(mail.Fetches);
        Assert.Equal(0, completed);
    }

    [Fact]
    public async Task AnImapAccountThatNeverConnected_LeavesItForNextLaunch()
    {
        // Review of #806: the sync is handed the connected accounts only. Gmail needing an interactive sign-in on
        // upgrade day while another IMAP account connects must not get the marker written over Gmail's older rows.
        var connected = Account();
        var neverConnected = Account();
        await Seed(connected, "1", "Some Company");
        await Seed(neverConnected, "2", "Other Company");
        var (sync, mail) = Build();
        mail.OnServer["1"] = "news@company.com";
        var completed = 0;
        sync.RequestSenderEmailBackfill([connected.Id, neverConnected.Id], () => completed++);

        await sync.BackfillSenderEmailsFromServerAsync([connected], CancellationToken.None);

        Assert.Equal("news@company.com", (await _store.LoadFolderSummariesAsync(connected.Id, "INBOX")).Single().FromEmail);
        Assert.Equal(0, completed);
    }

    [Fact]
    public async Task TheNextLaunch_FinishesOnceTheMissingAccountConnects()
    {
        var first = Account();
        var second = Account();
        await Seed(first, "1", "Some Company");
        await Seed(second, "2", "Other Company");
        var (launch1, mail1) = Build();
        mail1.OnServer["1"] = "news@company.com";
        launch1.RequestSenderEmailBackfill([first.Id, second.Id], () => { });
        await launch1.BackfillSenderEmailsFromServerAsync([first], CancellationToken.None);

        var (launch2, mail2) = Build();
        mail2.OnServer["2"] = "info@other.example";
        var completed = 0;
        launch2.RequestSenderEmailBackfill([first.Id, second.Id], () => completed++);
        await launch2.BackfillSenderEmailsFromServerAsync([first, second], CancellationToken.None);

        Assert.Equal(["2"], Assert.Single(mail2.Fetches).Ids);   // the first account's rows were already filled
        Assert.Equal(1, completed);
    }

    [Fact]
    public async Task AFolderDeletedOnTheServer_IsSkippedForGood_AndTheAccountStillFinishes()
    {
        var imap = Account();
        await Seed(imap, "1", "Some Company");
        await Seed(imap, "2", "Other Company", folder: "Old Projects");
        var (sync, mail) = Build();
        mail.OnServer["1"] = "news@company.com";
        mail.FailFolder["Old Projects"] = new MailKit.FolderNotFoundException("Old Projects");
        var completed = 0;
        sync.RequestSenderEmailBackfill([imap.Id], () => completed++);

        await sync.BackfillSenderEmailsFromServerAsync([imap], CancellationToken.None);

        Assert.Equal("news@company.com", (await _store.LoadFolderSummariesAsync(imap.Id, "INBOX")).Single().FromEmail);
        Assert.Equal(1, completed);   // retrying a folder that is gone would never succeed
    }

    public static TheoryData<Exception> TemporaryRefusals() => new()
    {
        // Gmail throttling: the server answered, so it is not a connection failure — but it passes.
        new MailKit.Net.Imap.ImapCommandException(MailKit.Net.Imap.ImapCommandResponse.No, "[UNAVAILABLE] Try again later"),
        new MailKit.Security.AuthenticationException("The sign-in needs renewing."),
        new InvalidOperationException("Something the backfill did not expect."),
    };

    [Theory]
    [MemberData(nameof(TemporaryRefusals))]
    public async Task ARefusalThatCanPass_LeavesTheAccountForNextLaunch(Exception refusal)
    {
        // Review of #806: these used to skip the folder for good, so the marker was written over rows the next launch
        // could have filled.
        var imap = Account();
        await Seed(imap, "1", "Some Company");
        var (sync, mail) = Build();
        mail.FailFolder["INBOX"] = refusal;
        var completed = 0;
        sync.RequestSenderEmailBackfill([imap.Id], () => completed++);

        await sync.BackfillSenderEmailsFromServerAsync([imap], CancellationToken.None);

        Assert.Equal(0, completed);
    }

    [Fact]
    public void OnlyAGoneFolderOrABadId_IsSkippedForGood()
    {
        Assert.True(SyncService.IsPermanentlyUnfetchable(new MailKit.FolderNotFoundException("Old")));
        Assert.True(SyncService.IsPermanentlyUnfetchable(new FormatException()));     // a non-numeric stored id
        Assert.True(SyncService.IsPermanentlyUnfetchable(new OverflowException()));   // too large for a UID
        Assert.False(SyncService.IsPermanentlyUnfetchable(
            new MailKit.Net.Imap.ImapCommandException(MailKit.Net.Imap.ImapCommandResponse.No, "[UNAVAILABLE]")));
        Assert.False(SyncService.IsPermanentlyUnfetchable(new MailKit.Security.AuthenticationException("expired")));
    }

    [Fact]
    public async Task ARefusingAccount_DoesNotHoldUpAnotherAccount()
    {
        var refusing = Account();
        var fine = Account();
        await Seed(refusing, "1", "Some Company", folder: "Busy");
        await Seed(fine, "2", "Other Company");
        var (sync, mail) = Build();
        mail.FailFolder["Busy"] = new MailKit.Net.Imap.ImapCommandException(MailKit.Net.Imap.ImapCommandResponse.No, "[UNAVAILABLE]");
        mail.OnServer["2"] = "info@other.example";
        sync.RequestSenderEmailBackfill([refusing.Id, fine.Id], () => { });

        await sync.BackfillSenderEmailsFromServerAsync([refusing, fine], CancellationToken.None);

        Assert.Equal("info@other.example", (await _store.LoadFolderSummariesAsync(fine.Id, "INBOX")).Single().FromEmail);
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
        sync.RequestSenderEmailBackfill([imap.Id], () => { });

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
        sync.RequestSenderEmailBackfill([imap.Id], () => completed++);

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
