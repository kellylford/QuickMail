// Search beyond the cache (#717, phase 3): the query each server is sent, and Search the Server Too adding
// what the servers find to a Search Results folder.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class ServerSearchQueryTests
{
    private static MessageSearchQuery Q(string text) => MessageSearchQuery.Parse(text);

    [Fact]
    public void Gmail_UsesItsOwnOperators()
        => Assert.Equal(
            "budget -draft from:sam subject:\"quarterly report\" filename:pdf has:attachment is:unread is:starred after:2026/01/15 before:2026/02/01",
            ServerSearchQuery.ToGmailRaw(Q("budget -draft from:sam subject:\"quarterly report\" attachment:pdf has:attachment is:unread is:flagged after:2026-01-15 before:2026-02-01")));

    [Fact]
    public void Gmail_NothingToSearchFor_IsNull()
        => Assert.Null(ServerSearchQuery.ToGmailRaw(Q("folder:work account:home")));

    [Fact]
    public void Graph_UsesKql_AndLeavesReadAndFlagToTheCaller()
        => Assert.Equal(
            "budget AND NOT draft AND from:sam AND subject:\\\"quarterly report\\\" AND attachment:pdf AND hasAttachments:true AND received>=2026-01-15 AND received<2026-02-01",
            ServerSearchQuery.ToGraphKql(Q("budget -draft from:sam subject:\"quarterly report\" attachment:pdf has:attachment is:unread is:flagged after:2026-01-15 before:2026-02-01")));

    [Fact]
    public void AnExcludedWordTheServerCannotPlaceIsLeftOut_NotWidened()
    {
        // Gmail has no body-only operator and IMAP no attachment-name one, so sending these as a plain
        // exclusion would hide messages that have the word somewhere else. The caller checks them instead.
        Assert.Equal("budget", ServerSearchQuery.ToGmailRaw(Q("budget -body:draft")));
        Assert.Null(ServerSearchQuery.ToImap(Q("-attachment:draft")));
    }

    [Fact]
    public void WordsThatWouldReadAsSyntaxAreQuoted()
    {
        Assert.Equal("\\\"AND\\\" AND subject:\\\"(re) budget\\\"", ServerSearchQuery.ToGraphKql(Q("AND subject:\"(re) budget\"")));
        // A quote inside a word is dropped rather than closing the one around it.
        Assert.Equal("sayhi", ServerSearchQuery.ToGmailRaw(MessageSearchQuery.Parse("say\"hi")));
    }

    [Fact]
    public void Graph_OnlyExclusions_IsNull()
        => Assert.Null(ServerSearchQuery.ToGraphKql(Q("-draft is:unread")));

    [Fact]
    public void Imap_HasCriteriaForWordsAndConditions_AndNoneForNothing()
    {
        Assert.NotNull(ServerSearchQuery.ToImap(Q("budget -draft from:sam is:unread after:2026-01-15")));
        Assert.Null(ServerSearchQuery.ToImap(Q("has:attachment folder:work")));
    }
}

public class SearchServerTooTests
{
    private sealed class FixedAccounts(List<AccountModel> accounts) : IAccountService
    {
        public List<AccountModel> LoadAccounts() => [.. accounts];
        public void SaveAccounts(List<AccountModel> saved) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    /// <summary>A server that answers searches with a fixed list, and can fail for one account.</summary>
    private sealed class SearchingMail : StubImapMailServiceBase, IMailService
    {
        public Dictionary<Guid, List<MailMessageSummary>> Answers { get; } = [];
        public Guid? FailFor { get; set; }
        public List<(Guid Account, string Query, IReadOnlyList<string> Folders)> Asked { get; } = [];

        public Task<List<MailMessageSummary>> SearchServerAsync(Guid accountId, MessageSearchQuery query,
            IReadOnlyList<string> folderNames, int maxResults, CancellationToken ct = default)
        {
            Asked.Add((accountId, query.ToQueryString(), folderNames));
            if (accountId == FailFor) throw new InvalidOperationException("server said no");
            return Task.FromResult(Answers.TryGetValue(accountId, out var rows) ? [.. rows] : new List<MailMessageSummary>());
        }
    }

    private sealed class RowsStore(IEnumerable<MailMessageSummary> rows) : StubLocalStoreService
    {
        private readonly List<MailMessageSummary> _rows = [.. rows];
        public override Task<List<MailMessageSummary>> LoadAllSummariesAsync() => Task.FromResult(new List<MailMessageSummary>(_rows));
        public override Task<List<MailMessageSummary>> LoadAllSummariesAsync(Guid accountId) => LoadAllSummariesAsync();
        public override Task<List<MailMessageSummary>> LoadFolderSummariesAsync(Guid accountId, string folderName, int? limit = null) => LoadAllSummariesAsync();
    }

