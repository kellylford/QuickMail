// Advanced Search (#717, phase 2): the fields build the search box's query, a reopened search spreads
// back over them, and across accounts the results become a Search Results folder built from the store.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class AdvancedSearchViewModelTests
{
    private static readonly Guid Work = Guid.NewGuid();
    private static readonly Guid Home = Guid.NewGuid();

    private static AdvancedSearchViewModel Vm(string? folder = "Inbox", AdvancedSearchRequest? previous = null)
        => new([(Work, "Work"), (Home, "Home")], folder, previous);

    private static AdvancedSearchRequest? Run(AdvancedSearchViewModel vm)
    {
        AdvancedSearchRequest? request = null;
        vm.SearchRequested += r => request = r;
        vm.SearchCommand.Execute(null);
        return request;
    }

    [Fact]
    public void EachFieldPutsItsWordsInItsOwnPlace()
    {
        var vm = Vm();
        vm.Words = "budget -draft";
        vm.From = "Sam Smith";
        vm.Cc = "lee";
        vm.Subject = "\"quarterly report\"";
        vm.Attachment = "pdf";
        vm.HasAttachments = true;
        vm.ReadState = AdvancedSearchViewModel.ReadChoices[1];
        vm.FlagState = AdvancedSearchViewModel.FlagChoices[2];
        vm.ReceivedFrom = new DateTime(2026, 1, 15);
        vm.ReceivedTo = new DateTime(2026, 1, 31);

        Assert.Equal(
            "budget -draft from:Sam from:Smith cc:lee subject:\"quarterly report\" attachment:pdf " +
            "has:attachment is:unread is:unflagged after:2026-01-15 before:2026-02-01",
            vm.BuildQuery().ToQueryString());
    }

    [Fact]
    public void ThisFolderIsTheDefault_WhenThereIsOne()
    {
        var vm = Vm();
        vm.Words = "budget";
        var request = Run(vm);
        Assert.NotNull(request);
        Assert.True(request.InCurrentFolder);
    }

    [Fact]
    public void WithNoFolderOnScreen_ItSearchesTheChosenAccounts()
    {
        var vm = Vm(folder: null);
        Assert.False(vm.CanSearchCurrentFolder);
        vm.Words = "budget";
        vm.Accounts[1].IsChosen = false;

        var request = Run(vm);

        Assert.NotNull(request);
        Assert.False(request.InCurrentFolder);
        Assert.Equal([Work], request.AccountIds);
    }

    [Fact]
    public void NothingToSearchFor_SaysSo_AndRequestsNothing()
    {
        var vm = Vm();
        Assert.Null(Run(vm));
        Assert.Equal("Enter something to search for.", vm.Problem);
    }

    [Fact]
    public void NoAccountChosen_SaysSo()
    {
        var vm = Vm();
        vm.Words = "budget";
        vm.SearchInAccounts = true;
        foreach (var a in vm.Accounts) a.IsChosen = false;
        Assert.Null(Run(vm));
        Assert.Equal("Choose at least one account to search.", vm.Problem);
    }

    [Fact]
    public void DatesTheWrongWayRound_SaysSo()
    {
        var vm = Vm();
        vm.ReceivedFrom = new DateTime(2026, 2, 1);
        vm.ReceivedTo = new DateTime(2026, 1, 1);
        Assert.Null(Run(vm));
        Assert.Contains("before", vm.Problem);
    }

    [Fact]
    public void AReopenedSearchSpreadsBackOverTheFields()
    {
        var previous = new AdvancedSearchRequest(
            "budget folder:Projects from:\"Sam Smith\" -cc:lee is:read is:flagged after:2026-01-15 before:2026-02-01",
            InCurrentFolder: false, [Home]);

        var vm = Vm(previous: previous);

        Assert.Equal("budget folder:Projects", vm.Words);
        Assert.Equal("\"Sam Smith\"", vm.From);
        Assert.Equal("-lee", vm.Cc);
        Assert.True(vm.ReadState.Value);
        Assert.True(vm.FlagState.Value);
        Assert.Equal(new DateTime(2026, 1, 15), vm.ReceivedFrom);
        Assert.Equal(new DateTime(2026, 1, 31), vm.ReceivedTo);
        Assert.True(vm.SearchInAccounts);
        Assert.Equal([Home], vm.Accounts.Where(a => a.IsChosen).Select(a => a.Id));
        Assert.Equal(MessageSearchQuery.Parse(previous.Query).ToQueryString(), vm.BuildQuery().ToQueryString());
    }

    [Fact]
    public void WhatIsTypedInAFieldIsWordsOfThatField_EvenIfItLooksLikeACondition()
    {
        var vm = Vm();
        vm.From = "is:unread -spam \"Sam Smith\"";
        Assert.Equal(
            [new SearchTerm(SearchField.From, "is:unread"), new SearchTerm(SearchField.From, "spam", Negated: true),
             new SearchTerm(SearchField.From, "Sam Smith", IsPhrase: true)],
            vm.BuildQuery().Terms);
    }

    [Fact]
    public void AReopenedFieldReadsBackAsTyped()
    {
        var vm = Vm();
        vm.From = "a:b";
        vm.Words = "budget -has:attachment";
        var reopened = Vm(previous: new AdvancedSearchRequest(vm.BuildQuery().ToQueryString(), InCurrentFolder: true, []));
        Assert.Equal("a:b", reopened.From);
        Assert.Contains("-has:attachment", reopened.Words);
        Assert.Equal(vm.BuildQuery().ToQueryString(), reopened.BuildQuery().ToQueryString());
    }

    [Fact]
    public void AlsoSearchTheServer_GoesWithAnAccountSearch_NotWithThisFolder()
    {
        var vm = Vm();
        vm.Words = "budget";
        vm.AlsoSearchServer = true;
        Assert.False(Run(vm)!.SearchServer);   // This folder is searched on this computer only.

        vm.SearchInAccounts = true;
        var request = Run(vm)!;
        Assert.True(request.SearchServer);

        // Reopening keeps the choice.
        Assert.True(Vm(previous: request).AlsoSearchServer);
    }

    [Fact]
    public void WhenNothingIsFound_ItSaysWhyTheServerDidNotHelp()
    {
        var accounts = new AdvancedSearchRequest("x", InCurrentFolder: false, [Guid.NewGuid()]);
        var asked = accounts with { SearchServer = true };
        MainViewModel.AdvancedSearchOutcome None(MainViewModel.ServerSearchOutcome? s = null) => new(0, false, Server: s);

        Assert.Equal("No messages found.", QuickMail.Views.AdvancedSearchWindow.NothingFoundText(accounts with { InCurrentFolder = true }, None()));
        Assert.Contains("Check Also search the mail server", QuickMail.Views.AdvancedSearchWindow.NothingFoundText(accounts, None()));
        Assert.Contains("No account's mail server could be searched",
            QuickMail.Views.AdvancedSearchWindow.NothingFoundText(asked, None(new(0, [], 0))));
        Assert.Contains("could not search the server",
            QuickMail.Views.AdvancedSearchWindow.NothingFoundText(asked, None(new(0, ["Work"], 1))));
        Assert.Equal("No messages found.", QuickMail.Views.AdvancedSearchWindow.NothingFoundText(asked, None(new(0, [], 1))));
        // The server's part unknown (a search already running from the results bar): no claim about it.
        Assert.Equal("No messages found.", QuickMail.Views.AdvancedSearchWindow.NothingFoundText(asked, None()));
        Assert.Equal("Could not search.", QuickMail.Views.AdvancedSearchWindow.NothingFoundText(asked, new(0, true)));
    }

    [Fact]
    public void AnAccountNameWithAnUnderscoreIsShownAsWritten()
    {
        var account = new AdvancedSearchAccount(Guid.NewGuid(), "my_work", isChosen: true);
        Assert.Equal("my__work", account.DisplayName);   // doubled, so the check box shows one underscore
        Assert.Equal("my_work", account.ToString());
    }

    [Fact]
    public void ClearKeepsWhereToLook()
    {
        var vm = Vm();
        vm.Words = "x";
        vm.SearchInAccounts = true;
        vm.ClearCommand.Execute(null);
        Assert.Equal(string.Empty, vm.Words);
        Assert.True(vm.SearchInAccounts);
    }

    [Fact]
    public void TheFolderChoiceNamesTheFolder_WithNoAccessKeyAndUnderscoresShownAsThemselves()
    {
        // The text is a binding, so an access key written into it is not markup: it is read out and shown
        // as an underscore ("Th_is folder (All Inboxes)").
        Assert.Equal("This folder (All Inboxes)", Vm(folder: "All Inboxes").CurrentFolderChoiceLabel);
        Assert.Equal("This folder (my__stuff)", Vm(folder: "my_stuff").CurrentFolderChoiceLabel);
        Assert.Equal("This folder", Vm(folder: null).CurrentFolderChoiceLabel);
    }
}

