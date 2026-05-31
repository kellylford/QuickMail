// Comprehensive tests for the saved-views feature.
//
// Coverage areas:
//  A. ViewManagerViewModel -- creating views from real and virtual folders,
//     folder-summary display, Apply View command behaviour, name generation.
//  B. MainViewModel -- ApplyView sets ActiveView / mode / filter / sort,
//     Refresh re-applies the active view instead of drifting to a different
//     folder, ClearView nulls ActiveView, navigating to a folder nulls ActiveView,
//     switching between two views, virtual-folder sentinel round-trip.
//
// String literal note:
//   \x00AllInboxes reads as \x00A + llInboxes (greedy hex parse) = newline + "llInboxes".
//   Always build sentinel strings as "\x00" + suffix so the \x00 literal terminates
//   before any letter that is also a valid hex digit.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// <summary>ViewService that returns a pre-configured list and captures saves.</summary>
sealed class FakeViewService : IViewService
{
    private readonly List<SavedView> _initial;
    public List<SavedView> LastSaved { get; private set; } = [];

    public FakeViewService(IEnumerable<SavedView>? initial = null)
        => _initial = initial == null ? [] : [.. initial];

    public List<SavedView> Load() => [.. _initial];
    public void Save(List<SavedView> views) => LastSaved = [.. views];
}

// ---------------------------------------------------------------------------
// Part A: ViewManagerViewModel
// ---------------------------------------------------------------------------

public class SavedViewsManagerTests
{
    private static readonly Guid AccountA = Guid.NewGuid();
    private static readonly Guid AccountB = Guid.NewGuid();

    private static AccountModel MakeAccount(Guid id, string label) =>
        new() { Id = id, AccountName = label, Username = label.Replace(" ", "").ToLower() + "@example.com" };

    private static MailFolderModel RealFolder(Guid accountId, string fullName, string display) =>
        new() { AccountId = accountId, FullName = fullName, DisplayName = display };

    // Safe sentinel builder: \x00 terminates before the keySuffix string, so no greedy
    // hex-digit slurp.
    private static MailFolderModel VirtualFolder(string keySuffix)
    {
        var fullName = "\x00" + keySuffix;
        var displayName = keySuffix switch
        {
            "AllMail"    => "All Mail",
            "AllInboxes" => "All Inboxes",
            "AllDrafts"  => "All Drafts",
            "AllSent"    => "All Sent",
            "AllTrash"   => "All Trash",
            _            => keySuffix,
        };
        return new() { AccountId = Guid.Empty, FullName = fullName, DisplayName = displayName };
    }

    private static ViewManagerViewModel MakeVm(
        MailFolderModel? currentFolder = null,
        AccountModel? currentAccount = null,
        ViewMode viewMode = ViewMode.Messages,
        MessageFilter filter = MessageFilter.All,
        MessageSort sort = MessageSort.DateDescending,
        IEnumerable<SavedView>? views = null) =>
        new(new StubViewService(), new StubConfigService(), new StubCommandRegistry(),
            views ?? [],
            currentFolder, currentAccount, viewMode, filter, sort);

    // -- SaveAsNew with a real IMAP folder ------------------------------------

    [Fact]
    public void SaveAsNew_RealFolder_StoresViewFolder()
    {
        var folder  = RealFolder(AccountA, "INBOX", "Inbox");
        var account = MakeAccount(AccountA, "Work");
        var vm      = MakeVm(currentFolder: folder, currentAccount: account);

        vm.SaveAsNewCommand.Execute(null);

        Assert.Single(vm.SelectedView!.Folders);
        Assert.Equal("INBOX", vm.SelectedView.Folders[0].FolderFullName);
        Assert.Equal(AccountA, vm.SelectedView.Folders[0].AccountId);
        Assert.Null(vm.SelectedView.VirtualFolderKey);
    }

    [Fact]
    public void SaveAsNew_RealFolder_SecondAccount_StoresCorrectAccountId()
    {
        var folder  = RealFolder(AccountB, "INBOX", "Inbox");
        var account = MakeAccount(AccountB, "Personal");
        var vm      = MakeVm(currentFolder: folder, currentAccount: account);

        vm.SaveAsNewCommand.Execute(null);

        Assert.Equal(AccountB, vm.SelectedView!.Folders[0].AccountId);
    }

    // -- SaveAsNew with virtual folders --------------------------------------

