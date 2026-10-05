// Window-level wiring for the address book's account filter.
//
// The ViewModel tests (AddressBookAccountFilterTests) cover what filtering does. These
// cover the parts only the real window can prove:
//
//   - The Filter button is the second tab stop, not the first — the window still opens
//     on the search box and the contact list.
//   - Activating the button drops the menu and puts keyboard focus on the filter that is
//     currently in effect, so Up/Down move from there and Enter applies.
//   - Each menu item is checkable (which is what makes the automation peer expose the
//     Toggle pattern, so the active filter is announced as checked) and is wired to the
//     VM command through the ContextMenu's inherited DataContext. That RelativeSource
//     binding fails silently in XAML — nothing happens on Enter — so it is asserted here.
//   - An underscore in an account name does not become a hidden menu mnemonic or corrupt
//     the announced item name.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

[Collection("WpfTests")]
public class AddressBookFilterMenuTests
{
    private sealed class FakeAccountService : IAccountService
    {
        private readonly List<AccountModel> _accounts;
        public FakeAccountService(params AccountModel[] accounts) => _accounts = accounts.ToList();
        public List<AccountModel> LoadAccounts() => _accounts;
        public void SaveAccounts(List<AccountModel> accounts) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    [StaFact]
    public void FilterButton_IsTheSecondTabStop_BehindTheSearchBox()
    {
        EnsureApplication();
        var (_, window, dir) = BuildWindow(out var cleanup);
        try
        {
            var search = window!.FindName("SearchBox") as TextBox;
            var button = window.FindName("AccountFilterButton") as Button;
            Assert.NotNull(search);
            Assert.NotNull(button);

            Assert.Equal(0, search!.TabIndex);
            Assert.Equal(1, button!.TabIndex);
            Assert.True(button.IsTabStop);
        }
        finally { window!.Close(); cleanup(dir); }
    }

    [StaFact]
    public void FilterButton_LabelAndAccessibleName_ReportTheActiveFilter()
    {
        EnsureApplication();
        var (vm, window, dir) = BuildWindow(out var cleanup);
        try
        {
            var button = window!.FindName("AccountFilterButton") as Button;
            Assert.NotNull(button);

            Assert.Equal("_Filter: All accounts", button!.Content);
            Assert.Equal("Filter: All accounts",
                AutomationProperties.GetName(button));

            vm.SelectAccountFilter(vm.AccountFilterOptions.Single(o => o.Name == "Work"));
            DoEvents();

            Assert.Equal("_Filter: Work", button.Content);
            Assert.Equal("Filter: Work", AutomationProperties.GetName(button));
        }
        finally { window!.Close(); cleanup(dir); }
    }

    [StaFact]
    public void ActivatingTheButton_OpensTheMenu_OnTheActiveFilter()
    {
        EnsureApplication();
        var (vm, window, dir) = BuildWindow(out var cleanup);
        try
        {
            var button = window!.FindName("AccountFilterButton") as Button;
            Assert.NotNull(button);
            var menu = button!.ContextMenu;
            Assert.NotNull(menu);

            // Deliberately not the default filter: with "All accounts" in effect, "the filter in
            // effect" and "the first item" are the same MenuItem, so the assertion below would
            // hold even for a menu that always opened on the first item.
            vm.SelectAccountFilter(vm.AccountFilterOptions.Single(o => o.Name == "Work"));
            DoEvents();

            Assert.True(window.IsActive,
                "The address book window is not active, so the menu would keep focus on itself "
                + "whatever AccountFilterMenu_Opened does. See the comment at the focus assertion.");
            OpenMenu(button, menu!);

            Assert.True(menu!.IsOpen);
            Assert.Equal(
                ["All accounts", "Local address book", "Home", "Work"],
                menu.Items.OfType<AccountFilterOption>().Select(o => o.Name).ToArray());

            // Focus lands on the filter in effect, not on the first item.
            //
            // Asserted through the menu's own focus scope rather than Keyboard.FocusedElement.
            // What AddressBookWindow.AccountFilterMenu_Opened does is call item.Focus(); whether
            // Win32 keyboard focus then follows into the popup's own HWND depends on the test
            // process holding the foreground, which it does not. Measured mid-test, and again in an
            // isolated probe: Window.IsActive was true — that is WPF's own notion, separate from
            // foreground state — while the process was not foreground, and so Keyboard.FocusedElement stayed
            // on the window's search box even though the right MenuItem held focus in the menu's
            // scope. The Keyboard form of this assertion therefore passed or failed on how the run
            // happened to be launched, which is what made it the suite's one flaky test.
            //
            // Opening the menu differently does not change that: setting menu.IsOpen = true with no
            // button involved measures identically, and OpenAccountFilterMenu is that one line, so a
            // real mouse click, Enter on the button, and this synthesized ClickEvent all converge on
            // it. There is no code path a real click takes that the test misses.
            //
            // A ContextMenu is a focus scope by default, and one element per scope holds focus, so
            // this is the same claim as the Keyboard form — every other item excluded — without the
            // dependency on foreground state. It does still depend on the window being active in
            // WPF's sense: in an inactive window WPF makes the ContextMenu its own focused element
            // before Opened runs, item.Focus() then returns false, and the scope stays on the menu.
            // That is exactly the failure the v0.8.51 release run recorded, so BuildWindow
            // activates the window and the precondition is asserted up front, where a failure
            // names the real cause instead of a focus mismatch. What this cannot speak for is
            // whether the popup takes keyboard focus in a real, foreground session; that would
            // need a foreground-gated test under QUICKMAIL_RUN_INPUT_TESTS.
            var active = ItemFor(menu, vm.SelectedAccountFilter);
            Assert.NotNull(active);
            Assert.Same(active, FocusManager.GetFocusedElement(menu));

            menu.IsOpen = false;
            DoEvents();
        }
        finally { window!.Close(); cleanup(dir); }
    }