    private static MailMessageSummary Msg(Guid account, string id, string subject, bool read = false, string imid = "") => new()
    {
        MessageId = id, AccountId = account, FolderName = "INBOX", From = "Sam <sam@example.com>",
        To = "kelly@example.com", Subject = subject, IsRead = read, InternetMessageId = imid,
        Date = DateTimeOffset.Now.AddDays(-400),
    };

    private static readonly AccountModel Work = new() { AccountName = "Work", Username = "k@work.example" };
    private static readonly AccountModel Home = new() { AccountName = "Home", Username = "k@home.example" };

    /// <summary>Opens Search Results for <paramref name="query"/> over one cached message that matches it, so the folder
    /// opens (a search that finds nothing goes back where it started).</summary>
    private static async Task<(MainViewModel Vm, SearchingMail Mail)> OpenResultsAsync(string query)
    {
        var cached = new[] { new MailMessageSummary
        {
            MessageId = "cached", AccountId = Work.Id, FolderName = "INBOX", From = "Sam <sam@example.com>",
            To = "kelly@example.com", Subject = "Budget (cached)", Date = DateTimeOffset.Now,
        } };
        var mail = new SearchingMail();
        var vm = new MainViewModel(
            mail, new FixedAccounts([Work, Home]), new StubCredentialService(),
            new RowsStore(cached), new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService())
        {
            SearchIndexDelay = TimeSpan.Zero,
        };
        vm.LoadAccountList();
        await vm.InitialLoadAsync();
        await vm.RunAdvancedSearchAsync(new AdvancedSearchRequest(query, InCurrentFolder: false, [Work.Id, Home.Id]));
        return (vm, mail);
    }

    [Fact]
    public async Task AddsWhatTheServersFind_ThatTheResultsDoNotHave()
    {
        var (vm, mail) = await OpenResultsAsync("budget");
        Assert.True(vm.CanSearchServer);
        mail.Answers[Work.Id] = [Msg(Work.Id, "old-1", "Budget 2024")];
        mail.Answers[Home.Id] = [Msg(Home.Id, "old-2", "Budget 2023")];

        var outcome = await vm.SearchServerTooAsync();

        Assert.Equal(2, outcome.Added);
        Assert.Empty(outcome.FailedAccounts);
        Assert.Equal(["cached", "old-1", "old-2"], vm.Messages.Select(m => m.MessageId).Order());
        Assert.All(mail.Asked, a => Assert.Equal("budget", a.Query));
    }

    [Fact]
    public async Task NothingIsAddedTwice_AndConditionsStillApply()
    {
        var (vm, mail) = await OpenResultsAsync("budget is:unread");
        mail.Answers[Work.Id] =
        [
            Msg(Work.Id, "a", "Budget", imid: "<a@x>"),
            Msg(Work.Id, "a-copy", "Budget", imid: "<a@x>"),
            Msg(Work.Id, "read", "Budget", read: true),
        ];

        var first = await vm.SearchServerTooAsync();
        var second = await vm.SearchServerTooAsync();

        Assert.Equal(1, first.Added);
        Assert.Equal(0, second.Added);
        // One of the two copies of <a@x>, and not the read message.
        Assert.Equal(2, vm.Messages.Count);
        Assert.DoesNotContain(vm.Messages, m => m.MessageId == "read");
    }

    [Fact]
    public async Task AnAccountThatFailsIsNamed_AndTheOthersStillCount()
    {
        var (vm, mail) = await OpenResultsAsync("budget");
        mail.FailFor = Home.Id;
        mail.Answers[Work.Id] = [Msg(Work.Id, "w", "Budget")];

        var outcome = await vm.SearchServerTooAsync();

        Assert.Equal(1, outcome.Added);
        Assert.Equal(["Home"], outcome.FailedAccounts);
        Assert.Equal(2, outcome.Asked);
    }

    [Fact]
    public async Task NothingOnThisComputer_ButAskingTheServerFromTheForm_OpensItsResults()
    {
        // The case phase 3 exists for: mail older than the sync range. Without the form's check box the
        // results folder would close on an empty local search and leave no way to ask the server.
        var mail = new SearchingMail();
        var vm = new MainViewModel(
            mail, new FixedAccounts([Work, Home]), new StubCredentialService(), new RowsStore([]),
            new StubOAuthService(), new StubSyncService(), new StubConfigService(), new StubCommandRegistry(),
            new StubViewService(), new StubRuleService(), new StubSmtpService())
        {
            SearchIndexDelay = TimeSpan.Zero,
        };
        vm.LoadAccountList();
        await vm.InitialLoadAsync();
        mail.Answers[Work.Id] = [Msg(Work.Id, "ancient", "Budget 2019")];

        var outcome = await vm.RunAdvancedSearchAsync(
            new AdvancedSearchRequest("budget", InCurrentFolder: false, [Work.Id, Home.Id], SearchServer: true));

        Assert.Equal(1, outcome.Found);
        Assert.True(vm.IsSearchResultsView);
        Assert.Equal(["ancient"], vm.Messages.Select(m => m.MessageId));
        Assert.Equal(2, outcome.Server!.Asked);
        // Change Search reopens with the server box still checked.
        Assert.True(vm.CurrentSearchResultsRequest!.SearchServer);
    }

