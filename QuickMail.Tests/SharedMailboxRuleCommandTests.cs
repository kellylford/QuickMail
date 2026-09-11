using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The main window's side of #678: a shared mailbox's rules belong in Outlook. Create Rule from Message
/// is taken out of a shared mailbox's message menu, Ctrl+Shift+T and the palette say why instead of acting,
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
    public void CtrlShiftT_StaysWithCreateRule_OnASharedMailboxsMessage()
    {
        // Ctrl+Shift+T is also Focus Tab Strip's default, registered after this one, as the main window does.
        // The registry hands a key to the first AVAILABLE command bound to it, so an unavailable Create Rule
        // would pass the key on and move focus to the tab strip.
        var registry = new CommandRegistry();
        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            registry, new StubViewService(), new StubRuleService(), new StubSmtpService());
        vm.Accounts = new ObservableCollection<AccountModel>([Home, Team]);
        registry.Register(new CommandDefinition(
            id: "view.focusTabStrip", category: "View", title: "Focus Tab Strip", execute: () => { },
            defaultKey: Key.T, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift, isAvailable: () => true));

        vm.SelectedMessage = MessageIn(Team);

        Assert.Equal("mail.createRuleFromMessage",
                     registry.FindByGesture(Key.T, ModifierKeys.Control | ModifierKeys.Shift)?.Id);
    }

    private static (MainViewModel Vm, StubCommandRegistry Registry) VmWithRegistry()
    {
        var registry = new StubCommandRegistry();
        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            registry, new StubViewService(), new StubRuleService(), new StubSmtpService());
        vm.Accounts = new ObservableCollection<AccountModel>([Home, Team]);
        return (vm, registry);
    }

    [Fact]
    public void CtrlShiftT_OnASharedMailboxsMessage_SaysWhy_AsAResult()
    {
        // A command that declines to act has to say so: the palette lists it on every message, and the key no
        // longer falls through to the tab strip. The status bar shows it; the window announces it as a result.
        var (vm, registry) = VmWithRegistry();
        vm.SelectedMessage = MessageIn(Team);
        var requested = false;
        vm.CreateRuleFromMessageRequested += (_, _) => requested = true;
        AnnouncementCategory? category = null;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.StatusText)) category = vm.StatusAnnouncementCategory;
        };

        registry.FindById("mail.createRuleFromMessage")!.Execute();

        Assert.False(requested);
        Assert.Equal("Rules for the shared mailbox Team are managed in Outlook.", vm.StatusText);
        Assert.Equal(AnnouncementCategory.Result, category);
    }

    [Fact]
    public void CtrlShiftT_PressedAgain_SaysWhyAgain()
    {
        // The window announces a change of StatusText, and an unchanged assignment raises none, so the second
        // press on the same message would be silent.
        var (vm, registry) = VmWithRegistry();
        vm.SelectedMessage = MessageIn(Team);
        var said = new List<AnnouncementCategory>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.StatusText)) said.Add(vm.StatusAnnouncementCategory);
        };
        var command = registry.FindById("mail.createRuleFromMessage")!;

        command.Execute();
        command.Execute();

        Assert.Equal(new[] { AnnouncementCategory.Result, AnnouncementCategory.Result }, said);
    }

    [Fact]
    public void ThePalette_WithNothingSelected_SaysSo()
    {
        // The palette runs a command without consulting IsAvailable; declining must not be silent.
        var (vm, registry) = VmWithRegistry();
        vm.SelectedMessage = null;

        registry.FindById("mail.createRuleFromMessage")!.Execute();

        Assert.Equal("Select a message to create a rule from.", vm.StatusText);
    }

    [Fact]
    public void CtrlShiftT_OnAnOrdinaryMessage_MakesTheRule()
    {
        var (vm, registry) = VmWithRegistry();
        vm.SelectedMessage = MessageIn(Home);
        var requested = false;
        vm.CreateRuleFromMessageRequested += (_, _) => requested = true;

        registry.FindById("mail.createRuleFromMessage")!.Execute();

        Assert.True(requested);
    }

    [Fact]
    public void LoadingAccounts_TellsTheMenuToRecheck()
    {
        // Whether a message's account is a shared mailbox can change under an unmoved selection.
        var vm = Vm();
        vm.SelectedMessage = MessageIn(Home);
        var told = 0;
        vm.CreateRuleFromMessageCommand.CanExecuteChanged += (_, _) => told++;

        vm.LoadAccountList([Home, Team]);

        Assert.True(told > 0, "Create Rule from Message was not told to re-check when accounts loaded.");
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

        Assert.StartsWith("Client-side rules: 1 active, 0 disabled", vm.RulesStatusText);
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
        Assert.StartsWith("Client-side rules: 1 active", vm.RulesStatusText);   // before accounts: cannot tell

        vm.LoadAccountList([Home, Team]);

        Assert.Equal("No active client-side rules", vm.RulesStatusText);
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
        Assert.Equal("Client-side rules: 1 active, 0 disabled — Last run: not yet run", vm.RulesStatusText);
    }

    [Fact]
    public void RulesStatusBar_OnASharedMailboxsAccount_SaysItsRulesAreInOutlook()
    {
        // With no folder selected the view's account is the selected account, so choosing one must recount.
        var vm = Vm();
        Assert.Equal("No active client-side rules", vm.RulesStatusText);

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
        Assert.Equal("Client-side rules: 1 active, 0 disabled — Last run: not yet run", vm.RulesStatusText);

        views.View = new SavedView
        {
            Id = view.Id, Name = "Mine", Folders = [new ViewFolder { AccountId = Team.Id, FolderFullName = "INBOX" }],
        };
        vm.UpdateSavedViews();

        Assert.Equal("Rules for this shared mailbox are managed in Outlook", vm.RulesStatusText);
    }

    [StaFact]
    public void AWithdrawnItem_IsOutOfTheMenu_AndOutOfTheCount()
    {
        // Out of the menu, not grayed or collapsed: WPF skips a disabled item when arrowing, and a collapsed
        // one stays in the accessibility tree and in its siblings' position and set size (a screen reader
        // heard 9 items with 8 reachable). What a screen reader is told comes from the automation peer.
        var reply = new MenuItem { Header = "Reply" };
        var between = new Separator();
        var flags = new MenuItem { Header = "Flags" };
        var separator = new Separator { Tag = "CreateRuleSeparator" };
        var item = new MenuItem { Header = "Create Rule from Message", Tag = "CreateRuleItem" };
        var menu = Menu(reply, between, flags, separator, item);

        MainWindow.ShowCreateRuleItem(menu, 3, separator, item, offered: false);
        Assert.Equal(new object[] { reply, between, flags }, menu.Items.Cast<object>());
        Assert.Equal((1, 2), PositionAndSize(reply));
        Assert.Equal((2, 2), PositionAndSize(flags));

        MainWindow.ShowCreateRuleItem(menu, 3, separator, item, offered: true);
        Assert.Equal(new object[] { reply, between, flags, separator, item }, menu.Items.Cast<object>());
        Assert.Equal((3, 3), PositionAndSize(item));

        MainWindow.ShowCreateRuleItem(menu, 3, separator, item, offered: true);   // offering twice adds nothing
        Assert.Equal(5, menu.Items.Count);
    }

    [StaFact]
    public void AWithdrawnItem_ComesBackWhereItWas()
    {
        var first = new MenuItem { Header = "First" };
        var separator = new Separator { Tag = "CreateRuleSeparator" };
        var item = new MenuItem { Header = "Create Rule from Message", Tag = "CreateRuleItem" };
        var after = new MenuItem { Header = "After" };
        var menu = Menu(first, separator, item, after);

        MainWindow.ShowCreateRuleItem(menu, 1, separator, item, offered: false);
        Assert.Equal(new object[] { first, after }, menu.Items.Cast<object>());
        Assert.Equal((2, 2), PositionAndSize(after));

        MainWindow.ShowCreateRuleItem(menu, 1, separator, item, offered: true);
        Assert.Equal(new object[] { first, separator, item, after }, menu.Items.Cast<object>());
    }

    private static ContextMenu Menu(params Control[] elements)
    {
        var menu = new ContextMenu();
        foreach (var element in elements)
            menu.Items.Add(element);
        return menu;
    }

    private static (int Position, int Size) PositionAndSize(MenuItem item)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(item);
        return (peer.GetPositionInSet(), peer.GetSizeOfSet());
    }

    [Fact]
    public void TheRulesStatusButton_IsNamedByItsTextAlone()
    {
        // Every status string says what it is about; a "Rules — " prefix made the name say "Rules" twice.
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Views", "MainWindow.xaml"));

        Assert.Contains("AutomationProperties.Name=\"{Binding RulesStatusText, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("StringFormat='Rules", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRulesManagersTitle_IsBoundToTheViewModel()
    {
        // Opened from a shared mailbox the title names the account shown; a fixed title would lose that.
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Views", "UnifiedRulesWindow.xaml"));

        Assert.Contains("Title=\"{Binding WindowTitle, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMessageMenu_DecidesAsItOpens_AndAsTheSelectionMoves()
    {
        // The helper above acts on the items the window finds by these tags, and only if the window calls it.
        var root = RepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "QuickMail", "Views", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "QuickMail", "Views", "MainWindow.xaml.cs"));

        Assert.Contains("<Separator Tag=\"CreateRuleSeparator\"/>", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"CreateRuleItem\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Crea_te Rule from Message…\"", xaml, StringComparison.Ordinal);   // R is Reply's
        Assert.Contains("messageMenu.Opened += (_, _) => OfferCreateRuleItem();", code, StringComparison.Ordinal);
        Assert.Matches(@"vm\.CreateRuleFromMessageCommand\.CanExecuteChanged \+= \(_, _\) =>\s*\{\s*if \(Dispatcher\.CheckAccess\(\)\) ApplyCreateRuleOffer\(\);", code);

        var offer = Body(code, "private void OfferCreateRuleItem()");
        Assert.Contains("CreateRuleFromMessageCommand.NotifyCanExecuteChanged()", offer, StringComparison.Ordinal);
        Assert.Contains("ApplyCreateRuleOffer();", offer, StringComparison.Ordinal);
        Assert.Contains("_vm.CanCreateRuleFromMessage()", Body(code, "private void ApplyCreateRuleOffer()"), StringComparison.Ordinal);
    }

    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} is gone.");
        return code[start..code.IndexOf("\n    }", start, StringComparison.Ordinal)];
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
