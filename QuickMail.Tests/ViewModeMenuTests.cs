using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using QuickMail.Models;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// View → View Mode, and the toolbar's View mode button (issue #663).
///
/// The menu carries two mutually exclusive sets: the mail groupings (Messages, Conversations,
/// From, To) and the calendar slices (Agenda, Day, Week, Month). It used to
/// declare all eight items and collapse the inapplicable four. A collapsed MenuItem is still a
/// child of its parent in the automation tree, so a screen reader counted eight items in a menu
/// that showed four and announced "1 of 8" while arrowing through it.
///
/// The fix binds both menus to <see cref="MainViewModel.ViewModeOptions"/>, which holds only the
/// applicable four. These tests guard the two halves of that: the collection never carries the
/// other set, and both menus really are bound to it (a menu that quietly went back to declaring
/// its items in XAML is the regression).
/// </summary>
[Collection("WpfTests")]
public class ViewModeMenuTests
{
    private static void EnsureThemedApplication() =>
        WpfTestHost.EnsureStyles("AccessibleStyles", "ThemedControls");

    private static MainViewModel MakeVm()
    {
        var imap     = new StubImapMailService();
        var accounts = new StubAccountService();
        var creds    = new StubCredentialService();
        var store    = new StubLocalStoreService();
        var config   = new StubConfigService();
        var registry = new StubCommandRegistry();

        return new MainViewModel(imap, accounts, creds, store, new StubOAuthService(),
            new StubSyncService(), config, registry, new StubViewService(), new StubRuleService(),
            new StubSmtpService());
    }

    private static MainWindow MakeWindow(out MainViewModel vm)
    {
        var imap     = new StubImapMailService();
        var accounts = new StubAccountService();
        var creds    = new StubCredentialService();
        var store    = new StubLocalStoreService();
        var config   = new StubConfigService();
        var registry = new StubCommandRegistry();

        vm = new MainViewModel(imap, accounts, creds, store, new StubOAuthService(),
            new StubSyncService(), config, registry, new StubViewService(), new StubRuleService(),
            new StubSmtpService());

        return new MainWindow(vm, new StubSmtpService(), accounts, creds, imap,
            new StubOAuthService(), registry, new StubContactService(), config, store,
            new StubViewService(), new StubRuleService(), new StubTemplateService(),
            new StubFeatureGate());
    }

    private static MailFolderModel CalendarFolder() => new()
    {
        FullName    = MainViewModel.CalendarSourcePrefix + "local",
        DisplayName = "Local Calendar",
    };

    // ── The collection ────────────────────────────────────────────────────────

    /// <summary>
    /// The count is the bug. Four mail modes must mean four entries — not four plus four
    /// hidden calendar slices.
    /// </summary>
    [Fact]
    public void MailModes_AreTheOnlyEntries_WhenTheCalendarIsNotOpen()
    {
        var vm = MakeVm();

        Assert.False(vm.IsCalendarView);
        Assert.Equal(new[] { "Messages", "Conversations", "From", "To" },
                     vm.ViewModeOptions.Select(o => o.Id).ToArray());
        Assert.Equal(new[] { "Messages", "Conversations", "From", "To" },
                     vm.ViewModeOptions.Select(o => o.Name).ToArray());
        Assert.All(vm.ViewModeOptions, o => Assert.False(o.IsCalendarMode));
    }

    /// <summary>Selecting the calendar swaps the set rather than adding to it.</summary>
    [Fact]
    public void CalendarSlices_ReplaceTheMailModes_WhenTheCalendarOpens()
    {
        var vm = MakeVm();
        vm.SelectedFolder = CalendarFolder();

        Assert.True(vm.IsCalendarView);
        Assert.Equal(new[] { "Agenda", "Day", "Week", "Month" },
                     vm.ViewModeOptions.Select(o => o.Id).ToArray());
        Assert.All(vm.ViewModeOptions, o => Assert.True(o.IsCalendarMode));

        // …and back, so leaving the calendar does not strand the calendar slices in the menu.
        vm.SelectedFolder = new MailFolderModel { FullName = "INBOX", DisplayName = "Inbox" };
        Assert.Equal(new[] { "Messages", "Conversations", "From", "To" },
                     vm.ViewModeOptions.Select(o => o.Id).ToArray());
    }

    /// <summary>
    /// Exactly one entry is checked, and it follows the mode in effect. This is what a screen
    /// reader announces as the checked item while arrowing through the menu.
    /// </summary>
    [Fact]
    public void ExactlyOneEntryIsChecked_AndItTracksTheActiveMode()
    {
        var vm = MakeVm();

        Assert.Equal("Messages", Assert.Single(vm.ViewModeOptions.Where(o => o.IsSelected)).Id);

        vm.ViewMode = ViewMode.From;
        Assert.Equal("From", Assert.Single(vm.ViewModeOptions.Where(o => o.IsSelected)).Id);
    }

    /// <summary>Activating an entry is what actually changes the mode.</summary>
    [Fact]
    public void ActivatingAnEntry_SwitchesTheViewMode()
    {
        var vm = MakeVm();
        var conversations = vm.ViewModeOptions.Single(o => o.Id == "Conversations");

        vm.SelectViewModeCommand.Execute(conversations);

        Assert.Equal(ViewMode.Conversations, vm.ViewMode);
        Assert.Equal("Conversations", Assert.Single(vm.ViewModeOptions.Where(o => o.IsSelected)).Id);
    }

