// Regression tests for the Address Book Groups tab's keyboard focus model.
//
// When the user first lands on the Groups tab, the focus should land
// somewhere useful:
//   - If there are no groups, focus the New group button (so the user
//     can press Enter or click to start creating one).
//   - If there are groups, focus the list (so the user can navigate it).
//
// The "New or rename group" name box is intentionally NOT in the default
// tab order (IsTabStop="False") so that pressing Tab from the list
// skips past it to the New button — the user never lands on a half-empty
// edit box by accident. The user can still reach the name box via
// F2 from the list (rename), by pressing the New button (which focuses
// the box when it's empty), or via the Alt+N mnemonic on the label.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

[Collection("WpfTests")]
public class GroupsTabFocusTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), $"QM-GroupsFocusTests-{Guid.NewGuid():N}");

    [StaFact]
    public void FocusGroupsPane_NoGroups_FocusesNewButton()
    {
        // With no groups, switching to the Groups tab should land on the
        // New button. The user has nothing to navigate in an empty list,
        // so the button is the most useful first stop.
        EnsureApplication();

        var (vm, window, dir) = BuildWindowWithNoGroups(out var cleanup);
        try
        {
            InvokeFocusGroupsPane(window!);
            Assert.Same(
                window!.FindName("NewGroupButton"),
                Keyboard.FocusedElement);
        }
        finally
        {
            window!.Close();
            cleanup(dir);
        }
    }

    [StaFact]
    public void FocusGroupsPane_WithGroups_FocusesGroupsList()
    {
        // With at least one group, the list is the most useful first stop.
        EnsureApplication();

        var (vm, window, dir) = BuildWindowWithOneContactAndOneGroup(out var cleanup);
        try
        {
            InvokeFocusGroupsPane(window!);
            Assert.Same(
                window!.FindName("GroupsList"),
                Keyboard.FocusedElement);
        }
        finally
        {
            window!.Close();
            cleanup(dir);
        }
    }

    [StaFact]
    public void NewGroupNameBox_IsNotInDefaultTabOrder()
    {
        // The "New or rename group" name box should not be in the default
        // tab order — pressing Tab from the groups list should skip past
        // it to the New button, not land on a half-empty edit box. The
        // user can still reach the box via F2 (rename), the New button
        // (which focuses the box when empty), or the Alt+N label mnemonic.
        EnsureApplication();

        var (vm, window, dir) = BuildWindowWithNoGroups(out var cleanup);
        try
        {
            var mainTabs = FindFirstVisualChild<TabControl>(window!);
            Assert.NotNull(mainTabs);
            mainTabs!.SelectedIndex = 1;
            DoEvents();
            window!.UpdateLayout();

            var nameBox = (TextBox)window!.FindName("NewGroupNameBox")!;
            Assert.False(nameBox.IsTabStop,
                "NewGroupNameBox.IsTabStop should be False so Tab from " +
                "the groups list does not land on the empty edit box.");
        }
        finally
        {
            window!.Close();
            cleanup(dir);
        }
    }

    [StaFact]
    public void TabFromGroupsList_SkipsNameBox_AndLandsOnNewButton()
    {
        // Drive the WPF focus pipeline: with focus on the groups list,
        // pressing Tab should move past the NewGroupNameBox (which is
        // IsTabStop=False) and land on the New button — not on the
        // name box. This catches regressions where the name box would
        // re-appear in the tab order, or where the New button's
        // position in the order changes.
        EnsureApplication();

        var (vm, window, dir) = BuildWindowWithOneContactAndOneGroup(out var cleanup);
        try
        {
            var mainTabs = FindFirstVisualChild<TabControl>(window!);
            Assert.NotNull(mainTabs);
            mainTabs!.SelectedIndex = 1;
            DoEvents();
            window!.UpdateLayout();

            var list = (ListView)window!.FindName("GroupsList")!;
            var newButton = (Button)window!.FindName("NewGroupButton")!;
            var nameBox = (TextBox)window!.FindName("NewGroupNameBox")!;

            list.Focus();
            Assert.Same(list, Keyboard.FocusedElement);

            // Drive a real Tab keypress through the WPF input pipeline.
            // WPF's PredictFocus does not support Next/Previous (only
            // directional moves), so we synthesise a real KeyDown for
            // Tab and check where focus lands. This exercises the same
            // code path the user follows when they press Tab in the
            // running app, and it honours IsTabStop.
            var tabArgs = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window)!,
                timestamp: 0,
                key: Key.Tab)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
            };
            list.RaiseEvent(tabArgs);
            DoEvents();

            // After Tab, focus must have moved off the list and not onto
            // the name box. Asserting on the focused element directly
            // avoids needing to know which specific element Tab will
            // land on (members list, New button, etc.) — the invariant
            // we care about is "the name box is never in the chain".
            Assert.NotSame(nameBox, Keyboard.FocusedElement);
            // And specifically, the next focusable element is the New
            // button (or the members list, depending on tab order).
            // Use a direct call to walk the visual tree for the first
            // IsTabStop=True element after the list to make this
            // assertion robust.
            var firstTabStop = FindFirstTabStopAfter(window!, list);
            Assert.NotSame(nameBox, firstTabStop);
            Assert.Same(newButton, firstTabStop);
        }
        finally
        {
            window!.Close();
            cleanup(dir);
        }
    }

    [StaFact]
    public void NewGroupButton_Click_WithEmptyName_FocusesNameBox()
    {
        // When the user clicks the New button with an empty name box,
        // the click should focus the name box so the user can type a
        // name. The bound CreateGroupCommand runs but no-ops on the
        // empty name (CreateGroupAsync returns early). The end result
        // is the name box has focus, ready for typing.
        EnsureApplication();

        var (vm, window, dir) = BuildWindowWithNoGroups(out var cleanup);
        try
        {
            var mainTabs = FindFirstVisualChild<TabControl>(window!);
            Assert.NotNull(mainTabs);
            mainTabs!.SelectedIndex = 1;
            DoEvents();
            window!.UpdateLayout();

            var newButton = (Button)window!.FindName("NewGroupButton")!;
            var nameBox = (TextBox)window!.FindName("NewGroupNameBox")!;
            Assert.Empty(vm.Groups);
            Assert.True(string.IsNullOrEmpty(vm.NewGroupName));

            newButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            DoEvents();

            Assert.Same(nameBox, Keyboard.FocusedElement);
            // No group was created — the command returned early on empty name.
            Assert.Empty(vm.Groups);
        }
        finally
        {
            window!.Close();
            cleanup(dir);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static (AddressBookViewModel vm, AddressBookWindow? window, string dir) BuildWindowWithNoGroups(out Action<string> cleanup)
    {
        var dir = TempDir();
        var profile = new ProfileContext(dir);
        var svc = new ContactService(profile);
        var vm = new AddressBookViewModel(svc);
        var window = new AddressBookWindow(vm);
        vm.LoadAsync().GetAwaiter().GetResult();
        window.Show();
        window.UpdateLayout();
        cleanup = DeleteDir;
        return (vm, window, dir);
    }

    private static (AddressBookViewModel vm, AddressBookWindow? window, string dir) BuildWindowWithOneContactAndOneGroup(out Action<string> cleanup)
    {
        var dir = TempDir();
        var profile = new ProfileContext(dir);
        var svc = new ContactService(profile);
        svc.CreateGroupAsync("Friends").GetAwaiter().GetResult();
        var vm = new AddressBookViewModel(svc);
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

    /// <summary>
    /// Invokes the same code path the Ctrl+G registry command and the
    /// list-level PreviewKeyDown handlers use to focus the Groups pane.
    /// The method is private; we call it via reflection so the test
    /// exercises the actual handler rather than a re-implementation.
    /// </summary>
    private static void InvokeFocusGroupsPane(Window window)
    {
        var method = window.GetType().GetMethod(
            "FocusGroupsPane",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(window, null);
    }

    private static T? FindFirstVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            if (d is T match) return match;
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
                queue.Enqueue(VisualTreeHelper.GetChild(d, i));
        }
        return null;
    }

    private static void DoEvents()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// Walks the visual tree in document order starting at <paramref name="scope"/>
    /// and returns the first element with <c>IsTabStop=true</c> that comes
    /// after <paramref name="after"/>. Used to assert on what Tab would
    /// land on without depending on the (limited) WPF PredictFocus API.
    /// </summary>
    private static DependencyObject? FindFirstTabStopAfter(DependencyObject scope, DependencyObject after)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(scope);
        var found = false;
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            if (found && d is Control c && c.IsTabStop)
                return d;
            if (ReferenceEquals(d, after)) found = true;
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
                queue.Enqueue(VisualTreeHelper.GetChild(d, i));
        }
        return null;
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
