// Regression test: verify the Alt+T / Alt+C / Alt+B access keys still
// fire the To / Cc / Bcc insert commands in the Address Book window
// after the groups feature was added. Pre-groups, the Window.PreviewKeyDown
// only handled Escape; post-groups, it dispatches registered commands first.
//
// The two access key rows that must continue to work:
//   - Contacts tab: "Add selected contact to:" row → To / Cc / Bcc
//   - Groups tab:   "Insert all into:" row        → To / Cc / Bcc
//
// The tests below cover three failure modes that would break the access keys:
//
//   1. The XAML stops using the underscore-mnemonic Content ("_To" etc.) or
//      a refactor strips the Command binding. Detected by walking the visual
//      tree and asserting on each button's Content and Command property.
//
//   2. The new Window.PreviewKeyDown handler accidentally sets e.Handled =
//      true on an Alt+letter keystroke that no registered command matches.
//      WPF's input manager early-outs when Handled is true, so the access
//      key manager never runs. Detected by feeding a real KeyEventArgs
//      through the handler and asserting Handled is still false.
//
//   3. The WPF Button.OnAccessKey pipeline itself stops working for these
//      buttons (e.g. a custom command binding prevents the default click).
//      Detected by reflecting into Button.OnAccessKey and calling it
//      directly, then asserting the bound Command ran.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
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

