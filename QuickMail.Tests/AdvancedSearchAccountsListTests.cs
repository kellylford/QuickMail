// Advanced Search's list of accounts (#717): one tab stop for the whole list, arrow keys between the
// accounts, and one named element per row — the check box itself, with no wrapper repeating its name.
// Modelled on RowFieldsWindowCheckBoxTests, which guards the same shape for the message list fields.

using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

[Collection("WpfTests")]
public class AdvancedSearchAccountsListTests
{
    private static (AdvancedSearchWindow Window, AdvancedSearchViewModel Vm) MakeWindow(string? folder = "Inbox")
    {
        WpfTestHost.EnsureStyles("AccessibleStyles", "ThemedControls");
        var vm = new AdvancedSearchViewModel(
            [(Guid.NewGuid(), "Work"), (Guid.NewGuid(), "Home"), (Guid.NewGuid(), "Archive")], folder);
        var window = new AdvancedSearchWindow(vm)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            // Off-screen: these tests move real keyboard focus, and a window appearing over the desktop
            // is what perturbs the other focus-dependent suites (#380).
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
        };
        return (window, vm);
    }

    private static void CloseAndReleaseFocus(Window window)
    {
        window.Close();
        Keyboard.ClearFocus();
        DrainDispatcher();
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static FieldCheckList AccountsList(AdvancedSearchWindow w)
    {
        var list = w.FindName("AccountsList") as FieldCheckList;
        Assert.NotNull(list);
        return list!;
    }

    [StaFact]
    public void EachAccountIsOneNamedCheckBox_WithNoWrapperRepeatingIt()
    {
        var (window, vm) = MakeWindow();
        window.Show();
        try
        {
            DrainDispatcher();
            var peer = UIElementAutomationPeer.CreatePeerForElement(AccountsList(window));
            Assert.NotNull(peer);
            var children = peer!.GetChildren();
            Assert.NotNull(children);

            Assert.Equal(vm.Accounts.Count, children!.Count);
            Assert.All(children, c => Assert.Equal(AutomationControlType.CheckBox, c.GetAutomationControlType()));
            Assert.Equal(["Work", "Home", "Archive"], children.Select(c => c.GetName()));
        }
        finally { CloseAndReleaseFocus(window); }
    }

    [StaFact]
    public void TheListIsNotATabStop_TheRowsAreTheArrowStops()
    {
        var (window, vm) = MakeWindow();
        window.Show();
        try
        {
            DrainDispatcher();
            var list = AccountsList(window);
            Assert.False(list.IsTabStop, "the rows are the tab stop, not the list");

            var boxes = list.RowCheckBoxes().ToArray();
            Assert.Equal(vm.Accounts.Count, boxes.Length);
            Assert.All(boxes, b => Assert.True(b.Focusable, "the row's check box must be the arrow stop"));
            Assert.Equal(KeyboardNavigationMode.Once, KeyboardNavigation.GetTabNavigation(list));
            Assert.Equal(KeyboardNavigationMode.Contained, KeyboardNavigation.GetDirectionalNavigation(list));

            // The label's access key lands on a row, not on the container.
            var label = window.FindName("AccountsLabel") as Label;
            Assert.NotNull(label);
            Assert.Same(list, label!.Target);
        }
        finally { CloseAndReleaseFocus(window); }
    }

    [StaFact]
    public void FocusingTheListLandsOnARow_AndSpaceTogglesThatAccount()
    {
        var (window, vm) = MakeWindow();
        window.Show();
        try
        {
            DrainDispatcher();
            var list = AccountsList(window);
            vm.SearchInAccounts = true;
            DrainDispatcher();

            list.Focus();
            DrainDispatcher();

            Assert.IsType<CheckBox>(Keyboard.FocusedElement);
            Assert.Equal(0, list.FocusedIndex());

            var box = list.RowCheckBoxes().First();
            var toggle = (IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(box)!
                .GetPattern(PatternInterface.Toggle);
            toggle.Toggle();
            DrainDispatcher();

            Assert.False(vm.Accounts[0].IsChosen);
        }
        finally { CloseAndReleaseFocus(window); }
    }

    [StaFact]
    public void AnAccountNameWithAnUnderscoreIsReadAsWritten_AndClaimsNoAccessKey()
    {
        WpfTestHost.EnsureStyles("AccessibleStyles", "ThemedControls");
        var vm = new AdvancedSearchViewModel([(Guid.NewGuid(), "my_work")], "Inbox");
        var window = new AdvancedSearchWindow(vm)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
        };
        window.Show();
        try
        {
            DrainDispatcher();
            var box = AccountsList(window).RowCheckBoxes().Single();
            Assert.Equal("my_work", UIElementAutomationPeer.CreatePeerForElement(box)!.GetName());
            Assert.Equal(string.Empty, UIElementAutomationPeer.CreatePeerForElement(box)!.GetAccessKey() ?? string.Empty);
        }
        finally { CloseAndReleaseFocus(window); }
    }

    [StaFact]
    public void TheFolderChoiceReadsWithNoStrayUnderscore()
    {
        var (window, _) = MakeWindow(folder: "All Inboxes");
        window.Show();
        try
        {
            DrainDispatcher();
            var radio = window.FindName("CurrentFolderRadio") as RadioButton;
            Assert.NotNull(radio);
            var name = UIElementAutomationPeer.CreatePeerForElement(radio!)!.GetName();
            Assert.Equal("This folder (All Inboxes)", name);
        }
        finally { CloseAndReleaseFocus(window); }
    }
}
