// Deleting and archiving from the flat message list — issue #667.
//
// Two things are pinned here, and both are inaudible to a test that only checks the end state.
//
// 1. ORDER. Focus has to land on the surviving row BEFORE the doomed rows leave the collection.
//    Remove the row that owns keyboard focus and WPF is left with focus on a container whose item
//    is gone; its ItemAutomationPeer then reports IsEnabled = false, and a screen reader says
//    "unavailable" before it reads the next message. Every end-state assertion passes either way —
//    the selection and the list are identical once the dust settles — so these tests capture the
//    state at the moment MessageListFocusNowRequested fires and assert on that.
//
// 2. WHAT IS SPOKEN. Deleting one message says nothing: the row has gone and the next one has just
//    been read, so a spoken "1 message deleted" repeats what the user was told a moment ago and
//    interrupts the reading to do it. A count of several, an emptied folder and every failure stay
//    audible. The category is one-shot, so it is captured through StatusAnnouncementRecorder, which
//    listens the way MainWindow does.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class MessageRemovalFocusTests
{
    private static readonly Guid Work = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static AccountModel Account() => new()
    {
        Id = Work, AccountName = "Work", Username = "work@example.com", AuthType = AuthType.OAuth2Microsoft,
    };

    private static MailFolderModel Folder(string fullName, SpecialFolderKind kind) => new()
    {
        AccountId = Work, FullName = fullName, DisplayName = fullName, Kind = kind,
    };

    private static MailMessageSummary Msg(string id, int daysAgo) => new()
    {
        MessageId  = id,
        AccountId  = Work,
        FolderName = "INBOX",
        From       = "someone@example.com",
        Subject    = $"Subject {id}",
        Date       = DateTimeOffset.Now.AddDays(-daysAgo),
    };

    private sealed class SilentSyncService : ISyncService
    {
#pragma warning disable CS0067 // nothing in these tests raises them
        public event Action<IReadOnlyList<MailMessageSummary>>? MessagesRemoved;
        public event Action<IReadOnlyList<MailMessageSummary>>? FolderReadStatesReconciled;
        public event Action<IReadOnlyList<MailMessageSummary>>? FolderSynced;
        public event Action<int>? RulesApplied;
        public event Action<int, int>? SyncProgressChanged;
        public event Action<int, int>? OfflineBodyProgressChanged;
        public event Action<int, int>? OfflineBodyPassCompleted;
#pragma warning restore CS0067

        public Task SyncAllAccountsAsync(IEnumerable<AccountModel> accounts,
            IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<MailMessageSummary>> SyncOneFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>(Array.Empty<MailMessageSummary>());
        public Task<IReadOnlyList<MailMessageSummary>> SyncOneFolderOnlineAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>(Array.Empty<MailMessageSummary>());
        public Task<IReadOnlyList<MailMessageSummary>> SyncFolderFullAsync(AccountModel account, MailFolderModel folder, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>(Array.Empty<MailMessageSummary>());
        public Task<int> ReconcileFolderAsync(AccountModel account, MailFolderModel folder, CancellationToken ct) => Task.FromResult(0);
        public DateTimeOffset? LastSyncedUtc(Guid accountId) => null;
        public void SeedRebuildBaseline(IEnumerable<Guid> accountIds) { }
        public Task BackfillOfflineBodiesAsync(IEnumerable<AccountModel> accounts,
            IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// A view model on a five-message inbox (newest first, so "a" is row 0 and "e" is row 4), with
    /// an Archive folder available, plus the recording hooks the assertions need.
    /// </summary>
    private sealed class Fixture
    {
        public MainViewModel Vm { get; }
        public StatusAnnouncementRecorder Announced { get; }

        /// <summary>What Messages held each time the ViewModel asked for a synchronous focus move.</summary>
        public List<(MailMessageSummary? Selected, List<MailMessageSummary> Rows)> FocusNowCalls { get; } = new();

        /// <summary>How many times the ViewModel asked for the queued (post-removal) focus move.</summary>
        public int QueuedFocusCalls { get; private set; }

        /// <summary>What the stand-in View reports back: did focus reach the row?</summary>
        public bool FocusNowSucceeds { get; set; } = true;

        private Fixture(MainViewModel vm)
        {
            Vm        = vm;
            Announced = StatusAnnouncementRecorder.Watch(vm);
            vm.MessageListFocusNowRequested += () =>
            {
                FocusNowCalls.Add((vm.SelectedMessage, vm.Messages.ToList()));
                return FocusNowSucceeds;
            };
            vm.MessageListFocusRequested += () => QueuedFocusCalls++;
        }

        public static async Task<Fixture> CreateAsync(int messageCount = 5)
        {
            var inbox   = Folder("INBOX", SpecialFolderKind.Inbox);
            var archive = Folder("Archive", SpecialFolderKind.Archive);
            var ids     = "abcdefgh".Substring(0, messageCount);

            var vm = new MainViewModel(
                new FolderedMailService(
                    new Dictionary<Guid, List<MailFolderModel>> { [Work] = [inbox, archive] },
                    new Dictionary<(Guid, string), List<MailMessageSummary>>
                    {
                        // daysAgo ascending with the letter, so newest-first order is a, b, c, …
                        [(Work, "INBOX")] = [.. ids.Select((c, i) => Msg(c.ToString(), i))],
                    }),
                new StubAccountService(), new StubCredentialService(), new StubLocalStoreService(),
                new StubOAuthService(), new SilentSyncService(), new StubConfigService(),
                new StubCommandRegistry(), new StubViewService(), new StubRuleService(),
                new StubSmtpService());

            vm.Accounts.Add(Account());
            await vm.ConnectAllAccountsAsync();
            await vm.SelectFolderCommand.ExecuteAsync(inbox);

            var f = new Fixture(vm);
            Assert.Equal(ViewMode.Messages, vm.ViewMode);
            Assert.Equal(messageCount, vm.Messages.Count);
            return f;
        }

        public MailMessageSummary Row(string id) => Vm.Messages.First(m => m.MessageId == id);
    }

    // ── Order: focus lands before the row leaves ─────────────────────────────────

    [Fact]
    public async Task DeleteAsksForFocusWhileTheDoomedRowIsStillInTheList()
    {
        var f = await Fixture.CreateAsync();
        var doomed = f.Row("b");

        await f.Vm.DeleteMessagesAsync([doomed]);

        var call = Assert.Single(f.FocusNowCalls);
        // The whole point: at the moment focus was asked for, the row being deleted had not left.
        Assert.Contains(doomed, call.Rows);
        // …and the selection had already moved off it, onto the row that survives.
        Assert.Equal("c", call.Selected?.MessageId);
    }

    [Fact]
    public async Task MoveAsksForFocusWhileTheMovedRowIsStillInTheList() // #670
    {
        var f = await Fixture.CreateAsync();
        var moving = f.Row("b");
        f.Vm.SelectedMessage = moving;

        await f.Vm.MoveSelectedMessagesToFolderAsync([moving], Folder("Archive", SpecialFolderKind.Archive));

        var call = Assert.Single(f.FocusNowCalls);
        Assert.Contains(moving, call.Rows);                  // asked for while the row was still there …
        Assert.Equal("c", call.Selected?.MessageId);         // … with the selection already on the survivor
        Assert.Equal(0, f.QueuedFocusCalls);                 // landed, so no second focus move afterwards
        Assert.DoesNotContain(moving, f.Vm.Messages);
    }

    [Fact]
    public async Task MoveFallsBackToTheQueuedFocusWhenLandingFails() // #670
    {
        var f = await Fixture.CreateAsync();
        f.FocusNowSucceeds = false;
        f.Vm.SelectedMessage = f.Row("b");

        await f.Vm.MoveSelectedMessagesToFolderAsync([f.Row("b")], Folder("Archive", SpecialFolderKind.Archive));

        Assert.Equal(1, f.QueuedFocusCalls);
        Assert.Equal("c", f.Vm.SelectedMessage?.MessageId);
        // Still a single move: not spoken, as a single delete is not, whether or not landing worked.
        Assert.DoesNotContain(f.Announced.Announced, a => a.Category != AnnouncementCategory.Silent);
    }

    [Fact]
    public async Task MoveLeavesTheSelectionAlone_WhenItIsNotAmongTheMoved() // #670
    {
        // The landing runs after the server move, and by then the user may be somewhere else: on another row,
        // reading it. Neither focus nor the reading pane is taken from them.
        var f = await Fixture.CreateAsync();
        f.Vm.SelectedMessage = f.Row("e");
        f.Vm.IsMessageOpen = true;

        await f.Vm.MoveSelectedMessagesToFolderAsync([f.Row("b")], Folder("Archive", SpecialFolderKind.Archive));

        Assert.Empty(f.FocusNowCalls);
        Assert.Equal(0, f.QueuedFocusCalls);
        Assert.Equal("e", f.Vm.SelectedMessage?.MessageId);
        Assert.True(f.Vm.IsMessageOpen);
    }

    [Fact]
    public async Task MovingInAGroupView_ClearsTheMovedSelection() // #670, as delete does
    {
        // In the trees, focus is the view's business after the rebuild. A selection left on a moved message
        // keeps the per-message hotkeys live against a message that has gone.
        var f = await Fixture.CreateAsync();
        f.Vm.ViewMode = ViewMode.Conversations;
        f.Vm.SelectedMessage = f.Row("b");

        await f.Vm.MoveSelectedMessagesToFolderAsync([f.Row("b")], Folder("Archive", SpecialFolderKind.Archive));

        Assert.Null(f.Vm.SelectedMessage);
        Assert.Equal(0, f.QueuedFocusCalls);
    }

    [Fact]
    public async Task MovingEveryMessage_PutsFocusOnTheListBeforeTheRowsLeave() // #670
    {
        // With no survivor to land on, focus would stay on a row that has gone, or in a message the reading pane has
        // just cleared: a blank page a screen reader user could leave only with Alt+Tab. The list itself takes it,
        // asked for while the row is still there.
        var f = await Fixture.CreateAsync(messageCount: 1);
        var only = f.Row("a");
        f.Vm.SelectedMessage = only;
        f.Vm.IsMessageOpen = true;

        await f.Vm.MoveSelectedMessagesToFolderAsync([only], Folder("Archive", SpecialFolderKind.Archive));

        var call = Assert.Single(f.FocusNowCalls);
        Assert.Null(call.Selected);            // nothing left to select: the list itself takes focus
        Assert.Contains(only, call.Rows);      // asked for while the row was still listed
        Assert.Equal(0, f.QueuedFocusCalls);
        Assert.Empty(f.Vm.Messages);
    }

    [Fact]
    public async Task MovingTheLastMessageSaysTheFolderIsNowEmpty() // #670, as delete says it
    {
        var f = await Fixture.CreateAsync(messageCount: 1);
        f.Vm.SelectedMessage = f.Row("a");

        await f.Vm.MoveSelectedMessagesToFolderAsync([f.Row("a")], Folder("Archive", SpecialFolderKind.Archive));

        Assert.Equal(("1 message moved to Archive. Folder is now empty.", AnnouncementCategory.MessageAction),
            f.Announced.Last);
    }

    [Fact]
    public async Task MovingTheOpenMessage_ClosesTheReadingPane() // #670
    {
        // As delete, archive and unwatch do: otherwise the pane keeps showing a message no longer in the list.
        var f = await Fixture.CreateAsync();
        f.Vm.SelectedMessage = f.Row("b");
        f.Vm.IsMessageOpen = true;

        await f.Vm.MoveSelectedMessagesToFolderAsync([f.Row("b")], Folder("Archive", SpecialFolderKind.Archive));

        Assert.False(f.Vm.IsMessageOpen);
    }

    [Fact]
    public async Task MovingOneMessageIsNeverSpoken() // #670, as #667 for delete
    {
        // The next message has just been read out; "1 message moved" would interrupt it.
        var f = await Fixture.CreateAsync();
        f.Vm.SelectedMessage = f.Row("b");

        await f.Vm.MoveSelectedMessagesToFolderAsync([f.Row("b")], Folder("Archive", SpecialFolderKind.Archive));

        Assert.DoesNotContain(f.Announced.Announced, a => a.Category != AnnouncementCategory.Silent);
        Assert.Equal(("1 message moved to Archive.", AnnouncementCategory.Silent), f.Announced.Last);
    }

    [Fact]
    public async Task MovingSeveralMessagesSaysTheCount() // #670
    {
        var f = await Fixture.CreateAsync();
        f.Vm.SelectedMessage = f.Row("b");

        await f.Vm.MoveSelectedMessagesToFolderAsync([f.Row("b"), f.Row("c")], Folder("Archive", SpecialFolderKind.Archive));

        Assert.Equal(("2 messages moved to Archive.", AnnouncementCategory.MessageAction), f.Announced.Last);
    }

    [Fact]
    public async Task DeleteLandsOnTheRowAfterTheDeletedBlock()
    {
        var f = await Fixture.CreateAsync();

        await f.Vm.DeleteMessagesAsync([f.Row("b"), f.Row("c")]);

        Assert.Equal("d", Assert.Single(f.FocusNowCalls).Selected?.MessageId);
        Assert.Equal("d", f.Vm.SelectedMessage?.MessageId);
    }

    [Fact]
    public async Task DeletingTheLastRowLandsOnTheOneBeforeIt()
    {
        var f = await Fixture.CreateAsync();

        await f.Vm.DeleteMessagesAsync([f.Row("e")]);

        Assert.Equal("d", Assert.Single(f.FocusNowCalls).Selected?.MessageId);
        Assert.Equal("d", f.Vm.SelectedMessage?.MessageId);
    }

    [Fact]
    public async Task DeletingEveryRowAsksForNoFocusMoveAtAll()
    {
        var f = await Fixture.CreateAsync();

        await f.Vm.DeleteMessagesAsync([.. f.Vm.Messages]);

        // Nowhere to land, so nothing is asked for — rather than focusing a row about to vanish.
        Assert.Empty(f.FocusNowCalls);
        Assert.Empty(f.Vm.Messages);
    }

    [Fact]
    public async Task FocusIsNotAskedForTwiceWhenItAlreadyLanded()
    {
        var f = await Fixture.CreateAsync();

        await f.Vm.DeleteMessagesAsync([f.Row("b")]);

        // The queued request lands on a later dispatcher pass, by which time the removal may have
        // regenerated the container — focusing a fresh one reads the row out a second time. Once
        // focus has landed there is nothing left to ask for.
        Assert.Single(f.FocusNowCalls);
        Assert.Equal(0, f.QueuedFocusCalls);
    }

    [Fact]
    public async Task TheQueuedRequestStillRunsWhenFocusCouldNotLand()
    {
        var f = await Fixture.CreateAsync();
        f.FocusNowSucceeds = false;   // e.g. the container was never realized

        await f.Vm.DeleteMessagesAsync([f.Row("b")]);

        // Skipping the fallback is only safe when focus actually arrived. It did not, so the user
        // must not be left with focus nowhere.
        Assert.Single(f.FocusNowCalls);
        Assert.Equal(1, f.QueuedFocusCalls);
        Assert.Equal("c", f.Vm.SelectedMessage?.MessageId);
    }

    [Fact]
    public async Task ArchiveAsksForFocusWhileTheDoomedRowIsStillInTheList()
    {
        var f = await Fixture.CreateAsync();
        var doomed = f.Row("b");

        await f.Vm.ArchiveMessagesAsync([doomed]);

        var call = Assert.Single(f.FocusNowCalls);
        Assert.Contains(doomed, call.Rows);
        Assert.Equal("c", call.Selected?.MessageId);
    }

    // ── What is spoken ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DeletingOneMessageIsNeverSpoken()
    {
        var f = await Fixture.CreateAsync();

        await f.Vm.DeleteMessagesAsync([f.Row("b")]);

        Assert.DoesNotContain(f.Announced.Announced, a => a.Category != AnnouncementCategory.Silent);
        // The status bar still says it — this is about speech, not about hiding the outcome.
        Assert.Equal("1 message deleted.", f.Vm.StatusText);
    }

    [Fact]
    public async Task DeletingSeveralMessagesSpeaksTheCount()
    {
        var f = await Fixture.CreateAsync();

        await f.Vm.DeleteMessagesAsync([f.Row("b"), f.Row("c"), f.Row("d")]);

        Assert.Equal(("3 messages deleted.", AnnouncementCategory.MessageAction), f.Announced.Last);
    }

    [Fact]
    public async Task EmptyingTheFolderBySayingSoIsSpoken()
    {
        var f = await Fixture.CreateAsync(messageCount: 1);

        await f.Vm.DeleteMessagesAsync([f.Row("a")]);

        // No next row is being read, so there is nothing to interrupt, and "empty" is a state the
        // user cannot otherwise get from a list that shows nothing.
        Assert.Equal(("1 message deleted. Folder is now empty.", AnnouncementCategory.MessageAction),
            f.Announced.Last);
    }

    [Fact]
    public async Task ArchivingOneMessageIsNeverSpoken()
    {
        var f = await Fixture.CreateAsync();

        await f.Vm.ArchiveMessagesAsync([f.Row("b")]);

        Assert.DoesNotContain(f.Announced.Announced, a => a.Category != AnnouncementCategory.Silent);
        Assert.Equal("1 message archived.", f.Vm.StatusText);
    }

    [Fact]
    public async Task ArchivingSeveralMessagesSpeaksTheCount()
    {
        var f = await Fixture.CreateAsync();

        await f.Vm.ArchiveMessagesAsync([f.Row("b"), f.Row("c")]);

        Assert.Equal(("2 messages archived.", AnnouncementCategory.MessageAction), f.Announced.Last);
    }

    [Fact]
    public void SilentIsNotSomethingTheUserCanTurnBackOn()
    {
        // Every other category answers to a setting. Silent does not: it is the statement that a
        // status carries nothing new, which no preference makes untrue. If this starts failing
        // because Silent became configurable, the announcement it was meant to remove is back.
        var configurable = new[]
        {
            AnnouncementCategory.Hint, AnnouncementCategory.Status,
            AnnouncementCategory.Result, AnnouncementCategory.MessageAction,
        };
        Assert.DoesNotContain(AnnouncementCategory.Silent, configurable);
        Assert.Equal(configurable.Length + 1, Enum.GetValues<AnnouncementCategory>().Length);
    }
}
