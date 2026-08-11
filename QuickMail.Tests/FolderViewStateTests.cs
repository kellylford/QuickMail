// Per-folder view state (#520).
//
// Coverage:
//  A. ListState            -- record semantics the whole design leans on.
//  B. FolderViewStateService -- round-trip, missing/corrupt file, key isolation.
//  C. MainViewModel        -- the three-layer resolver, detach-to-custom, and the
//     per-field global-default scoping that keeps a view's grouping out of config.ini.
//
// String literal note (same trap as SavedViewsTests): "\x00AllMail" parses as \x00A + "llMail".
// Always build sentinels as "\x00" + suffix.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

// ---------------------------------------------------------------------------
// Part A: ListState
// ---------------------------------------------------------------------------

public class ListStateTests
{
    [Fact]
    public void Default_IsFlatUnfilteredNewestFirst()
    {
        var s = ListState.Default;

        Assert.Equal(ViewMode.Messages, s.Mode);
        Assert.Equal(MessageFilter.All, s.Filter);
        Assert.Null(s.FlagFilterId);
        Assert.Equal(MessageSort.DateDescending, s.Sort);
        Assert.Null(s.DayLimit);
    }

    [Fact]
    public void Equality_IsByValue()
    {
        var a = new ListState(ViewMode.Conversations, MessageFilter.Unread, null, MessageSort.DateAscending, 7);
        var b = new ListState(ViewMode.Conversations, MessageFilter.Unread, null, MessageSort.DateAscending, 7);

        Assert.Equal(a, b);
    }

    [Fact]
    public void With_ProducesADistinctValue_LeavingTheOriginalAlone()
    {
        var a = ListState.Default;
        var b = a with { Mode = ViewMode.From };

        Assert.Equal(ViewMode.Messages, a.Mode);
        Assert.Equal(ViewMode.From, b.Mode);
        Assert.NotEqual(a, b);
    }
}

// ---------------------------------------------------------------------------
// Part B: FolderViewStateService
// ---------------------------------------------------------------------------

public class FolderViewStateServiceTests : IDisposable
{
    private readonly string _dir;

    public FolderViewStateServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "qm-folderviews-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private FolderViewStateService MakeService() => new(new ProfileContext(_dir));

    private string StateFile => Path.Combine(_dir, "folderviews.json");

    private static readonly ListState Sample =
        new(ViewMode.Conversations, MessageFilter.Unread, "flag-id", MessageSort.AlphaAscending, 30);

    [Fact]
    public void Recall_UnknownFolder_ReturnsNull()
        => Assert.Null(MakeService().Recall(Guid.NewGuid(), "INBOX"));

    [Fact]
    public void Remember_ThenRecall_RoundTripsEveryField()
    {
        var account = Guid.NewGuid();
        var svc = MakeService();

        svc.Remember(account, "INBOX", Sample);
        var back = svc.Recall(account, "INBOX");

        Assert.NotNull(back);
        Assert.Equal(Sample, back!.Value);
    }

    [Fact]
    public void Remember_SurvivesANewServiceInstance()
    {
        var account = Guid.NewGuid();
        MakeService().Remember(account, "INBOX", Sample);

        // A second instance reads from disk rather than the first instance's cache.
        Assert.Equal(Sample, MakeService().Recall(account, "INBOX")!.Value);
    }

    [Fact]
    public void Remember_Twice_Overwrites()
    {
        var account = Guid.NewGuid();
        var svc = MakeService();

        svc.Remember(account, "INBOX", Sample);
        svc.Remember(account, "INBOX", ListState.Default);

        Assert.Equal(ListState.Default, svc.Recall(account, "INBOX")!.Value);
    }

    [Fact]
    public void Forget_RemovesTheEntry()
    {
        var account = Guid.NewGuid();
        var svc = MakeService();
        svc.Remember(account, "INBOX", Sample);

        svc.Forget(account, "INBOX");

        Assert.Null(svc.Recall(account, "INBOX"));
        Assert.Null(MakeService().Recall(account, "INBOX"));   // and on disk
    }

    [Fact]
    public void SameFolderName_UnderDifferentAccounts_DoesNotCollide()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var svc = MakeService();

        svc.Remember(a, "Archive", Sample);
        svc.Remember(b, "Archive", ListState.Default);