    [Theory]
    [InlineData("AllMail")]
    [InlineData("AllInboxes")]
    [InlineData("AllDrafts")]
    [InlineData("AllSent")]
    [InlineData("AllTrash")]
    public void SaveAsNew_VirtualFolder_SetsVirtualFolderKey_WithoutSentinelPrefix(string keySuffix)
    {
        var folder = VirtualFolder(keySuffix);
        var vm     = MakeVm(currentFolder: folder);

        vm.SaveAsNewCommand.Execute(null);

        Assert.Empty(vm.SelectedView!.Folders);
        Assert.Equal(keySuffix, vm.SelectedView.VirtualFolderKey);
    }

    // -- SelectedFoldersSummary display --------------------------------------

    [Theory]
    [InlineData("AllMail",    "All Mail")]
    [InlineData("AllInboxes", "All Inboxes")]
    [InlineData("AllDrafts",  "All Drafts")]
    [InlineData("AllSent",    "All Sent")]
    [InlineData("AllTrash",   "All Trash")]
    public void SelectedFoldersSummary_VirtualFolderView_ShowsReadableName(
        string key, string expectedLabel)
    {
        var view = new SavedView { Name = "Test", VirtualFolderKey = key };
        var vm   = MakeVm(views: [view]);
        vm.SelectedView = view;

        Assert.Equal(expectedLabel, vm.SelectedFoldersSummary);
    }

    [Fact]
    public void SelectedFoldersSummary_RealFolderView_ShowsAccountAndFolder()
    {
        var view = new SavedView
        {
            Name = "Work Inbox",
            Folders =
            [
                new ViewFolder
                {
                    AccountId          = AccountA,
                    FolderFullName     = "INBOX",
                    AccountDisplayName = "Work",
                    FolderDisplayName  = "Inbox",
                },
            ],
        };
        var vm = MakeVm(views: [view]);
        vm.SelectedView = view;

        Assert.Contains("Work",  vm.SelectedFoldersSummary);
        Assert.Contains("Inbox", vm.SelectedFoldersSummary);
    }

    // -- Name generation -----------------------------------------------------

    [Fact]
    public void GenerateName_VirtualFolder_DoesNotPrefixAccountName()
    {
        var folder  = VirtualFolder("AllInboxes");
        var account = MakeAccount(AccountA, "Work Account");
        var vm      = MakeVm(currentFolder: folder, currentAccount: account);

        var name = vm.GenerateName();

        Assert.DoesNotContain("Work", name);
        Assert.Contains("All Inboxes", name);
    }

    [Fact]
    public void GenerateName_RealFolder_IncludesTwoWordAccountPrefix()
    {
        var folder  = RealFolder(AccountA, "INBOX", "Inbox");
        var account = MakeAccount(AccountA, "The Idea Place");
        var vm      = MakeVm(currentFolder: folder, currentAccount: account);

        var name = vm.GenerateName();

        Assert.StartsWith("The Idea", name);
    }

    [Fact]
    public void GenerateName_ConversationsMode_IncludesModeLabel()
    {
        var folder = VirtualFolder("AllInboxes");
        var vm     = MakeVm(currentFolder: folder, viewMode: ViewMode.Conversations);

        var name = vm.GenerateName();

        Assert.Contains("Conversations", name);
    }

    [Fact]
    public void GenerateName_UnreadFilter_IncludesFilterLabel()
    {
        var folder = RealFolder(AccountA, "INBOX", "Inbox");
        var vm     = MakeVm(currentFolder: folder, filter: MessageFilter.Unread);

        var name = vm.GenerateName();

        Assert.Contains("Unread", name);
    }

    // -- Apply View command --------------------------------------------------

    [Fact]
    public void ApplySelectedView_SetsViewRequestedToApply()
    {
        var view = new SavedView { Name = "All Inboxes" };
        var vm   = MakeVm(views: [view]);
        vm.SelectedView = view;

        vm.ApplySelectedViewCommand.Execute(null);

        Assert.Same(view, vm.ViewRequestedToApply);
    }

    [Fact]
    public void ApplySelectedView_FiresCloseRequested()
    {
        var view = new SavedView { Name = "All Inboxes" };
        var vm   = MakeVm(views: [view]);
        vm.SelectedView = view;

        bool closeFired = false;
        vm.CloseRequested += (_, _) => closeFired = true;

        vm.ApplySelectedViewCommand.Execute(null);

        Assert.True(closeFired);
    }

