// Regression and behaviour tests for GrabAddressesDialog.
//
// Two bugs these tests guard against:
//
//   1. Focus ordering: FocusFirstAddress() was called *after* awaiting
//      LoadGroupsAsync() in the Loaded handler, so the dialog appeared
//      with no focused control. ShowDialog() blocking the owner made the
//      whole app look frozen. The fix moves FocusFirstAddress() before
//      the await; this test fails if that order is reversed.
//
//   2. Tab cycling: TabNavigation was "Contained", so Tab was permanently
//      trapped cycling through every address checkbox. The fix changes it
//      to "Once". This test fails if the mode reverts to Contained.

using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

[Collection("WpfTests")]
public class GrabAddressesDialogTests
{
    private static readonly System.Collections.Generic.List<(string Name, string Address)> TwoAddresses =
        [("Alice Smith", "alice@example.com"), ("Bob Jones", "bob@example.com")];

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), $"QM-GrabAddrTests-{Guid.NewGuid():N}");

    private static void DeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    // ── Focus ─────────────────────────────────────────────────────────────────

    [StaFact]
    public void OpenDialog_FocusesFirstAddressCheckbox()
    {
        // Regression for the async ordering bug: FocusFirstAddress() must
        // run before the LoadGroupsAsync() await, not after. When it ran
        // after, the dialog appeared with no focused control and the app
        // looked frozen because ShowDialog() disabled the owner.
        EnsureApplication();
        var dialog = new GrabAddressesDialog(TwoAddresses, new StubContactService());
        try
        {
            dialog.Show();
            dialog.UpdateLayout();
            DoEvents();

            var focused = Keyboard.FocusedElement as CheckBox;
            Assert.NotNull(focused);
            Assert.Contains("alice@example.com", focused!.Content?.ToString() ?? "");
        }
        finally { dialog.Close(); }
    }

    // ── Tab navigation ────────────────────────────────────────────────────────

    [StaFact]
    public void Tab_FromAddressList_MovesToAddToGroupCheckbox()
    {
        // Regression for TabNavigation="Contained": Tab was permanently
        // trapped cycling through every address checkbox. With "Once", Tab
        // exits the list on the next press and lands on "Add to group".
        //
        // MoveFocus(Next) is what WPF's input manager calls for Tab and is
        // the correct way to exercise TabNavigation in tests — RaiseEvent
        // with KeyDownEvent does not go through the full navigation pipeline.
        EnsureApplication();
        var dialog = new GrabAddressesDialog(TwoAddresses, new StubContactService());
        try
        {
            dialog.Show();
            dialog.UpdateLayout();
            DoEvents();

            // Confirm we start on the first address checkbox.
            Assert.IsType<CheckBox>(Keyboard.FocusedElement);

            var moved = (Keyboard.FocusedElement as UIElement)
                ?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Assert.True(moved, "MoveFocus returned false — no next tab stop found.");
            DoEvents();

            var addToGroup = dialog.FindName("AddToGroupCheckBox") as CheckBox;
            Assert.NotNull(addToGroup);
            Assert.Same(addToGroup, Keyboard.FocusedElement);
        }
        finally { dialog.Close(); }
    }

    // ── Group combo state ─────────────────────────────────────────────────────

    [StaFact]
    public void GroupCombo_IsDisabled_Initially()
    {
        // "Add to group" starts unchecked so the group picker must not be
        // reachable by Tab until the user opts in.
        EnsureApplication();
        var dialog = new GrabAddressesDialog(TwoAddresses, new StubContactService());
        try
        {
            dialog.Show();
            dialog.UpdateLayout();

            var combo = dialog.FindName("GroupComboBox") as ComboBox;
            Assert.NotNull(combo);
            Assert.False(combo!.IsEnabled);
        }
        finally { dialog.Close(); }
    }

    [StaFact]
    public void CheckingAddToGroup_EnablesGroupCombo()
    {
        EnsureApplication();
        var dialog = new GrabAddressesDialog(TwoAddresses, new StubContactService());
        try
        {
            dialog.Show();
            dialog.UpdateLayout();

            var checkBox = dialog.FindName("AddToGroupCheckBox") as CheckBox;
            var combo    = dialog.FindName("GroupComboBox") as ComboBox;
            Assert.NotNull(checkBox);
            Assert.NotNull(combo);

            checkBox!.IsChecked = true;
            dialog.UpdateLayout();

            Assert.True(combo!.IsEnabled);
        }
        finally { dialog.Close(); }
    }

    [StaFact]
    public void UncheckingAddToGroup_DisablesGroupCombo()
    {
        EnsureApplication();
        var dialog = new GrabAddressesDialog(TwoAddresses, new StubContactService());
        try
        {
            dialog.Show();
            dialog.UpdateLayout();

            var checkBox = dialog.FindName("AddToGroupCheckBox") as CheckBox;
            var combo    = dialog.FindName("GroupComboBox") as ComboBox;
            Assert.NotNull(checkBox);
            Assert.NotNull(combo);

            checkBox!.IsChecked = true;
            dialog.UpdateLayout();
            checkBox.IsChecked = false;
            dialog.UpdateLayout();

            Assert.False(combo!.IsEnabled);
        }
        finally { dialog.Close(); }
    }

    // ── New group name row visibility ─────────────────────────────────────────

    [StaFact]
    public void NewGroupNameRow_IsHidden_Initially()
    {
        EnsureApplication();
        var dialog = new GrabAddressesDialog(TwoAddresses, new StubContactService());
        try
        {
            dialog.Show();
            dialog.UpdateLayout();
            DoEvents();

            var label  = dialog.FindName("NewGroupNameLabel") as TextBlock;
            var textBox = dialog.FindName("NewGroupNameBox") as TextBox;
            Assert.NotNull(label);
            Assert.NotNull(textBox);
            Assert.Equal(Visibility.Collapsed, label!.Visibility);
            Assert.Equal(Visibility.Collapsed, textBox!.Visibility);
        }
        finally { dialog.Close(); }
    }

    [StaFact]
    public void NewGroupNameRow_BecomesVisible_WhenCreateNewGroupSelected()
    {
        // With no existing groups the only combo item is "Create new group".
        // Checking "Add to group" should make the name row appear.
        EnsureApplication();
        var dialog = new GrabAddressesDialog(TwoAddresses, new StubContactService());
        try
        {
            dialog.Show();
            dialog.UpdateLayout();
            DoEvents(); // let async group load settle — combo now has "Create new group"

            var checkBox = dialog.FindName("AddToGroupCheckBox") as CheckBox;
            var label    = dialog.FindName("NewGroupNameLabel") as TextBlock;
            var textBox  = dialog.FindName("NewGroupNameBox") as TextBox;
            Assert.NotNull(checkBox);
            Assert.NotNull(label);
            Assert.NotNull(textBox);

            checkBox!.IsChecked = true;
            dialog.UpdateLayout();

            Assert.Equal(Visibility.Visible, label!.Visibility);
            Assert.Equal(Visibility.Visible, textBox!.Visibility);
        }
        finally { dialog.Close(); }
    }

    [StaFact]
    public void NewGroupNameRow_IsHidden_WhenExistingGroupSelected()
    {
        // With an existing group present, that group is selected by default
        // in the combo (it comes before "Create new group"). Checking "Add
        // to group" should leave the name row hidden.
        EnsureApplication();
        var dir = TempDir();
        var profile = new ProfileContext(dir);
        var svc = new ContactService(profile);
        svc.CreateGroupAsync("Test Group").GetAwaiter().GetResult();
        var dialog = new GrabAddressesDialog(TwoAddresses, svc);
        try
        {
            dialog.Show();
            dialog.UpdateLayout();
            DoEvents();

            var checkBox = dialog.FindName("AddToGroupCheckBox") as CheckBox;
            var label    = dialog.FindName("NewGroupNameLabel") as TextBlock;
            var textBox  = dialog.FindName("NewGroupNameBox") as TextBox;
            Assert.NotNull(checkBox);
            Assert.NotNull(label);
            Assert.NotNull(textBox);

            // Check "Add to group" — existing group "Test Group" is selected
            checkBox!.IsChecked = true;
            dialog.UpdateLayout();

            Assert.Equal(Visibility.Collapsed, label!.Visibility);
            Assert.Equal(Visibility.Collapsed, textBox!.Visibility);
        }
        finally
        {
            dialog.Close();
            DeleteDir(dir);
        }
    }

    [StaFact]
    public void NewGroupNameRow_Hides_WhenSwitchingFromCreateNewToExistingGroup()
    {
        // If the user selects "Create new group" (name row appears) then
        // switches to an existing group, the name row must disappear again.
        EnsureApplication();
        var dir = TempDir();
        var profile = new ProfileContext(dir);
        var svc = new ContactService(profile);
        svc.CreateGroupAsync("Test Group").GetAwaiter().GetResult();
        var dialog = new GrabAddressesDialog(TwoAddresses, svc);
        try
        {
            dialog.Show();
            dialog.UpdateLayout();
            DoEvents();

            var checkBox = dialog.FindName("AddToGroupCheckBox") as CheckBox;
            var combo    = dialog.FindName("GroupComboBox") as ComboBox;
            var label    = dialog.FindName("NewGroupNameLabel") as TextBlock;
            var textBox  = dialog.FindName("NewGroupNameBox") as TextBox;
            Assert.NotNull(checkBox);
            Assert.NotNull(combo);
            Assert.NotNull(label);
            Assert.NotNull(textBox);

            checkBox!.IsChecked = true;
            dialog.UpdateLayout();

            // Last item is "Create new group" — select it
            combo!.SelectedIndex = combo.Items.Count - 1;
            dialog.UpdateLayout();
            Assert.Equal(Visibility.Visible, label!.Visibility);

            // Switch back to the existing group (index 0)
            combo.SelectedIndex = 0;
            dialog.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, label!.Visibility);
            Assert.Equal(Visibility.Collapsed, textBox!.Visibility);
        }
        finally
        {
            dialog.Close();
            DeleteDir(dir);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

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

    private static void DoEvents()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
}