        Assert.Equal(Sample, svc.Recall(a, "Archive")!.Value);
        Assert.Equal(ListState.Default, svc.Recall(b, "Archive")!.Value);
    }

    [Fact]
    public void VirtualFolderSentinel_DoesNotCollideWithARealFolderOfTheSameName()
    {
        // Virtual folders carry Guid.Empty; the account id is why this cannot collide.
        var real = Guid.NewGuid();
        var sentinel = "\x00" + "AllMail";
        var svc = MakeService();

        svc.Remember(Guid.Empty, sentinel, Sample);
        svc.Remember(real, sentinel, ListState.Default);

        Assert.Equal(Sample, svc.Recall(Guid.Empty, sentinel)!.Value);
        Assert.Equal(ListState.Default, svc.Recall(real, sentinel)!.Value);
    }

    [Fact]
    public void ViewSentinelKey_IsStoredLikeAnyOtherFolder()
    {
        var sentinel = "\x00" + "View:" + Guid.NewGuid();
        var svc = MakeService();

        svc.Remember(Guid.Empty, sentinel, Sample);

        Assert.Equal(Sample, MakeService().Recall(Guid.Empty, sentinel)!.Value);
    }

    [Fact]
    public void CorruptFile_IsTreatedAsEmpty_RatherThanThrowing()
    {
        File.WriteAllText(StateFile, "{ this is not json");

        var svc = MakeService();

        Assert.Null(svc.Recall(Guid.NewGuid(), "INBOX"));
        // And it recovers: a write over the corrupt file succeeds.
        var account = Guid.NewGuid();
        svc.Remember(account, "INBOX", Sample);
        Assert.Equal(Sample, MakeService().Recall(account, "INBOX")!.Value);
    }

    [Fact]
    public void EmptyFolderName_IsIgnoredRatherThanStored()
    {
        var svc = MakeService();

        svc.Remember(Guid.NewGuid(), "", Sample);

        Assert.False(File.Exists(StateFile));
    }

    [Fact]
    public void StoredValues_AreStrings_NotEnumOrdinals()
    {
        // Reordering ViewMode/MessageSort must not silently repoint existing entries.
        MakeService().Remember(Guid.NewGuid(), "INBOX", Sample);

        var json = File.ReadAllText(StateFile);

        Assert.Contains("conversations", json, StringComparison.Ordinal);
        Assert.Contains("unread", json, StringComparison.Ordinal);
        Assert.Contains("alphaAsc", json, StringComparison.Ordinal);
    }
}

// ---------------------------------------------------------------------------
// Part C: MainViewModel resolver, detach, and default scoping
// ---------------------------------------------------------------------------

/// <summary>Minimal account service for the startup-path tests, which resolve a startup folder
/// scoped by account id.</summary>
sealed class StartupAccountsStub(Guid accountId) : IAccountService
{
    public List<AccountModel> LoadAccounts() =>
        [new AccountModel { Id = accountId, AccountName = "Test", Username = "test@example.com" }];
    public void SaveAccounts(List<AccountModel> accounts) { }
    public void SetDefaultAccount(Guid id) { }
}

public class PerFolderViewStateTests
{
    private static readonly Guid AccountA = Guid.NewGuid();

    private static MainViewModel MakeVm(
        StubFolderViewStateService store,
        StubConfigService config,
        IEnumerable<SavedView>? views = null)
        => new(new StubImapMailService(),
               new StubAccountService(),
               new StubCredentialService(),
               new StubLocalStoreService(),
               new StubOAuthService(),
               new StubSyncService(),
               config,
               new StubCommandRegistry(),
               new FakeViewService(views),
               new StubRuleService(),
               new StubSmtpService(),
               folderViewState: store);

    private static MailFolderModel Folder(string fullName) =>
        new() { AccountId = AccountA, FullName = fullName, DisplayName = fullName };

    private static async Task SelectFolder(MainViewModel vm, MailFolderModel folder)
        => await ((IAsyncRelayCommand)vm.SelectFolderCommand).ExecuteAsync(folder);

    private static async Task SelectView(MainViewModel vm, SavedView view)
        => await ((IAsyncRelayCommand)vm.SelectViewCommand).ExecuteAsync(view.Id.ToString());

    private static async Task ClearView(MainViewModel vm)
        => await ((IAsyncRelayCommand)vm.ClearViewCommand).ExecuteAsync(null);