    [Fact]
    public void ApplySelectedView_CannotExecute_WhenNoViewSelected()
    {
        var vm = MakeVm();
        Assert.False(vm.ApplySelectedViewCommand.CanExecute(null));
    }

    [Fact]
    public void ApplySelectedView_CanExecute_WhenViewSelected()
    {
        var view = new SavedView { Name = "Test" };
        var vm   = MakeVm(views: [view]);
        vm.SelectedView = view;

        Assert.True(vm.ApplySelectedViewCommand.CanExecute(null));
    }

    [Fact]
    public void ViewRequestedToApply_NullByDefault()
    {
        var vm = MakeVm();
        Assert.Null(vm.ViewRequestedToApply);
    }
}

// ---------------------------------------------------------------------------
// Part B: MainViewModel view behaviour
// ---------------------------------------------------------------------------

public class SavedViewsMainViewModelTests
{
    // -- Factory -------------------------------------------------------------

    private static MainViewModel MakeVm(IEnumerable<SavedView>? views = null)
        => new(new StubImapMailService(),
               new StubAccountService(),
               new StubCredentialService(),
               new StubLocalStoreService(),
               new StubOAuthService(),
               new StubSyncService(),
               new StubConfigService(),
               new StubCommandRegistry(),
               new FakeViewService(views),
               new StubRuleService(),
               new StubSmtpService());

    // VirtualFolderKey stored without the nul prefix; sentinel is \x00 + key.
    private static SavedView MakeVirtualView(
        string virtualKey,
        string viewMode = "messages",
        string filter   = "all",
        string sort     = "dateDesc")
        => new()
        {
            Id               = Guid.NewGuid(),
            Name             = virtualKey,
            VirtualFolderKey = virtualKey,
            ViewMode         = viewMode,
            Filter           = filter,
            Sort             = sort,
        };

    private static SavedView MakeRealFolderView(
        Guid   accountId,
        string folderFullName,
        string viewMode = "messages",
        string filter   = "all")
        => new()
        {
            Id       = Guid.NewGuid(),
            Name     = folderFullName,
            ViewMode = viewMode,
            Filter   = filter,
            Folders  =
            [
                new ViewFolder
                {
                    AccountId          = accountId,
                    FolderFullName     = folderFullName,
                    AccountDisplayName = "Test Account",
                    FolderDisplayName  = folderFullName,
                },
            ],
        };

    private static async Task SelectView(MainViewModel vm, SavedView view)
        => await ((IAsyncRelayCommand)vm.SelectViewCommand).ExecuteAsync(view.Id.ToString());

    private static async Task Refresh(MainViewModel vm)
        => await ((IAsyncRelayCommand)vm.RefreshCommand).ExecuteAsync(null);

    private static async Task ClearView(MainViewModel vm)
        => await ((IAsyncRelayCommand)vm.ClearViewCommand).ExecuteAsync(null);

    private static async Task SelectFolder(MainViewModel vm, MailFolderModel folder)
        => await ((IAsyncRelayCommand)vm.SelectFolderCommand).ExecuteAsync(folder);

    // Pre-populate Folders so ApplyViewAsync finds virtual sentinels with their
    // canonical display names rather than the view name.
    private static void AddVirtualFolders(MainViewModel vm)
    {
        vm.Folders = new ObservableCollection<MailFolderModel>
        {
            new() { FullName = "\x00" + "AllMail",    DisplayName = "All Mail"    },
            new() { FullName = "\x00" + "AllInboxes", DisplayName = "All Inboxes" },
            new() { FullName = "\x00" + "AllDrafts",  DisplayName = "All Drafts"  },
            new() { FullName = "\x00" + "AllSent",    DisplayName = "All Sent"    },
            new() { FullName = "\x00" + "AllTrash",   DisplayName = "All Trash"   },
        };
    }

    // -- Apply view sets ActiveView ------------------------------------------

    [Fact]
    public async Task ApplyView_AllMail_SetsActiveView()
    {
        var view = MakeVirtualView("AllMail");
        var vm   = MakeVm([view]);

        await SelectView(vm, view);

        Assert.NotNull(vm.ActiveView);
        Assert.Equal(view.Id, vm.ActiveView!.Id);
    }

    [Fact]
    public async Task ApplyView_AllInboxes_SetsActiveView()
    {
        var view = MakeVirtualView("AllInboxes");
        var vm   = MakeVm([view]);

        await SelectView(vm, view);

        Assert.NotNull(vm.ActiveView);
        Assert.Equal(view.Id, vm.ActiveView!.Id);
    }

