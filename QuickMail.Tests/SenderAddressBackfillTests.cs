// The one-time sender-address backfill (#803): rows cached before from_email existed get it filled in place.
// Nothing is cleared — POP3's cache is the only copy of its mail — so these pin that it only ever fills what
// is missing, splits only Graph rows, never trusts an IMAP display text to be an address, asks the server only
// for IMAP rows with no cached body, and writes the server's answer only where its Message-ID matches the row's.

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

    private MailMessageSummary Row(Guid account, string id, string from, string fromEmail = "", string internetMessageId = "") => new()
    {
        MessageId = id, AccountId = account, FolderName = "INBOX", From = from, FromEmail = fromEmail,
        InternetMessageId = internetMessageId, Subject = id, Date = DateTimeOffset.UtcNow,
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

    private async Task<List<string>> FoundWithoutIndexing(string words)
    {
        // indexPendingFirst: false, so a row waiting to be reindexed is not found: what a search sees while the
        // background indexer is still working through a backlog.
        var match = SearchMatchExpression.AllOf(MessageSearchQuery.Parse(words).Terms)!;
        var hits = await _store.FindMessagesAsync(match, null, indexPendingFirst: false, ct: TestContext.Current.CancellationToken);
        return [.. hits.Select(h => h.MessageId).Order()];
    }

    private async Task IndexEverything()
    {
        while (await _store.IndexPendingSearchAsync(1000, TestContext.Current.CancellationToken) > 0) { }
    }

    [Fact]
    public async Task RowsFilledLocally_StayInTheSearchIndex()
    {
        // Review of #806: the address filled here was already in each row's search text, so reindexing them all was
        // a long background job that only hid them from index results until it finished.
        await _store.UpsertSummariesAsync([
            Row(_graph, "g1", "Alice <alice@x.com>"),
            Row(_imap,  "i1", "Ford, Kelly"),
        ]);
        await _store.UpsertDetailAsync(new MailMessageDetail
        {
            MessageId = "i1", AccountId = _imap, FolderName = "INBOX", From = "\"Ford, Kelly\" <kelly@example.com>",
        });
        await IndexEverything();

        Assert.Equal(2, await _store.BackfillSenderEmailsLocallyAsync([_graph]));

        Assert.Equal(["g1"], await FoundWithoutIndexing("alice@x.com"));
        Assert.Equal(["i1"], await FoundWithoutIndexing("kelly@example.com"));
    }

    [Fact]
    public async Task ARowStillWaitingToBeIndexed_IsLeftForTheIndexer()
    {
        // Only a row the index had settled is settled again; one it had not reached keeps waiting for it.
        await _store.UpsertSummariesAsync([Row(_graph, "g1", "Alice <alice@x.com>")]);

        await _store.BackfillSenderEmailsLocallyAsync([_graph]);

        Assert.Empty(await FoundWithoutIndexing("alice"));
        await IndexEverything();
        Assert.Equal(["g1"], await FoundWithoutIndexing("alice"));
    }

    [Fact]
    public async Task AnAddressFromTheServer_IsReindexed_SoItCanBeFound()
    {
        // The server half's addresses are words the index has never had.
        await _store.UpsertSummariesAsync([Row(_imap, "i1", "Some Company")]);
        await IndexEverything();
        Assert.Empty(await FoundWithoutIndexing("news@company.com"));

        await _store.UpdateSenderEmailsBatchAsync(_imap, "INBOX", [("i1", "news@company.com", "")]);
        await IndexEverything();

        Assert.Equal(["i1"], await FoundWithoutIndexing("news@company.com"));
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

        var written = await _store.UpdateSenderEmailsBatchAsync(_imap, "INBOX",
            [("i1", "news@company.com", ""), ("i2", "other@x.com", "")]);

        Assert.Equal(1, written);
        Assert.Equal("news@company.com", (await Load(_imap, "i1")).FromEmail);
        Assert.Equal("kept@x.com", (await Load(_imap, "i2")).FromEmail);
    }

    [Fact]
    public async Task ServerAddresses_AreWrittenOnlyWhereTheMessageIdStillMatches()
    {
        // Review of #806: after a server renumbers a folder, a stored id can name another message, and its sender
        // must not be written onto the cached one.
        await _store.UpsertSummariesAsync([
            Row(_imap, "same",       "Some Company",   internetMessageId: "a1@company.com"),
            Row(_imap, "renumbered", "Some Company",   internetMessageId: "a2@company.com"),
            Row(_imap, "noIdCached", "Other Company"),
            Row(_imap, "brackets",   "Third Company",  internetMessageId: "<a4@company.com>"),
            Row(_imap, "noIdThere",  "Fourth Company", internetMessageId: "a5@company.com"),
        ]);

        var written = await _store.UpdateSenderEmailsBatchAsync(_imap, "INBOX", [
            ("same",       "news@company.com",      "a1@company.com"),
            ("renumbered", "someone@other.example", "b2@other.example"),
            ("noIdCached", "info@other.example",    "a3@other.example"),   // nothing cached to check against
            ("brackets",   "info@third.example",    "a4@company.com"),     // the same id, written two ways
            ("noIdThere",  "info@fourth.example",   ""),                   // the server's has none: another message
        ]);

        Assert.Equal(3, written);
        Assert.Equal("news@company.com",   (await Load(_imap, "same")).FromEmail);
        Assert.Equal("",                   (await Load(_imap, "renumbered")).FromEmail);
        Assert.Equal("info@other.example", (await Load(_imap, "noIdCached")).FromEmail);
        Assert.Equal("info@third.example", (await Load(_imap, "brackets")).FromEmail);
        Assert.Equal("",                   (await Load(_imap, "noIdThere")).FromEmail);
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
        /// <summary>The Message-ID the server gives a message; none when not listed.</summary>
        public Dictionary<string, string> MessageIdOnServer { get; } = [];
        public List<(string Folder, List<string> Ids)> Fetches { get; } = [];
        public bool Unreachable { get; set; }
        /// <summary>A folder whose fetch throws the given exception, the way a real server can fail it.</summary>
        public Dictionary<string, Exception> FailFolder { get; } = [];
        /// <summary>Runs as each fetch starts, so a test can act part-way through a pass.</summary>
        public Action? OnFetch { get; set; }

        public Task<IReadOnlyDictionary<string, FetchedSender>> FetchSenderEmailsAsync(
            Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
        {
            if (Unreachable) throw new SocketException((int)SocketError.HostUnreachable);
            if (FailFolder.TryGetValue(folderName, out var failure)) throw failure;
            OnFetch?.Invoke();
            Fetches.Add((folderName, messageIds.ToList()));
            return Task.FromResult<IReadOnlyDictionary<string, FetchedSender>>(
                messageIds.Where(OnServer.ContainsKey).ToDictionary(
                    id => id, id => new FetchedSender(OnServer[id], MessageIdOnServer.GetValueOrDefault(id, ""))));
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

    private Task Seed(AccountModel account, string id, string from, string fromEmail = "", string folder = "INBOX",
                      string internetMessageId = "")
        => _store.UpsertSummariesAsync([new MailMessageSummary
        {
            MessageId = id, AccountId = account.Id, FolderName = folder, From = from, FromEmail = fromEmail,
            InternetMessageId = internetMessageId, Subject = id, Date = DateTimeOffset.UtcNow,
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
    public async Task AnIdNowNamingAnotherMessage_IsNotWritten_AndTheAccountStillFinishes()
    {
        // Review of #806: the folder was renumbered on the server, so id 1 is now somebody else's message. Its
        // sender must not land on the cached one, and asking again next launch would get the same answer.
        var imap = Account();
        await Seed(imap, "1", "Some Company", internetMessageId: "kept@company.com");
        await Seed(imap, "2", "Other Company", internetMessageId: "<same@other.example>");
        var (sync, mail) = Build();
        mail.OnServer["1"] = "someone@other.example";
        mail.MessageIdOnServer["1"] = "different@other.example";
        mail.OnServer["2"] = "info@other.example";
        mail.MessageIdOnServer["2"] = "same@other.example";
        var results = new List<(int Filled, int Planned)>();
        sync.SenderEmailBackfillEnded += (f, p) => results.Add((f, p));
        var completed = 0;
        sync.RequestSenderEmailBackfill([imap.Id], () => completed++);

        await sync.BackfillSenderEmailsFromServerAsync([imap], CancellationToken.None);

        var rows = await _store.LoadFolderSummariesAsync(imap.Id, "INBOX");
        Assert.Equal("", rows.Single(m => m.MessageId == "1").FromEmail);
        Assert.Equal("info@other.example", rows.Single(m => m.MessageId == "2").FromEmail);
        Assert.Equal([(1, 2)], results);   // the count says what was written, not what was fetched
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
    public async Task ABigCache_IsFetchedInBatches_WithProgressAndOneEnd()
    {
        // It is real traffic the user did not ask for, so it is never invisible: progress after each batch,
        // and one end, which clears it.
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
        var ended = new List<(int, int)>();
        sync.SenderEmailBackfillProgressChanged += (d, t) => progress.Add((d, t));
        sync.SenderEmailBackfillEnded += (f, p) => ended.Add((f, p));
        sync.RequestSenderEmailBackfill([imap.Id], () => { });

        await sync.BackfillSenderEmailsFromServerAsync([imap], CancellationToken.None);

        Assert.Equal(3, mail.Fetches.Count);
        Assert.All(mail.Fetches, f => Assert.True(f.Ids.Count <= SyncService.SenderEmailFetchBatch));
        Assert.Equal([(500, total), (1000, total)], progress);
        Assert.Equal([(total, total)], ended);
    }

    [Fact]
    public async Task AServerThatGoesAway_StillEndsThePass()
    {
        // Review of #806: the progress used to stay in the status bar when the connection dropped part-way.
        var imap = Account();
        await Seed(imap, "1", "Some Company");
        var (sync, mail) = Build();
        mail.Unreachable = true;
        var ended = new List<(int, int)>();
        sync.SenderEmailBackfillEnded += (f, p) => ended.Add((f, p));
        sync.RequestSenderEmailBackfill([imap.Id], () => { });

        await sync.BackfillSenderEmailsFromServerAsync([imap], CancellationToken.None);

        Assert.Equal([(0, 1)], ended);
    }

    [Fact]
    public async Task ACancelledPass_StillEndsThePass()
    {
        // Shutting down cancels the trickle mid-pass; the end must still be raised so nothing is left showing.
        var imap = Account();
        await Seed(imap, "1", "Some Company");
        await Seed(imap, "2", "Other Company", folder: "Archive");
        var (sync, mail) = Build();
        mail.OnServer["1"] = "news@company.com";
        mail.OnServer["2"] = "info@other.example";
        using var cts = new CancellationTokenSource();
        mail.OnFetch = cts.Cancel;   // cancelled while the first folder's fetch is under way
        var ended = new List<(int, int)>();
        sync.SenderEmailBackfillEnded += (f, p) => ended.Add((f, p));
        var completed = 0;
        sync.RequestSenderEmailBackfill([imap.Id], () => completed++);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sync.BackfillSenderEmailsFromServerAsync([imap], cts.Token));

        Assert.Equal([(1, 2)], ended);
        Assert.Equal(0, completed);   // not finished, so the next launch carries on
    }

    [Fact]
    public async Task NothingToFetch_SaysNothing()
    {
        var imap = Account();
        await Seed(imap, "1", "Done", "done@x.com");
        var (sync, _) = Build();
        var said = 0;
        sync.SenderEmailBackfillProgressChanged += (_, _) => said++;
        sync.SenderEmailBackfillEnded += (_, _) => said++;
        var completed = 0;
        sync.RequestSenderEmailBackfill([imap.Id], () => completed++);

        await sync.BackfillSenderEmailsFromServerAsync([imap], CancellationToken.None);

        Assert.Equal(0, said);
        Assert.Equal(1, completed);   // still done, so the marker is written
    }
}

/// <summary>
/// The status bar's region for the backfill (review of #806): its own text, so the message count is never
/// overwritten, there while the pass runs and gone the moment it ends.
/// </summary>
public class SenderAddressBackfillStatusTests
{
    private static (QuickMail.ViewModels.MainViewModel Vm, StubSyncService Sync) Build()
    {
        var sync = new StubSyncService();
        var vm = new QuickMail.ViewModels.MainViewModel(new StubImapMailService(), new StubAccountService(),
            new StubCredentialService(), new StubLocalStoreService(), new StubOAuthService(), sync,
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService());
        return (vm, sync);
    }

    [Fact]
    public void Progress_ReadsAsACount()
        => Assert.Equal(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, "Updating sender addresses: {0:N0} of {1:N0}", 2000, 8000),
            QuickMail.ViewModels.MainViewModel.DescribeSenderEmailBackfill(2000, 8000));

    [Fact]
    public void Progress_HasItsOwnRegion_AndLeavesTheStatusTextAlone()
    {
        var (vm, sync) = Build();
        vm.StatusText = "7,323 messages.";

        sync.RaiseSenderEmailBackfillProgress(500, 2000);

        Assert.Equal(QuickMail.ViewModels.MainViewModel.DescribeSenderEmailBackfill(500, 2000), vm.SenderAddressStatusText);
        Assert.Equal("7,323 messages.", vm.StatusText);
    }

    [Theory]
    [InlineData(2000, 2000)]   // finished
    [InlineData(500, 2000)]    // stopped by a server, or cancelled
    [InlineData(0, 2000)]
    public void TheRegionIsCleared_HoweverThePassEnds(int filled, int planned)
    {
        var (vm, sync) = Build();
        vm.StatusText = "7,323 messages.";
        sync.RaiseSenderEmailBackfillProgress(500, 2000);

        sync.RaiseSenderEmailBackfillEnded(filled, planned);

        Assert.Equal("", vm.SenderAddressStatusText);    // empty, so the region is hidden
        Assert.Equal("7,323 messages.", vm.StatusText);  // and no result left in the status text either
    }

    [Fact]
    public void TheRegion_IsHiddenWhenEmpty_AndReachedWithTheArrowKeys()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!Directory.Exists(Path.Combine(root.FullName, "QuickMail", "Views"))) root = root.Parent!;
        var xaml = File.ReadAllText(Path.Combine(root.FullName, "QuickMail", "Views", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root.FullName, "QuickMail", "Views", "MainWindow.xaml.cs"));

        Assert.Contains("Visibility=\"{Binding SenderAddressStatusText, Converter={StaticResource StringToVisibilityConverter}}\"",
            xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisibleChanged=\"SenderAddressStatusItem_IsVisibleChanged\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SenderAddressStatusTextBox.IsKeyboardFocused) return 5;", code, StringComparison.Ordinal);
        Assert.Contains("StatusBarFocusPolicy.ShouldMoveFocusOffHiddenRegion(", code, StringComparison.Ordinal);
    }

    [Theory]
    // Sitting on the region when it hides: focus moves to a neighbour (the user's manual test of #806 found
    // focus otherwise left on the hidden region until the next key press).
    [InlineData(false, true,  true,  true)]
    // On the region, but another application is in front: the window remembers the region, so coming back
    // would land on it.
    [InlineData(false, false, true,  true)]
    // Anywhere else in the window, or in another application with focus remembered elsewhere: left alone.
    [InlineData(false, false, false, false)]
    // The region appearing never moves anyone.
    [InlineData(true,  true,  true,  false)]
    [InlineData(true,  false, false, false)]
    public void FocusMovesOffTheHiddenRegion_OnlyForSomeoneOnIt(
        bool nowVisible, bool hasKeyboardFocus, bool hasLogicalFocus, bool moves)
        => Assert.Equal(moves, StatusBarFocusPolicy.ShouldMoveFocusOffHiddenRegion(nowVisible, hasKeyboardFocus, hasLogicalFocus));
}
