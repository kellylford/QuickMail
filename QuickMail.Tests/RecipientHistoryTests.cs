using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Remembering the people messages are sent to (#1037): the store, the setting, what a send records,
/// and where remembered people land among the address suggestions.
/// </summary>
public class RecipientHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qm-recipients-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "recipients.json");

    public RecipientHistoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private RecipientHistoryService NewStore(long startTicks = 100)
    {
        var ticks = startTicks;
        return new RecipientHistoryService(FilePath) { UtcTicks = () => ticks++ };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── The store ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Record_ThenSearch_FindsByNameOrAddress_MostRecentFirst_AndSurvivesARestart()
    {
        using (var store = NewStore())
        {
            await store.RecordAsync([("Dr Jane Smith", "jsmith@uni.edu"), ("", "registrar@uni.edu")]);
            await store.RecordAsync([("John Jones", "jj@uni.edu")]);
        }

        using var reopened = NewStore(1000);
        Assert.Equal(3, await reopened.CountAsync());
        Assert.Equal(["jj@uni.edu", "jsmith@uni.edu"], (await reopened.SearchAsync("j", 10, Ct)).Select(p => p.EmailAddress));
        Assert.Equal("Dr Jane Smith", Assert.Single(await reopened.SearchAsync("SMITH", 10, Ct)).DisplayName);
        Assert.Single(await reopened.SearchAsync("registrar", 10, Ct));
        Assert.Single(await reopened.SearchAsync("j", 1, Ct));
        Assert.Empty(await reopened.SearchAsync("  ", 10, Ct));
    }

    [Fact]
    public async Task Record_TheSameAddressAgain_IsOnePerson_KeepingTheNameIfNoneIsGiven()
    {
        using var store = NewStore();
        await store.RecordAsync([("Jane Smith", "Jane@Uni.edu")]);
        await store.RecordAsync([("", "jane@uni.edu"), ("jane@uni.edu", "JANE@UNI.EDU")]);

        var jane = Assert.Single(await store.SearchAsync("jane", 10, Ct));
        Assert.Equal("Jane Smith", jane.DisplayName);       // an empty name, or the address as a name, keeps it
        Assert.Equal(1, await store.CountAsync());

        await store.RecordAsync([("Professor Smith", "jane@uni.edu")]);
        Assert.Equal("Professor Smith", Assert.Single(await store.SearchAsync("jane", 10, Ct)).DisplayName);
    }

    [Fact]
    public async Task Record_IgnoresWhatIsNotAnAddress()
    {
        using var store = NewStore();
        await store.RecordAsync([("Nobody", ""), ("Nobody", "   "), ("Nobody", "not-an-address")]);
        Assert.Equal(0, await store.CountAsync());
        Assert.False(File.Exists(FilePath)); // nothing to keep, nothing written
    }

    [Fact]
    public async Task Record_PastTheCap_DropsTheLeastRecentlyUsed()
    {
        using var store = NewStore();
        var people = Enumerable.Range(0, RecipientHistoryService.MaxRemembered)
            .Select(i => ($"Person {i}", $"p{i}@example.com")).ToList();
        foreach (var p in people) await store.RecordAsync([p]);   // p0 is the oldest
        await store.RecordAsync([("Person 0", "p0@example.com")]); // ...until it is used again
        await store.RecordAsync([("Newcomer", "new@example.com")]);

        Assert.Equal(RecipientHistoryService.MaxRemembered, await store.CountAsync());
        Assert.Single(await store.SearchAsync("p0@", 10, Ct));
        Assert.Empty(await store.SearchAsync("p1@", 10, Ct));     // the least recently used went
        Assert.Single(await store.SearchAsync("new@", 10, Ct));
    }

    [Fact]
    public async Task Clear_ForgetsEveryone_AndDeletesTheFile()
    {
        using var store = NewStore();
        await store.RecordAsync([("Jane", "jane@uni.edu")]);
        Assert.True(File.Exists(FilePath));

        await store.ClearAsync();

        Assert.Equal(0, await store.CountAsync());
        Assert.False(File.Exists(FilePath));
        using var reopened = NewStore();
        Assert.Equal(0, await reopened.CountAsync());
    }

    [Fact]
    public async Task AnUnreadableFile_StartsEmpty_AndIsReplacedOnTheNextRecord()
    {
        await File.WriteAllTextAsync(FilePath, "{ this is not json", Ct);
        using var store = NewStore();
        Assert.Equal(0, await store.CountAsync());

        await store.RecordAsync([("Jane", "jane@uni.edu")]);
        using var reopened = NewStore();
        Assert.Equal(1, await reopened.CountAsync());
    }

    [Fact]
    public async Task AFileHoldingNulls_KeepsOnlyUsableEntries_AndSearchStillWorks()
    {
        await File.WriteAllTextAsync(FilePath,
            """[null, {"DisplayName": null, "EmailAddress": "jane@uni.edu"}, {"DisplayName": "X", "EmailAddress": null}]""", Ct);
        using var store = NewStore();
        var jane = Assert.Single(await store.SearchAsync("jane", 10, Ct));
        Assert.Equal(string.Empty, jane.DisplayName);
        Assert.Equal(1, await store.CountAsync());
    }

    [Fact]
    public async Task Clear_WhenTheFileCannotBeDeleted_ForgetsNoOne()
    {
        using var store = NewStore();
        await store.RecordAsync([("Jane", "jane@uni.edu")]);
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(store.ClearAsync);
        Assert.Equal(1, await store.CountAsync()); // not gone now only to come back at the next start
    }

    [Fact]
    public async Task TheFile_HoldsOnlyNameAddressAndTime()
    {
        using var store = NewStore();
        await store.RecordAsync([("Jane", "jane@uni.edu")]);
        var json = await File.ReadAllTextAsync(FilePath, Ct);
        Assert.Contains("\"EmailAddress\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Source", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Id\"", json, StringComparison.Ordinal);
    }

    // ── What a send records ──────────────────────────────────────────────────

    private sealed class FakeHistory : IRecipientHistory
    {
        public List<IReadOnlyList<(string DisplayName, string EmailAddress)>> Recorded { get; } = [];
        public List<ContactModel> People { get; } = [];
        public Task RecordAsync(IReadOnlyList<(string DisplayName, string EmailAddress)> recipients)
        { Recorded.Add(recipients); return Task.CompletedTask; }
        public Task<List<ContactModel>> SearchAsync(string text, int max, CancellationToken ct = default)
            => Task.FromResult(People.Where(p => p.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)
                                              || p.EmailAddress.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(max).ToList());
        public Task<int> CountAsync() => Task.FromResult(People.Count);
        public Task ClearAsync() { People.Clear(); return Task.CompletedTask; }
        public Task<List<ContactModel>> ListAsync() => Task.FromResult(People.ToList());
        public Task<bool> RemoveAsync(string emailAddress)
            => Task.FromResult(People.RemoveAll(p => p.EmailAddress.Equals(emailAddress, StringComparison.OrdinalIgnoreCase)) > 0);
    }

    private static (ComposeViewModel Vm, StubSmtpService Smtp, StubConnectivityService Net, FakeHistory History) NewCompose(bool remember)
    {
        var smtp = new StubSmtpService();
        var net = new StubConnectivityService();
        var history = new FakeHistory();
        var vm = new ComposeViewModel(smtp, new StubAccountService(), new StubCredentialService(),
            new StubImapMailService(), new StubTemplateService(), outbox: new StubOutboxService(), connectivity: net)
        {
            SenderAccount = new AccountModel { Id = Guid.NewGuid(), Username = "me@example.com", AuthType = AuthType.OAuth2Google },
            To = "\"Smith, Jane\" <jane@uni.edu>; bob@uni.edu",
            Cc = "Carol <carol@uni.edu>",
            Bcc = "JANE@uni.edu, dean@uni.edu",
            Body = "Hello",
            RecipientHistory = history,
            RememberRecipientsProvider = () => remember,
        };
        return (vm, smtp, net, history);
    }

    [Fact]
    public async Task Sending_WithTheSettingOn_RemembersEveryRecipientOnce()
    {
        var (vm, smtp, _, history) = NewCompose(remember: true);
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Single(smtp.Sent);
        await vm.PendingRecipientRecord!;

        var recorded = Assert.Single(history.Recorded);
        Assert.Equal(["jane@uni.edu", "bob@uni.edu", "carol@uni.edu", "dean@uni.edu"], recorded.Select(r => r.EmailAddress));
        Assert.Equal("Smith, Jane", recorded[0].DisplayName); // the quoted comma stays one person
        Assert.Equal("Carol", recorded[2].DisplayName);
    }

    [Fact]
    public async Task Sending_WithTheSettingOff_RemembersNothing()
    {
        var (vm, smtp, _, history) = NewCompose(remember: false);
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Single(smtp.Sent);
        Assert.Null(vm.PendingRecipientRecord);
        Assert.Empty(history.Recorded);
    }

    [Fact]
    public async Task AFailedSend_RemembersNothing()
    {
        var (vm, smtp, _, history) = NewCompose(remember: true);
        smtp.SendFailure = new InvalidOperationException("550 no such user");
        await vm.SendCommand.ExecuteAsync(null);
        Assert.False(vm.IsSent);
        Assert.Empty(history.Recorded);
    }

    [Fact]
    public async Task AQueuedSend_IsRemembered_SinceItWillGo()
    {
        var (vm, smtp, net, history) = NewCompose(remember: true);
        net.SetAccount(vm.SenderAccount!.Id, online: false);
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Empty(smtp.Sent);
        Assert.True(vm.IsSent);
        await vm.PendingRecipientRecord!;
        Assert.Single(history.Recorded);
    }

    // ── The setting ──────────────────────────────────────────────────────────

    [Fact]
    public void TheSetting_IsOffByDefault_AndRoundTripsThroughConfigIni()
    {
        Assert.False(new ConfigModel().RememberRecipients);
        var config = new ConfigService(new ProfileContext(_dir));
        Assert.False(config.Load().RememberRecipients);

        var settings = new SettingsViewModel(config, new StubCommandRegistry()) { RememberRecipients = true };
        settings.SaveCommand.Execute(null);

        var reloaded = new ConfigService(new ProfileContext(_dir));
        Assert.True(reloaded.Load().RememberRecipients);
        Assert.True(new SettingsViewModel(reloaded, new StubCommandRegistry()).RememberRecipients);
        Assert.Contains(nameof(ConfigModel.RememberRecipients), SettingsConfigTransfer.Portable);
    }

    [Fact]
    public void TheSuggestionSwitches_AreOnByDefault_AndEachRoundTripsThroughConfigIni()
    {
        var defaults = new ConfigModel();
        Assert.True(defaults.SuggestAddresses);
        Assert.True(defaults.SuggestFromAddressBook);
        Assert.True(defaults.SuggestPeopleEmailed);
        Assert.Equal(new SuggestionSources(AddressBook: true, PeopleEmailed: true), SuggestionSources.From(defaults));

        var config = new ConfigService(new ProfileContext(_dir));
        var settings = new SettingsViewModel(config, new StubCommandRegistry())
        {
            SuggestAddresses = false, SuggestFromAddressBook = false, SuggestPeopleEmailed = true,
        };
        settings.SaveCommand.Execute(null);

        var reloaded = new ConfigService(new ProfileContext(_dir)).Load();
        Assert.False(reloaded.SuggestAddresses);
        Assert.False(reloaded.SuggestFromAddressBook);
        Assert.True(reloaded.SuggestPeopleEmailed);
        var vm = new SettingsViewModel(new ConfigService(new ProfileContext(_dir)), new StubCommandRegistry());
        Assert.False(vm.SuggestAddresses);
        Assert.False(vm.SuggestFromAddressBook);
        Assert.True(vm.SuggestPeopleEmailed);
        foreach (var name in new[] { nameof(ConfigModel.SuggestAddresses), nameof(ConfigModel.SuggestFromAddressBook), nameof(ConfigModel.SuggestPeopleEmailed) })
            Assert.Contains(name, SettingsConfigTransfer.Portable);
    }

    [Theory]
    [InlineData(true, true, true, true, true)]
    [InlineData(true, false, true, false, true)]
    [InlineData(true, true, false, true, false)]
    [InlineData(false, true, true, false, false)] // the master switch overrides both sources
    public void SuggestionSources_FollowTheMasterSwitch(bool master, bool book, bool people, bool expectBook, bool expectPeople)
        => Assert.Equal(new SuggestionSources(expectBook, expectPeople), SuggestionSources.From(new ConfigModel
        {
            SuggestAddresses = master, SuggestFromAddressBook = book, SuggestPeopleEmailed = people,
        }));

    [Fact]
    public async Task Forget_AsksFirst_AndOnlyClearsOnYes()
    {
        var history = new FakeHistory();
        history.People.Add(new ContactModel { DisplayName = "Jane", EmailAddress = "jane@uni.edu" });
        history.People.Add(new ContactModel { DisplayName = "Bob", EmailAddress = "bob@uni.edu" });
        var settings = new SettingsViewModel(new StubConfigService(), new StubCommandRegistry()) { RecipientHistory = history };
        Assert.True(settings.HasRecipientHistory);
        var said = new List<string>();
        settings.RecipientResultAnnounced += said.Add;
        string? asked = null;

        settings.ConfirmRequested = (q, _) => { asked = q; return false; };
        await settings.ForgetRecipientsCommand.ExecuteAsync(null);
        Assert.Contains("all 2 people", asked, StringComparison.Ordinal);
        Assert.Equal(2, history.People.Count);
        Assert.Empty(said);

        settings.ConfirmRequested = (_, _) => true;
        await settings.ForgetRecipientsCommand.ExecuteAsync(null);
        Assert.Empty(history.People);
        Assert.Equal(["Forgot 2 people."], said);

        await settings.ForgetRecipientsCommand.ExecuteAsync(null);
        Assert.Equal("No one is remembered.", said[^1]);
    }

    [Fact]
    public void Settings_WithNoStore_HidesForget()
        => Assert.False(new SettingsViewModel(new StubConfigService(), new StubCommandRegistry()).HasRecipientHistory);

    // ── Where they land among the suggestions ────────────────────────────────

    private static ContactModel P(string name, string email, long used = 0)
        => new() { DisplayName = name, EmailAddress = email, LastUsedTicks = used };

    [Fact]
    public void Ranking_PutsWhatBeginsWithTheTypedText_BeforeWhatOnlyContainsIt()
    {
        var combined = AddressSuggestionRanking.Combine("jo",
            groups: [new GroupModel { Name = "Major Projects" }],          // contains "jo"
            contacts: [P("Marjorie Lee", "mlee@uni.edu"), P("Ann Jones", "ann@uni.edu")],
            recents: [P("Joe Bloggs", "joe@uni.edu")]);

        // Words beginning "jo" first, contacts before remembered people; then the "contains" matches,
        // people before groups.
        Assert.Equal(["Ann Jones", "Joe Bloggs", "Marjorie Lee", "Major Projects"], combined.Select(s => s.PrimaryText));
        Assert.True(combined[1].IsRecent);
        Assert.False(combined[0].IsRecent);
    }

    [Fact]
    public void Ranking_AnAddressBeginningWithTheText_CountsAsBeginning()
    {
        var combined = AddressSuggestionRanking.Combine("pr",
            groups: [], contacts: [P("Ann Lee", "ann.prior@uni.edu")], recents: [P("", "prof@uni.edu")]);
        Assert.Equal(["prof@uni.edu", "Ann Lee"], combined.Select(s => s.PrimaryText));
    }

    [Fact]
    public void Ranking_SomeoneInBothTheAddressBookAndTheRemembered_IsOfferedOnce_AsTheContact()
    {
        var combined = AddressSuggestionRanking.Combine("ja",
            groups: [], contacts: [P("Jane Smith", "jane@uni.edu")], recents: [P("J. Smith", "JANE@uni.edu "), P("Jack", "jack@uni.edu")]);
        Assert.Equal(["Jane Smith", "Jack"], combined.Select(s => s.PrimaryText));
        Assert.False(combined[0].IsRecent);
    }

    [Fact]
    public void ARememberedSuggestion_ReadsLikeAContact_AndSaysWhereItCameFromOnScreen()
    {
        var recent = new AddressSuggestion(P("Jane Smith", "jane@uni.edu"), isRecent: true);
        Assert.Equal("Jane Smith jane@uni.edu", recent.ToString());
        Assert.Equal("jane@uni.edu — sent to before", recent.SecondaryText);
        Assert.Equal("jane@uni.edu", new AddressSuggestion(P("Jane Smith", "jane@uni.edu")).SecondaryText);
    }

    [Fact]
    public void TheAnnouncement_NamesWhatTabWouldEnter_ThenHowManyThereAre()
    {
        var one = new List<AddressSuggestion> { new(P("Jane Smith", "jane@uni.edu")) };
        Assert.Equal("Jane Smith jane@uni.edu, 1 suggestion", AddressSuggestionController.AnnouncementFor(one, "ja"));

        var three = new List<AddressSuggestion>
        {
            new(new GroupModel { Name = "Lab", ResolvedMemberCount = 2 }),
            new(P("", "lab.tech@uni.edu")),
            new(P("Labelle", "jo@uni.edu"), isRecent: true),
        };
        Assert.Equal("Lab, group, 2 members, 3 suggestions", AddressSuggestionController.AnnouncementFor(three, "la"));
    }

    [Fact]
    public void TheAnnouncement_IsOnlyTheCount_WhenTabWouldKeepWhatWasTyped()
    {
        // "ann" is only inside joann: Tab will not take it, so it is not named.
        var inside = new List<AddressSuggestion> { new(P("", "joann@example.com")) };
        Assert.Equal("1 suggestion", AddressSuggestionController.AnnouncementFor(inside, "ann"));
    }

    // ── What Tab may enter ───────────────────────────────────────────────────

    private static List<AddressSuggestion> Ranked(string typed, params ContactModel[] contacts)
        => AddressSuggestionRanking.Combine(typed, [], contacts, []);

    [Fact]
    public void Tab_NeverTurnsAnAddressIntoOneThatMerelyContainsIt()
    {
        // The reviewer's case: a new address typed in full, and a contact that contains it.
        var list = Ranked("ann@example.com", P("", "joann@example.com"));
        Assert.False(AddressSuggestionRanking.TabTakesFirst(list, "ann@example.com"));
        Assert.False(AddressSuggestionRanking.TabTakesFirst(Ranked("ann", P("Joann Lee", "jl@example.com")), "ann"));
    }

    [Fact]
    public void Tab_TakesAnExactAddress_EvenWhenALongerOneWasUsedMoreRecently()
    {
        var list = Ranked("bob@x.com", P("", "bob@x.com.au", used: 9), P("", "bob@x.com", used: 1));
        Assert.Equal("bob@x.com", list[0].Contact!.EmailAddress);
        Assert.True(AddressSuggestionRanking.TabTakesFirst(list, "bob@x.com"));
    }

    [Fact]
    public void Tab_TakesAPersonBeforeAGroup_UnlessTheGroupIsTypedInFull()
    {
        var marketing = new GroupModel { Name = "Marketing", ResolvedMemberCount = 20 };
        var mary = P("Mary Hall", "mary@uni.edu");

        var ma = AddressSuggestionRanking.Combine("ma", [marketing], [mary], []);
        Assert.Equal("Mary Hall", ma[0].PrimaryText);   // one Tab must not add twenty people

        var named = AddressSuggestionRanking.Combine("Marketing", [marketing], [P("Marketing Desk", "desk@uni.edu")], []);
        Assert.True(named[0].IsGroup);                  // the group's whole name ranks with an exact address
        Assert.True(AddressSuggestionRanking.TabTakesFirst(named, "Marketing"));
    }

    [Fact]
    public void Tab_KeepsACompleteTypedAddress_ThatIsNotInTheList()
    {
        var list = Ranked("bob@x.com", P("", "bob@x.com.au"));
        Assert.False(AddressSuggestionRanking.TabTakesFirst(list, "bob@x.com"));
    }

    [Theory]
    [InlineData("ja", true)]
    [InlineData("smi", true)]          // a word of the name
    [InlineData("jane@uni", true)]     // an address still being typed
    [InlineData("jane@uni.e", true)]   // one letter after the dot is not yet an address
    [InlineData("JANE@UNI.EDU", true)] // the exact address, whatever its case
    public void Tab_TakesWhatTheUserIsTypingTowards(string typed, bool takes)
        => Assert.Equal(takes, AddressSuggestionRanking.TabTakesFirst(Ranked(typed, P("Jane Smith", "jane@uni.edu")), typed));

    [Theory]
    [InlineData("bob@x.com", true)]
    [InlineData("bob@x.co", true)]
    [InlineData("bob@x.c", false)]
    [InlineData("bob@x.", false)]
    [InlineData("bob@x", false)]
    [InlineData("bob", false)]
    [InlineData("@x.com", false)]
    public void ACompleteAddress_HasADomainWithAtLeastTwoLettersAfterTheLastDot(string typed, bool complete)
        => Assert.Equal(complete, AddressSuggestionRanking.LooksComplete(typed));

    [Fact]
    public void Ranking_LeavesOutARememberedPerson_WhoIsAnywhereInTheAddressBook()
    {
        // Jane is a contact, but not among the few contact matches passed in.
        var book = new HashSet<string>(["jane@uni.edu", "other@uni.edu"], StringComparer.OrdinalIgnoreCase);
        var list = AddressSuggestionRanking.Combine("ja", [], [], [P("Jane", "JANE@uni.edu"), P("Jack", "jack@uni.edu")], book);
        Assert.Equal(["Jack"], list.Select(s => s.PrimaryText));
    }

    [Fact]
    public async Task TheAddressBookSearch_KeepsWhatBeginsWithTheText_InsideItsTopTen()
    {
        var contacts = new ContactService(new ProfileContext(_dir));
        try
        {
            // Twelve recent contacts that only contain "jo", and one older that begins with it.
            for (var i = 0; i < 12; i++)
                await contacts.UpsertContactAsync(new ContactModel { DisplayName = $"Marjorie {i}", EmailAddress = $"m{i}@uni.edu", LastUsedTicks = 100 + i });
            await contacts.UpsertContactAsync(new ContactModel { DisplayName = "Joan Bell", EmailAddress = "joan@uni.edu", LastUsedTicks = 1 });

            var found = await contacts.SearchContactsAsync("jo", Ct);
            Assert.Equal(10, found.Count);
            Assert.Equal("joan@uni.edu", found[0].EmailAddress);
            Assert.Equal("m11@uni.edu", found[1].EmailAddress); // then the rest by recency
        }
        finally { contacts.Dispose(); }
    }
}