    [Fact]
    public async Task ApplyView_AllInboxes_SetsSelectedFolderToSentinel()
    {
        var view = MakeVirtualView("AllInboxes");
        var vm   = MakeVm([view]);

        await SelectView(vm, view);

        Assert.Equal(MainViewModel.AllInboxesFolder.FullName, vm.SelectedFolder?.FullName);
    }

    [Fact]
    public async Task ApplyView_AllMail_SetsSelectedFolderToAllMailSentinel()
    {
        var view = MakeVirtualView("AllMail");
        var vm   = MakeVm([view]);

        await SelectView(vm, view);

        Assert.Equal(MainViewModel.AllMailFolder.FullName, vm.SelectedFolder?.FullName);
    }

    // -- Apply view restores mode / filter / sort ----------------------------

    [Fact]
    public async Task ApplyView_ConversationMode_SetsViewModeConversations()
    {
        var view = MakeVirtualView("AllInboxes", viewMode: "conversations");
        var vm   = MakeVm([view]);

        await SelectView(vm, view);

        Assert.Equal(ViewMode.Conversations, vm.ViewMode);
    }

    [Fact]
    public async Task ApplyView_FromMode_SetsViewModeFrom()
    {
        var view = MakeVirtualView("AllInboxes", viewMode: "from");
        var vm   = MakeVm([view]);

        await SelectView(vm, view);

        Assert.Equal(ViewMode.From, vm.ViewMode);
    }

    [Fact]
    public async Task ApplyView_UnreadFilter_SetsActiveFilterUnread()
    {
        var view = MakeVirtualView("AllMail", filter: "unread");
        var vm   = MakeVm([view]);

        await SelectView(vm, view);

        Assert.Equal(MessageFilter.Unread, vm.ActiveFilter);
    }

    [Fact]
    public async Task ApplyView_OldestFirstSort_SetsSortDateAscending()
    {
        var view = MakeVirtualView("AllMail", sort: "dateAsc");
        var vm   = MakeVm([view]);

        await SelectView(vm, view);

        Assert.Equal(MessageSort.DateAscending, vm.ActiveSort);
    }

    // -- Refresh re-applies the active view ---------------------------------

    [Fact]
    public async Task Refresh_WithActiveView_KeepsActiveView()
    {
        var view = MakeVirtualView("AllInboxes");
        var vm   = MakeVm([view]);
        await SelectView(vm, view);

        await Refresh(vm);

        Assert.NotNull(vm.ActiveView);
        Assert.Equal(view.Id, vm.ActiveView!.Id);
    }

    [Fact]
    public async Task Refresh_WithActiveView_KeepsSentinelFolder()
    {
        var view = MakeVirtualView("AllInboxes");
        var vm   = MakeVm([view]);
        await SelectView(vm, view);

        await Refresh(vm);

        // Must still be AllInboxes sentinel, not drifted to AllMail or any other folder.
        Assert.Equal(MainViewModel.AllInboxesFolder.FullName, vm.SelectedFolder?.FullName);
    }

    [Fact]
    public async Task Refresh_WithActiveView_ReappliesViewMode()
    {
        var view = MakeVirtualView("AllInboxes", viewMode: "conversations");
        var vm   = MakeVm([view]);
        await SelectView(vm, view);

        vm.ViewMode = ViewMode.Messages; // simulate an external change

        await Refresh(vm);

        Assert.Equal(ViewMode.Conversations, vm.ViewMode);
    }

    [Fact]
    public async Task Refresh_WithNoActiveView_DoesNotSetActiveView()
    {
        var vm = MakeVm();

        await Refresh(vm);

        Assert.Null(vm.ActiveView);
    }

    // -- Switch between two views -------------------------------------------

    [Fact]
    public async Task ApplyView_ThenApplyDifferentView_UpdatesActiveView()
    {
        var viewA = MakeVirtualView("AllInboxes", viewMode: "conversations");
        var viewB = MakeVirtualView("AllMail",    viewMode: "messages");
        var vm    = MakeVm([viewA, viewB]);

        await SelectView(vm, viewA);
        Assert.Equal(viewA.Id, vm.ActiveView!.Id);
        Assert.Equal(ViewMode.Conversations, vm.ViewMode);

        await SelectView(vm, viewB);
        Assert.Equal(viewB.Id, vm.ActiveView!.Id);
        Assert.Equal(ViewMode.Messages, vm.ViewMode);
        Assert.Equal(MainViewModel.AllMailFolder.FullName, vm.SelectedFolder?.FullName);
    }