/// <summary>The Search Results query on a real store: the index, the rows and the conditions together.</summary>
public class SearchSummariesStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"qm-searchsum-{Guid.NewGuid():N}");
    private readonly LocalStoreService _store;
    private readonly Guid _work = Guid.NewGuid();
    private readonly Guid _home = Guid.NewGuid();

    public SearchSummariesStoreTests()
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

    private MailMessageSummary Row(Guid account, string id, string subject = "Hello", string folder = "INBOX",
        bool read = false, int daysAgo = 0, string from = "Sam <sam@example.com>") => new()
    {
        MessageId = id, AccountId = account, FolderName = folder, From = from, To = "kelly@example.com",
        Subject = subject, IsRead = read, Date = DateTimeOffset.UtcNow.AddDays(-daysAgo),
    };

    private async Task<List<string>> Search(string query, params Guid[] accounts)
    {
        var rows = await _store.SearchSummariesAsync(MessageSearchQuery.Parse(query),
            accounts.Length == 0 ? [_work, _home] : accounts, TestContext.Current.CancellationToken);
        return [.. rows.Select(r => r.MessageId)];
    }

    [Fact]
    public async Task FindsBodyWords_AndTheMiddleOfAWordInTheRow_AcrossAccounts()
    {
        await _store.UpsertSummariesAsync([Row(_work, "w1"), Row(_home, "h1", subject: "Rebudgeting"), Row(_home, "h2")]);
        await _store.UpsertDetailAsync(new MailMessageDetail { MessageId = "w1", AccountId = _work, FolderName = "INBOX", PlainTextBody = "the budget" });

        Assert.Equal(["h1", "w1"], (await Search("budget")).Order());
        Assert.Equal(["w1"], await Search("budget", _work));
    }

    [Fact]
    public async Task ExcludedWordsInTheBodyOrTheRowRemove()
    {
        await _store.UpsertSummariesAsync([Row(_work, "1", "budget"), Row(_work, "2", "budget draft"), Row(_work, "3", "budget")]);
        await _store.UpsertDetailAsync(new MailMessageDetail { MessageId = "3", AccountId = _work, FolderName = "INBOX", PlainTextBody = "a draft" });

        Assert.Equal(["1"], await Search("budget -draft"));
    }

    [Fact]
    public async Task ConditionsAreApplied()
    {
        var folder = new MailFolderModel { AccountId = _work, FullName = "id-123", DisplayName = "Projects" };
        await _store.SaveFoldersAsync(_work, [folder]);
        await _store.UpsertSummariesAsync(
        [
            Row(_work, "unread-old", daysAgo: 40),
            Row(_work, "read-new", read: true),
            Row(_work, "in-projects", folder: "id-123", read: true),
        ]);

        Assert.Equal(["unread-old"], await Search("is:unread"));
        Assert.Equal(["read-new", "in-projects"], (await Search($"after:{DateTime.Today.AddDays(-7):yyyy-MM-dd}")).OrderByDescending(x => x));
        Assert.Equal(["unread-old"], await Search($"before:{DateTime.Today.AddDays(-7):yyyy-MM-dd}"));
        Assert.Equal(["in-projects"], await Search("folder:proj"));
    }

    [Fact]
    public async Task FolderMatchesTheFolderName_NotAMicrosoft365FolderId()
    {
        await _store.SaveFoldersAsync(_work, [new MailFolderModel { AccountId = _work, FullName = "AAMkADY3Zjk", DisplayName = "Archive" }]);
        await _store.UpsertSummariesAsync([Row(_work, "1", folder: "AAMkADY3Zjk"), Row(_work, "2", folder: "Unlisted")]);

        Assert.Empty(await Search("folder:aamk"));
        Assert.Equal(["1"], await Search("folder:arch"));
        // A folder the stored list does not know is matched by the name it is stored under.
        Assert.Equal(["2"], await Search("folder:unlist"));
    }

    [Fact]
    public async Task LikeWildcardsInAWordAreLiteral()
    {
        // A lone % or _ has no letters for the index, so only the row match answers — and it must not read
        // them as LIKE wildcards, which would match every message.
        await _store.UpsertSummariesAsync([Row(_work, "1", "100% done"), Row(_work, "2", "a_b"), Row(_work, "3", "axb")]);
        Assert.Equal(["1"], await Search("%"));
        Assert.Equal(["2"], await Search("_"));
    }

    [Fact]
    public async Task NoAccounts_NoResults()
    {
        await _store.UpsertSummariesAsync([Row(_work, "1")]);
        Assert.Empty(await _store.SearchSummariesAsync(MessageSearchQuery.Parse("hello"), [], TestContext.Current.CancellationToken));
    }
}