    private static SavedView ConversationsView() => new()
    {
        Id               = Guid.NewGuid(),
        Name             = "Grouped",
        VirtualFolderKey = "AllInboxes",
        ViewMode         = "conversations",
        Filter           = "unread",
        Sort             = "dateAsc",
        DaysOfMail       = 7,
    };

    /// <summary>A view over one real folder, so applying it stays on INBOX rather than moving to a
    /// virtual folder — which is what the detach tests need in order to assert against INBOX.</summary>
    private static SavedView InboxConversationsView() => new()
    {
        Id         = Guid.NewGuid(),
        Name       = "Inbox unread",
        ViewMode   = "conversations",
        Filter     = "unread",
        Sort       = "dateAsc",
        DaysOfMail = 7,
        Folders    = [new ViewFolder { AccountId = AccountA, FolderFullName = "INBOX" }],
    };

    /// <summary>Populates Folders so ApplyViewFoldersAsync resolves a single-folder view to the
    /// real folder instead of falling back to a multi-folder sentinel.</summary>
    private static void SeedFolders(MainViewModel vm, params string[] names)
        => vm.Folders = new System.Collections.ObjectModel.ObservableCollection<MailFolderModel>(
               names.Select(Folder));

    // -- The reported scenario ----------------------------------------------

    [Fact]
    public async Task TwoFolders_KeepTwoDifferentGroupings()
    {
        var store = new StubFolderViewStateService();
        var vm = MakeVm(store, new StubConfigService());

        await SelectFolder(vm, Folder("INBOX"));
        vm.ViewMode = ViewMode.Conversations;

        await SelectFolder(vm, Folder("Receipts"));
        vm.ViewMode = ViewMode.Messages;

        await SelectFolder(vm, Folder("INBOX"));
        Assert.Equal(ViewMode.Conversations, vm.ViewMode);

        await SelectFolder(vm, Folder("Receipts"));
        Assert.Equal(ViewMode.Messages, vm.ViewMode);
    }

    [Fact]
    public async Task UncustomisedFolder_InheritsTheGlobalDefault()
    {
        var store = new StubFolderViewStateService();
        var vm = MakeVm(store, new StubConfigService());

        await SelectFolder(vm, Folder("INBOX"));
        vm.ViewMode = ViewMode.From;          // moves the global default

        await SelectFolder(vm, Folder("NeverVisited"));

        Assert.Equal(ViewMode.From, vm.ViewMode);
    }

    [Fact]
    public async Task CustomisedFolder_IgnoresLaterGlobalDefaultChanges()
    {
        var store = new StubFolderViewStateService();
        var vm = MakeVm(store, new StubConfigService());

        // From, not Messages: assigning the value a folder already shows is not a change, so it
        // fires no handler and pins nothing. A folder is customised by changing it, not by visiting.
        await SelectFolder(vm, Folder("Receipts"));
        vm.ViewMode = ViewMode.From;          // Receipts is now pinned

        await SelectFolder(vm, Folder("Other"));
        vm.ViewMode = ViewMode.Conversations; // global default moves

        await SelectFolder(vm, Folder("Receipts"));

        Assert.Equal(ViewMode.From, vm.ViewMode);
    }

    [Fact]
    public async Task VisitingAFolderWithoutChangingAnything_DoesNotPinIt()
    {
        var store = new StubFolderViewStateService();
        var vm = MakeVm(store, new StubConfigService());

        await SelectFolder(vm, Folder("Receipts"));   // shows the default; user changes nothing

        Assert.Equal(0, store.Writes);
        Assert.Null(store.Recall(AccountA, "Receipts"));
    }

    [Fact]
    public async Task SettingOff_SkipsTheFolderLayer()
    {
        var config = new StubConfigService();
        var cfg = config.Load();
        cfg.RememberViewPerFolder = false;
        config.Save(cfg);

        var store = new StubFolderViewStateService();
        var vm = MakeVm(store, config);

        await SelectFolder(vm, Folder("INBOX"));
        vm.ViewMode = ViewMode.Conversations;

        await SelectFolder(vm, Folder("Receipts"));

        // Nothing was recorded, and the grouping stayed put — the pre-#520 behaviour.
        Assert.Equal(0, store.Writes);
        Assert.Equal(ViewMode.Conversations, vm.ViewMode);
    }