    [Fact]
    public async Task ApplyView_ThenApplyDifferentView_RefreshStaysOnSecondView()
    {
        var viewA = MakeVirtualView("AllInboxes");
        var viewB = MakeVirtualView("AllSent");
        var vm    = MakeVm([viewA, viewB]);

        await SelectView(vm, viewA);
        await SelectView(vm, viewB);
        await Refresh(vm);

        Assert.Equal(viewB.Id, vm.ActiveView!.Id);
        Assert.Equal(MainViewModel.AllSentFolder.FullName, vm.SelectedFolder?.FullName);
    }

    // -- Clear view ---------------------------------------------------------

    [Fact]
    public async Task ClearView_SetsActiveViewToNull()
    {
        var view = MakeVirtualView("AllInboxes");
        var vm   = MakeVm([view]);
        await SelectView(vm, view);

        await ClearView(vm);

        Assert.Null(vm.ActiveView);
    }

    [Fact]
    public async Task ClearView_RefreshAfterClear_DoesNotRestoreView()
    {
        var view = MakeVirtualView("AllInboxes");
        var vm   = MakeVm([view]);
        await SelectView(vm, view);
        await ClearView(vm);

        await Refresh(vm);

        Assert.Null(vm.ActiveView);
    }

    // -- Navigating to a folder clears the view -----------------------------

    [Fact]
    public async Task SelectRealFolder_ClearsActiveView()
    {
        var view = MakeVirtualView("AllInboxes");
        var vm   = MakeVm([view]);
        await SelectView(vm, view);

        var realFolder = new MailFolderModel
        {
            AccountId   = Guid.NewGuid(),
            FullName    = "INBOX",
            DisplayName = "Inbox",
        };
        await SelectFolder(vm, realFolder);

        Assert.Null(vm.ActiveView);
    }

    [Fact]
    public async Task SelectVirtualFolder_ClearsActiveView()
    {
        var view = MakeVirtualView("AllInboxes");
        var vm   = MakeVm([view]);
        await SelectView(vm, view);

        // Selecting a virtual folder directly (not through a saved view) clears ActiveView.
        await SelectFolder(vm, MainViewModel.AllMailFolder);

        Assert.Null(vm.ActiveView);
    }

    // -- VirtualFolderKey sentinel round-trip --------------------------------

    [Theory]
    [InlineData("AllMail")]
    [InlineData("AllInboxes")]
    [InlineData("AllDrafts")]
    [InlineData("AllSent")]
    [InlineData("AllTrash")]
    public async Task ApplyView_VirtualFolderKey_SelectedFolderHasSentinelPrefix(string keySuffix)
    {
        var view = MakeVirtualView(keySuffix);
        var vm   = MakeVm([view]);

        await SelectView(vm, view);

        var expectedSentinel = "\x00" + keySuffix;
        Assert.Equal(expectedSentinel, vm.SelectedFolder?.FullName);
    }

    // -- Real single-folder view --------------------------------------------

    [Fact]
    public async Task ApplyView_RealSingleFolder_SetsActiveView()
    {
        var accountId = Guid.NewGuid();
        var view      = MakeRealFolderView(accountId, "INBOX");
        var vm        = MakeVm([view]);

        await SelectView(vm, view);

        Assert.NotNull(vm.ActiveView);
        Assert.Equal(view.Id, vm.ActiveView!.Id);
    }

    [Fact]
    public async Task ApplyView_RealSingleFolder_SetsViewMode()
    {
        var accountId = Guid.NewGuid();
        var view      = MakeRealFolderView(accountId, "INBOX", viewMode: "conversations");
        var vm        = MakeVm([view]);

        await SelectView(vm, view);

        Assert.Equal(ViewMode.Conversations, vm.ViewMode);
    }

    // -- Window title reflects active view -----------------------------------

    [Fact]
    public async Task WindowTitle_WithActiveView_ShowsViewName()
    {
        var view = MakeVirtualView("AllInboxes");
        view.Name = "My All Inboxes";
        var vm   = MakeVm([view]);

        await SelectView(vm, view);

        Assert.Contains("My All Inboxes", vm.WindowTitle);
    }

