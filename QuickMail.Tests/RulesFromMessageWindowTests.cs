using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Rules from a message open in its own window. The window had neither Create Rule from Message nor the
/// Rules Manager — not on a menu, not on a key, not in its palette — so a message read there could not
/// become a rule. The rule comes from the WINDOW's message, which Prev/Next moves independently of the
/// main list's selection.
/// </summary>
public class RulesFromMessageWindowTests
{
    private static readonly AccountModel Home = new() { Id = Guid.NewGuid(), AccountName = "Home" };
    private static readonly AccountModel Team = new()
    {
        Id = Guid.NewGuid(), AccountName = "Team", IsShared = true, BackendKind = BackendKind.MicrosoftGraph,
    };

    private static MainViewModel Vm()
    {
        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService());
        vm.Accounts = new ObservableCollection<AccountModel>([Home, Team]);
        return vm;
    }

    private static MailMessageSummary MessageIn(AccountModel account, string from = "boss@work.com") => new()
    {
        MessageId = Guid.NewGuid().ToString(), AccountId = account.Id, FolderName = "INBOX",
        From = from, Subject = "Weekly Report",
    };

    [Fact]
    public void TheTemplate_IsForTheWindowsMessage_NotTheMainSelection()
    {
        var vm = Vm();
        vm.SelectedMessage = MessageIn(Home, "someone@else.com");
        var open = MessageIn(Home);

        var template = vm.RuleTemplateForOpenMessage(open);

        Assert.NotNull(template);
        Assert.Equal("boss@work.com", template.SenderContains);
        Assert.True(template.UseSenderCondition);
        Assert.Equal("Weekly Report", template.SubjectContains);
        Assert.False(template.UseSubjectCondition);   // #665: the subject is offered, not switched on
        Assert.Equal(Home.Id, template.AccountId);
        Assert.Equal("someone@else.com", vm.SelectedMessage!.From);   // the main selection is left alone
    }

    [Fact]
    public void OnASharedMailboxsMessage_ThereIsNoTemplate_AndItSaysWhy_AsAResult()
    {
        // The refusal must go through AnnouncementRequested, synchronously, so the main window can aim it
        // at the message window. The status bar's own announcement is debounced onto the MAIN window, so it
        // has to stay silent here or the user hears it twice, once from a window they are not in.
        var vm = Vm();
        var announced = new System.Collections.Generic.List<(string Text, AnnouncementCategory Category)>();
        vm.AnnouncementRequested += (_, a) => announced.Add(a);
        AnnouncementCategory? statusCategory = null;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.StatusText)) statusCategory = vm.StatusAnnouncementCategory;
        };

        Assert.Null(vm.RuleTemplateForOpenMessage(MessageIn(Team)));

        const string why = "Rules for the shared mailbox Team are managed in Outlook.";
        Assert.Equal([(why, AnnouncementCategory.Result)], announced);
        Assert.Equal(why, vm.StatusText);
        Assert.Equal(AnnouncementCategory.Silent, statusCategory);
    }

    [Fact]
    public void WithNoMessage_ThereIsNoTemplate()
        => Assert.Null(Vm().RuleTemplateForOpenMessage(null));

    [Fact]
    public void TheRulesManager_OpensOnTheWindowsMessagesAccount()
    {
        var vm = Vm();
        Assert.Equal(Home.Id, vm.RulesAccountContextFor(MessageIn(Home)));
        // A shared mailbox's rules are not QuickMail's, so it falls back to the main window's view.
        Assert.Equal(vm.RulesAccountContext, vm.RulesAccountContextFor(MessageIn(Team)));
        Assert.Equal(vm.RulesAccountContext, vm.RulesAccountContextFor(null));
    }

    [Fact]
    public void TheWindowsCommands_RunTheirActions()
    {
        var created = 0;
        var managed = 0;
        var winVm = new MessageWindowViewModel
        {
            CreateRuleAction = () => created++,
            ManageRulesAction = () => managed++,
        };

        winVm.CreateRuleFromMessageCommand.Execute(null);
        winVm.ManageRulesCommand.Execute(null);

        Assert.Equal((1, 1), (created, managed));
    }

    [StaFact]
    public void TheWindow_HasBothOnItsKeysAndInItsPalette()
    {
        var created = 0;
        var managed = 0;
        var winVm = new MessageWindowViewModel
        {
            CreateRuleAction = () => created++,
            ManageRulesAction = () => managed++,
        };
        var win = new MessageWindow(winVm, new StubImapMailService(), new StubLocalStoreService());
        try
        {
            var registry = (CommandRegistry)typeof(MessageWindow)
                .GetField("_localRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(win)!;

            var create = registry.FindByGesture(Key.T, ModifierKeys.Control | ModifierKeys.Shift);
            var manage = registry.FindByGesture(Key.L, ModifierKeys.Control | ModifierKeys.Shift);
            Assert.Equal("Create Rule from Message", create?.Title);
            Assert.Equal("Manage Rules", manage?.Title);

            create!.Execute();
            manage!.Execute();
            Assert.Equal((1, 1), (created, managed));
        }
        finally
        {
            win.Close();   // constructed windows join Application.Current.Windows (#252)
        }
    }

    [StaFact]
    public void TheWindowsMenus_OfferBoth_WithTheMainWindowsGestures()
    {
        var win = new MessageWindow(new MessageWindowViewModel(), new StubImapMailService(), new StubLocalStoreService());
        try
        {
            var menu = win.FindName("MainMenuBar") as Menu;
            Assert.NotNull(menu);
            var items = menu.Items.OfType<MenuItem>().SelectMany(m => m.Items.OfType<MenuItem>()).ToList();

            var create = items.SingleOrDefault(i => (string)i.Header == "Crea_te Rule from Message…");
            var manage = items.SingleOrDefault(i => (string)i.Header == "_Rules…");
            Assert.NotNull(create);
            Assert.NotNull(manage);
            Assert.Equal("Ctrl+Shift+T", create.InputGestureText);
            Assert.Equal("Ctrl+Shift+L", manage.InputGestureText);
        }
        finally
        {
            win.Close();
        }
    }

    [Fact]
    public void TheMainMessageMenu_OffersCreateRule()
    {
        // It was only on the message list's context menu.
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Views", "MainWindow.xaml"));

        Assert.Contains("Header=\"Create R_ule from Message…\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"MenuCreateRule_Click\"", xaml, StringComparison.Ordinal);
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