/// <summary>The main view model's side: Search Results as a folder, and a search in the current folder.</summary>
public class SearchResultsFolderTests
{
    private static MailMessageSummary Msg(Guid account, string id, string subject) => new()
    {
        MessageId = id, AccountId = account, FolderName = "INBOX", From = "Sam <sam@example.com>",
        To = "kelly@example.com", Subject = subject, Date = DateTimeOffset.Now,
    };

    private sealed class FixedAccounts(List<AccountModel> accounts) : IAccountService
    {
        public List<AccountModel> LoadAccounts() => [.. accounts];
        public void SaveAccounts(List<AccountModel> saved) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    private sealed class RowsStore(IEnumerable<MailMessageSummary> rows) : StubLocalStoreService
    {
        private readonly List<MailMessageSummary> _rows = [.. rows];
        public override Task<List<MailMessageSummary>> LoadAllSummariesAsync() => Task.FromResult(new List<MailMessageSummary>(_rows));
        public override Task<List<MailMessageSummary>> LoadAllSummariesAsync(Guid accountId) => LoadAllSummariesAsync();
        public override Task<List<MailMessageSummary>> LoadFolderSummariesAsync(Guid accountId, string folderName, int? limit = null) => LoadAllSummariesAsync();
    }

    private static async Task<(MainViewModel Vm, RowsStore Store, Guid Work, Guid Home)> MakeVmAsync()
    {
        var work = new AccountModel { AccountName = "Work", Username = "k@work.example" };
        var home = new AccountModel { AccountName = "Home", Username = "k@home.example" };
        var accounts = new FixedAccounts([work, home]);
        var store = new RowsStore([Msg(work.Id, "w1", "Budget"), Msg(home.Id, "h1", "Budget"), Msg(home.Id, "h2", "Lunch")]);
        var vm = new MainViewModel(
            new StubImapMailService(), accounts, new StubCredentialService(),
            store, new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService())
        {
            SearchIndexDelay = TimeSpan.Zero,
        };
        vm.LoadAccountList();
        await vm.InitialLoadAsync();
        return (vm, store, work.Id, home.Id);
    }

