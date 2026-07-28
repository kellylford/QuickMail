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
//   - Escape closes the menu without closing the address book.

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

            Click(button);
            DoEvents();

            Assert.True(menu!.IsOpen);
            Assert.Equal(
                ["All accounts", "Local address book", "Home", "Work"],
                menu.Items.OfType<AccountFilterOption>().Select(o => o.Name).ToArray());

            // Focus lands on the filter in effect, not on the first item.
            var active = ItemFor(menu, vm.SelectedAccountFilter);
            Assert.NotNull(active);
            Assert.Same(active, Keyboard.FocusedElement);

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
            Click(button);
            DoEvents();

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
            Click(button);
            DoEvents();

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
    public void Escape_WithTheMenuOpen_DoesNotCloseTheAddressBook()
    {
        EnsureApplication();
        var (_, window, dir) = BuildWindow(out var cleanup);
        var closed = false;
        try
        {
            window!.Closed += (_, _) => closed = true;
            var button = (Button)window.FindName("AccountFilterButton");
            var menu   = button.ContextMenu!;
            menu.PlacementTarget = button;
            menu.IsOpen = true;
            DoEvents();

            // The window-level handler must decline Escape while the menu owns it.
            var handled = InvokeWindowPreviewKeyDown(window, Key.Escape);

            Assert.False(handled);
            Assert.False(closed);

            menu.IsOpen = false;
            DoEvents();
        }
        finally { if (!closed) window!.Close(); cleanup(dir); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static MenuItem? ItemFor(ContextMenu menu, AccountFilterOption option) =>
        menu.ItemContainerGenerator.ContainerFromItem(option) as MenuItem;

    private static void Click(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

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

    /// <summary>
    /// Feeds a real key event through the window's PreviewKeyDown handler and reports
    /// whether it was handled, without going through the input manager.
    /// </summary>
    private static bool InvokeWindowPreviewKeyDown(Window window, Key key)
    {
        var args = new KeyEventArgs(
            Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("no source"),
            0,
            key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };

        var method = window.GetType().GetMethod(
            "Window_PreviewKeyDown",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Window_PreviewKeyDown not found");
        method.Invoke(window, [window, args]);
        return args.Handled;
    }

    private static (AddressBookViewModel vm, AddressBookWindow? window, string dir) BuildWindow(out Action<string> cleanup)
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
            new AccountModel { Id = work, AccountName = "Work" },
            new AccountModel { Id = home, AccountName = "Home" });
        var vm = new AddressBookViewModel(svc, null, accounts);
        var window = new AddressBookWindow(vm);
        vm.LoadAsync().GetAwaiter().GetResult();
        window.Show();
        window.UpdateLayout();
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

    private static void EnsureApplication()
    {
        lock (typeof(Application))
        {
            if (Application.Current == null)
                new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            const string stylesUri = "pack://application:,,,/QuickMail;component/Styles/AccessibleStyles.xaml";
            var uri = new Uri(stylesUri, UriKind.Absolute);
            if (Application.Current!.Resources.MergedDictionaries.All(d => d.Source != uri))
                Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
        }
    }
}