    /// <summary>
    /// Re-choosing the mode already in effect must leave it checked. Activating a checkable
    /// MenuItem clears its own IsChecked locally; the option re-raises PropertyChanged so the
    /// authoritative value is pushed back.
    /// </summary>
    [Fact]
    public void ReChoosingTheActiveMode_LeavesItChecked()
    {
        var vm = MakeVm();
        var messages = vm.ViewModeOptions.Single(o => o.Id == "Messages");
        var raised = 0;
        messages.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModeOption.IsSelected)) raised++;
        };

        vm.SelectViewModeCommand.Execute(messages);

        Assert.True(messages.IsSelected);
        Assert.True(raised > 0);
    }

    // ── The menus ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Both View Mode surfaces are bound to the one collection. Asserting the resolved
    /// ItemsSource — not just that a binding exists — is what catches a menu that went back to
    /// declaring eight items in XAML, and a DataContext that silently fails to resolve.
    /// </summary>
    [StaFact]
    public void BothViewModeMenus_AreBoundToTheOptionsCollection()
    {
        EnsureThemedApplication();
        var window = MakeWindow(out var vm);
        try
        {
            var menuItem = window.FindName("ViewModeMenuItem") as MenuItem;
            Assert.NotNull(menuItem);
            DoEvents();   // bindings on the unrealized submenu resolve at Dispatcher priority
            Assert.Same(vm.ViewModeOptions, menuItem!.ItemsSource);
            Assert.Equal(4, vm.ViewModeOptions.Count);

            var button = window.FindName("ViewModeButton") as Button;
            Assert.NotNull(button);
            var menu = button!.ContextMenu;
            Assert.NotNull(menu);

            // The toolbar menu takes its DataContext from the button it drops from, which the
            // window sets just before opening it.
            menu!.PlacementTarget = button;
            DoEvents();
            Assert.Same(vm.ViewModeOptions, menu.ItemsSource);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The generated menu item announces the mode without the mnemonic underscore, exposes the
    /// Toggle pattern (so the active mode reads as checked), and is wired to the command through
    /// its MenuBase ancestor. That last binding fails silently in XAML — nothing happens on
    /// Enter — so it is asserted from the live style.
    /// </summary>
    [StaFact]
    public void GeneratedMenuItem_AnnouncesTheMode_AndIsWiredToTheCommand()
    {
        EnsureThemedApplication();
        var window = MakeWindow(out var vm);
        try
        {
            var style = window.FindResource("ViewModeMenuItemStyle") as Style;
            Assert.NotNull(style);

            var option = vm.ViewModeOptions.Single(o => o.Id == "From");

            // Stand the container up the way the generator does: logical parent is a MenuBase
            // carrying the ViewModel, DataContext is the option.
            var host = new ContextMenu { DataContext = vm };
            var item = new MenuItem();
            host.Items.Add(item);
            item.DataContext = option;
            item.BeginInit();
            item.Style = style;
            item.EndInit();
            DoEvents();

            Assert.Equal("_From", item.Header);
            Assert.Equal("From", AutomationProperties.GetName(item));
            Assert.True(item.IsCheckable);
            Assert.NotNull(item.Command);
            Assert.Same(option, item.CommandParameter);

            var peer = UIElementAutomationPeer.CreatePeerForElement(item);
            Assert.NotNull(peer);
            Assert.Equal("From", peer!.GetName());

            // The command reached through the ancestor is the ViewModel's, and it works.
            Assert.True(item.Command!.CanExecute(option));
            item.Command.Execute(option);
            Assert.Equal(ViewMode.From, vm.ViewMode);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The count is the whole of issue #663, so it is asserted on the containers the View menu
    /// really generates, in their real ancestry — two levels inside the menu bar, which is what
    /// the command's MenuBase ancestor binding has to resolve through. Containers are generated
    /// here the way an open submenu's items host generates them, so the test needs no shown
    /// window and no synthesized input.
    /// </summary>
    [StaFact]
    public void TheViewModeSubmenu_GeneratesFourItems_EachWiredToTheCommand()
    {
        EnsureThemedApplication();
        var window = MakeWindow(out var vm);
        try
        {
            var submenu = window.FindName("ViewModeMenuItem") as MenuItem;
            Assert.NotNull(submenu);
            DoEvents();

            var items = Generate(submenu!);

            // Four on screen, four generated. Before the fix the menu declared eight.
            Assert.Equal(4, items.Count);
            Assert.Equal(new[] { "Messages", "Conversations", "From", "To" },
                         items.Select(AutomationProperties.GetName).ToArray());
            Assert.Equal(new[] { "_Messages", "_Conversations", "_From", "_To" },
                         items.Select(i => i.Header as string).ToArray());
            Assert.All(items, i => Assert.True(i.IsCheckable));
            Assert.Equal("Messages", AutomationProperties.GetName(Assert.Single(items.Where(i => i.IsChecked))));

            vm.SelectedFolder = CalendarFolder();
            DoEvents();
            Assert.Equal(new[] { "Agenda", "Day", "Week", "Month" },
                         Generate(submenu).Select(AutomationProperties.GetName).ToArray());
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Realizes an ItemsControl's containers without opening its popup — the same calls an items
    /// host makes, so the containers get the ItemContainerStyle and their item as DataContext.
    /// It does not attach them to a parent, so the Command setter's ancestor binding stays
    /// unresolved here; GeneratedMenuItem_AnnouncesTheMode_AndIsWiredToTheCommand covers that.
    /// </summary>
    private static List<MenuItem> Generate(ItemsControl owner)
    {
        var generator = (IItemContainerGenerator)owner.ItemContainerGenerator;
        var containers = new List<MenuItem>();
        using (generator.StartAt(new GeneratorPosition(-1, 0), GeneratorDirection.Forward))
        {
            while (generator.GenerateNext() is MenuItem container)
            {
                generator.PrepareItemContainer(container);
                containers.Add(container);
            }
        }
        DoEvents();
        return containers;
    }

    private static void DoEvents() =>
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            System.Windows.Threading.DispatcherPriority.Background, new System.Action(() => { }));
}
