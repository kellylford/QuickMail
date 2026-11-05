// Nothing the user did not ask for moves their place in the message list.
//
// What this pins is the row OBJECT and the list OBJECT, because those are what focus hangs on. Replacing
// the collection, or raising Reset on it, destroys the focused row's container; WPF then focuses and selects
// whichever row sits at the old index. Seen for real: one new message moved focus to a different message, so
// Delete would have acted on the wrong one, and a reconnect with the app in the background put focus at the
// top of the list. The focus itself needs a real window and is checked in the test VM; these tests check
// that the conditions for keeping it hold.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class ReloadKeepsPlaceTests
{
    private static readonly Guid Work = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static AccountModel Account() => new()
    {
        Id = Work, AccountName = "Work", Username = "work@example.com", AuthType = AuthType.OAuth2Microsoft,
    };

    private static MailFolderModel Sent() => new()
    {
        AccountId = Work, FullName = "Sent", DisplayName = "Sent", Kind = SpecialFolderKind.Sent,
    };

    private static MailMessageSummary Msg(string id, int hoursAgo, bool read = true) => new()
    {
        MessageId  = id,
        AccountId  = Work,
        FolderName = "Sent",
        From       = "someone@example.com",
        Subject    = $"Subject {id}",
        Date       = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).AddHours(-hoursAgo),
        IsRead     = read,
    };

    private sealed class RaisableSyncService : ISyncService
    {
#pragma warning disable CS0067 // not raised by this fake
        public event Action<IReadOnlyList<MailMessageSummary>>? FolderReadStatesReconciled;
        public event Action<int>? RulesApplied;
        public event Action<int, int>? SyncProgressChanged;
        public event Action<int, int>? OfflineBodyProgressChanged;
        public event Action<int, int>? OfflineBodyPassCompleted;
        public event Action<int, int>? SenderEmailBackfillProgressChanged;
        public event Action<int, int>? SenderEmailBackfillEnded;
#pragma warning restore CS0067
        public event Action<IReadOnlyList<MailMessageSummary>>? FolderSynced;
        public event Action<IReadOnlyList<MailMessageSummary>>? MessagesRemoved;

        public void RaiseSynced(params MailMessageSummary[] messages) => FolderSynced?.Invoke(messages);
        public void RaiseRemoved(params MailMessageSummary[] messages) => MessagesRemoved?.Invoke(messages);

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
        public Task ApplyPendingRulesAsync(AccountModel account, MailFolderModel folder, CancellationToken ct) => Task.CompletedTask;
        public Task BackfillOfflineBodiesAsync(IEnumerable<AccountModel> accounts,
            IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>A view model on the Sent folder, rows a (newest) to f, each load giving new objects as a real one does.</summary>
    private sealed class Fixture
    {
        public required MainViewModel Vm { get; init; }
        public required RaisableSyncService Sync { get; init; }
        public required List<MailMessageSummary> Server { get; init; }

        public int Resets { get; private set; }
        public int Replacements { get; private set; }
        public List<List<MailMessageSummary>> FocusNowCalls { get; } = new();
        public bool ListHasFocus { get; set; }
        public bool ReadingPaneHasFocus { get; set; }
        public MailMessageSummary? KeptDuringReplacement { get; private set; }

        public MailMessageSummary Row(string id) => Vm.Messages.First(m => m.MessageId == id);

        public static async Task<Fixture> CreateAsync()
        {
            var folder = Sent();
            var server = "abcdef".Select((c, i) => Msg(c.ToString(), i)).ToList();
            var sync = new RaisableSyncService();
            var vm = new MainViewModel(
                new FreshCopiesMailService(new Dictionary<Guid, List<MailFolderModel>> { [Work] = [folder] }, server),
                new StubAccountService(), new StubCredentialService(), new StubLocalStoreService(),
                new StubOAuthService(), sync, new StubConfigService(),
                new StubCommandRegistry(), new StubViewService(), new StubRuleService(),
                new StubSmtpService());
            vm.Accounts.Add(Account());
            await vm.ConnectAllAccountsAsync();
            await vm.SelectFolderCommand.ExecuteAsync(folder);
            await SettleAsync(vm, () => vm.Messages.Count == server.Count);

            var f = new Fixture { Vm = vm, Sync = sync, Server = server };
            vm.Messages.CollectionChanged += f.OnCollectionChanged;
            vm.PropertyChanged += f.OnPropertyChanged;
            vm.MessageListFocusNowRequested += () => { f.FocusNowCalls.Add(vm.Messages.ToList()); return true; };
            vm.MessageListHasFocusQuery += () => f.ListHasFocus;
            vm.ReadingPaneHasFocusQuery += () => f.ReadingPaneHasFocus;
            return f;
        }

        private void OnCollectionChanged(object? s, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) Resets++;
        }

        private void OnPropertyChanged(object? s, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(MainViewModel.Messages)) return;
            Replacements++;
            KeptDuringReplacement = Vm.RowKeptByReplacement;
        }

        public async Task ReloadAsync()
        {
            await Vm.RefreshCommand.ExecuteAsync(null);
            await SettleAsync(Vm, () => true);
        }
    }

    private static async Task SettleAsync(MainViewModel vm, Func<bool> done)
    {
        for (var i = 0; i < 200 && (vm.IsBusy || !done()); i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.False(vm.IsBusy, "the load never finished");
        Assert.True(done(), "the load finished without the expected result");
    }

    // ── A reload of the open folder ──────────────────────────────────────────

    [Fact]
    public async Task Reload_WithNothingNew_LeavesTheListAndTheSelectionUntouched()
    {
        var f = await Fixture.CreateAsync();
        var list = f.Vm.Messages;
        var rows = list.ToList();
        var selected = f.Row("c");
        f.Vm.SelectedMessage = selected;

        await f.ReloadAsync();

        Assert.Same(list, f.Vm.Messages);
        Assert.Equal(rows, f.Vm.Messages);   // the same objects, not equal copies
        Assert.Same(selected, f.Vm.SelectedMessage);
        Assert.Equal(0, f.Resets);
        Assert.Equal(0, f.Replacements);
        Assert.Empty(f.FocusNowCalls);
    }

    [Fact]
    public async Task Reload_BringsAKeptRowUpToDate()
    {
        var f = await Fixture.CreateAsync();
        var row = f.Row("c");
        Assert.True(row.IsRead);
        f.Server.Single(m => m.MessageId == "c").IsRead = false;

        await f.ReloadAsync();

        Assert.Same(row, f.Row("c"));
        Assert.False(row.IsRead);
    }

    [Fact]
    public async Task Reload_WithAFewNewMessages_InsertsThemWithoutReplacingAnything()
    {
        var f = await Fixture.CreateAsync();
        var selected = f.Row("c");
        f.Vm.SelectedMessage = selected;
        f.Server.Insert(0, Msg("new1", -1));
        f.Server.Insert(0, Msg("new2", -2));

        await f.ReloadAsync();

        Assert.Equal(8, f.Vm.Messages.Count);
        Assert.Equal(["new2", "new1", "a"], f.Vm.Messages.Take(3).Select(m => m.MessageId));
        Assert.Same(selected, f.Vm.SelectedMessage);
        Assert.Equal(0, f.Resets);
        Assert.Equal(0, f.Replacements);
    }

    [Fact]
    public async Task Reload_WithManyNewMessages_ReplacesTheListAndKeepsTheRow()
    {
        var f = await Fixture.CreateAsync();
        var selected = f.Row("c");
        f.Vm.SelectedMessage = selected;
        for (var i = 1; i <= 15; i++) f.Server.Insert(0, Msg($"new{i}", -i));

        await f.ReloadAsync();

        Assert.Equal(21, f.Vm.Messages.Count);
        Assert.Equal(1, f.Replacements);
        Assert.Same(selected, f.KeptDuringReplacement);   // the View corrects WPF's index-based reselection with it
        Assert.Same(selected, f.Vm.SelectedMessage);
        Assert.Null(f.Vm.RowKeptByReplacement);           // one-shot: only meaningful during the notification
    }

    [Fact]
    public async Task Reload_WhenTheSelectedMessageIsGone_LandsOnItsNeighbour_NotTheTop()
    {
        var f = await Fixture.CreateAsync();
        f.Vm.SelectedMessage = f.Row("d");
        f.Server.RemoveAll(m => m.MessageId == "d");

        await f.ReloadAsync();

        Assert.Equal("e", f.Vm.SelectedMessage?.MessageId);
    }

    [Fact]
    public async Task Reload_WhenTheFocusedRowIsGone_MovesFocusBeforeTheRowLeaves() // the #667 order
    {
        var f = await Fixture.CreateAsync();
        var doomed = f.Row("d");
        f.Vm.SelectedMessage = doomed;
        f.ListHasFocus = true;
        f.Server.RemoveAll(m => m.MessageId == "d");

        await f.ReloadAsync();

        Assert.Contains(doomed, Assert.Single(f.FocusNowCalls));
    }

    [Fact]
    public async Task Reload_WhenTheSelectedRowIsGone_LeavesFocusAloneIfItIsElsewhere()
    {
        var f = await Fixture.CreateAsync();
        f.Vm.SelectedMessage = f.Row("d");
        f.ListHasFocus = false;
        f.Server.RemoveAll(m => m.MessageId == "d");

        await f.ReloadAsync();

        Assert.Empty(f.FocusNowCalls);
        Assert.Equal("e", f.Vm.SelectedMessage?.MessageId);
    }

    [Fact]
    public async Task Reload_WhenTheOpenMessageIsGone_SelectsNothing_SoDeleteCannotHitAMessageNobodySaw()
    {
        var f = await Fixture.CreateAsync();
        f.Vm.SelectedMessage = f.Row("d");
        f.Vm.IsMessageOpen = true;
        f.ListHasFocus = false;
        f.Server.RemoveAll(m => m.MessageId == "d");

        await f.ReloadAsync();

        Assert.Null(f.Vm.SelectedMessage);
        Assert.True(f.Vm.IsMessageOpen);   // the user is reading it; a reload does not close it
        Assert.Empty(f.FocusNowCalls);
    }

    [Fact]
    public async Task Reload_ManyChangesWhenTheOpenMessageIsGone_SelectsNothing()
    {
        // The replacement path, past ten edits: the same rule as the in-place one above.
        var f = await Fixture.CreateAsync();
        f.Vm.SelectedMessage = f.Row("d");
        f.Vm.IsMessageOpen = true;
        f.Server.RemoveAll(m => m.MessageId == "d");
        for (var i = 1; i <= 15; i++) f.Server.Insert(0, Msg($"new{i}", -i));

        await f.ReloadAsync();

        Assert.Equal(1, f.Replacements);
        Assert.Null(f.Vm.SelectedMessage);
        Assert.Null(f.KeptDuringReplacement);
    }

    [Fact]
    public async Task Reload_OpenMessageGone_WithFocusOnItsRowInTheList_StillMovesFocusOffIt()
    {
        // F6 back to the list with the message open, and the focused row is the one leaving.
        var f = await Fixture.CreateAsync();
        var doomed = f.Row("d");
        f.Vm.SelectedMessage = doomed;
        f.Vm.IsMessageOpen = true;
        f.ListHasFocus = true;
        f.Server.RemoveAll(m => m.MessageId == "d");

        await f.ReloadAsync();

        Assert.Null(f.Vm.SelectedMessage);
        Assert.Contains(doomed, Assert.Single(f.FocusNowCalls));
    }

    [Fact]
    public async Task Sync_WhenTheOpenMessageLeavesTheFilter_SelectsNothing()
    {
        var f = await Fixture.CreateAsync();
        foreach (var m in f.Server) m.IsRead = false;
        await f.ReloadAsync();
        f.Vm.ActiveFilter = MessageFilter.Unread;
        f.Vm.SelectedMessage = f.Row("d");
        f.Vm.IsMessageOpen = true;

        f.Sync.RaiseSynced(Msg("d", 3, read: true));

        Assert.Null(f.Vm.SelectedMessage);
        Assert.True(f.Vm.IsMessageOpen);
    }

    [Fact]
    public async Task ARemovalReportedByTheServer_ClosesTheOpenMessage_AndLandsOnItsNeighbour()
    {
        var f = await Fixture.CreateAsync();
        var doomed = f.Row("d");
        f.Vm.SelectedMessage = doomed;
        f.Vm.IsMessageOpen = true;

        f.Sync.RaiseRemoved(doomed);

        Assert.False(f.Vm.IsMessageOpen);
        Assert.Equal("e", f.Vm.SelectedMessage?.MessageId);
    }

    [Fact]
    public async Task ARemovalReportedByTheServer_WithFocusInTheOpenMessage_BringsFocusToTheNeighbour()
    {
        // The pane closing strands focus on an empty page otherwise. The question has to be asked before
        // the pane closes, since closing is what takes focus away.
        var f = await Fixture.CreateAsync();
        var doomed = f.Row("d");
        f.Vm.SelectedMessage = doomed;
        f.Vm.IsMessageOpen = true;
        bool? openWhenAsked = null;
        f.Vm.ReadingPaneHasFocusQuery += () => { openWhenAsked ??= f.Vm.IsMessageOpen; return true; };

        f.Sync.RaiseRemoved(doomed);

        Assert.True(openWhenAsked);
        Assert.False(f.Vm.IsMessageOpen);
        Assert.Equal("e", f.Vm.SelectedMessage?.MessageId);
        Assert.Contains(doomed, Assert.Single(f.FocusNowCalls));   // before the row left, as #667 requires
    }

    [Fact]
    public async Task Reload_WhenTheLastRowHoldingFocusGoes_StillAsksForFocus() // onto the list itself, #670
    {
        var f = await Fixture.CreateAsync();
        f.Server.RemoveAll(m => m.MessageId != "a");
        await f.ReloadAsync();
        var last = f.Row("a");
        f.Vm.SelectedMessage = last;
        f.ListHasFocus = true;
        f.Server.Clear();

        await f.ReloadAsync();

        Assert.Empty(f.Vm.Messages);
        Assert.Null(f.Vm.SelectedMessage);
        Assert.Contains(last, Assert.Single(f.FocusNowCalls));
    }

    [Fact]
    public async Task Reload_UpdatesTextOnAKeptRow_AndTheRowSaysSo()
    {
        // The Outbox relists through SetMessages with its state in the subject.
        var f = await Fixture.CreateAsync();
        var row = f.Row("c");
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        f.Server.Single(m => m.MessageId == "c").Subject = "Failed: Subject c";

        await f.ReloadAsync();

        Assert.Same(row, f.Row("c"));
        Assert.Equal("Failed: Subject c", row.Subject);
        Assert.Contains(nameof(MailMessageSummary.Subject), raised);
    }

    [Fact]
    public async Task Reload_ManyNewMessages_SaysTheReplacementIsNotANavigation()
    {
        var f = await Fixture.CreateAsync();
        var background = new List<bool>();
        f.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Messages)) background.Add(f.Vm.IsReplacingInBackground);
        };
        for (var i = 1; i <= 15; i++) f.Server.Insert(0, Msg($"new{i}", -i));

        await f.ReloadAsync();

        Assert.Equal([true], background);
        Assert.False(f.Vm.IsReplacingInBackground);
    }

    [Fact]
    public async Task GoingToTheFolder_EvenTheOpenOne_IsNotAReload()
    {
        // Enter on a folder in the tree is the user going there: it loads afresh and lands at the top,
        // which is what moves focus into the list.
        var f = await Fixture.CreateAsync();
        f.Vm.SelectedMessage = f.Row("c");
        var old = f.Vm.Messages.ToList();

        await f.Vm.SelectFolderCommand.ExecuteAsync(f.Vm.SelectedFolder);
        await SettleAsync(f.Vm, () => f.Vm.Messages.Count == old.Count);

        Assert.True(f.Replacements > 0);
        Assert.DoesNotContain(f.Vm.Messages, old.Contains);
    }

    [Fact]
    public async Task AnEmptyListIsNotReloaded_SoTheFirstRowsStillArriveAsAReplacement()
    {
        // Opening a folder with nothing cached: rows arrive after an empty first pass. That has to replace
        // the list, because the replacement is what moves focus into it.
        var f = await Fixture.CreateAsync();
        f.Server.Clear();
        await f.ReloadAsync();
        Assert.Empty(f.Vm.Messages);

        var replacementsBefore = f.Replacements;
        f.Server.Add(Msg("only", 0));
        await f.ReloadAsync();

        Assert.Single(f.Vm.Messages);
        Assert.True(f.Replacements > replacementsBefore);
    }

    // ── Live arrivals ────────────────────────────────────────────────────────

    [Fact]
    public async Task ANewMessage_ArrivesWithoutAReset() // the bug: Reset moved focus to a different message
    {
        var f = await Fixture.CreateAsync();
        var selected = f.Row("c");
        f.Vm.SelectedMessage = selected;

        f.Sync.RaiseSynced(Msg("new", -1));

        Assert.Equal("new", f.Vm.Messages[0].MessageId);
        Assert.Same(selected, f.Vm.SelectedMessage);
        Assert.Equal(0, f.Resets);
        Assert.Equal(0, f.Replacements);
    }

    [Fact]
    public async Task ManyNewMessages_ArriveAsOneReplacementThatKeepsTheRow()
    {
        var f = await Fixture.CreateAsync();
        var selected = f.Row("c");
        f.Vm.SelectedMessage = selected;

        f.Sync.RaiseSynced([.. Enumerable.Range(1, 15).Select(i => Msg($"new{i}", -i))]);

        Assert.Equal(21, f.Vm.Messages.Count);
        Assert.Equal(1, f.Replacements);
        Assert.Same(selected, f.KeptDuringReplacement);
        Assert.Same(selected, f.Vm.SelectedMessage);
    }

    [Fact]
    public async Task ASyncThatBringsNothingNew_ChangesNothing()
    {
        var f = await Fixture.CreateAsync();
        f.Vm.SelectedMessage = f.Row("c");
        var replacements = f.Replacements;

        f.Sync.RaiseSynced(Msg("c", 2));

        Assert.Equal(replacements, f.Replacements);
        Assert.Equal(0, f.Resets);
    }

    [Fact]
    public async Task AMessageReadElsewhere_LeavesTheUnreadViewWithoutMovingTheUser()
    {
        var f = await Fixture.CreateAsync();
        foreach (var m in f.Server) m.IsRead = false;
        await f.ReloadAsync();
        f.Vm.ActiveFilter = MessageFilter.Unread;
        var selected = f.Row("c");
        f.Vm.SelectedMessage = selected;
        var resetsBefore = f.Resets;

        f.Sync.RaiseSynced(Msg("e", 4, read: true));

        Assert.DoesNotContain(f.Vm.Messages, m => m.MessageId == "e");
        Assert.Same(selected, f.Vm.SelectedMessage);
        Assert.Equal(resetsBefore, f.Resets);
    }

    // ── Removals another client made ─────────────────────────────────────────

    [Fact]
    public async Task ASelectedMessageRemovedElsewhere_LandsOnItsNeighbour_NotTheTop()
    {
        var f = await Fixture.CreateAsync();
        var doomed = f.Row("d");
        f.Vm.SelectedMessage = doomed;

        f.Sync.RaiseRemoved(doomed);

        Assert.Equal("e", f.Vm.SelectedMessage?.MessageId);
        Assert.DoesNotContain(doomed, f.Vm.Messages);
    }

    [Fact]
    public async Task ASelectedLastRowRemovedElsewhere_LandsOnTheRowAboveIt()
    {
        var f = await Fixture.CreateAsync();
        var doomed = f.Row("f");
        f.Vm.SelectedMessage = doomed;

        f.Sync.RaiseRemoved(doomed);

        Assert.Equal("e", f.Vm.SelectedMessage?.MessageId);
    }

    [Fact]
    public async Task AnotherMessageRemovedElsewhere_LeavesTheSelectionAlone()
    {
        var f = await Fixture.CreateAsync();
        var selected = f.Row("c");
        f.Vm.SelectedMessage = selected;

        f.Sync.RaiseRemoved(f.Row("a"));

        Assert.Same(selected, f.Vm.SelectedMessage);
        Assert.Empty(f.FocusNowCalls);
    }

    // ── Grouped views ────────────────────────────────────────────────────────

    [Fact]
    public async Task AReloadWithNothingNew_DoesNotRebuildTheConversationTree()
    {
        // Replacing the tree's items is what collapses the user's place into a group row.
        var f = await Fixture.CreateAsync();
        f.Vm.ViewMode = ViewMode.Conversations;
        await SettleAsync(f.Vm, () => f.Vm.Conversations.Count == 6);
        var replacements = 0;
        f.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Conversations)) replacements++; };

        await f.ReloadAsync();
        // Rebuilds run off the thread and only the latest counts, so an arrival is what proves they are done.
        f.Sync.RaiseSynced(Msg("new", -1));
        await SettleAsync(f.Vm, () => f.Vm.Conversations.Count == 7);

        Assert.Equal(1, replacements);
    }

    // Where a place is found again. A selected group is what Delete acts on, so the open-message cases
    // are as much about what must not be chosen.
    private static ConversationGroup Conv(string key, params MailMessageSummary[] messages) =>
        new() { NormalizedSubject = key, Messages = messages };

    private static readonly Func<MailMessageSummary, string?> ConversationKey = m => m.Subject;

    [Fact]
    public void GroupedPlace_FindsAMessageWhereverItsGroupHasGone()
    {
        var m = Msg("m", 0);
        var home = Conv("home", m);
        var (group, message) = GroupedPlace.Find([Conv("other"), home], m, 0, messageOpen: false, ConversationKey);

        Assert.Same(home, group);
        Assert.Same(m, message);
    }

    [Fact]
    public void GroupedPlace_FindsAGroupByKey_NotPosition()
    {
        var was = Conv("same", Msg("a", 0));
        var now = Conv("SAME", Msg("a", 0));

        Assert.Same(now, GroupedPlace.Find([Conv("new"), now], was, 0, messageOpen: false, ConversationKey).Group);
    }

    [Fact]
    public void GroupedPlace_AMessageThatHasGone_FallsToItsConversation_ThenToItsPosition()
    {
        var gone = Msg("gone", 0);
        gone.Subject = "thread";
        var thread = Conv("thread", Msg("a", 0));
        var other = Conv("other", Msg("b", 1));

        var (group, message) = GroupedPlace.Find([other, thread], gone, 0, messageOpen: false, ConversationKey);
        Assert.Same(thread, group);
        Assert.Null(message);

        Assert.Same(other, GroupedPlace.Find([Conv("x"), other], gone, 1, messageOpen: false, ConversationKey).Group);
    }

    [Fact]
    public void GroupedPlace_WithAMessageOpen_NeverFallsBackToAnotherGroup()
    {
        var gone = Msg("gone", 0);
        gone.Subject = "thread";
        var (group, message) = GroupedPlace.Find([Conv("x"), Conv("y")], gone, 1, messageOpen: true, ConversationKey);

        Assert.Null(group);
        Assert.Null(message);
        Assert.Null(GroupedPlace.Find([Conv("x")], Conv("was"), 0, messageOpen: true, ConversationKey).Group);
    }

    [Fact]
    public void GroupedPlace_WithAMessageOpen_AMessageThatHasGone_DoesNotSelectItsConversation()
    {
        // A selected conversation is what Delete would remove, all of it, with one message showing.
        var gone = Msg("gone", 0);
        gone.Subject = "thread";

        var (group, _) = GroupedPlace.Find([Conv("thread", Msg("a", 0))], gone, 0, messageOpen: true, ConversationKey);

        Assert.Null(group);
    }

    [Fact]
    public void GroupedPlace_NoPlaceKnown_IsTheTop_UnlessAMessageIsOpen()
    {
        var first = Conv("first");
        Assert.Same(first, GroupedPlace.Find([first, Conv("second")], null, 0, messageOpen: false, ConversationKey).Group);
        Assert.Null(GroupedPlace.Find([first, Conv("second")], null, 0, messageOpen: true, ConversationKey).Group);
    }

    [Fact]
    public void GroupedPlace_IndexOfAMessage_IsItsGroups()
    {
        var m = Msg("m", 0);
        var groups = new object[] { Conv("a"), Conv("b"), Conv("c", m) };

        Assert.Equal(2, GroupedPlace.IndexOf(groups, m));
        Assert.Equal(1, GroupedPlace.IndexOf(groups, groups[1]));
        Assert.Equal(-1, GroupedPlace.IndexOf(groups, Msg("gone", 0)));
    }

    [Fact]
    public void AGroupHearsItsNewestMessagesSubject()
    {
        var row = Msg("a", 0);
        var group = new ConversationGroup { NormalizedSubject = "s", Messages = [row] };
        var raised = new List<string?>();
        group.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.Subject = "Failed: s";

        Assert.Contains(nameof(ConversationGroup.Subject), raised);
    }

    [Fact]
    public void GroupsLetGoOfTheirMessages()
    {
        var row = Msg("a", 0);
        var group = new ConversationGroup { NormalizedSubject = "s", Messages = [row] };
        var raised = 0;
        group.PropertyChanged += (_, _) => raised++;

        row.IsRead = false;
        Assert.True(raised > 0);

        raised = 0;
        group.ReleaseMessages();
        row.IsRead = true;
        Assert.Equal(0, raised);
    }

    [Fact]
    public void AGroupHearsItsNewestMessagesPreview()
    {
        var row = Msg("a", 0);
        var group = new SenderGroup { SenderKey = "s", Messages = [row] };
        var raised = new List<string?>();
        group.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.Preview = "now known";

        Assert.Contains(nameof(SenderGroup.Preview), raised);
    }

    [Fact]
    public async Task ANewMessage_DoesRebuildTheConversationTree()
    {
        var f = await Fixture.CreateAsync();
        f.Vm.ViewMode = ViewMode.Conversations;
        await SettleAsync(f.Vm, () => f.Vm.Conversations.Count == 6);

        f.Sync.RaiseSynced(Msg("new", -1));

        await SettleAsync(f.Vm, () => f.Vm.Conversations.Count == 7);
    }

    // ── The pieces ───────────────────────────────────────────────────────────

    [Fact]
    public void CopyLoadedStateFrom_CoversEverySettableField()
    {
        // A field added to the row and missed here keeps its old value through every reload, quietly.
        string[] deliberatelyNotCopied =
        [
            nameof(MailMessageSummary.IsWatched),          // derived; a loaded message has not been stamped
            nameof(MailMessageSummary.FolderDisplayName),  // stamped by the load afterwards
        ];
        var fields = typeof(MailMessageSummary)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && !deliberatelyNotCopied.Contains(p.Name));

        foreach (var field in fields)
        {
            var row = new MailMessageSummary();
            var fresh = new MailMessageSummary();
            var value = SampleOf(field.PropertyType);
            field.SetValue(fresh, value);

            row.CopyLoadedStateFrom(fresh);

            Assert.True(Equals(value, field.GetValue(row)), $"{field.Name} was not copied");
        }
    }

    private static object SampleOf(Type type)
    {
        if (type == typeof(string)) return "sample";
        if (type == typeof(bool)) return true;
        if (type == typeof(Guid)) return Work;
        if (type == typeof(DateTimeOffset)) return new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        throw new InvalidOperationException($"Add a sample value for {type}.");
    }

    [Fact]
    public void CopyLoadedStateFrom_KeepsWhatAServerListDoesNotCarry()
    {
        var row = new MailMessageSummary { Preview = "kept", FlagId = "f", FlagName = "Work", FlagColorHex = "#fff" };
        row.CopyLoadedStateFrom(new MailMessageSummary { FlagId = "f" });

        Assert.Equal("kept", row.Preview);
        Assert.Equal("Work", row.FlagName);
        Assert.Equal("#fff", row.FlagColorHex);
    }

    [Fact]
    public void CopyLoadedStateFrom_ClearsAFlagRemovedElsewhere()
    {
        var row = new MailMessageSummary { FlagId = "f", FlagName = "Work", FlagColorHex = "#fff" };
        row.CopyLoadedStateFrom(new MailMessageSummary());

        Assert.Null(row.FlagId);
        Assert.Null(row.FlagName);
        Assert.Null(row.FlagColorHex);
    }

    public sealed class Row(string name)
    {
        public override string ToString() => name;
    }

    private static readonly Row A = new("a"), B = new("b"), C = new("c"), D = new("d");

    [Theory]
    [MemberData(nameof(InPlaceCases))]
    public void ListMerge_AppliesTheChangeInPlace(Row[] from, Row[] to)
    {
        var list = from.ToList();
        Assert.True(ListMerge.CanApplyInPlace(list, to, maxEdits: 10));

        var changed = ListMerge.ApplyInPlace(list, to);

        Assert.Equal(to, list);
        Assert.Equal(!from.SequenceEqual(to), changed);
    }

    public static TheoryData<Row[], Row[]> InPlaceCases => new()
    {
        { [A, B, C], [A, B, C] },      // nothing
        { [B, C], [A, B, C] },         // at the top
        { [A, C], [A, B, C] },         // in the middle
        { [A, B], [A, B, C] },         // at the end
        { [A, B, C], [A, C] },         // removed
        { [A, B, C], [] },             // all removed
        { [], [A, B] },                // all new
        { [A, B, C], [D, A, C] },      // one of each
    };

    [Fact]
    public void ListMerge_RefusesToReorder()
    {
        Assert.False(ListMerge.CanApplyInPlace([A, B, C], [B, A, C], maxEdits: 10));
    }

    [Fact]
    public void ListMerge_RefusesMoreEditsThanAllowed()
    {
        Assert.True(ListMerge.CanApplyInPlace([A, B], [A, B, C, D], maxEdits: 2));
        Assert.False(ListMerge.CanApplyInPlace([A, B], [A, B, C, D], maxEdits: 1));
    }

    [Fact]
    public void ListMerge_ComparesByReferenceNotEquality()
    {
        Assert.False(ListMerge.CanApplyInPlace(["x"], [new string('x', 1)], maxEdits: 0));
    }

    [Fact]
    public void SameGroups_IsFalseForADifferentKeyOrderOrMessage()
    {
        (string Key, IReadOnlyList<Row> Rows) G(string k, params Row[] rows) => (k, rows);
        bool Same(IReadOnlyList<(string Key, IReadOnlyList<Row> Rows)> x, IReadOnlyList<(string Key, IReadOnlyList<Row> Rows)> y) =>
            ListMerge.SameGroups(x, y, g => g.Key, g => g.Rows);

        Assert.True(Same([G("one", A, B)], [G("ONE", A, B)]));   // group keys ignore case, as the builders do
        Assert.False(Same([G("one", A)], [G("two", A)]));
        Assert.False(Same([G("one", A), G("two", B)], [G("two", B), G("one", A)]));
        Assert.False(Same([G("one", A)], [G("one", B)]));
        Assert.False(Same([G("one", A)], [G("one", A, B)]));
        Assert.False(Same([G("one", A)], []));
        Assert.False(Same([], []));   // a landing may be waiting on the replacement
    }

    /// <summary>Serves the list a test edits, as new objects on every call, the way a real load does.</summary>
    private sealed class FreshCopiesMailService(Dictionary<Guid, List<MailFolderModel>> folders, List<MailMessageSummary> server)
        : StubImapMailServiceBase
    {
        public override Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default) =>
            Task.FromResult(folders.TryGetValue(accountId, out var f) ? f : []);

        public override Task<List<MailMessageSummary>> GetMessageSummariesAsync(
            Guid accountId, string folderName, int maxMessages, CancellationToken ct = default) =>
            Task.FromResult(server.Select(Copy).ToList());

        public override Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(
            Guid accountId, string folderName, DateTime since, CancellationToken ct = default) =>
            Task.FromResult(server.Select(Copy).ToList());

        private static MailMessageSummary Copy(MailMessageSummary m) => new()
        {
            MessageId = m.MessageId, AccountId = m.AccountId, FolderName = m.FolderName,
            From = m.From, Subject = m.Subject, Date = m.Date, IsRead = m.IsRead,
        };
    }
}