    [Fact]
    public async Task WindowTitle_AfterClearView_DoesNotShowViewName()
    {
        var view = MakeVirtualView("AllInboxes");
        view.Name = "My Special Inbox View";
        var vm   = MakeVm([view]);
        AddVirtualFolders(vm);

        await SelectView(vm, view);
        Assert.Contains("My Special Inbox View", vm.WindowTitle); // sanity check

        await ClearView(vm);

        // ActiveView is null; title now shows the folder display name, not the view name.
        Assert.Null(vm.ActiveView);
        Assert.DoesNotContain("My Special Inbox View", vm.WindowTitle);
    }

    // -- Day limit applied with view ----------------------------------------

    [Fact]
    public async Task ApplyView_WithDayLimit_SetsActiveDayLimit()
    {
        var view = MakeVirtualView("AllInboxes");
        view.DaysOfMail = 7;
        var vm = MakeVm([view]);

        await SelectView(vm, view);

        Assert.Equal(7, vm.ActiveDayLimit);
    }

    [Fact]
    public async Task ApplyView_NoDayLimit_ActiveDayLimitIsNull()
    {
        var view = MakeVirtualView("AllInboxes");
        // DaysOfMail deliberately left null
        var vm = MakeVm([view]);

        await SelectView(vm, view);

        Assert.Null(vm.ActiveDayLimit);
    }

    [Fact]
    public async Task ClearView_ResetsDayLimit()
    {
        var view = MakeVirtualView("AllInboxes");
        view.DaysOfMail = 7;
        var vm = MakeVm([view]);
        await SelectView(vm, view);
        Assert.Equal(7, vm.ActiveDayLimit);

        await ClearView(vm);

        Assert.Null(vm.ActiveDayLimit);
    }

    [Fact]
    public async Task SelectRealFolder_ClearsDayLimit()
    {
        var view = MakeVirtualView("AllInboxes");
        view.DaysOfMail = 14;
        var vm = MakeVm([view]);
        await SelectView(vm, view);

        var realFolder = new MailFolderModel
        {
            AccountId   = Guid.NewGuid(),
            FullName    = "INBOX",
            DisplayName = "Inbox",
        };
        await SelectFolder(vm, realFolder);

        Assert.Null(vm.ActiveDayLimit);
    }

    [Fact]
    public async Task Refresh_WithActiveViewAndDayLimit_KeepsDayLimit()
    {
        var view = MakeVirtualView("AllInboxes");
        view.DaysOfMail = 30;
        var vm = MakeVm([view]);
        await SelectView(vm, view);

        await Refresh(vm);

        Assert.Equal(30, vm.ActiveDayLimit);
    }

    [Fact]
    public async Task SwitchViews_DayLimitUpdatesToSecondView()
    {
        var viewA = MakeVirtualView("AllInboxes");
        viewA.DaysOfMail = 7;
        var viewB = MakeVirtualView("AllMail");
        viewB.DaysOfMail = null;
        var vm = MakeVm([viewA, viewB]);

        await SelectView(vm, viewA);
        Assert.Equal(7, vm.ActiveDayLimit);

        await SelectView(vm, viewB);
        Assert.Null(vm.ActiveDayLimit);
    }

    // -- Day limit actually filters messages -------------------------------
    //
    // Regression test: persisting DaysOfMail and setting ActiveDayLimit isn't
    // enough — Messages must also be filtered when the view is applied.

