// Full-message search (#717): the query parser, the index expression it becomes, the matcher that
// combines the rows with the index, the index itself on a real store, and the search box driving it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class MessageSearchQueryTests
{
    [Fact]
    public void PlainWordsAreAllWanted()
    {
        var q = MessageSearchQuery.Parse("budget  meeting");
        Assert.Equal([new SearchTerm(SearchField.Any, "budget"), new SearchTerm(SearchField.Any, "meeting")], q.Terms);
        Assert.False(q.HasConditions);
    }

    [Fact]
    public void QuotesKeepAPhraseTogether_AndMinusExcludes()
    {
        var q = MessageSearchQuery.Parse("\"quarterly report\" -draft -\"do not\"");
        Assert.Equal(
        [
            new SearchTerm(SearchField.Any, "quarterly report", IsPhrase: true),
            new SearchTerm(SearchField.Any, "draft", Negated: true),
            new SearchTerm(SearchField.Any, "do not", IsPhrase: true, Negated: true),
        ], q.Terms);
    }

    [Theory]
    [InlineData("from:sam", SearchField.From, "sam")]
    [InlineData("TO:ann", SearchField.To, "ann")]
    [InlineData("cc:lee", SearchField.Cc, "lee")]
    [InlineData("subject:invoice", SearchField.Subject, "invoice")]
    [InlineData("body:agenda", SearchField.Body, "agenda")]
    [InlineData("attachment:pdf", SearchField.Attachment, "pdf")]
    public void FieldPrefixesNarrowAWord(string text, SearchField field, string word)
        => Assert.Equal([new SearchTerm(field, word)], MessageSearchQuery.Parse(text).Terms);

    [Fact]
    public void AFieldCanTakeAQuotedValue()
    {
        var q = MessageSearchQuery.Parse("from:\"Sam Smith\" -subject:\"out of office\"");
        Assert.Equal(
        [
            new SearchTerm(SearchField.From, "Sam Smith", IsPhrase: true),
            new SearchTerm(SearchField.Subject, "out of office", IsPhrase: true, Negated: true),
        ], q.Terms);
    }

    [Fact]
    public void AnUnknownPrefixIsJustText()
    {
        // A pasted subject line or a time must still be findable.
        var q = MessageSearchQuery.Parse("Re: lunch 12:30");
        Assert.Equal(["Re:", "lunch", "12:30"], q.Terms.Select(t => t.Text));
        Assert.All(q.Terms, t => Assert.Equal(SearchField.Any, t.Field));
    }

    [Fact]
    public void Conditions()
    {
        var q = MessageSearchQuery.Parse("has:attachment is:unread is:flagged after:2026-01-15 before:2026-02-01 folder:Projects account:work");
        Assert.True(q.HasAttachment);
        Assert.False(q.IsRead);
        Assert.True(q.IsFlagged);
        Assert.Equal(new DateTime(2026, 1, 15), q.After);
        Assert.Equal(new DateTime(2026, 2, 1), q.Before);
        Assert.Equal(["Projects"], q.Folders);
        Assert.Equal(["work"], q.Accounts);
        Assert.False(q.HasText);
    }

    [Fact]
    public void NegatedConditionsInvert()
    {
        var q = MessageSearchQuery.Parse("-has:attachment -is:read -is:flagged");
        Assert.False(q.HasAttachment);
        Assert.False(q.IsRead);
        Assert.False(q.IsFlagged);
    }

    [Theory]
    [InlineData("from:")]
    [InlineData("after:2026-0")]
    [InlineData("is:")]
    [InlineData("before:yesterday")]
    public void AHalfTypedConditionIsLeftOut_NotTurnedIntoText(string text)
    {
        // Otherwise the list would empty on every keystroke of a date being typed.
        Assert.True(MessageSearchQuery.Parse(text).IsEmpty);
    }

    [Fact]
    public void AnUnclosedQuoteRunsToTheEnd()
        => Assert.Equal([new SearchTerm(SearchField.Any, "still typing", IsPhrase: true)], MessageSearchQuery.Parse("\"still typing").Terms);

    [Theory]
    [InlineData("budget")]
    [InlineData("\"quarterly report\" -draft")]
    [InlineData("from:\"Sam Smith\" subject:invoice -cc:lee")]
    [InlineData("has:attachment is:unread after:2026-01-15 before:2026-02-01 folder:\"Old Projects\" account:work")]
    [InlineData("\"Re: lunch\"")]
    public void ToQueryStringReadsBackToTheSameQuery(string text)
    {
        var q = MessageSearchQuery.Parse(text);
        var again = MessageSearchQuery.Parse(q.ToQueryString());
        Assert.Equal(q.Terms, again.Terms);
        Assert.Equal(q.ToQueryString(), again.ToQueryString());
        Assert.Equal(q.HasAttachment, again.HasAttachment);
        Assert.Equal(q.IsRead, again.IsRead);
        Assert.Equal(q.After, again.After);
        Assert.Equal(q.Before, again.Before);
        Assert.Equal(q.Folders, again.Folders);
        Assert.Equal(q.Accounts, again.Accounts);
    }
}