    // -- Startup ------------------------------------------------------------

    [Fact]
    public async Task StartupFolder_OpensWithItsRememberedPresentation()
    {
        // The startup paths assign SelectedFolder directly rather than going through
        // SelectFolderAsync, so they need the resolver applied explicitly. Without that the one
        // folder the user opens into is the one folder that ignores its own settings — and the
        // failure is invisible in every test that navigates rather than starts up.
        var localStore = new StubLocalStoreService();
        localStore.SeededFolders[AccountA] =
        [
            new MailFolderModel { AccountId = AccountA, FullName = "INBOX", DisplayName = "Inbox",
                                  Kind = SpecialFolderKind.Inbox },
            new MailFolderModel { AccountId = AccountA, FullName = "Receipts", DisplayName = "Receipts" },
        ];

        var config = new StubConfigService();
        var cfg = config.Load();
        cfg.StartupFolder        = "Receipts";
        cfg.StartupFolderAccount = AccountA.ToString();
        cfg.StartupFolderLabel   = "Receipts";
        config.Save(cfg);

        // Receipts was left grouped by sender.
        var store = new StubFolderViewStateService();
        store.Remember(AccountA, "Receipts", ListState.Default with { Mode = ViewMode.From });

        var vm = new MainViewModel(
            new StubImapMailService(), new StartupAccountsStub(AccountA), new StubCredentialService(),
            localStore, new StubOAuthService(), new StubSyncService(), config,
            new StubCommandRegistry(), new FakeViewService(), new StubRuleService(),
            new StubSmtpService(), folderViewState: store);
        vm.LoadAccountList();

        await vm.InitialLoadAsync();

        Assert.Equal("Receipts", vm.SelectedFolder?.FullName);
        Assert.Equal(ViewMode.From, vm.ViewMode);
    }

    [Fact]
    public async Task StartupFolder_WithNoRememberedState_UsesTheGlobalDefault()
    {
        var localStore = new StubLocalStoreService();
        localStore.SeededFolders[AccountA] =
        [
            new MailFolderModel { AccountId = AccountA, FullName = "INBOX", DisplayName = "Inbox",
                                  Kind = SpecialFolderKind.Inbox },
        ];

        var config = new StubConfigService();
        var cfg = config.Load();
        cfg.StartupFolder        = "INBOX";
        cfg.StartupFolderAccount = AccountA.ToString();
        cfg.ViewMode             = "conversations";
        config.Save(cfg);

        var vm = new MainViewModel(
            new StubImapMailService(), new StartupAccountsStub(AccountA), new StubCredentialService(),
            localStore, new StubOAuthService(), new StubSyncService(), config,
            new StubCommandRegistry(), new FakeViewService(), new StubRuleService(),
            new StubSmtpService(), folderViewState: new StubFolderViewStateService());
        vm.LoadAccountList();

        await vm.InitialLoadAsync();

        Assert.Equal("INBOX", vm.SelectedFolder?.FullName);
        Assert.Equal(ViewMode.Conversations, vm.ViewMode);
    }

    // -- Apply / clear symmetry ---------------------------------------------

    [Fact]
    public async Task ClearView_RestoresEveryListStateField()
    {
        var view = ConversationsView();
        var store = new StubFolderViewStateService();
        var vm = MakeVm(store, new StubConfigService(), [view]);

        await SelectFolder(vm, Folder("INBOX"));
        vm.ViewMode = ViewMode.From;
        vm.ActiveSort = MessageSort.AlphaDescending;

        await SelectView(vm, view);
        Assert.Equal(ViewMode.Conversations, vm.ViewMode);
        Assert.Equal(MessageFilter.Unread, vm.ActiveFilter);
        Assert.Equal(MessageSort.DateAscending, vm.ActiveSort);
        Assert.Equal(7, vm.ActiveDayLimit);

        await ClearView(vm);

        // All five, not the two ClearView used to reset.
        Assert.Equal(ViewMode.From, vm.ViewMode);
        Assert.Equal(MessageFilter.All, vm.ActiveFilter);
        Assert.Null(vm.ActiveFlagFilterId);
        Assert.Equal(MessageSort.AlphaDescending, vm.ActiveSort);
        Assert.Null(vm.ActiveDayLimit);
    }

