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

        // Words beginning "jo" first, contacts before remembered people; then the "contains" matches.
        Assert.Equal(["Ann Jones", "Joe Bloggs", "Major Projects", "Marjorie Lee"], combined.Select(s => s.PrimaryText));
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
        Assert.Equal("Jane Smith jane@uni.edu, 1 suggestion", AddressSuggestionController.AnnouncementFor(one));

        var three = new List<AddressSuggestion>
        {
            new(new GroupModel { Name = "Lab", ResolvedMemberCount = 2 }),
            new(P("", "joe@uni.edu")),
            new(P("Jo", "jo@uni.edu"), isRecent: true),
        };
        Assert.Equal("Lab, group, 2 members, 3 suggestions", AddressSuggestionController.AnnouncementFor(three));
    }
}