public class SearchMatchExpressionTests
{
    private static string? All(string text) => SearchMatchExpression.AllOf(MessageSearchQuery.Parse(text).Terms);

    [Fact]
    public void WordsArePrefixMatches_AndedTogether()
        => Assert.Equal("\"budg\"* AND \"meet\"*", All("budg meet"));

    [Fact]
    public void FieldsNameTheirColumn()
        => Assert.Equal("sender : \"sam\"* AND attachments : \"pdf\"*", All("from:sam attachment:pdf"));

    [Fact]
    public void APhraseIsExact_AndASingleCharacterIsAWholeWord()
        => Assert.Equal("\"quarterly report\" AND \"a\"", All("\"quarterly report\" a"));

    [Fact]
    public void QuotesInsideAreDoubled()
        => Assert.Equal("\"say \"\"hi\"\"\"", SearchMatchExpression.AllOf([new SearchTerm(SearchField.Any, "say \"hi\"", IsPhrase: true)]));

    [Fact]
    public void ATermWithNoLettersMakesTheWholeQueryUnanswerable()
        => Assert.Null(All("budget ---"));

    [Fact]
    public void AnyOfSkipsTermsItCannotExpress()
        => Assert.Equal("\"draft\"* OR \"spam\"*", SearchMatchExpression.AnyOf(
            [new SearchTerm(SearchField.Any, "draft"), new SearchTerm(SearchField.Any, "!!"), new SearchTerm(SearchField.Any, "spam")]));
}

public class MessageSearchMatcherTests
{
    private static readonly Guid Account = Guid.NewGuid();

    private static MailMessageSummary Row(string id, string subject = "", string from = "", string preview = "",
        bool read = true, bool attachments = false, int daysAgo = 0, string folder = "INBOX") => new()
    {
        MessageId = id, AccountId = Account, FolderName = folder, Subject = subject, From = from,
        Preview = preview, IsRead = read, HasAttachments = attachments, Date = DateTimeOffset.Now.AddDays(-daysAgo),
    };

    private static MessageSearchMatcher Matcher(string text)
        => new(MessageSearchQuery.Parse(text), m => m.FolderName, _ => "Work kelly@example.com");

    private static SearchHit Hit(MailMessageSummary m) => new(m.AccountId, m.FolderName, m.MessageId, m.InternetMessageId);

    [Fact]
    public void TheRowsStillMatchTheMiddleOfAWord_AsTheSearchBoxAlwaysHas()
        => Assert.True(Matcher("xample").Matches(Row("1", from: "sam@example.com")));