    [Fact]
    public async Task ApplyView_WritesNeitherConfigNorFolderMemory()
    {
        var view = ConversationsView();
        var config = new StubConfigService();
        var store = new StubFolderViewStateService();
        var vm = MakeVm(store, config, [view]);

        await SelectFolder(vm, Folder("INBOX"));
        var writesBefore = store.Writes;
        var modeBefore = config.Load().ViewMode;
        var sortBefore = config.Load().Sort;

        await SelectView(vm, view);

        Assert.Equal(writesBefore, store.Writes);
        Assert.Equal(modeBefore, config.Load().ViewMode);
        Assert.Equal(sortBefore, config.Load().Sort);
    }

    [Fact]
    public async Task ChangingGroupingWithViewActive_DetachesFromTheView()
    {
        var view = ConversationsView();
        var vm = MakeVm(new StubFolderViewStateService(), new StubConfigService(), [view]);

        await SelectView(vm, view);
        Assert.NotNull(vm.ActiveView);

        vm.ViewMode = ViewMode.To;

        Assert.Null(vm.ActiveView);
    }

    // -- Detaching keeps only what the user chose ---------------------------

    [Fact]
    public async Task DetachingByChangingSort_DoesNotInheritTheViewsFilterOrDayLimit()
    {
        // The trap: carrying the whole on-screen state into the folder's memory wrote the VIEW's
        // filter and 7-day limit as this folder's own setting. Clear View then resolved to that
        // same record and changed nothing, and there is no UI control for the day limit at all —
        // so the user was pinned to a filtered, date-limited folder with no way out but Reset.
        var view = InboxConversationsView();     // unread, 7 days, conversations, dateAsc
        var store = new StubFolderViewStateService();
        var vm = MakeVm(store, new StubConfigService(), [view]);
        SeedFolders(vm, "INBOX");

        await SelectFolder(vm, Folder("INBOX"));
        await SelectView(vm, view);
        Assert.Equal("INBOX", vm.SelectedFolder?.FullName);
        Assert.Equal(MessageFilter.Unread, vm.ActiveFilter);
        Assert.Equal(7, vm.ActiveDayLimit);

        vm.ActiveSort = MessageSort.AlphaDescending;   // user touches sort only

        Assert.Null(vm.ActiveView);
        Assert.Equal(MessageSort.AlphaDescending, vm.ActiveSort);   // kept — the user chose it
        Assert.Equal(MessageFilter.All, vm.ActiveFilter);           // dropped — the view's, not theirs
        Assert.Null(vm.ActiveDayLimit);
        Assert.Equal(ViewMode.Messages, vm.ViewMode);

        var stored = store.Recall(AccountA, "INBOX");
        Assert.NotNull(stored);
        Assert.Equal(MessageFilter.All, stored!.Value.Filter);
        Assert.Null(stored.Value.DayLimit);
    }

    [Fact]
    public async Task DetachingByChangingGrouping_KeepsTheGroupingAndNothingElse()
    {
        var view = InboxConversationsView();
        var vm = MakeVm(new StubFolderViewStateService(), new StubConfigService(), [view]);
        SeedFolders(vm, "INBOX");

        await SelectFolder(vm, Folder("INBOX"));
        await SelectView(vm, view);

        vm.ViewMode = ViewMode.To;

        Assert.Null(vm.ActiveView);
        Assert.Equal(ViewMode.To, vm.ViewMode);
        Assert.Equal(MessageFilter.All, vm.ActiveFilter);
        Assert.Null(vm.ActiveDayLimit);
        Assert.Equal(MessageSort.DateDescending, vm.ActiveSort);    // the view's dateAsc is dropped
    }

    [Fact]
    public async Task DetachingRestoresTheFoldersOwnState_NotTheGlobalDefault()
    {
        var view = InboxConversationsView();
        var store = new StubFolderViewStateService();
        var config = new StubConfigService();
        var vm = MakeVm(store, config, [view]);
        SeedFolders(vm, "INBOX", "Other");

        // INBOX is remembered as From; the GLOBAL default is then moved to To elsewhere, so a
        // fallback to the default rather than to the folder would be visible as To.
        await SelectFolder(vm, Folder("INBOX"));
        vm.ViewMode = ViewMode.From;
        await SelectFolder(vm, Folder("Other"));
        vm.ViewMode = ViewMode.To;
        Assert.Equal("to", config.Load().ViewMode);

        await SelectFolder(vm, Folder("INBOX"));
        await SelectView(vm, view);
        vm.ActiveSort = MessageSort.AlphaAscending;

        // Detach falls back to INBOX's own grouping, not to the global default.
        Assert.Equal(ViewMode.From, vm.ViewMode);
        Assert.Equal(MessageSort.AlphaAscending, vm.ActiveSort);
    }

