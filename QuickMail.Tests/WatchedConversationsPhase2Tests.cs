// Phase 2 of watched conversations: the manager, the Watched filter, and notifications.
//
// The two tests that matter most here are the ones covering silent failures:
//   * EveryMessageFilterValue_RoundTripsThroughItsStringKey — adding a MessageFilter value means
//     touching eleven sites and NOTHING in the suite enumerated the enum, so a missed one shipped
//     quietly. This closes that.
//   * The notification ordering tests — the watched and new-mail paths share one dedup set, and
//     NewMailFilter.SelectNew *consumes* whatever it is shown, so getting the order or the input
//     wrong silently suppresses one toast or the other.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class WatchedConversationsPhase2Tests
{
    private static readonly Guid AccountA = Guid.Parse("cccccccc-1111-1111-1111-111111111111");

    private static MailMessageSummary Msg(string id, string subject, bool read = false, int daysAgo = 0) => new()
    {
        MessageId  = id,
        AccountId  = AccountA,
        FolderName = "INBOX",
        From       = "someone@example.com",
        Subject    = subject,
        Date       = DateTimeOffset.Now.AddDays(-daysAgo),
        IsRead     = read,
    };

    private static ProfileContext MakeTempProfile() =>
        new(Path.Combine(Path.GetTempPath(), $"QM-WATCH2-{Guid.NewGuid():N}"));

    // ── WatchService: rename and delete-by-id ────────────────────────────────

    [Fact]
    public void Rename_ChangesTheLabelButNotWhatIsMatched()
    {
        // The whole reason rename is label-only: editing the matching key would silently change
        // which messages the watch collects while presenting to the user as a rename.
        var svc = new WatchService(MakeTempProfile());
        svc.Watch("Re: Budget Review");
        var id = svc.GetAll().Single().Id;

        Assert.True(svc.Rename(id, "Q3 budget thread"));

        var entry = svc.GetAll().Single();
        Assert.Equal("Q3 budget thread", entry.Label);
        Assert.Equal("Budget Review", entry.NormalizedSubject);
        Assert.True(svc.IsWatched("Budget Review"));
        Assert.True(svc.IsWatched("Re: Budget Review"));
    }

    [Fact]
    public void Rename_IsPersisted_AndRefusesABlankLabel()
    {
        var profile = MakeTempProfile();
        var svc = new WatchService(profile);
        svc.Watch("Budget Review");
        var id = svc.GetAll().Single().Id;

        Assert.False(svc.Rename(id, "   "));
        Assert.False(svc.Rename(Guid.NewGuid(), "orphan"));
        Assert.True(svc.Rename(id, "Renamed"));

        Assert.Equal("Renamed", new WatchService(profile).GetAll().Single().Label);
    }

    [Fact]
    public void UnwatchById_RemovesExactlyThatWatch()
    {
        var svc = new WatchService(MakeTempProfile());
        svc.Watch("Budget Review");
        svc.Watch("Trip to Dublin");
        var id = svc.GetAll().Single(w => w.NormalizedSubject == "Budget Review").Id;

        Assert.True(svc.Unwatch(id));
        Assert.False(svc.Unwatch(id));   // already gone

        Assert.Equal("Trip to Dublin", svc.GetAll().Single().NormalizedSubject);
    }

    // ── The Watched filter, and the enum-coverage guard ──────────────────────

    private static (MainViewModel Vm, StubWatchService Watch) MakeVm(IEnumerable<MailMessageSummary>? cached = null)
    {
        var watch = new StubWatchService();
        ILocalStoreService store = cached != null
            ? new FilterableStoreForFlags(cached)
            : new StubLocalStoreService();
        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            store, new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(),
            new StubSmtpService(), watchService: watch);
        return (vm, watch);
    }

    [Fact]
    public async Task WatchedFilter_ShowsOnlyMessagesInWatchedConversations()
    {
        var (vm, watch) = MakeVm(
        [
            Msg("1", "Budget Review",     daysAgo: 2),
            Msg("2", "Re: Budget Review", daysAgo: 1),
            Msg("3", "Unrelated",         daysAgo: 0),
        ]);
        watch.Watch("Budget Review");
        await vm.SelectFolderCommand.ExecuteAsync(MainViewModel.AllMailFolder);

        await vm.SetFilterCommand.ExecuteAsync("watched");

        Assert.Equal(MessageFilter.Watched, vm.ActiveFilter);
        Assert.True(vm.IsFilterWatched);
        Assert.Equal(new[] { "2", "1" }, vm.Messages.Select(m => m.MessageId).ToArray());
    }

    [Fact]
    public async Task EveryMessageFilterValue_RoundTripsThroughItsStringKey()
    {
        // Adding a MessageFilter value means touching eleven sites, and until this test nothing
        // enumerated the enum — so a missed site failed silently. Reflection reaches the two
        // private maps in ViewManagerViewModel deliberately: they are the sites most easily missed
        // and they have no public surface of their own.
        var vmType    = typeof(ViewManagerViewModel);
        var filterKey = vmType.GetMethod("FilterKey", BindingFlags.NonPublic | BindingFlags.Static);
        var filterLbl = vmType.GetMethod("FilterLabel", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(filterKey);
        Assert.NotNull(filterLbl);

        var (vm, _) = MakeVm();
        var seenKeys = new Dictionary<string, MessageFilter>(StringComparer.Ordinal);

        foreach (var filter in Enum.GetValues<MessageFilter>())
        {
            var key = (string)filterKey!.Invoke(null, [filter])!;

            // Every value needs its OWN key. A missing FilterKey arm falls through to "all",
            // which is exactly how a saved view silently loses its filter.
            Assert.False(seenKeys.TryGetValue(key, out var clash),
                $"MessageFilter.{filter} and MessageFilter.{clash} both map to the key \"{key}\" — " +
                $"ViewManagerViewModel.FilterKey is missing an arm.");
            seenKeys[key] = filter;

            // The key must survive the trip back through the command that saved views use.
            await vm.SetFilterCommand.ExecuteAsync(key);
            Assert.Equal(filter, vm.ActiveFilter);

            // …and be nameable in both directions, or the View Manager describes it as "All".
            var label = (string)filterLbl!.Invoke(null, [key])!;
            if (filter == MessageFilter.All)
            {
                Assert.Equal("All", label);
                Assert.Equal(string.Empty, vm.FilterLabel);
            }
            else
            {
                Assert.NotEqual("All", label);
                Assert.False(string.IsNullOrEmpty(vm.FilterLabel),
                    $"MainViewModel.FilterLabel has no arm for MessageFilter.{filter}.");
            }
        }
    }

    // ── Manager view model ───────────────────────────────────────────────────

    private static WatchedConversationsViewModel MakeManager(
        StubWatchService watch, IEnumerable<MailMessageSummary>? cached = null, bool onlineMode = false)
    {
        ILocalStoreService store = cached != null
            ? new FilterableStoreForFlags(cached)
            : new StubLocalStoreService();
        return new WatchedConversationsViewModel(watch, store, onlineMode);
    }

    [Fact]
    public async Task Manager_RowsCarryLabelAndCachedMessageCount()
    {
        var watch = new StubWatchService();
        watch.Watch("Budget Review");
        watch.Watch("Trip to Dublin");
        var mgr = MakeManager(watch,
        [
            Msg("1", "Budget Review"),
            Msg("2", "Re: Budget Review"),
            Msg("3", "Unrelated"),
        ]);

        await mgr.LoadCountsAsync(CancellationToken.None);

        var budget = mgr.Watches.Single(w => w.NormalizedSubject == "Budget Review");
        Assert.Equal(2, budget.MessageCount);
        Assert.Equal("2 messages", budget.CountText);

        // A watch with nothing cached reads 0, not "unavailable" — the cache was readable.
        Assert.Equal(0, mgr.Watches.Single(w => w.NormalizedSubject == "Trip to Dublin").MessageCount);
    }

    [Fact]
    public async Task Manager_InOnlineMode_LeavesCountsUnknownRatherThanZero()
    {
        // There is no local cache to count in --online mode, and a confident zero would be a lie.
        var watch = new StubWatchService();
        watch.Watch("Budget Review");
        var mgr = MakeManager(watch, [Msg("1", "Budget Review")], onlineMode: true);

        await mgr.LoadCountsAsync(CancellationToken.None);

        var row = mgr.Watches.Single();
        Assert.Null(row.MessageCount);
        Assert.Equal("count unavailable", row.CountText);
    }

    [Fact]
    public void Manager_StopWatching_RemovesTheRowAndKeepsThePlaceInTheList()
    {
        var watch = new StubWatchService();
        watch.Watch("First");
        watch.Watch("Second");
        watch.Watch("Third");
        var mgr = MakeManager(watch);

        mgr.SelectedWatch = mgr.Watches[1];
        var survivor = mgr.Watches[2].NormalizedSubject;
        mgr.StopWatchingCommand.Execute(null);

        Assert.Equal(2, mgr.Watches.Count);
        // Selection lands on what took the removed row's place, not back at the top.
        Assert.Equal(survivor, mgr.SelectedWatch!.NormalizedSubject);
    }

    [Fact]
    public void Manager_StopWatching_OnTheLastRow_LandsOnTheNewLastRow()
    {
        var watch = new StubWatchService();
        watch.Watch("First");
        watch.Watch("Second");
        var mgr = MakeManager(watch);

        mgr.SelectedWatch = mgr.Watches[^1];
        mgr.StopWatchingCommand.Execute(null);

        Assert.Single(mgr.Watches);
        Assert.Same(mgr.Watches[0], mgr.SelectedWatch);
    }

    [Fact]
    public void Manager_Rename_UpdatesTheRow_AndRefusesAnEmptyLabel()
    {
        var watch = new StubWatchService();
        watch.Watch("Budget Review");
        var mgr = MakeManager(watch);
        var announcements = new List<string>();
        mgr.AnnouncementRequested += (text, _) => announcements.Add(text);

        mgr.SelectedWatch = mgr.Watches[0];
        mgr.BeginRenameCommand.Execute(null);
        Assert.True(mgr.IsRenaming);
        Assert.Equal("Budget Review", mgr.EditLabel);

        mgr.EditLabel = "   ";
        mgr.SaveRenameCommand.Execute(null);
        Assert.True(mgr.IsRenaming);                       // still editing
        Assert.Equal("A label cannot be empty.", mgr.RenameError);
        Assert.Contains(announcements, a => a.Contains("cannot be empty", StringComparison.OrdinalIgnoreCase));

        mgr.EditLabel = "Q3 budget thread";
        mgr.SaveRenameCommand.Execute(null);

        Assert.False(mgr.IsRenaming);
        Assert.Equal("Q3 budget thread", mgr.Watches[0].Label);
        Assert.True(watch.IsWatched("Budget Review"));     // matching unchanged
    }

    [Fact]
    public async Task Manager_GoTo_RefusesAConversationWithNothingCached()
    {
        var watch = new StubWatchService();
        watch.Watch("Nothing here");
        var mgr = MakeManager(watch, [Msg("1", "Something else")]);
        await mgr.LoadCountsAsync(CancellationToken.None);

        var announcements = new List<string>();
        var navigated = false;
        mgr.AnnouncementRequested += (text, _) => announcements.Add(text);
        mgr.GoToRequested += _ => navigated = true;

        mgr.SelectedWatch = mgr.Watches[0];
        mgr.GoToCommand.Execute(null);

        Assert.False(navigated);
        Assert.Contains(announcements, a => a.Contains("no cached messages", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Manager_GoTo_RaisesTheConversationKey()
    {
        var watch = new StubWatchService();
        watch.Watch("Budget Review");
        var mgr = MakeManager(watch, [Msg("1", "Re: Budget Review")]);
        await mgr.LoadCountsAsync(CancellationToken.None);

        string? raised = null;
        mgr.GoToRequested += s => raised = s;
        mgr.SelectedWatch = mgr.Watches[0];
        mgr.GoToCommand.Execute(null);

        Assert.Equal("Budget Review", raised);
    }

    [Fact]
    public void Manager_RowDisplayText_IsWhatAScreenReaderWillRead()
    {
        // A Selector item's accessible name comes from ToString(), not the template.
        var watch = new StubWatchService();
        watch.Watch("Budget Review");
        var mgr = MakeManager(watch);
        var row = mgr.Watches[0];
        row.MessageCount = 1;

        Assert.Equal(row.DisplayText, row.ToString());
        Assert.StartsWith("Budget Review, 1 message, watched ", row.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("QuickMail.", row.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("{", row.ToString(), StringComparison.Ordinal);
    }

    // ── Notifications: the ordering contract ─────────────────────────────────
    //
    // MaybeNotifyWatchedMail and MaybeNotifyNewMail share _notifiedMessageKeys, and
    // NewMailFilter.SelectNew *consumes* whatever it is shown. Two things therefore have to hold,
    // and neither is visible from any public surface:
    //   * watched runs first, so a watched inbox message gets the watched toast, not the generic one
    //   * watched is handed only the watched subset, so it does not claim ordinary inbox mail and
    //     silently suppress the new-mail toast

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<(string Kind, List<string> Subjects)> Toasts { get; } = [];

        public bool IsSupported => true;

#pragma warning disable CS0067 // never raised by this fake
        public event Action<NotificationActivation>? Activated;
#pragma warning restore CS0067

        public void ShowNewMail(string accountLabel, Guid accountId, IReadOnlyList<MailMessageSummary> newMessages)
            => Toasts.Add(("new", newMessages.Select(m => m.Subject).ToList()));

        public void ShowWatchedMail(string accountLabel, Guid accountId, IReadOnlyList<MailMessageSummary> newMessages)
            => Toasts.Add(("watched", newMessages.Select(m => m.Subject).ToList()));

        public void ShowInfo(string title, string message) { }
    }

    private static (MainViewModel Vm, StubWatchService Watch, RecordingNotificationService Toasts, AccountModel Account)
        MakeNotifyVm(bool notifyNewMail = true, bool notifyWatched = true)
    {
        var watch  = new StubWatchService();
        var toasts = new RecordingNotificationService();
        var config = new StubConfigService();
        var cfg = config.Load();
        cfg.NotifyOnNewMail             = notifyNewMail;
        cfg.NotifyOnWatchedConversation = notifyWatched;
        config.Save(cfg);

        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(), config,
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(),
            new StubSmtpService(), notificationService: toasts, watchService: watch);

        var account = new AccountModel
        {
            Id = AccountA, AccountName = "Work", Username = "work@example.com",
            AuthType = AuthType.OAuth2Microsoft,
        };
        return (vm, watch, toasts, account);
    }

    [Fact]
    public void WatchedMessageInTheInbox_ProducesExactlyOneToast_AndItIsTheWatchedOne()
    {
        var (vm, watch, toasts, account) = MakeNotifyVm();
        watch.Watch("Budget Review");
        var incoming = new[] { Msg("1", "Re: Budget Review") };

        // Exactly the call order the sync paths use.
        vm.MaybeNotifyWatchedMail(account, incoming);
        vm.MaybeNotifyNewMail(account, incoming);

        var toast = Assert.Single(toasts.Toasts);
        Assert.Equal("watched", toast.Kind);
        Assert.Equal(["Re: Budget Review"], toast.Subjects);
    }

    [Fact]
    public void OrdinaryInboxMail_StillToastsNormally_AfterTheWatchedPathHasRun()
    {
        // The consumption trap: if the watched path were handed the whole incoming list it would
        // claim these messages too and this toast would silently never appear.
        var (vm, watch, toasts, account) = MakeNotifyVm();
        watch.Watch("Budget Review");
        var incoming = new[] { Msg("1", "Re: Budget Review"), Msg("2", "Something unrelated") };

        vm.MaybeNotifyWatchedMail(account, incoming);
        vm.MaybeNotifyNewMail(account, incoming);

        Assert.Equal(2, toasts.Toasts.Count);
        Assert.Equal("watched", toasts.Toasts[0].Kind);
        Assert.Equal(["Re: Budget Review"], toasts.Toasts[0].Subjects);
        Assert.Equal("new", toasts.Toasts[1].Kind);
        Assert.Equal(["Something unrelated"], toasts.Toasts[1].Subjects);
    }

    [Fact]
    public void WatchedMailOutsideTheInbox_StillToasts()
    {
        // The reason this is a separate path at all: MaybeNotifyNewMail is inbox-only by design,
        // but a watched thread's next reply can land in any folder.
        var (vm, watch, toasts, account) = MakeNotifyVm();
        watch.Watch("Budget Review");
        var inArchive = new MailMessageSummary
        {
            MessageId = "9", AccountId = AccountA, FolderName = "Archive",
            From = "someone@example.com", Subject = "Re: Budget Review", Date = DateTimeOffset.Now,
        };

        // A non-inbox folder: the sweep calls only the watched path here.
        vm.MaybeNotifyWatchedMail(account, [inArchive]);

        var toast = Assert.Single(toasts.Toasts);
        Assert.Equal("watched", toast.Kind);
    }

    [Fact]
    public void WatchedNotifications_RespectTheirOwnSetting()
    {
        var (vm, watch, toasts, account) = MakeNotifyVm(notifyNewMail: false, notifyWatched: false);
        watch.Watch("Budget Review");

        vm.MaybeNotifyWatchedMail(account, [Msg("1", "Re: Budget Review")]);

        Assert.Empty(toasts.Toasts);
    }

    [Fact]
    public void AlreadyReadMail_DoesNotToast()
    {
        // NewMailFilter's contract, re-asserted through the watched path.
        var (vm, watch, toasts, account) = MakeNotifyVm();
        watch.Watch("Budget Review");

        vm.MaybeNotifyWatchedMail(account, [Msg("1", "Re: Budget Review", read: true)]);

        Assert.Empty(toasts.Toasts);
    }

    // ── Config round trip ────────────────────────────────────────────────────

    [Fact]
    public void NotifyOnWatchedConversation_SurvivesASaveAndReload()
    {
        // A setting is not wired up until it survives this round trip.
        var profile = new ProfileContext(Path.Combine(Path.GetTempPath(), $"QM-CFG-{Guid.NewGuid():N}"));
        var service = new ConfigService(profile);

        var config = service.Load();
        Assert.False(config.NotifyOnWatchedConversation);   // default off
        config.NotifyOnWatchedConversation = true;
        service.Save(config);

        Assert.True(new ConfigService(profile).Load().NotifyOnWatchedConversation);
    }
}