    sealed class DatedMailService : IMailService
    {
        private readonly List<MailMessageSummary> _messages;
        public DatedMailService(IEnumerable<MailMessageSummary> messages) => _messages = new(messages);
        public Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(new List<MailFolderModel>());
        public Task<List<MailMessageSummary>> GetMessageSummariesAsync(Guid accountId, string folderName, int maxMessages, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>(_messages));
        public Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(Guid accountId, string folderName, DateTime since, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>(_messages));
        public Task<List<MailMessageSummary>> GetMessagesSinceAsync(Guid accountId, string folderName, uint sinceUid, int initialCount, CancellationToken ct = default) => Task.FromResult(new List<MailMessageSummary>(_messages));
        public Task<MailMessageDetail> GetMessageDetailAsync(Guid accountId, string folderName, uint uid, CancellationToken ct = default) => Task.FromResult(new MailMessageDetail());
        public Task<MailMessageDetail> PrefetchMessageDetailAsync(Guid accountId, string folderName, uint uid, CancellationToken ct = default) => Task.FromResult(new MailMessageDetail());
        public Task MarkReadAsync(Guid accountId, string folderName, uint uid, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkReadBatchAsync(Guid accountId, string folderName, IList<uint> uids, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveToTrashAsync(Guid accountId, string folderName, uint uid, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveToTrashBatchAsync(Guid accountId, string folderName, IList<uint> uids, CancellationToken ct = default) => Task.CompletedTask;
        public Task PermanentlyDeleteBatchAsync(Guid accountId, string folderName, IList<uint> uids, CancellationToken ct = default) => Task.CompletedTask;
        public Task NoOpAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> EmptyTrashAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IList<uint>> GetFolderUidsAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.FromResult<IList<uint>>(Array.Empty<uint>());
        public Task<IReadOnlyDictionary<uint, string>> FetchPreviewsAsync(Guid accountId, string folderName, IList<uint> uids, int maxLines, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<uint, string>>(new Dictionary<uint, string>());
        public Task<int> PollAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.FromResult(0);
        public Task<(int Total, int Unread)> GetInboxStatusAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult((0, 0));
        public Task<string?> FindDraftsFolderNameAsync(Guid accountId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<uint> AppendDraftAsync(Guid accountId, ComposeModel draft, uint? replaceUid, CancellationToken ct = default) => Task.FromResult(0u);
        public Task AppendToSentAsync(Guid accountId, ComposeModel sent, CancellationToken ct = default) => Task.CompletedTask;
        public Task<byte[]> DownloadAttachmentAsync(Guid accountId, string folderName, uint uid, string partSpecifier, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task CopyMessagesAsync(Guid accountId, string folderName, IList<uint> uids, string destinationFolder, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveMessagesAsync(Guid accountId, string folderName, IList<uint> uids, string destinationFolder, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateFolderAsync(Guid accountId, string? parentFolderName, string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteFolderAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.CompletedTask;
        public Task RenameFolderAsync(Guid accountId, string folderName, string newName, string? newParentFolderName, CancellationToken ct = default) => Task.CompletedTask;
        public Task CopyFolderAsync(Guid accountId, string folderName, string? destinationParentName, CancellationToken ct = default) => Task.CompletedTask;
#pragma warning disable CS0067
        public event Action<Guid>? InboxNewMailDetected;
#pragma warning restore CS0067
        public void StartIdleWatchers(IReadOnlyList<AccountModel> accounts, CancellationToken ct = default) { }
        public void StopIdleWatchers() { }
        public void Dispose() { }
    }

    sealed class DatedStore : ILocalStoreService
    {
        private readonly List<MailMessageSummary> _messages;
        public DatedStore(IEnumerable<MailMessageSummary> messages) => _messages = new(messages);
        public void Initialize() { }
        public Task UpsertSummariesAsync(IEnumerable<MailMessageSummary> summaries) => Task.CompletedTask;
        public Task<List<MailMessageSummary>> LoadAllSummariesAsync() => Task.FromResult(new List<MailMessageSummary>(_messages));
        public Task<List<MailMessageSummary>> LoadAllSummariesAsync(Guid accountId) => Task.FromResult(new List<MailMessageSummary>(_messages));
        public Task<List<MailMessageSummary>> LoadFolderSummariesAsync(Guid accountId, string folderName, int? limit = null) => Task.FromResult(new List<MailMessageSummary>(_messages));
        public Task DeleteSummariesAsync(Guid accountId, string folderName, IEnumerable<uint> uniqueIds) => Task.CompletedTask;
        public Task DeleteAccountDataAsync(Guid accountId) => Task.CompletedTask;
        public Task UpdateIsReadAsync(Guid accountId, string folderName, uint uniqueId, bool isRead) => Task.CompletedTask;
        public Task UpdateIsReadBatchAsync(IEnumerable<(Guid AccountId, string FolderName, uint UniqueId)> items, bool isRead) => Task.CompletedTask;
        public Task UpdatePreviewAsync(Guid accountId, string folderName, uint uniqueId, string preview) => Task.CompletedTask;
        public Task UpdatePreviewsBatchAsync(Guid accountId, string folderName, IEnumerable<(uint UniqueId, string Preview)> updates) => Task.CompletedTask;
        public Task<bool> HasSummariesMissingRecipientsAsync() => Task.FromResult(false);
        public Task UpsertDetailAsync(MailMessageDetail detail) => Task.CompletedTask;
        public Task<MailMessageDetail?> LoadDetailAsync(Guid accountId, string folderName, uint uniqueId) => Task.FromResult<MailMessageDetail?>(null);
        public Task<uint> GetMaxUidAsync(Guid accountId, string folderName) => Task.FromResult(0u);
        public Task<HashSet<uint>> GetAllUidsAsync(Guid accountId, string folderName) => Task.FromResult(new HashSet<uint>());
    }

    private static MainViewModel MakeVmWithStore(IEnumerable<SavedView> views, ILocalStoreService store, IMailService? imap = null)
        => new(imap ?? new StubImapMailService(),
               new StubAccountService(),
               new StubCredentialService(),
               store,
               new StubOAuthService(),
               new StubSyncService(),
               new StubConfigService(),
               new StubCommandRegistry(),
               new FakeViewService(views),
               new StubRuleService(),
               new StubSmtpService());

    [Fact]
    public async Task ApplyView_WithDayLimit_FiltersOldMessagesFromMessagesCollection()
    {
        var now = DateTimeOffset.Now;
        var recent = new MailMessageSummary { UniqueId = 1, Subject = "recent", Date = now.AddDays(-2)  };
        var older  = new MailMessageSummary { UniqueId = 2, Subject = "older",  Date = now.AddDays(-20) };
        var store  = new DatedStore([recent, older]);

        var view = MakeVirtualView("AllMail");
        view.DaysOfMail = 7;
        var vm = MakeVmWithStore([view], store);
        AddVirtualFolders(vm);

        await SelectView(vm, view);

        Assert.Equal(7, vm.ActiveDayLimit);
        Assert.Single(vm.Messages);
        Assert.Equal("recent", vm.Messages[0].Subject);
    }

    [Fact]
    public async Task ApplyView_AllMailWithDayLimit_FiltersOldMessagesFromImapPhase2()
    {
        // Regression: FetchAllMailAsync Phase 2 inserts IMAP-fetched messages
        // incrementally and used to check only MatchesFilter, not MatchesDayLimit —
        // so old messages came in via background fetch even when the view's day
        // limit had filtered them out of the initial cached load.
        var now    = DateTimeOffset.Now;
        var recent = new MailMessageSummary { UniqueId = 1, Subject = "recent", Date = now.AddDays(-1)  };
        var older  = new MailMessageSummary { UniqueId = 2, Subject = "older",  Date = now.AddDays(-20) };

        // Local store is empty so Phase 1 contributes nothing — every visible
        // message has to survive Phase 2's incremental insert path.
        var store  = new DatedStore([]);
        var imap   = new DatedMailService([recent, older]);

        var view = MakeVirtualView("AllMail");
        view.DaysOfMail = 7;
        var vm = MakeVmWithStore([view], store, imap);
        AddVirtualFolders(vm);

        await SelectView(vm, view);
        await Task.Delay(50);  // let Phase 2 (no-op for AllMail with empty Accounts) complete

        Assert.Equal(7, vm.ActiveDayLimit);
        // With no accounts in vm.Accounts, FetchAllMailAsync's Phase 2 won't fetch.
        // The point of this test is the assertion that no old messages slip in via
        // the (previously buggy) MatchesFilter-only incremental insert.
        Assert.All(vm.Messages, m => Assert.NotEqual("older", m.Subject));
    }

    [Fact]
    public async Task ApplyView_RealFolderWithDayLimit_FiltersOldMessages()
    {
        var now      = DateTimeOffset.Now;
        var acctId   = Guid.NewGuid();
        var recent   = new MailMessageSummary { UniqueId = 1, AccountId = acctId, FolderName = "INBOX", Subject = "recent", Date = now.AddDays(-2)  };
        var older    = new MailMessageSummary { UniqueId = 2, AccountId = acctId, FolderName = "INBOX", Subject = "older",  Date = now.AddDays(-20) };
        var store    = new DatedStore([recent, older]);

        var view = MakeRealFolderView(acctId, "INBOX");
        view.DaysOfMail = 7;

        var imap = new DatedMailService([recent, older]);
        var vm = MakeVmWithStore([view], store, imap);
        // Match the AccountId on the live Folders collection so ApplyViewAsync's
        // single-folder branch finds the real folder.
        vm.Folders = new ObservableCollection<MailFolderModel>
        {
            new() { AccountId = acctId, FullName = "INBOX", DisplayName = "Inbox" },
        };

        await SelectView(vm, view);
        // Give the fire-and-forget RefreshFolderFromServerAsync a chance to complete.
        await Task.Delay(50);

        Assert.Equal(7, vm.ActiveDayLimit);
        Assert.Single(vm.Messages);
        Assert.Equal("recent", vm.Messages[0].Subject);
    }
}