    // -- Clear View on a multi-folder view ----------------------------------

    [Fact]
    public async Task ClearView_OnAMultiFolderView_NavigatesToAllMail()
    {
        // A multi-folder view leaves SelectedFolder on a \0View:{id} sentinel whose message set
        // IS the view, so restoring presentation alone would change only the window title.
        var view = new SavedView
        {
            Id       = Guid.NewGuid(),
            Name     = "Two folders",
            ViewMode = "conversations",
            Folders  =
            [
                new ViewFolder { AccountId = AccountA, FolderFullName = "INBOX" },
                new ViewFolder { AccountId = AccountA, FolderFullName = "Receipts" },
            ],
        };
        var vm = MakeVm(new StubFolderViewStateService(), new StubConfigService(), [view]);

        await SelectView(vm, view);
        Assert.StartsWith("\x00" + "View:", vm.SelectedFolder!.FullName, StringComparison.Ordinal);

        await ClearView(vm);

        Assert.Equal(MainViewModel.AllMailFolder.FullName, vm.SelectedFolder?.FullName);
        Assert.Null(vm.ActiveView);
    }

    [Fact]
    public async Task AMultiFolderViewsSentinel_IsNeverStored()
    {
        // Nothing would ever read it back: SelectFolderAsync intercepts view sentinels before the
        // resolver runs. Storing one would only add rows to a file nothing prunes.
        var view = new SavedView
        {
            Id       = Guid.NewGuid(),
            Name     = "Two folders",
            ViewMode = "messages",
            Folders  =
            [
                new ViewFolder { AccountId = AccountA, FolderFullName = "INBOX" },
                new ViewFolder { AccountId = AccountA, FolderFullName = "Receipts" },
            ],
        };
        var store = new StubFolderViewStateService();
        var vm = MakeVm(store, new StubConfigService(), [view]);

        await SelectView(vm, view);
        vm.ActiveSort = MessageSort.AlphaAscending;   // detaches, then tries to remember

        Assert.Equal(0, store.Writes);
    }

    // -- A remembered flag filter whose flag was deleted ---------------------

    [Fact]
    public async Task ARememberedFlagFilter_IsDroppedWhenTheFlagIsGone()
    {
        // Otherwise the folder opens empty for ever, with nothing on screen to say why — the
        // silent-empty-state failure CLAUDE.md's feature checklist calls out. ApplyViewStateAsync
        // has always degraded this way for saved views; the per-folder path must match.
        var store = new StubFolderViewStateService();
        var vm = MakeVm(store, new StubConfigService());

        var live = Guid.NewGuid();
        vm.FlagDefinitions.Add(new FlagDefinition { Id = live, Name = "Live" });

        store.Remember(AccountA, "INBOX",
                       ListState.Default with { Filter = MessageFilter.Flagged,
                                                FlagFilterId = Guid.NewGuid().ToString() });

        await SelectFolder(vm, Folder("INBOX"));

        Assert.Equal(MessageFilter.Flagged, vm.ActiveFilter);   // still flagged…
        Assert.Null(vm.ActiveFlagFilterId);                     // …but not one deleted flag
    }

    [Fact]
    public async Task ARememberedFlagFilter_SurvivesWhenTheFlagStillExists()
    {
        var store = new StubFolderViewStateService();
        var vm = MakeVm(store, new StubConfigService());

        var live = Guid.NewGuid();
        vm.FlagDefinitions.Add(new FlagDefinition { Id = live, Name = "Waiting" });
        store.Remember(AccountA, "INBOX",
                       ListState.Default with { Filter = MessageFilter.Flagged,
                                                FlagFilterId = live.ToString() });

        await SelectFolder(vm, Folder("INBOX"));

        Assert.Equal(live.ToString(), vm.ActiveFlagFilterId);
    }

    // -- Toggling the setting at runtime ------------------------------------