    [StaFact]
    public void MenuItems_AreCheckable_AndTheActiveOneIsChecked()
    {
        EnsureApplication();
        var (vm, window, dir) = BuildWindow(out var cleanup);
        try
        {
            var button = (Button)window!.FindName("AccountFilterButton");
            var menu   = button.ContextMenu!;
            OpenMenu(button, menu!);

            var all  = ItemFor(menu, vm.AccountFilterOptions.Single(o => o.Name == "All accounts"))!;
            var work = ItemFor(menu, vm.AccountFilterOptions.Single(o => o.Name == "Work"))!;

            // IsCheckable is what makes MenuItemAutomationPeer expose the Toggle pattern.
            Assert.True(all.IsCheckable);
            Assert.True(work.IsCheckable);
            Assert.True(all.IsChecked);
            Assert.False(work.IsChecked);

            menu.IsOpen = false;
            DoEvents();
        }
        finally { window!.Close(); cleanup(dir); }
    }

    [StaFact]
    public void ChoosingAMenuItem_AppliesTheFilter_AndMovesTheCheckMark()
    {
        EnsureApplication();
        var (vm, window, dir) = BuildWindow(out var cleanup);
        try
        {
            var button = (Button)window!.FindName("AccountFilterButton");
            var menu   = button.ContextMenu!;
            OpenMenu(button, menu!);

            var work = ItemFor(menu, vm.AccountFilterOptions.Single(o => o.Name == "Work"))!;
            var all  = ItemFor(menu, vm.AccountFilterOptions.Single(o => o.Name == "All accounts"))!;

            // This is the binding that fails silently if the RelativeSource DataContext
            // lookup in the ItemContainerStyle breaks.
            Assert.Same(vm.SelectAccountFilterCommand, work.Command);

            InvokeMenuItemClick(work);
            DoEvents();

            Assert.Equal("Work", vm.SelectedAccountFilter.Name);
            Assert.Equal(["Work Person"], vm.FilteredContacts.Select(c => c.DisplayName).ToArray());
            Assert.True(work.IsChecked);
            Assert.False(all.IsChecked);

            menu.IsOpen = false;
            DoEvents();
        }
        finally { window!.Close(); cleanup(dir); }
    }