    [Fact]
    public void AWordOnlyInTheBodyMatchesThroughTheIndex()
    {
        var row = Row("1", subject: "Hello");
        var m = Matcher("budget");
        Assert.False(m.Matches(row));

        m.SetIndexHits([Hit(row)], null);
        Assert.True(m.Matches(row));
    }

    [Fact]
    public void AnUnwantedWordInTheBodyExcludes_EvenWhenTheRowLooksClean()
    {
        var row = Row("1", subject: "Budget");
        var m = Matcher("budget -confidential");
        Assert.True(m.Matches(row));

        m.SetIndexHits(positive: [Hit(row)], negative: [Hit(row)]);
        Assert.False(m.Matches(row));
    }

    [Fact]
    public void AnIndexHitOnAnotherCopyOfTheSameMessageCounts()
    {
        // Gmail: the aggregate view shows the Inbox copy; the body was cached under All Mail.
        var shown = Row("1", folder: "INBOX");
        shown.InternetMessageId = "<abc@example.com>";
        var cachedCopy = new SearchHit(Account, "[Gmail]/All Mail", "99", "<abc@example.com>");
        var m = Matcher("budget");
        m.SetIndexHits([cachedCopy], null);
        Assert.True(m.Matches(shown));
    }

    [Fact]
    public void ConditionsComeFromTheRow()
    {
        Assert.True(Matcher("is:unread").Matches(Row("1", read: false)));
        Assert.False(Matcher("is:unread").Matches(Row("1", read: true)));
        Assert.True(Matcher("has:attachment").Matches(Row("1", attachments: true)));
        Assert.False(Matcher($"after:{DateTime.Today:yyyy-MM-dd}").Matches(Row("1", daysAgo: 3)));
        Assert.True(Matcher($"before:{DateTime.Today:yyyy-MM-dd}").Matches(Row("1", daysAgo: 3)));
        Assert.True(Matcher("folder:inb").Matches(Row("1")));
        Assert.False(Matcher("folder:archive").Matches(Row("1")));
        Assert.True(Matcher("account:kelly@").Matches(Row("1")));
    }

    [Fact]
    public void ACcWordNeedsTheIndex_TheRowHasNoCc()
    {
        var row = Row("1", subject: "lee");
        Assert.False(Matcher("cc:lee").Matches(row));
    }
}