    [Fact]
    public async Task TurningTheSettingOffAndOnAgain_PreservesStoredState()
    {
        var store = new StubFolderViewStateService();
        var config = new StubConfigService();
        var vm = MakeVm(store, config);

        await SelectFolder(vm, Folder("Receipts"));
        vm.ViewMode = ViewMode.From;

        await SelectFolder(vm, Folder("Other"));
        vm.ViewMode = ViewMode.Conversations;

        // Off: Receipts stops getting its own grouping…
        var cfg = config.Load();
        cfg.RememberViewPerFolder = false;
        config.Save(cfg);
        vm.ApplySettings(cfg);

        await SelectFolder(vm, Folder("Receipts"));
        Assert.Equal(ViewMode.Conversations, vm.ViewMode);

        // …and on again, it comes back. The file is never cleared by the toggle.
        cfg = config.Load();
        cfg.RememberViewPerFolder = true;
        config.Save(cfg);
        vm.ApplySettings(cfg);

        await SelectFolder(vm, Folder("Other"));
        await SelectFolder(vm, Folder("Receipts"));
        Assert.Equal(ViewMode.From, vm.ViewMode);
    }

    // -- Per-field global-default scoping (the subtle form of #520) ----------

    [Fact]
    public async Task ChangingSort_DoesNotMoveTheGlobalViewMode()
    {
        var config = new StubConfigService();
        var vm = MakeVm(new StubFolderViewStateService(), config);

        await SelectFolder(vm, Folder("INBOX"));
        var modeBefore = config.Load().ViewMode;

        vm.ActiveSort = MessageSort.AlphaAscending;

        Assert.Equal(modeBefore, config.Load().ViewMode);
        Assert.Equal("alphaAsc", config.Load().Sort);
    }

    [Fact]
    public async Task ChangingGrouping_DoesNotMoveTheGlobalSort()
    {
        var config = new StubConfigService();
        var vm = MakeVm(new StubFolderViewStateService(), config);

        await SelectFolder(vm, Folder("INBOX"));
        var sortBefore = config.Load().Sort;

        vm.ViewMode = ViewMode.Conversations;

        Assert.Equal(sortBefore, config.Load().Sort);
        Assert.Equal("conversations", config.Load().ViewMode);
    }

    [Fact]
    public async Task ChangingSortWhileAConversationsViewIsActive_LeavesTheGlobalGroupingAlone()
    {
        // The failure this whole design exists to prevent: a grouping the user never chose
        // must not become the default for every folder that has not been customised.
        var view = ConversationsView();
        var config = new StubConfigService();
        var vm = MakeVm(new StubFolderViewStateService(), config, [view]);

        await SelectFolder(vm, Folder("INBOX"));
        var modeBefore = config.Load().ViewMode;          // "messages"

        await SelectView(vm, view);                       // view forces conversations
        vm.ActiveSort = MessageSort.AlphaDescending;      // user touches sort only

        Assert.Equal(modeBefore, config.Load().ViewMode);
        Assert.Null(vm.ActiveView);                       // and it detached
    }

    // -- Reset -------------------------------------------------------------

    [Fact]
    public async Task ResetFolderView_ForgetsTheEntryAndReturnsToTheDefault()
    {
        var store = new StubFolderViewStateService();
        var config = new StubConfigService();
        var vm = MakeVm(store, config);

        await SelectFolder(vm, Folder("Receipts"));
        vm.ViewMode = ViewMode.From;                      // Receipts pinned to From

        // Customising another folder afterwards is what moves the global default (Decision 3),
        // so "the default" at reset time is Conversations, not whatever Receipts was set to.
        await SelectFolder(vm, Folder("Other"));
        vm.ViewMode = ViewMode.Conversations;

        await SelectFolder(vm, Folder("Receipts"));
        Assert.Equal(ViewMode.From, vm.ViewMode);         // still pinned

        vm.ResetFolderViewCommand.Execute(null);

        Assert.Equal(ViewMode.Conversations, vm.ViewMode);
        Assert.Null(store.Recall(AccountA, "Receipts"));

        // And it stays reset after navigating away and back — the entry is gone, not overwritten.
        await SelectFolder(vm, Folder("Other"));
        await SelectFolder(vm, Folder("Receipts"));
        Assert.Equal(ViewMode.Conversations, vm.ViewMode);
    }
}
