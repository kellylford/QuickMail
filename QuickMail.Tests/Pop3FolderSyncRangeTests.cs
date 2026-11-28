// A POP3 folder lists everything it holds, whatever View > Sync Range says (#1113).
//
// The bug: opening a folder replaced the list with the Sync Range's window (30 days by default),
// read from the store by each message's Date header, while the folder's unread count counted every
// row. Mail older than the range was counted but could not be seen, so a user who deleted everything
// a folder listed was left with "600 unread" and an empty folder. For POP3 the store is the only
// copy, so the range limited no download; it only hid mail.
//
// These run against the REAL LocalStoreService and Pop3MailService, behind the real
// MailServiceRouter as in the app, driven through MainViewModel: the mismatch was between two SQL
// queries, and a fake store would agree with itself.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public sealed class Pop3FolderSyncRangeTests : IDisposable
{
    private static readonly Guid Account = Guid.NewGuid();
    private readonly List<string> _dirs = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var dir in _dirs)
            try { Directory.Delete(dir, recursive: true); } catch { }
    }

    private static MailMessageSummary Msg(string id, string folder, int daysAgo, bool isRead = false) => new()
    {
        MessageId  = id,
        AccountId  = Account,
        FolderName = folder,
        From       = "List <list@example.com>",
        To         = "me@example.com",
        Subject    = $"Message {id}",
        Date       = DateTimeOffset.UtcNow.AddDays(-daysAgo),
        IsRead     = isRead,
    };

    private Fixture NewFixture()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"QuickMailPop3Range-{Guid.NewGuid():N}");
        _dirs.Add(dir);
        return new Fixture(dir);
    }

    private sealed class Fixture
    {
        public LocalStoreService Store { get; }
        public Pop3MailService Pop { get; }
        public MailServiceRouter Router { get; }
        public MainViewModel Vm { get; private set; } = null!;
        public MailFolderModel Lists { get; private set; } = null!;

        public Fixture(string dir)
        {
            Store = new LocalStoreService(new ProfileContext(dir));
            Store.Initialize();
            Pop = new Pop3MailService(Store);
            // As App.xaml.cs wires it: IMAP is the router's default, and the POP3 account is bound to
            // its own backend. A router that stopped forwarding the POP3 answers would fall back to
            // the interface defaults and bring the bug back, so the VM talks to the router.
            Router = new MailServiceRouter([new StubImapMailService(), Pop]);
            Router.RegisterAccount(Account, Pop);
        }

        /// <summary>A local folder holding <paramref name="messages"/>, as rules filing list mail leave one.</summary>
        public async Task<Fixture> WithFolderAsync(Func<string, IEnumerable<MailMessageSummary>> messages)
        {
            await Pop.CreateFolderAsync(Account, null, "Lists");
            Lists = (await Pop.GetFoldersAsync(Account)).Single(f => f.DisplayName == "Lists");
            await Store.UpsertSummariesAsync(messages(Lists.FullName).ToList());
            // Startup draws the tree, counts included, from the folder cache.
            await Store.SaveFoldersAsync(Account, await Pop.GetFoldersAsync(Account));

            // SyncDays is left at ConfigModel's default, 30 days.
            Vm = new MainViewModel(
                Router, new StubAccountService(), new StubCredentialService(), Store,
                new StubOAuthService(), new StubSyncService(), new StubConfigService(),
                new StubCommandRegistry(), new StubViewService(), new StubRuleService(),
                new StubSmtpService());
            Vm.Accounts.Add(new AccountModel { Id = Account, AccountName = "POP", Username = "me@example.com" });
            await Vm.InitialLoadAsync();
            return this;
        }

        public MailFolderModel CachedLists => Vm.CachedFolders[Account].Single(f => f.FullName == Lists.FullName);

        public async Task OpenListsAsync(int timeoutMs = 15000)
        {
            await Vm.SelectFolderCommand.ExecuteAsync(CachedLists);
            await LoadFinishedAsync(Vm, timeoutMs);
        }
    }

    /// <summary>
    /// Waits for a folder load to finish. Opening a folder shows the cached rows first and then
    /// replaces them with the backend's answer, in a refresh that is fire-and-forget. IsBusy stays set
    /// until that refresh ends, so it is what says the rows on screen are the final ones: waiting for
    /// a row count instead would pass on the cached rows, before the refresh could take any away.
    /// </summary>
    private static Task LoadFinishedAsync(MainViewModel vm, int timeoutMs = 15000)
        => Until(() => !vm.IsBusy, "the folder load to finish (IsBusy to clear)", timeoutMs);

    private static async Task Until(Func<bool> done, string waitingFor, int timeoutMs = 15000, Func<string>? state = null)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(done(), $"Gave up after {timeoutMs} ms waiting for {waitingFor}" + (state is null ? "." : $"; {state()}."));
    }

    private static IEnumerable<MailMessageSummary> OldAndNew(string folder) =>
    [
        Msg("new1", folder, daysAgo: 2),
        Msg("new2", folder, daysAgo: 10, isRead: true),
        Msg("old1", folder, daysAgo: 90),
        Msg("old2", folder, daysAgo: 400),
        Msg("old3", folder, daysAgo: 45, isRead: true),
    ];

    // ── The cause ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheSweepsWindowedRead_SeesLessThanTheFolderCount()
    {
        // Records how things are today, not a requirement. The sweep still reads POP3 folders through
        // the Sync Range window (this PR leaves the sweep alone), and that read sees less than
        // GetFoldersAsync counts — the gap that, used for the folder LIST, was #1113. If the sweep's
        // window is ever changed on purpose, change or drop this test; it guards nothing a user needs.
        var f = NewFixture();
        await f.Pop.CreateFolderAsync(Account, null, "Lists");
        var lists = (await f.Pop.GetFoldersAsync(Account)).Single(x => x.DisplayName == "Lists");
        await f.Store.UpsertSummariesAsync(OldAndNew(lists.FullName).ToList());

        var windowed = await f.Pop.GetMessagesSinceDateAsync(Account, lists.FullName, DateTime.UtcNow.AddDays(-30));
        var counted  = (await f.Pop.GetFoldersAsync(Account)).Single(x => x.FullName == lists.FullName);

        Assert.Equal(1, windowed.Count(m => !m.IsRead));
        Assert.Equal(3, counted.UnreadCount);
    }

    [Fact]
    public void TheRouter_ReportsWhereEachAccountsFoldersLive()
    {
        var f = NewFixture();
        Assert.True(f.Router.FoldersLiveInLocalStore(Account));
        Assert.False(f.Router.FoldersLiveInLocalStore(Guid.NewGuid()));   // unregistered: the IMAP default
    }

    // ── The fix ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpeningAPop3Folder_ListsMailOlderThanTheSyncRange()
    {
        var f = await NewFixture().WithFolderAsync(OldAndNew);

        await f.OpenListsAsync();

        Assert.Equal(["new1", "new2", "old3", "old1", "old2"], f.Vm.Messages.Select(m => m.MessageId));
    }

    [Fact]
    public async Task APop3FoldersUnreadCount_EqualsTheUnreadRowsItLists()
    {
        var f = await NewFixture().WithFolderAsync(OldAndNew);

        await f.OpenListsAsync();

        Assert.Equal(3, f.CachedLists.UnreadCount);
        Assert.Equal(f.CachedLists.UnreadCount, f.Vm.Messages.Count(m => !m.IsRead));
    }

    [Fact]
    public async Task DeletingEverythingAPop3FolderLists_BringsItsUnreadCountToZero()
    {
        // The user's report: delete all listed, and the folder still said "600 unread". This also
        // answers the issue's second question: the count does follow a POP3 delete (the debounced
        // refresh recounts the store), once the list shows every row there is to delete. The wait
        // covers the refresh's one-second debounce.
        var f = await NewFixture().WithFolderAsync(OldAndNew);
        await f.OpenListsAsync();
        Assert.Equal(3, f.CachedLists.UnreadCount);

        await f.Vm.DeleteMessagesAsync(f.Vm.Messages.ToList());

        Assert.Empty(f.Vm.Messages);
        await Until(() => f.CachedLists.UnreadCount == 0, "the Lists folder's unread count to reach 0",
            state: () => $"it is {f.CachedLists.UnreadCount}");
        Assert.Empty(await f.Store.GetAllMessageIdsAsync(Account, f.Lists.FullName));
        Assert.Equal(5, (await f.Store.GetAllMessageIdsAsync(Account, Pop3MailService.TrashFolder)).Count);
    }

    [Fact]
    public async Task APop3FolderListsEverything_AtTheAllMailRangeToo()
    {
        var f = await NewFixture().WithFolderAsync(OldAndNew);
        await f.Vm.SetSyncDaysCommand.ExecuteAsync("0");

        await f.OpenListsAsync();

        Assert.Equal(5, f.Vm.Messages.Count);
    }

    [Fact]
    public async Task OpeningALargePop3Folder_StaysQuick()
    {
        // ~5,000 rows, nine in ten older than the range: what a few years of a mailing list filed by
        // a rule looks like. The bound is generous on purpose (CI runners vary); the timing is
        // written to the test output for the record.
        const int rows = 5000;
        const int limitMs = 20000;
        var f = await NewFixture().WithFolderAsync(folder =>
            Enumerable.Range(0, rows).Select(i => Msg($"m{i}", folder, daysAgo: i % 10 == 0 ? i % 30 : 31 + i % 1500, isRead: i % 3 == 0)));

        var sw = Stopwatch.StartNew();
        await f.OpenListsAsync(limitMs);
        sw.Stop();

        Assert.Equal(rows, f.Vm.Messages.Count);
        Assert.Equal(f.CachedLists.UnreadCount, f.Vm.Messages.Count(m => !m.IsRead));
        TestContext.Current.TestOutputHelper?.WriteLine($"Opened a POP3 folder of {rows} rows in {sw.ElapsedMilliseconds} ms");
    }

    // ── A folder load is not written back for a store-backed account ──────────

    /// <summary>
    /// A backend whose folder loads come from wherever <see cref="Load"/> says: a POP3-shaped one
    /// (folders in the local store, listing proves no deletion) or, with <see cref="InLocalStore"/>
    /// false, a server-shaped one.
    /// </summary>
    private sealed class ShapedMail : StubImapMailServiceBase, IMailService
    {
        public bool InLocalStore { get; init; } = true;
        public required List<MailFolderModel> Folders { get; init; }
        public required Func<string, DateTime, Task<List<MailMessageSummary>>> Load { get; init; }

        bool IMailService.FoldersLiveInLocalStore(Guid accountId) => InLocalStore;
        bool IMailService.ListingIsAuthoritativeForDeletions(Guid accountId) => !InLocalStore;

        public override Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default)
            => Task.FromResult(Folders.ToList());
        public override Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(Guid accountId, string folderName, DateTime since, CancellationToken ct = default)
            => Load(folderName, since);
        public override Task<List<MailMessageSummary>> GetMessageSummariesAsync(Guid accountId, string folderName, int maxMessages, CancellationToken ct = default)
            => Load(folderName, DateTime.MinValue);
    }

    /// <summary>A store that records every summary written to it.</summary>
    private sealed class RecordingStore : StubLocalStoreService
    {
        private readonly object _gate = new();
        private readonly List<MailMessageSummary> _written = [];
        public List<MailMessageSummary> Written { get { lock (_gate) return [.. _written]; } }

        public override Task UpsertSummariesAsync(IEnumerable<MailMessageSummary> summaries)
        {
            lock (_gate) _written.AddRange(summaries);
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpeningAFolder_WritesItsRowsBack_OnlyForAServerBackedAccount(bool inLocalStore)
    {
        // #1113 review: a POP3 folder now lists every row it holds, and those rows were just read from
        // the store. Writing them back would rewrite the whole folder on the UI thread on every open.
        // A server-backed account must still cache what it fetched; that half is the control, showing
        // the recording store does see the write when there is one.
        var inbox = new MailFolderModel { AccountId = Account, FullName = "Inbox", DisplayName = "Inbox", Kind = SpecialFolderKind.Inbox };
        var rows  = OldAndNew("Inbox").ToList();
        var mail  = new ShapedMail { InLocalStore = inLocalStore, Folders = [inbox], Load = (_, _) => Task.FromResult(rows.ToList()) };
        var store = new RecordingStore();
        store.SeededFolders[Account] = [inbox];
        var log   = new List<string>();
        var vm = new MainViewModel(
            mail, new StubAccountService(), new StubCredentialService(), store,
            new StubOAuthService(), new CacheAndSettleRulesTests.RecordingSync(log), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(),
            new StubSmtpService());
        vm.Accounts.Add(new AccountModel { Id = Account, AccountName = "Mail", Username = "me@example.com" });
        await vm.InitialLoadAsync();
        var writtenBefore = store.Written.Count;

        await vm.SelectFolderCommand.ExecuteAsync(inbox);
        await LoadFinishedAsync(vm);
        // The rules pass is asked for either way: mail a POP3 Inbox collected on the open is waiting for it.
        await Until(() => log.Contains("settle Inbox"), "the rules pass to be asked for on the Inbox",
            state: () => $"the sync was asked: {string.Join(", ", log)}");

        var written = store.Written.Skip(writtenBefore).ToList();
        Assert.Equal(5, vm.Messages.Count);
        if (inLocalStore)
            Assert.Empty(written);
        else
            Assert.Equal(rows.Select(r => r.MessageId).Order(), written.Select(r => r.MessageId).Order());
    }

    // ── All Inboxes with a big POP3 Inbox ───────────────────────────────────────

    /// <summary>
    /// A POP3 Inbox of <paramref name="rows"/> messages, about a tenth of them in the Sync Range,
    /// threaded the way list mail is (a few thousand subjects, replies among them).
    /// </summary>
    private static IEnumerable<MailMessageSummary> BigInbox(string folder, int rows) =>
        Enumerable.Range(0, rows).Select(i =>
        {
            var m = Msg($"m{i}", folder, daysAgo: i % 10 == 0 ? i % 30 : 31 + i % 2000, isRead: i % 3 != 0);
            m.Subject = (i % 2 == 0 ? "Re: " : "") + $"Thread {i % 4000}";
            m.From    = $"Sender {i % 300} <s{i % 300}@example.com>";
            m.InternetMessageId = $"<m{i}@example.com>";
            return m;
        });

    /// <summary>
    /// How long All Inboxes may take for <see cref="AllInboxesRows"/> rows. Measured in the test VM on
    /// 2026-11-27 at 30,000 rows: 285 ms for the list, 661 ms with conversations view on. Five
    /// seconds leaves room for a slow CI runner and still fails well before anything quadratic would.
    /// </summary>
    private const int AllInboxesLimitMs = 5000;

    private static int AllInboxesRows =>
        int.TryParse(Environment.GetEnvironmentVariable("QUICKMAIL_ALLINBOXES_ROWS"), out var n) && n > 0 ? n : 30000;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllInboxes_WithABigPop3Inbox_StaysQuick(bool conversations)
    {
        // All Inboxes has no cache phase: it shows what the fetch returns, and a POP3 Inbox now returns
        // every row it holds. This times that load, from the real store, at a size a mailing-list
        // subscriber can reach. QUICKMAIL_ALLINBOXES_ROWS overrides the size for a measurement run.
        var rows = AllInboxesRows;
        var f = NewFixture();
        var inbox = new MailFolderModel { AccountId = Account, FullName = "Inbox", DisplayName = "Inbox", Kind = SpecialFolderKind.Inbox };
        await f.Store.UpsertSummariesAsync(BigInbox("Inbox", rows).ToList());
        var mail = new ShapedMail
        {
            Folders = [inbox],
            Load = (folder, since) => f.Store.LoadFolderSummariesSinceAsync(
                Account, folder, new DateTimeOffset(DateTime.SpecifyKind(since, DateTimeKind.Utc), TimeSpan.Zero)),
        };
        var vm = new MainViewModel(
            mail, new StubAccountService(), new StubCredentialService(), f.Store,
            new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(),
            new StubSmtpService());
        // OAuth only so ConnectAllAccountsAsync connects it without a stored password: All Inboxes
        // fetches only from connected accounts.
        vm.Accounts.Add(new AccountModel { Id = Account, AccountName = "POP", Username = "me@example.com", AuthType = AuthType.OAuth2Microsoft });
        await vm.ConnectAllAccountsAsync();
        if (conversations) vm.ViewMode = ViewMode.Conversations;

        var sw = Stopwatch.StartNew();
        await vm.SelectFolderCommand.ExecuteAsync(MainViewModel.AllInboxesFolder);
        await Until(() => !vm.IsBusy && vm.Messages.Count == rows && (!conversations || vm.Conversations.Count > 0),
            $"All Inboxes to show all {rows} rows" + (conversations ? " and their conversations" : ""),
            AllInboxesLimitMs, () => $"{vm.Messages.Count} rows, {vm.Conversations.Count} conversations, busy={vm.IsBusy}");
        sw.Stop();

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"All Inboxes, one POP3 Inbox of {rows} rows{(conversations ? ", conversations view" : "")}: {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.ElapsedMilliseconds < AllInboxesLimitMs, $"took {sw.ElapsedMilliseconds} ms, limit {AllInboxesLimitMs} ms");
    }

    // ── Other backends keep the window ─────────────────────────────────────────

    private sealed class RecordingServerMail : StubImapMailServiceBase
    {
        public DateTime? Since;
        public override Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(Guid accountId, string folderName, DateTime since, CancellationToken ct = default)
        {
            Since = since;
            return Task.FromResult(new List<MailMessageSummary>());
        }
    }

    [Fact]
    public async Task AServerBackedFolder_IsStillListedWithinTheSyncRange()
    {
        var mail  = new RecordingServerMail();
        var store = new StubLocalStoreService();
        var inbox = new MailFolderModel { AccountId = Account, FullName = "INBOX", DisplayName = "Inbox", Kind = SpecialFolderKind.Inbox };
        store.SeededFolders[Account] = [inbox];
        var vm = new MainViewModel(
            mail, new StubAccountService(), new StubCredentialService(), store,
            new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(),
            new StubSmtpService());
        vm.Accounts.Add(new AccountModel { Id = Account, AccountName = "IMAP", Username = "me@example.com" });
        await vm.InitialLoadAsync();

        await vm.SelectFolderCommand.ExecuteAsync(inbox);
        await Until(() => mail.Since != null, "the folder load to ask the backend for the Inbox");

        Assert.False(((IMailService)mail).FoldersLiveInLocalStore(Account));
        var expected = DateTime.UtcNow.AddDays(-30);
        Assert.InRange(mail.Since!.Value, expected.AddMinutes(-5), expected.AddMinutes(5));
    }

    // ── All Mail does not drop POP3 rows a fetch did not return ────────────────

    /// <summary>
    /// A POP3-shaped backend for All Mail's merge: its listing proves no deletion, and its fetch
    /// returns the window plus a message collected just now with an old Date header, as
    /// Pop3MailService.GetMessagesSinceDateAsync does for resent or delayed mail.
    /// </summary>
    private sealed class CollectsOldDatedMail : StubImapMailServiceBase, IMailService
    {
        public required MailFolderModel Inbox { get; init; }
        public bool Fetched;

        bool IMailService.ListingIsAuthoritativeForDeletions(Guid accountId) => false;

        public override Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default)
            => Task.FromResult(new List<MailFolderModel> { Inbox });

        public override Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(Guid accountId, string folderName, DateTime since, CancellationToken ct = default)
        {
            Fetched = true;
            return Task.FromResult(new List<MailMessageSummary>
            {
                Msg("recent", Inbox.FullName, daysAgo: 1),
                Msg("resent", Inbox.FullName, daysAgo: 200),
            });
        }
    }

    [Fact]
    public async Task AllMail_KeepsPop3RowsTheWindowedFetchDidNotReturn()
    {
        var inbox = new MailFolderModel { AccountId = Account, FullName = "Inbox", DisplayName = "Inbox", Kind = SpecialFolderKind.Inbox };
        var mail  = new CollectsOldDatedMail { Inbox = inbox };
        var store = new StubLocalStoreService();
        store.SeededFolders[Account] = [inbox];
        store.SeededSummaries[(Account, "Inbox")] =
        [
            Msg("recent", "Inbox", daysAgo: 1),
            Msg("older1", "Inbox", daysAgo: 60),
            Msg("older2", "Inbox", daysAgo: 100),
        ];
        var vm = new MainViewModel(
            mail, new StubAccountService(), new StubCredentialService(), store,
            new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(),
            new StubSmtpService());
        // An OAuth sign-in, only so ConnectAllAccountsAsync connects it without a stored password: All
        // Mail fetches only from connected accounts. What matters here is the backend's answers.
        vm.Accounts.Add(new AccountModel { Id = Account, AccountName = "POP", Username = "me@example.com", AuthType = AuthType.OAuth2Microsoft });
        await vm.ConnectAllAccountsAsync();

        await vm.SelectFolderCommand.ExecuteAsync(MainViewModel.AllMailFolder);
        await Until(() => mail.Fetched && !vm.IsBusy && vm.Messages.Any(m => m.MessageId == "resent"),
            "All Mail to fetch from the account and show the just-collected \"resent\" row");

        // Before the fix, "resent" (200 days) widened the fetched range past both older rows, and the
        // merge removed them as deleted elsewhere.
        Assert.Contains(vm.Messages, m => m.MessageId == "older1");
        Assert.Contains(vm.Messages, m => m.MessageId == "older2");
    }
}
