// Regression tests for first-letter (type-ahead) navigation in the Address Book
// lists — issue #371.
//
// The contact list uses an ItemTemplate whose root is a Grid, so WPF's TextSearch
// has no text to match unless the list declares TextSearch.TextPath explicitly.
// Without it, typing a letter with focus on the list does nothing at all. These
// tests drive real TextInput events through the WPF input pipeline so a regression
// (a removed TextPath, or a renamed source property) fails here rather than in the
// running app.

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

    [StaFact]
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

    [StaFact]
    public void ContactList_RepeatingSameLetter_CyclesThroughMatches()
    {
        // Two contacts start with "B". Pressing B twice should land on the second
        // one — WPF treats a repeat of the same character as "next match", not as
        // a two-character prefix.
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
            SendText(list, "b");
            DoEvents();

            var selected = list.SelectedItem as ContactModel;
            Assert.NotNull(selected);
            Assert.Equal("Brenda Cole", selected!.DisplayName);
        }
        finally
        {
            window.Close();
            DeleteDir(dir);
        }
    }

    [StaFact]
    public void ContactList_TypingMultipleLetters_MatchesLongerPrefix()
    {
        // Typed characters accumulate within the type-ahead timeout, so "br"
        // should skip past "Bob Baker" and land on "Brenda Cole".
        EnsureApplication();
        var (_, window, dir) = BuildWindowWithContacts();
        try
        {
            var list = window.FindName("ContactList") as ListView;
            Assert.NotNull(list);
            list!.Focus();
            DoEvents();

            SendText(list, "b");
            SendText(list, "r");
            DoEvents();

            var selected = list.SelectedItem as ContactModel;
            Assert.NotNull(selected);
            Assert.Equal("Brenda Cole", selected!.DisplayName);
        }
        finally
        {
            window.Close();
            DeleteDir(dir);
        }
    }

    [StaFact]
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

    [StaFact]
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
