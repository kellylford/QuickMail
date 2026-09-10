using System;
using System.Collections.ObjectModel;
using System.IO;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The main window's side of #678: a shared mailbox's rules belong in Outlook. Create Rule from Message
/// stays in the menu but is unavailable on a shared mailbox's message, Ctrl+Shift+T does nothing there,
/// and the status bar does not count a rule that no longer runs as "active".
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
        Assert.Contains("isAvailable: CanCreateRuleFromMessage", source, StringComparison.Ordinal);
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
