using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The main window's side of #678: a shared mailbox's rules belong in Outlook. Create Rule from Message
/// is hidden from a shared mailbox's message menu and unavailable there, Ctrl+Shift+T does nothing there,
/// and the status bar does not count a rule that no longer runs as "active" — in a shared mailbox's
/// folders it says the mailbox's rules are managed in Outlook instead.
///
/// <para>Accounts are installed by REPLACING <see cref="MainViewModel.Accounts"/>, which is what rebuilds
/// the id lookup <see cref="MainViewModel.ResolveAccountById"/> reads.</para>
/// </summary>
public class SharedMailboxRuleCommandTests
{
    private static readonly AccountModel Home = new() { Id = Guid.NewGuid(), AccountName = "Home" };
    private static readonly AccountModel Team = new()
    {
        Id = Guid.NewGuid(), AccountName = "Team", IsShared = true, BackendKind = BackendKind.MicrosoftGraph,
    };

    private static MainViewModel Vm(StubRuleService? rules = null)
    {
        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), rules ?? new StubRuleService(), new StubSmtpService());
        vm.Accounts = new ObservableCollection<AccountModel>([Home, Team]);
        return vm;
    }

    private static MailMessageSummary MessageIn(AccountModel account) => new()
    {
        MessageId = Guid.NewGuid().ToString(), AccountId = account.Id, FolderName = "INBOX",
        From = "boss@work.com", Subject = "Weekly Report",
    };

    [Fact]
    public void CreateRuleFromMessage_IsUnavailable_OnASharedMailboxsMessage()
    {
        var vm = Vm();
        vm.SelectedMessage = MessageIn(Team);

        Assert.False(vm.CreateRuleFromMessageCommand.CanExecute(null));
    }

    [Fact]
    public void CreateRuleFromMessage_IsAvailable_OnAnOrdinaryAccountsMessage()
    {
        var vm = Vm();
        vm.SelectedMessage = MessageIn(Home);

        Assert.True(vm.CreateRuleFromMessageCommand.CanExecute(null));
    }

    [Fact]
    public void MovingTheSelection_ToASharedMailbox_TellsTheMenuToRecheck()
    {
        // A menu item bound to the command re-queries CanExecute only when CanExecuteChanged fires.
        var vm = Vm();
        vm.SelectedMessage = MessageIn(Home);
        var told = 0;
        vm.CreateRuleFromMessageCommand.CanExecuteChanged += (_, _) => told++;

        vm.SelectedMessage = MessageIn(Team);

        Assert.True(told > 0, "Create Rule from Message was never told to re-check when the selection moved.");
        Assert.False(vm.CreateRuleFromMessageCommand.CanExecute(null));
    }

    [Fact]
    public void CtrlShiftT_RespectsTheCheck()
    {
        // The registered command calls the RelayCommand directly, and RelayCommand.Execute does not check
        // CanExecute itself, so the registration has to. Read from source, as the rules-window wiring is.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "ViewModels", "MainViewModel.cs"));

        Assert.Contains("if (CreateRuleFromMessageCommand.CanExecute(null)) CreateRuleFromMessageCommand.Execute(null);",
                        source, StringComparison.Ordinal);
        Assert.Contains("isAvailable: () => CanActOnSelection() && CanCreateRuleFromMessage()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RulesStatusBar_DoesNotCountASharedMailboxsRuleAsActive()
    {
        var rules = new StubRuleService
        {
            LoadedRules =
            [
                new MailRule { Name = "Home rule", AccountId = Home.Id, SubjectContains = "x" },
                new MailRule { Name = "Team rule", AccountId = Team.Id, SubjectContains = "y" },
            ],
        };
        var vm = Vm(rules);

        vm.UpdateRulesStatusText();

        Assert.StartsWith("Rules: 1 active, 0 disabled", vm.RulesStatusText);
    }

    [Fact]
    public void OnAGroupHeader_TheCheckUsesTheGroupsNewestMessage()
    {
        // A header selection leaves SelectedMessage empty; the command would use the group's newest.
        var vm = Vm();
        vm.SelectedMessage = null;

        vm.SelectedGroupResolver = () => [MessageIn(Team), MessageIn(Home)];
        Assert.False(vm.CreateRuleFromMessageCommand.CanExecute(null));

        vm.SelectedGroupResolver = () => [MessageIn(Home), MessageIn(Team)];
        Assert.True(vm.CreateRuleFromMessageCommand.CanExecute(null));
    }

    [Fact]
    public void RefreshMessageTarget_TellsTheMenuToRecheck()
    {
        // A header selection changes the target without changing SelectedMessage, so the menu has to be
        // told when the Message menu recomputes its target.
        var vm = Vm();
        var told = 0;
        vm.CreateRuleFromMessageCommand.CanExecuteChanged += (_, _) => told++;

        vm.RefreshMessageTarget();

        Assert.True(told > 0, "Create Rule from Message was not told to re-check.");
    }

    [Fact]
    public void RulesStatusBar_IsRecountedOnceAccountsLoad()
    {
        // The constructor counts before accounts load, when a shared mailbox's rule cannot be told apart.
        // Loading the accounts must correct it, not leave "1 active" for a rule that never runs.
        var rules = new StubRuleService
        {
            LoadedRules = [new MailRule { Name = "Team rule", AccountId = Team.Id, SubjectContains = "y" }],
        };
        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), rules, new StubSmtpService());
        Assert.StartsWith("Rules: 1 active", vm.RulesStatusText);   // before accounts: cannot tell

        vm.LoadAccountList([Home, Team]);

        Assert.Equal("No active rules", vm.RulesStatusText);
    }

    [Fact]
    public void RulesStatusBar_InASharedMailboxsFolder_SaysItsRulesAreInOutlook()
    {
        // The count covers every account, so in a shared mailbox's folder it read as if those rules ran there.
        var rules = new StubRuleService
        {
            LoadedRules = [new MailRule { Name = "Home rule", AccountId = Home.Id, SubjectContains = "x" }],
        };
        var vm = Vm(rules);

        vm.SelectedFolder = new MailFolderModel { AccountId = Team.Id, FullName = "INBOX", DisplayName = "Inbox" };
        Assert.Equal("Rules for this shared mailbox are managed in Outlook", vm.RulesStatusText);

        vm.SelectedFolder = new MailFolderModel { AccountId = Home.Id, FullName = "INBOX", DisplayName = "Inbox" };
        Assert.Equal("Rules: 1 active, 0 disabled — Last run: not yet run", vm.RulesStatusText);
    }

    [Fact]
    public void RulesStatusBar_OnASharedMailboxsAccount_SaysItsRulesAreInOutlook()
    {
        // With no folder selected the view's account is the selected account, so choosing one must recount.
        var vm = Vm();
        Assert.Equal("No active rules", vm.RulesStatusText);

        vm.SelectedAccount = Team;

        Assert.Equal("Rules for this shared mailbox are managed in Outlook", vm.RulesStatusText);
    }

    private sealed class OneViewService(SavedView view) : IViewService
    {
        public SavedView View { get; set; } = view;
        public List<SavedView> Load() => [View];
        public void Save(List<SavedView> views) { }
    }

    [Fact]
    public void RulesStatusBar_IsRecounted_WhenASavedViewIsEdited()
    {
        // Editing the view in place changes its account without changing the selected folder.
        var rules = new StubRuleService
        {
            LoadedRules = [new MailRule { Name = "Home rule", AccountId = Home.Id, SubjectContains = "x" }],
        };
        var view = new SavedView { Name = "Mine", Folders = [new ViewFolder { AccountId = Home.Id, FolderFullName = "INBOX" }] };
        var views = new OneViewService(view);
        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), views, rules, new StubSmtpService());
        vm.Accounts = new ObservableCollection<AccountModel>([Home, Team]);
        vm.UpdateSavedViews();
        vm.SelectedFolder = new MailFolderModel { FullName = MainViewModel.ViewPrefix + view.Id, DisplayName = "Mine" };
        Assert.Equal("Rules: 1 active, 0 disabled — Last run: not yet run", vm.RulesStatusText);

        views.View = new SavedView
        {
            Id = view.Id, Name = "Mine", Folders = [new ViewFolder { AccountId = Team.Id, FolderFullName = "INBOX" }],
        };
        vm.UpdateSavedViews();

        Assert.Equal("Rules for this shared mailbox are managed in Outlook", vm.RulesStatusText);
    }

    [StaFact]
    public void CreateRuleItem_AndItsSeparator_AreHiddenWhenNotOffered()
    {
        // Hidden, not grayed: WPF skips a disabled menu item when arrowing, so a grayed one is never reached.
        var flags = new MenuItem { Header = "Flags" };
        var separator = new Separator { Tag = "CreateRuleSeparator" };
        var item = new MenuItem { Header = "Create Rule from Message", Tag = "CreateRuleItem" };
        var menu = new ContextMenu();
        menu.Items.Add(flags);
        menu.Items.Add(separator);
        menu.Items.Add(item);

        MainWindow.ShowCreateRuleItem(menu, offered: false);
        Assert.Equal(Visibility.Collapsed, item.Visibility);
        Assert.Equal(Visibility.Collapsed, separator.Visibility);
        Assert.Equal(Visibility.Visible, flags.Visibility);

        MainWindow.ShowCreateRuleItem(menu, offered: true);
        Assert.Equal(Visibility.Visible, item.Visibility);
        Assert.Equal(Visibility.Visible, separator.Visibility);
    }

    [Fact]
    public void TheMessageMenu_DecidesAsItOpens()
    {
        // The helper above only acts on items carrying these tags, and only if the menu calls it as it opens.
        var root = RepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "QuickMail", "Views", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "QuickMail", "Views", "MainWindow.xaml.cs"));

        Assert.Contains("<Separator Tag=\"CreateRuleSeparator\"/>", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"CreateRuleItem\"", xaml, StringComparison.Ordinal);
        Assert.Matches(@"FindResource\(""MessageContextMenu""\)\)\.Opened\s*\+=\s*\(_, _\) => OfferCreateRuleItem\(\);", code);

        var start = code.IndexOf("private void OfferCreateRuleItem()", StringComparison.Ordinal);
        Assert.True(start >= 0, "OfferCreateRuleItem is gone.");
        var body = code[start..code.IndexOf("\n    }", start, StringComparison.Ordinal)];
        Assert.Contains("ShowCreateRuleItem((ContextMenu)FindResource(\"MessageContextMenu\"), _vm.CanCreateRuleFromMessage())",
                        body, StringComparison.Ordinal);
        Assert.Contains("CreateRuleFromMessageCommand.NotifyCanExecuteChanged()", body, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
                return dir.FullName;
        }
        throw new InvalidOperationException($"Repo source tree not found from {AppContext.BaseDirectory}.");
    }
}