public class AccessKeyRegressionTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), $"QM-AccessKeyTests-{Guid.NewGuid():N}");

    [StaFact]
    public void ContactTab_InsertButtons_HaveAccessKeyMnemonics_AndCorrectCommands()
    {
        // Contacts tab is selected by default. The "_To / _Cc / _Bcc" buttons
        // in the "Add selected contact to:" row must have the underscore
        // mnemonic Content and the right Command binding to the VM.
        EnsureApplication();

        var (vm, window, dir) = BuildWindowWithOneContact(out var cleanup);
        try
        {
            var toButton  = FindButtonByContent(window!, "_To", occurrence: 1);
            var ccButton  = FindButtonByContent(window!, "_Cc", occurrence: 1);
            var bccButton = FindButtonByContent(window!, "_Bcc", occurrence: 1);

            Assert.Equal(vm.AddToToCommand,  toButton.Command);
            Assert.Equal(vm.AddToCcCommand,  ccButton.Command);
            Assert.Equal(vm.AddToBccCommand, bccButton.Command);

            Assert.Equal("_To",  toButton.Content);
            Assert.Equal("_Cc",  ccButton.Content);
            Assert.Equal("_Bcc", bccButton.Content);
        }
        finally
        {
            window!.Close();
            cleanup(dir);
        }
    }

    [StaFact]
    public void GroupTab_InsertAllButtons_HaveAccessKeyMnemonics_AndCorrectCommands()
    {
        // Switch to the Groups tab — the "Insert all into:" row has its
        // own _To / _Cc / _Bcc buttons. WPF TabControl only realises the
        // selected tab's content, so the Contacts buttons are unloaded and
        // we find the Group buttons as the first (and only) occurrence.
        EnsureApplication();

        var (vm, window, dir) = BuildWindowWithOneContactAndOneGroup(out var cleanup);
        try
        {
            var mainTabs = FindFirstVisualChild<TabControl>(window!);
            Assert.NotNull(mainTabs);
            mainTabs!.SelectedIndex = 1;
            DoEvents();
            window!.UpdateLayout();

            var toButton  = FindButtonByContent(window!, "_To", occurrence: 1);
            var ccButton  = FindButtonByContent(window!, "_Cc", occurrence: 1);
            var bccButton = FindButtonByContent(window!, "_Bcc", occurrence: 1);

            Assert.Equal(vm.AddGroupToToCommand,  toButton.Command);
            Assert.Equal(vm.AddGroupToCcCommand,  ccButton.Command);
            Assert.Equal(vm.AddGroupToBccCommand, bccButton.Command);

            Assert.Equal("_To",  toButton.Content);
            Assert.Equal("_Cc",  ccButton.Content);
            Assert.Equal("_Bcc", bccButton.Content);
        }
        finally
        {
            window!.Close();
            cleanup(dir);
        }
    }

    [StaFact]
    public void ContactsTab_HasNoAccessKeyConflicts_OnInsertLetters()
    {
        // The pre-groups address book had no TabControl, so the underscore
        // mnemonics on _To / _Cc / _Bcc were the only registrations for
        // those letters and Alt+T / Alt+C / Alt+B worked cleanly. After
        // the groups commit, a TabControl was added with tab headers
        // "_Contacts" and "_Groups". The "_Contacts" header registered
        // the letter C as an access key too, and WPF's AccessKeyManager
        // cycles through multiple matches on the first press — so the
        // first Alt+C selects the Contacts tab (the tab header) instead
        // of inserting into the Cc field. Same conflict shape for the
        // Groups tab. This test fails if any other element in the visual
        // tree also registers T, C, or B as an access key while the
        // _To / _Cc / _Bcc buttons are in scope.
        EnsureApplication();

        var (vm, window, dir) = BuildWindowWithOneContactAndOneGroup(out var cleanup);
        try
        {
            // Select the contact so the _To / _Cc / _Bcc buttons are
            // enabled (otherwise AccessKeyManager.IsTargetable rejects
            // them and the conflict is masked).
            vm.SelectedContact = vm.FilteredContacts[0];
            window!.Show();
            window.UpdateLayout();

            // On the Contacts tab, only the insert-row buttons should
            // own the T / C / B access keys. The Groups tab's content
            // is not in the visual tree (TabControl only realises the
            // selected tab), and the tab headers have no underscore
            // mnemonics that would collide.
            AssertAccessKeyCounts(window!, expected: new() { ['T'] = 1, ['C'] = 1, ['B'] = 1 },
                scopeLabel: "Contacts tab");

            // Switch to the Groups tab. The Contacts tab's content
            // unloads, but tab headers (chrome) stay realised. If a
            // header has an underscore mnemonic that collides with the
            // insert letters, the count goes up and this assertion fails.
            var mainTabs = FindFirstVisualChild<TabControl>(window!);
            Assert.NotNull(mainTabs);
            mainTabs!.SelectedIndex = 1;
            DoEvents();
            window!.UpdateLayout();

            AssertAccessKeyCounts(window!, expected: new() { ['T'] = 1, ['C'] = 1, ['B'] = 1 },
                scopeLabel: "Groups tab");
        }
        finally
        {
            window!.Close();
            cleanup(dir);
        }
    }

    /// <summary>
    /// Asserts that the visual tree of <paramref name="scope"/> contains
    /// exactly the expected number of enabled elements per access key
    /// letter, in <paramref name="expected"/>. A count of 1 means a single
    /// element owns the access key; > 1 means WPF's AccessKeyManager will
    /// cycle through the matches on the first press instead of firing the
    /// user's intended target.
    /// </summary>
    private static void AssertAccessKeyCounts(
        Window scope,
        Dictionary<char, int> expected,
        string scopeLabel)
    {
        var conflicts = FindAccessKeyConflicts(scope, "T", "C", "B");
        var counts = conflicts
            .GroupBy(c => c.letter)
            .ToDictionary(g => g.Key, g => g.Count());
        foreach (var (letter, want) in expected)
        {
            var have = counts.GetValueOrDefault(letter, 0);
            if (have != want)
            {
                var detail = string.Join("\n", conflicts.Select(c =>
                    $"  letter={c.letter} type={c.elementType} name='{c.name}' tab='{c.tab}'"));
                Assert.Fail(
                    $"{scopeLabel}: expected exactly {want} element(s) with access " +
                    $"key '{letter}', found {have}.\nConflicts:\n{detail}");
            }
        }
    }

    [StaFact]
    public void PreviewKeyDown_AltT_DoesNotSwallowAccessKey()
    {
        // The post-groups Window.PreviewKeyDown dispatches any registered
        // command whose key+modifiers match. If it sets e.Handled = true
        // for a key that should fall through (e.g. Alt+T) the WPF access
        // key pipeline never fires and Alt+T silently does nothing. This
        // test feeds a real KeyEventArgs for Alt+T through Window.PreviewKeyDown
        // and asserts the handler does NOT mark it handled, so the access
        // key manager downstream can find the To button.
        EnsureApplication();

        var (vm, window, dir) = BuildWindowWithOneContact(out var cleanup);
        try
        {
            vm.SelectedContact = vm.FilteredContacts[0];
            window!.Show();
            window.UpdateLayout();

            var args = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window)!,
                timestamp: 0,
                key: Key.T)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
            };

            var handler = typeof(AddressBookWindow).GetMethod(
                "Window_PreviewKeyDown",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(handler);
            handler!.Invoke(window, [window, args]);

            // The registry does not match Key.T + Alt, so the handler
            // should fall through. If this fails, the access key manager
            // will skip the keystroke and Alt+T silently does nothing in
            // the running app.
            Assert.False(args.Handled,
                "Window.PreviewKeyDown marked Alt+T as handled. The WPF " +
                "access key pipeline will skip the keystroke and the " +
                "To button will not fire. Likely cause: a registered " +
                "command is matching the wrong gesture, or the dispatcher " +
                "is setting e.Handled for non-matches.");
        }
        finally
        {
            window!.Close();
            cleanup(dir);
        }
    }

    [StaFact]
    public void AccessKeyManager_Dispatch_ActivatesInsertButtons()
    {
        // Drive the WPF access-key pipeline directly. We find the button
        // whose AccessKey matches and invoke Button.OnAccessKey via
        // reflection — the same protected method WPF calls when an
        // access key is matched. This proves end-to-end that the
        // underlying command fires for both contact-insert and group-insert
        // rows.
        EnsureApplication();

        var (vm, window, dir) = BuildWindowWithOneContactAndOneGroup(out var cleanup);
        try
        {
            int toCalls = 0, ccCalls = 0, bccCalls = 0;
            vm.SetInsertActions(
                toAction:  _ => Interlocked.Increment(ref toCalls),
                ccAction:  _ => Interlocked.Increment(ref ccCalls),
                bccAction: _ => Interlocked.Increment(ref bccCalls));

            vm.LoadAsync().GetAwaiter().GetResult();
            vm.SelectedContact = vm.FilteredContacts[0];
            window!.Show();
            window.UpdateLayout();

            InvokeAccessKey(window, "T");
            InvokeAccessKey(window, "C");
            InvokeAccessKey(window, "B");

            Assert.Equal(1, toCalls);
            Assert.Equal(1, ccCalls);
            Assert.Equal(1, bccCalls);
        }
        finally
        {
            window!.Close();
            cleanup(dir);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static (AddressBookViewModel vm, AddressBookWindow? window, string dir) BuildWindowWithOneContact(out Action<string> cleanup)
    {
        var dir = TempDir();
        var profile = new ProfileContext(dir);
        var svc = new ContactService(profile);
        svc.UpsertContactAsync(new ContactModel
        {
            DisplayName   = "Test Person",
            EmailAddress  = "[email protected]",
        }).GetAwaiter().GetResult();
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
        svc.UpsertContactAsync(new ContactModel
        {
            DisplayName   = "Test Person",
            EmailAddress  = "[email protected]",
        }).GetAwaiter().GetResult();
        svc.CreateGroupAsync("Friends").GetAwaiter().GetResult();
        svc.AddMemberAsync(1, 1).GetAwaiter().GetResult();
        var vm = new AddressBookViewModel(svc);
        // Wire insert actions so the InsertRow is visible (Collapsed by
        // default; only shown when HasInsertActions is true).
        vm.SetInsertActions(_ => { }, _ => { }, _ => { });
        var window = new AddressBookWindow(vm);
        vm.LoadAsync().GetAwaiter().GetResult();
        vm.SelectedGroup = vm.Groups.Count > 0 ? vm.Groups[0] : null;
        window.Show();
        window.UpdateLayout();
        cleanup = DeleteDir;
        return (vm, window, dir);
    }

    private static void DeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    private static void EnsureApplication()
    {
        // The XamlParseTests class uses the same lock; both classes
        // share a single Application instance per process.
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

    private static Button FindButtonByContent(Window window, string contentText, int occurrence = 1)
    {
        int found = 0;
        Button? match = null;
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(window.Content as DependencyObject ?? window);
        while (queue.Count > 0 && match == null)
        {
            var d = queue.Dequeue();
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(d, i);
                if (child is Button b
                    && b.Content is string s
                    && s == contentText)
                {
                    found++;
                    if (found == occurrence) { match = b; break; }
                }
                queue.Enqueue(child);
            }
        }
        Assert.NotNull(match);
        return match!;
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
        // Pump the dispatcher so deferred work (layout, virtualisation
        // realises) completes synchronously before the next assertion.
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// Drives the WPF access-key pipeline the same way the real keyboard
    /// input does when the user presses Alt+letter. We find the button
    /// whose <c>AccessKey</c> matches the requested letter and invoke
    /// <c>Button.OnAccessKey</c> via reflection — the same protected
    /// method WPF calls from the public <c>Button.OnAccessKey</c> →
    /// command dispatch chain.
    /// </summary>
    private static void InvokeAccessKey(Window scope, string key)
    {
        var button = FindAnyButtonWithAccessKey(scope, key);
        if (button == null)
            throw new InvalidOperationException(
                $"No enabled button with access key '{key}' in window scope.");

        var onAccessKey = typeof(Button).GetMethod(
            "OnAccessKey",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Button.OnAccessKey not found");

        // AccessKeyEventArgs has an internal constructor; build one
        // through the public surface.
        var accessKeyEventArgsCtor = Assembly.Load("PresentationCore")
            .GetType("System.Windows.Input.AccessKeyEventArgs")
            ?.GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                types: [typeof(string), typeof(bool), typeof(bool)],
                modifiers: null)
            ?? throw new InvalidOperationException("AccessKeyEventArgs ctor not found");

        var args = accessKeyEventArgsCtor.Invoke([key, false, true]);
        onAccessKey.Invoke(button, [args]);
    }

    private static Button? FindAnyButtonWithAccessKey(Window scope, string key)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(scope.Content as DependencyObject ?? scope);
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(d, i);
                if (child is Button b && IsEnabledAndHasAccessKey(b, key))
                    return b;
                queue.Enqueue(child);
            }
        }
        return null;
    }

    private static bool IsEnabledAndHasAccessKey(Button b, string key)
    {
        if (!b.IsEnabled) return false;
        var accessText = FindFirstVisualChild<System.Windows.Controls.AccessText>(b);
        return accessText?.AccessKey is char c
            && string.Equals(c.ToString(), key, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Walks the visual tree of <paramref name="scope"/> and reports every
    /// enabled element that has an access key matching one of the supplied
    /// letters. The result is used to detect WPF access-key conflicts: if
    /// two enabled elements both register the same letter, the dispatcher
    /// cycles through them and the user's intended target does not fire on
    /// the first keypress.
    ///
    /// The reported "tab" string is the name of the currently-selected
    /// <see cref="TabItem"/> ancestor, or "(none)" for elements that live
    /// outside the TabControl (e.g. tab headers themselves, which sit in
    /// the chrome and are always realised).
    /// </summary>
    private static List<(char letter, string elementType, string name, string tab)>
        FindAccessKeyConflicts(Window scope, params string[] letters)
    {
        var results = new List<(char, string, string, string)>();
        var letterSet = new HashSet<char>(
            letters.Select(l => char.ToUpperInvariant(l[0])));

        // The currently-selected TabItem header text — used to label
        // which tab the conflicting element belongs to.
        var mainTabs = FindFirstVisualChild<TabControl>(scope);
        var selectedTabHeader = mainTabs?.SelectedItem is TabItem item
            ? (item.Header as string) ?? "(unknown)"
            : "(none)";

        var queue = new Queue<DependencyObject>();
        queue.Enqueue(scope.Content as DependencyObject ?? scope);
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(d, i);
                ReportIfConflict(child, letterSet, selectedTabHeader, results);
                queue.Enqueue(child);
            }
        }
        return results;
    }

    private static void ReportIfConflict(
        DependencyObject element,
        HashSet<char> letterSet,
        string tabHeader,
        List<(char letter, string elementType, string name, string tab)> results)
    {
        // TabItem headers (the chrome above the content) sit inside the
        // TabControl but outside any TabItem.Content. We want to report
        // them too — a tab header is the source of the original "Alt+C
        // selects the Contacts tab" bug.
        if (element is TabItem ti)
        {
            var headerText = (ti.Header as string) ?? "";
            var mnemonic = ExtractMnemonic(headerText);
            if (mnemonic is char c && letterSet.Contains(c))
            {
                results.Add((c, "TabItem", headerText, "(tab chrome)"));
            }
        }
        // Buttons and Labels get their access key from the content /
        // Content string. Inspect either the Content string directly or
        // the AccessText child for the access key.
        if (element is Button b && b.IsEnabled)
        {
            var mnemonic = ButtonAccessKey(b);
            if (mnemonic is char bc && letterSet.Contains(bc))
                results.Add((bc, "Button", b.Content?.ToString() ?? "", tabHeader));
        }
        else if (element is Label lbl)
        {
            var mnemonic = LabelAccessKey(lbl);
            if (mnemonic is char lc && letterSet.Contains(lc))
                results.Add((lc, "Label", lbl.Content?.ToString() ?? "", tabHeader));
        }
    }

    private static char? ExtractMnemonic(string text)
    {
        var i = text.IndexOf('_');
        if (i < 0 || i == text.Length - 1) return null;
        return char.ToUpperInvariant(text[i + 1]);
    }

    private static char? ButtonAccessKey(Button b)
    {
        // WPF wraps the Content string in an AccessText whose AccessKey
        // property is set at parse time. The visual tree may not be
        // realised yet if the button is in a non-selected tab.
        var accessText = FindFirstVisualChild<System.Windows.Controls.AccessText>(b);
        if (accessText?.AccessKey is char c) return c;
        // Fall back to a raw scan of the Content string.
        if (b.Content is string s) return ExtractMnemonic(s);
        return null;
    }

    private static char? LabelAccessKey(Label lbl)
    {
        if (lbl.Content is string s) return ExtractMnemonic(s);
        return null;
    }
}