    [StaFact]
    public void MenuItemNames_SurviveAnUnderscoreInAnAccountName()
    {
        // MenuItem.Header renders through a ContentPresenter with RecognizesAccessKey, so an
        // account named "work_mail" would draw and announce as "workmail" and would quietly
        // claim Alt+M inside the menu. Accounts with no display name fall back to the
        // username, where underscores are common.
        EnsureApplication();
        var (vm, window, dir) = BuildWindow(out var cleanup, underscoreAccountName: true);
        try
        {
            var button = (Button)window!.FindName("AccountFilterButton");
            var menu   = button.ContextMenu!;
            OpenMenu(button, menu!);

            var item = ItemFor(menu, vm.AccountFilterOptions.Single(o => o.Name == "work_mail"))!;

            // Doubled for rendering...
            Assert.Equal("work__mail", item.Header);
            // ...and the announced name is the account name exactly, underscore intact.
            Assert.Equal("work_mail", AutomationProperties.GetName(item));

            menu.IsOpen = false;
            DoEvents();
        }
        finally { window!.Close(); cleanup(dir); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static MenuItem? ItemFor(ContextMenu menu, AccountFilterOption option) =>
        menu.ItemContainerGenerator.ContainerFromItem(option) as MenuItem;

    private static void Click(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    /// <summary>
    /// Activates the button and pumps until the menu is open with its items generated. A single
    /// DoEvents left ItemFor returning null on the v0.8.51 release run's slow CI runner, whether
    /// because the popup had not opened yet or its items were not generated.
    /// </summary>
    private static void OpenMenu(Button button, ContextMenu menu)
    {
        Click(button);
        Assert.True(
            PumpUntil(() => menu.IsOpen
                && menu.ItemContainerGenerator.Status
                   == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated),
            "The filter menu did not open with its items generated within 5 seconds.");
    }

    /// <summary>Pumps the dispatcher until <paramref name="condition"/> holds, or five seconds pass.
    /// Returns whether the condition held, so a timeout fails with a reason rather than as a
    /// NullReferenceException further on.</summary>
    private static bool PumpUntil(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        DoEvents();
        while (!condition())
        {
            if (clock.Elapsed >= TimeSpan.FromSeconds(5))
                return false;
            System.Threading.Thread.Sleep(10);
            DoEvents();
        }
        return true;
    }

    /// <summary>
    /// Runs the same code path pressing Enter on a menu item does. Raising ClickEvent is
    /// not enough — MenuItem toggles IsChecked and invokes its Command from OnClick, which
    /// a synthesized routed event never reaches.
    /// </summary>
    private static void InvokeMenuItemClick(MenuItem item)
    {
        var onClick = typeof(MenuItem).GetMethod(
            "OnClick",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, Type.EmptyTypes, null)
            ?? throw new InvalidOperationException("MenuItem.OnClick not found");
        onClick.Invoke(item, null);
    }

    private static (AddressBookViewModel vm, AddressBookWindow? window, string dir) BuildWindow(
        out Action<string> cleanup, bool underscoreAccountName = false)
    {
        var dir  = Path.Combine(Path.GetTempPath(), $"QM-AddrFilterMenu-{Guid.NewGuid():N}");
        var svc  = new ContactService(new ProfileContext(dir));
        var work = Guid.NewGuid();
        var home = Guid.NewGuid();
        svc.UpsertContactAsync(new ContactModel { DisplayName = "Local Person", EmailAddress = "local@x.test" })
           .GetAwaiter().GetResult();
        svc.ReplaceSyncedContactsAsync(work, ContactSource.Microsoft,
            [new ContactModel { SourceId = "w1", DisplayName = "Work Person", EmailAddress = "work@x.test" }])
           .GetAwaiter().GetResult();
        svc.ReplaceSyncedContactsAsync(home, ContactSource.Google,
            [new ContactModel { SourceId = "h1", DisplayName = "Home Person", EmailAddress = "home@x.test" }])
           .GetAwaiter().GetResult();

        var accounts = new FakeAccountService(
            new AccountModel { Id = work, AccountName = underscoreAccountName ? "work_mail" : "Work" },
            new AccountModel { Id = home, AccountName = "Home" });
        var vm = new AddressBookViewModel(svc, null, accounts);
        var window = new AddressBookWindow(vm);
        vm.LoadAsync().GetAwaiter().GetResult();
        window.Show();
        // Show() normally activates, but a window left over from an earlier test, or another
        // process, can hold activation; the filter menu's focus depends on it.
        window.Activate();
        window.UpdateLayout();
        // Only the focus test depends on activation, and it asserts IsActive itself with a reason.
        _ = PumpUntil(() => window.IsActive);
        cleanup = DeleteDir;
        return (vm, window, dir);
    }

    private static void DeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    private static void DoEvents()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>Delegates to the shared host: the Application must live on a thread that outlives
    /// the run, not on whichever [StaFact] thread happened to be first (issue #211).</summary>
    private static void EnsureApplication() => WpfTestHost.EnsureApplication();
}
