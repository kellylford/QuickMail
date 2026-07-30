// Type-ahead on the Address Book lists — issue #371. These raise TextInput events at a
// shown window, so they are opt-in (QUICKMAIL_RUN_INPUT_TESTS=1, which CI sets) and
// report as skipped with a reason elsewhere. See issue #380 for the flakiness.
//
// Scope, stated precisely because the earlier version of this comment overstated it:
//
//  * These are NOT end-to-end. SendText raises UIElement.TextInputEvent directly on the
//    list, so key translation, PreviewKeyDown, PreviewTextInput and AccessKeyManager are
//    all bypassed. A key stolen before TextSearch sees it will NOT fail these tests —
//    and that is how type-ahead has actually broken here twice (release notes v0.8.32:
//    the picker's "_New" mnemonic firing on the type-ahead "n", and bare-K competing with
//    folder-tree type-ahead). Those paths remain uncovered.
//
//  * The declaration-and-property wiring these were written to guard is now asserted
//    deterministically by TypeAheadWiringTests, which is why gating them is acceptable.
//
//  * Gating is not a fix. Issue #380 diagnoses a harness readiness bug — a fire-once
//    DoEvents() that assumes a single dispatcher pump is enough — and proposes a bounded
//    wait on ContainersGenerated plus focus actually landing. That is NOT implemented
//    here; these tests are simply no longer run by default on developer machines.
//
// Two tests were removed rather than gated (issue #380): they asserted that "b"+"r"
// accumulates into the prefix "br", and that a repeated "b" cycles to the next match.
// That is WPF's TextSearch doing the accumulating on these particular lists, and the
// assertions needed both synthesized keystrokes to land inside its reset window, which
// made them the least reliable tests in the suite.
//
// Note this does NOT mean the app has no prefix accumulator of its own — it does.
// TypeAheadPrefixTracker (extracted from MainWindow for issue #415) hand-rolls one for
// the message list and group trees (shipped in v0.5.5), and — since #418 — the folder
// tree and the folder picker's tree. (The v0.5.5 notes described the folder tree as
// accumulating, but its code was always single-shot until #418.) The tracker is covered
// deterministically by TypeAheadLogicTests, and it is not what the removed tests covered.

using System;
using System.Collections.Generic;
using System.IO;
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
public class AddressBookTypeAheadTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), $"QM-TypeAheadTests-{Guid.NewGuid():N}");

    [StaFact(Skip = InputTests.SkipReason,
             SkipUnless = nameof(InputTests.Enabled), SkipType = typeof(InputTests))]
    public void ContactList_TypingFirstLetter_SelectsMatchingContact()
    {
        EnsureApplication();
        var (_, window, dir) = BuildWindowWithContacts();
        try
        {
            var list = window.FindName("ContactList") as ListView;
            Assert.NotNull(list);
            list!.Focus();
            DoEvents();

            SendText(list, "b");
            DoEvents();

            var selected = list.SelectedItem as ContactModel;
            Assert.NotNull(selected);
            Assert.Equal("Bob Baker", selected!.DisplayName);
        }
        finally
        {
            window.Close();
            DeleteDir(dir);
        }
    }

    [StaFact(Skip = InputTests.SkipReason,
             SkipUnless = nameof(InputTests.Enabled), SkipType = typeof(InputTests))]
    public void ContactList_NamelessContact_IsReachableByAddress()
    {
        // Prior-recipient contacts often have no display name. TypeAheadText falls
        // back to the address so they are still reachable by typing.
        EnsureApplication();
        var (_, window, dir) = BuildWindowWithContacts();
        try
        {
            var list = window.FindName("ContactList") as ListView;
            Assert.NotNull(list);
            list!.Focus();
            DoEvents();

            SendText(list, "z");
            DoEvents();

            var selected = list.SelectedItem as ContactModel;
            Assert.NotNull(selected);
            Assert.Equal("zeta@example.com", selected!.EmailAddress);
            Assert.Equal(string.Empty, selected.DisplayName);
        }
        finally
        {
            window.Close();
            DeleteDir(dir);
        }
    }

    [StaFact(Skip = InputTests.SkipReason,
             SkipUnless = nameof(InputTests.Enabled), SkipType = typeof(InputTests))]
    public void GroupsList_TypingFirstLetter_SelectsMatchingGroup()
    {
        EnsureApplication();
        var (_, window, dir) = BuildWindowWithContacts();
        try
        {
            var tabs = FindFirstVisualChild<TabControl>(window);
            Assert.NotNull(tabs);
            tabs!.SelectedIndex = 1;
            DoEvents();
            window.UpdateLayout();

            var list = window.FindName("GroupsList") as ListView;
            Assert.NotNull(list);
            list!.Focus();
            DoEvents();

            SendText(list, "w");
            DoEvents();

            var selected = list.SelectedItem as GroupModel;
            Assert.NotNull(selected);
            Assert.Equal("Work", selected!.Name);
        }
        finally
        {
            window.Close();
            DeleteDir(dir);
        }
    }

    [Fact]
    public void TypeAheadText_PrefersName_FallsBackToAddress()
    {
        Assert.Equal("Bob Baker",
            new ContactModel { DisplayName = "Bob Baker", EmailAddress = "bob@example.com" }.TypeAheadText);
        Assert.Equal("zeta@example.com",
            new ContactModel { DisplayName = "", EmailAddress = "zeta@example.com" }.TypeAheadText);
        Assert.Equal("zeta@example.com",
            new ContactModel { DisplayName = "   ", EmailAddress = "zeta@example.com" }.TypeAheadText);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static (AddressBookViewModel vm, AddressBookWindow window, string dir) BuildWindowWithContacts()
    {
        var dir     = TempDir();
        var profile = new ProfileContext(dir);
        var svc     = new ContactService(profile);

        // Insertion order is deliberately not alphabetical: type-ahead must find the
        // match wherever it sits in the list, not just when the list happens to be sorted.
        foreach (var (name, email) in new[]
                 {
                     ("Alice Adams",  "alice@example.com"),
                     ("Bob Baker",    "bob@example.com"),
                     ("Brenda Cole",  "brenda@example.com"),
                     ("",             "zeta@example.com"),
                 })
        {
            svc.UpsertContactAsync(new ContactModel { DisplayName = name, EmailAddress = email })
               .GetAwaiter().GetResult();
        }
        svc.CreateGroupAsync("Family").GetAwaiter().GetResult();
        svc.CreateGroupAsync("Work").GetAwaiter().GetResult();

        var vm     = new AddressBookViewModel(svc);
        var window = new AddressBookWindow(vm);
        vm.LoadAsync().GetAwaiter().GetResult();
        window.Show();
        window.UpdateLayout();
        DoEvents();
        return (vm, window, dir);
    }

    /// <summary>
    /// Raises a real TextInput event on the list, which is what a keystroke produces
    /// once WPF has translated it — the same event ItemsControl's class handler uses
    /// to drive TextSearch.
    /// </summary>
    private static void SendText(UIElement target, string text)
    {
        var composition = new TextComposition(InputManager.Current, target, text);
        target.RaiseEvent(new TextCompositionEventArgs(
            InputManager.Current.PrimaryKeyboardDevice, composition)
        {
            RoutedEvent = UIElement.TextInputEvent,
        });
    }

    private static void DeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
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