    [Fact]
    public async Task AcrossAccounts_OpensSearchResults_ForTheChosenAccounts()
    {
        var (vm, store, work, _) = await MakeVmAsync();
        var before = vm.SelectedFolder;

        var outcome = await vm.RunAdvancedSearchAsync(new AdvancedSearchRequest("budget", InCurrentFolder: false, [work]));

        Assert.Equal(new MainViewModel.AdvancedSearchOutcome(1, Failed: false), outcome);
        Assert.True(vm.IsSearchResultsView);
        Assert.Equal("budget", vm.SearchResultsQuery);
        Assert.Equal(["w1"], vm.Messages.Select(m => m.MessageId));
        Assert.Equal([work], Assert.Single(store.SummarySearchesAsked).Accounts);

        var reopen = vm.CurrentSearchResultsRequest;
        Assert.NotNull(reopen);
        Assert.False(reopen.InCurrentFolder);
        Assert.Equal([work], reopen.AccountIds);

        await vm.CloseSearchResultsAsync();
        Assert.False(vm.IsSearchResultsView);
        Assert.Equal(before?.FullName, vm.SelectedFolder?.FullName);
    }

    [Fact]
    public async Task AccountConditionNarrowsTheAccounts()
    {
        var (vm, store, work, home) = await MakeVmAsync();

        await vm.RunAdvancedSearchAsync(new AdvancedSearchRequest("budget account:home", InCurrentFolder: false, [work, home]));

        Assert.Equal([home], Assert.Single(store.SummarySearchesAsked).Accounts);
        Assert.Equal(["h1"], vm.Messages.Select(m => m.MessageId));
    }

    [Fact]
    public async Task NothingFoundAcrossAccounts_StaysInTheFolderTheUserWasIn()
    {
        var (vm, _, work, home) = await MakeVmAsync();
        var before = vm.SelectedFolder?.FullName;

        var outcome = await vm.RunAdvancedSearchAsync(new AdvancedSearchRequest("zebra", InCurrentFolder: false, [work, home]));

        Assert.Equal(0, outcome.Found);
        Assert.False(outcome.Failed);
        Assert.False(vm.IsSearchResultsView);
        Assert.Equal(before, vm.SelectedFolder?.FullName);
    }

    [Fact]
    public async Task InTheCurrentFolder_ItIsTheSearchBox()
    {
        var (vm, store, _, _) = await MakeVmAsync();

        var outcome = await vm.RunAdvancedSearchAsync(new AdvancedSearchRequest("lunch", InCurrentFolder: true, []));

        Assert.Equal(1, outcome.Found);
        Assert.True(vm.IsSearchActive);
        Assert.Equal("lunch", vm.SearchText);
        Assert.False(vm.IsSearchResultsView);
        Assert.Empty(store.SummarySearchesAsked);
    }

    [Fact]
    public void TheSentinelRoundTripsQueryAndAccounts()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var folder = MainViewModel.CreateSearchResultsFolder("from:\"a|b\" 100%", ids);
        Assert.True(MainViewModel.TryGetSearchResultsFromSentinel(folder.FullName, out var q, out var back));
        Assert.Equal("from:\"a|b\" 100%", q);
        Assert.Equal(ids, back);
    }
}