/// <summary>The index on a real store: every write path that changes what a message says keeps it honest.</summary>
public class SearchIndexStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"qm-search-{Guid.NewGuid():N}");
    private readonly LocalStoreService _store;
    private readonly Guid _account = Guid.NewGuid();

    public SearchIndexStoreTests()
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

    private MailMessageSummary Summary(string id, string subject = "Hello", string folder = "INBOX", string preview = "") => new()
    {
        MessageId = id, AccountId = _account, FolderName = folder, From = "Sam <sam@example.com>",
        To = "kelly@example.com", Subject = subject, Preview = preview, Date = DateTimeOffset.UtcNow,
    };

    private Task Body(string id, string plain = "", string html = "", string cc = "", string folder = "INBOX", params string[] attachments)
        => _store.UpsertDetailAsync(new MailMessageDetail
        {
            MessageId = id, AccountId = _account, FolderName = folder, PlainTextBody = plain, HtmlBody = html, Cc = cc,
            Attachments = [.. attachments.Select(a => new AttachmentModel { FileName = a })],
        });

    private async Task<List<string>> Find(string text, IReadOnlyCollection<(Guid, string)>? folders = null)
    {
        var match = SearchMatchExpression.AllOf(MessageSearchQuery.Parse(text).Terms)!;
        var hits = await _store.FindMessagesAsync(match, folders, ct: TestContext.Current.CancellationToken);
        return [.. hits.Select(h => h.FolderName + "/" + h.MessageId).Order()];
    }

    [Fact]
    public void TheIndexIsAvailable()
        => Assert.True(_store.IsSearchIndexAvailable, "the bundled SQLite must have FTS5");

    [Fact]
    public async Task FindsAWordInThePlainBody_ByPrefix()
    {
        await _store.UpsertSummariesAsync([Summary("1"), Summary("2")]);
        await Body("1", plain: "The quarterly budget is attached.");

        Assert.Equal(["INBOX/1"], await Find("budg"));
        Assert.Equal(["INBOX/1"], await Find("\"quarterly budget\""));
        Assert.Empty(await Find("\"budget quarterly\""));
    }

    [Fact]
    public async Task AnHtmlOnlyBodyIsIndexedAsItsWords_NotItsMarkup()
    {
        await _store.UpsertSummariesAsync([Summary("1")]);
        await Body("1", html: "<div style=\"color:red\"><p>Holiday <b>schedule</b></p></div>");

        Assert.Equal(["INBOX/1"], await Find("schedule"));
        Assert.Empty(await Find("div"));
        Assert.Empty(await Find("color"));
    }

    [Fact]
    public async Task FieldsSearchTheirOwnColumn()
    {
        await _store.UpsertSummariesAsync([Summary("1", subject: "Invoice")]);
        await Body("1", plain: "see attached", cc: "Lee <lee@example.org>", attachments: ["Q3-Report.pdf"]);

        Assert.Equal(["INBOX/1"], await Find("cc:lee"));
        Assert.Equal(["INBOX/1"], await Find("attachment:report"));
        Assert.Equal(["INBOX/1"], await Find("subject:invoice"));
        Assert.Equal(["INBOX/1"], await Find("from:sam"));
        Assert.Empty(await Find("subject:attached"));
        Assert.Empty(await Find("from:lee"));
    }

    [Fact]
    public async Task AccentsDoNotMatter()
    {
        await _store.UpsertSummariesAsync([Summary("1", subject: "Café résumé")]);
        Assert.Equal(["INBOX/1"], await Find("cafe resume"));
    }

    [Fact]
    public async Task AMessageWithNoBodyYetIsFoundByItsPreview()
    {
        await _store.UpsertSummariesAsync([Summary("1", preview: "Agenda for Tuesday")]);
        Assert.Equal(["INBOX/1"], await Find("body:agenda"));
    }

    [Fact]
    public async Task AChangedBodyReplacesTheOldWords()
    {
        await _store.UpsertSummariesAsync([Summary("1")]);
        await Body("1", plain: "first draft");
        Assert.Equal(["INBOX/1"], await Find("draft"));

        await Body("1", plain: "final version");
        Assert.Empty(await Find("draft"));
        Assert.Equal(["INBOX/1"], await Find("final"));
    }

    [Fact]
    public async Task ADeletedMessageIsGoneFromTheIndex()
    {
        await _store.UpsertSummariesAsync([Summary("1"), Summary("2")]);
        await Body("1", plain: "budget");
        await Body("2", plain: "budget");
        Assert.Equal(2, (await Find("budget")).Count);

        await _store.DeleteSummariesAsync(_account, "INBOX", ["1"]);
        Assert.Equal(["INBOX/2"], await Find("budget"));
    }

    [Fact]
    public async Task AMovedMessageIsFoundInItsNewFolder_AndACopyInBoth()
    {
        await _store.UpsertSummariesAsync([Summary("1"), Summary("2")]);
        await Body("1", plain: "moving budget");
        await Body("2", plain: "copied budget");

        await _store.RefileMessagesAsync(_account, "INBOX", "Archive", ["1"], copy: false);
        await _store.RefileMessagesAsync(_account, "INBOX", "Kept", ["2"], copy: true);

        Assert.Equal(["Archive/1"], await Find("moving"));
        Assert.Equal(["INBOX/2", "Kept/2"], await Find("copied"));
    }

    [Fact]
    public async Task ClearingAnAccountEmptiesItsIndex()
    {
        await _store.UpsertSummariesAsync([Summary("1")]);
        await Body("1", plain: "budget");
        await _store.ClearCachedMailAsync([_account]);
        Assert.Empty(await Find("budget"));
    }

    [Fact]
    public async Task RemovingAnAccountEmptiesItsIndex_AndLeavesOthers()
    {
        var other = Guid.NewGuid();
        await _store.UpsertSummariesAsync([Summary("1"), new MailMessageSummary
        {
            MessageId = "2", AccountId = other, FolderName = "INBOX", Subject = "budget", Date = DateTimeOffset.UtcNow,
        }]);
        await Body("1", plain: "budget");
        Assert.Equal(2, (await Find("budget")).Count);

        await _store.DeleteAccountDataAsync(_account);

        Assert.Equal(["INBOX/2"], await Find("budget"));
    }

    [Fact]
    public async Task AMessageWaitingToBeReindexedIsNotMatchedOnItsOldText()
    {
        await _store.UpsertSummariesAsync([Summary("1")]);
        await Body("1", plain: "old words");
        Assert.Equal(["INBOX/1"], await Find("old"));

        await Body("1", plain: "new words");
        var match = SearchMatchExpression.AllOf(MessageSearchQuery.Parse("old").Terms)!;
        Assert.Empty(await _store.FindMessagesAsync(match, null, indexPendingFirst: false, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheScopeLimitsToTheGivenFolders()
    {
        await _store.UpsertSummariesAsync([Summary("1"), Summary("2", folder: "Archive")]);
        await Body("1", plain: "budget");
        await Body("2", plain: "budget", folder: "Archive");

        Assert.Equal(["Archive/2"], await Find("budget", [(_account, "Archive")]));
        Assert.Equal(2, (await Find("budget", [(_account, "Archive"), (_account, "INBOX")])).Count);
        Assert.Empty(await Find("budget", []));
    }

    [Fact]
    public async Task MailCachedBeforeTheIndexExistedIsIndexedOnUpgrade()
    {
        await _store.UpsertSummariesAsync([Summary("1")]);
        await Body("1", plain: "legacy budget");

        // Take the index away, as a profile from before #717 has none.
        await using (var conn = new SqliteConnection($"Data Source={Path.Combine(_dir, "mail.db")}"))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DROP TRIGGER trg_search_summary_insert; DROP TRIGGER trg_search_summary_update;
                DROP TRIGGER trg_search_summary_delete; DROP TRIGGER trg_search_detail_insert;
                DROP TRIGGER trg_search_detail_update; DROP TRIGGER trg_search_detail_delete;
                DROP TABLE MessageSearch; DROP TABLE SearchKey;
                """;
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var upgraded = new LocalStoreService(new ProfileContext(_dir));
        upgraded.Initialize();
        var match = SearchMatchExpression.AllOf(MessageSearchQuery.Parse("legacy").Terms)!;
        var hits = await upgraded.FindMessagesAsync(match, null, ct: TestContext.Current.CancellationToken);
        Assert.Equal("1", Assert.Single(hits).MessageId);
    }

    [Fact]
    public async Task IndexingIsNewestFirst_AndStopsAtTheLimit()
    {
        var old = Summary("old");
        old.Date = DateTimeOffset.UtcNow.AddDays(-30);
        await _store.UpsertSummariesAsync([old, Summary("new")]);

        Assert.Equal(1, await _store.IndexPendingSearchAsync(1, TestContext.Current.CancellationToken));
        Assert.Equal(1, await _store.IndexPendingSearchAsync(10, TestContext.Current.CancellationToken));
        Assert.Equal(0, await _store.IndexPendingSearchAsync(10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnExpressionFts5RejectsThrows_SoCallersCanFallBack()
    {
        await _store.UpsertSummariesAsync([Summary("1")]);
        await Assert.ThrowsAsync<SqliteException>(() =>
            _store.FindMessagesAsync("AND AND \"x", null, ct: TestContext.Current.CancellationToken));
    }
}

/// <summary>The search box: the folder's rows, plus what the index says about their bodies.</summary>
public class SearchBoxIndexTests
{
    private static readonly Guid Account = Guid.NewGuid();

    private static MailMessageSummary Msg(string id, string subject, int daysAgo = 0) => new()
    {
        MessageId = id, AccountId = Account, FolderName = "INBOX", From = "Sam <sam@example.com>",
        To = "kelly@example.com", Subject = subject, Date = DateTimeOffset.Now.AddDays(-daysAgo),
    };

    private sealed class IndexedStore(IEnumerable<MailMessageSummary> rows) : StubLocalStoreService
    {
        private readonly List<MailMessageSummary> _rows = [.. rows];
        public override Task<List<MailMessageSummary>> LoadAllSummariesAsync() => Task.FromResult(new List<MailMessageSummary>(_rows));
        public override Task<List<MailMessageSummary>> LoadAllSummariesAsync(Guid accountId) => LoadAllSummariesAsync();
        public override Task<List<MailMessageSummary>> LoadFolderSummariesAsync(Guid accountId, string folderName, int? limit = null) => LoadAllSummariesAsync();
    }

    private static (MainViewModel Vm, IndexedStore Store) MakeVm(params MailMessageSummary[] rows)
    {
        var store = new IndexedStore(rows);
        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            store, new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService())
        {
            SearchIndexDelay = TimeSpan.Zero,
        };
        return (vm, store);
    }

    private static async Task Search(MainViewModel vm, string text)
    {
        vm.IsSearchActive = true;
        vm.SearchText = text;
        if (vm.PendingSearchIndexQuery != null) await vm.PendingSearchIndexQuery;
    }

    [Fact]
    public async Task AWordOnlyInTheBodyIsFound()
    {
        var withBody = Msg("1", "Hello");
        var other = Msg("2", "Lunch");
        var (vm, store) = MakeVm(withBody, other);
        store.SearchHits = new() { ["\"budget\"*"] = [new SearchHit(Account, "INBOX", "1", "")] };
        await vm.InitialLoadAsync();

        await Search(vm, "budget");

        Assert.Equal(["1"], vm.Messages.Select(m => m.MessageId));
        Assert.Equal("1 message found", vm.SearchAnnouncement);
    }

    [Fact]
    public async Task WithoutAnIndexTheRowsStillAnswer()
    {
        var (vm, store) = MakeVm(Msg("1", "Budget review"), Msg("2", "Lunch"));
        await vm.InitialLoadAsync();

        await Search(vm, "budget");

        Assert.Empty(store.SearchMatchesAsked);
        Assert.Equal(["1"], vm.Messages.Select(m => m.MessageId));
    }

    [Fact]
    public async Task ConditionsAloneNeverAskTheIndex()
    {
        var read = Msg("2", "B");
        read.IsRead = true;
        var (vm, store) = MakeVm(Msg("1", "A"), read);
        store.SearchHits = [];
        await vm.InitialLoadAsync();

        await Search(vm, "is:unread");

        Assert.Empty(store.SearchMatchesAsked);
        Assert.Equal(["1"], vm.Messages.Select(m => m.MessageId));
    }

    [Fact]
    public async Task AnAnswerForTextNoLongerInTheBoxIsIgnored()
    {
        var (vm, store) = MakeVm(Msg("1", "Hello"), Msg("2", "Lunch"));
        store.SearchHits = new() { ["\"budget\"*"] = [new SearchHit(Account, "INBOX", "1", "")] };
        await vm.InitialLoadAsync();
        vm.SearchIndexDelay = TimeSpan.FromMilliseconds(200);

        vm.IsSearchActive = true;
        vm.SearchText = "budget";
        var stale = vm.PendingSearchIndexQuery;
        vm.SearchText = "lunch";
        if (stale != null) await stale;
        if (vm.PendingSearchIndexQuery != null) await vm.PendingSearchIndexQuery;

        Assert.Equal(["2"], vm.Messages.Select(m => m.MessageId));
    }
}