    [Fact]
    public async Task NothingAnywhere_GoesBackWhereItStarted_EvenAfterAskingTheServer()
    {
        var mail = new SearchingMail();
        var vm = new MainViewModel(
            mail, new FixedAccounts([Work]), new StubCredentialService(), new RowsStore([]),
            new StubOAuthService(), new StubSyncService(), new StubConfigService(), new StubCommandRegistry(),
            new StubViewService(), new StubRuleService(), new StubSmtpService());
        vm.LoadAccountList();
        await vm.InitialLoadAsync();
        var before = vm.SelectedFolder?.FullName;

        var outcome = await vm.RunAdvancedSearchAsync(
            new AdvancedSearchRequest("zebra", InCurrentFolder: false, [Work.Id], SearchServer: true));

        Assert.Equal(0, outcome.Found);
        Assert.Single(mail.Asked);
        Assert.Equal(before, vm.SelectedFolder?.FullName);
    }

    [Fact]
    public async Task OutsideSearchResults_ItDoesNothing()
    {
        var mail = new SearchingMail();
        var vm = new MainViewModel(
            mail, new FixedAccounts([Work]), new StubCredentialService(), new RowsStore([]),
            new StubOAuthService(), new StubSyncService(), new StubConfigService(), new StubCommandRegistry(),
            new StubViewService(), new StubRuleService(), new StubSmtpService());
        vm.LoadAccountList();
        await vm.InitialLoadAsync();

        Assert.False(vm.CanSearchServer);
        Assert.Equal(0, (await vm.SearchServerTooAsync()).Asked);
        Assert.Empty(mail.Asked);
    }
}

public class SavedSearchTests
{
    [Fact]
    public void SavingAViewFromSearchResults_KeepsTheSearch()
    {
        var accounts = new[] { Guid.NewGuid() };
        var folder = MainViewModel.CreateSearchResultsFolder("from:sam budget", accounts);
        var vm = new ViewManagerViewModel(new StubViewService(), new StubConfigService(), new StubCommandRegistry(),
            [], folder, null, ViewMode.Messages, MessageFilter.All, MessageSort.DateDescending);

        vm.SaveAsNewCommand.Execute(null);

        var key = vm.SelectedView!.VirtualFolderKey;
        Assert.NotNull(key);
        Assert.Empty(vm.SelectedView.Folders);
        Assert.True(MainViewModel.TryGetSearchResultsFromSentinel("\u0000" + key, out var query, out var ids));
        Assert.Equal("from:sam budget", query);
        Assert.Equal(accounts, ids);
    }
}

public class OfflineBodiesFolderChoiceTests
{
    private static MailFolderModel F(string name, SpecialFolderKind kind = SpecialFolderKind.None, bool excluded = false)
        => new() { FullName = name, DisplayName = name, Kind = kind, ExcludeFromAllMail = excluded };

    private static readonly MailFolderModel[] Folders =
    [
        F("Projects"), F("INBOX", SpecialFolderKind.Inbox), F("Sent", SpecialFolderKind.Sent),
        F("Trash", SpecialFolderKind.Trash), F("Junk", SpecialFolderKind.Junk), F("Drafts", SpecialFolderKind.Drafts),
        F("[Gmail]/All Mail", SpecialFolderKind.AllMail), F("Hidden", excluded: true),
        new MailFolderModel { FullName = string.Empty, DisplayName = "Header", IsHeader = true },
    ];

    [Fact]
    public void GmailLabelFoldersAreLeftOut_TheirMailIsInItsRealFolderToo()
        => Assert.DoesNotContain(
            SyncService.FoldersForBodies(
                [F("INBOX", SpecialFolderKind.Inbox), F("[Gmail]/Important", SpecialFolderKind.Important),
                 F("[Gmail]/Starred", SpecialFolderKind.Starred)], allFolders: true),
            f => f.Kind is SpecialFolderKind.Important or SpecialFolderKind.Starred);

    [Fact]
    public void InboxOnlyByDefault()
        => Assert.Equal(["INBOX"], SyncService.FoldersForBodies(Folders, allFolders: false).Select(f => f.FullName));

    [Fact]
    public void AllFolders_InboxFirst_SentIncluded_LeavingOutTrashJunkDraftsAndAllMail()
        => Assert.Equal(["INBOX", "Projects", "Sent", "Hidden"], SyncService.FoldersForBodies(Folders, allFolders: true).Select(f => f.FullName));
}
