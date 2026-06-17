// Behaviour tests for GrabAddressesDialog.
//
// The dialog is shown modeless (Show(), not ShowDialog()). Showing it modally
// ran a nested message loop that hard-deadlocked the UI thread the moment an
// editable text field received focus with a screen reader active. Because it
// is modeless, Escape and Cancel no longer auto-close (that is ShowDialog-only
// behaviour) and are wired to Close() explicitly — Escape_ClosesDialog guards
// that.
//
// The group section uses a static layout: the "Add to group" checkbox, the
// group combo, and the "New group name" box are always present, enabled, and
// visible. The group is simply ignored at Save time when "Add to group" is
// unchecked. These tests assert that static behaviour and exercise the Save
// logic (create-new vs existing group, gating on the checkbox, empty-name
// validation).

using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
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
        // Regression for the async ordering bug: FocusFirstAddress() must run
        // before the LoadGroupsAsync() await, not after — otherwise the dialog
        // appears with no focused control.
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
        // Regression for TabNavigation="Contained": Tab was permanently trapped
        // cycling through every address checkbox. With "Once", Tab exits the
        // list on the next press and lands on "Add to group".
        EnsureApplication();
        var dialog = new GrabAddressesDialog(TwoAddresses, new StubContactService());
        try
        {
            dialog.Show();
            dialog.UpdateLayout();
            DoEvents();

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

    // ── Static group section ────────────────────────────────────────────────────

    [StaFact]
    public void GroupControls_AreEnabledAndVisible_OnOpen()
    {
        // Static design: every group control is always present, enabled, and
        // visible — no dynamic IsEnabled/Visibility toggling. "Add to group"
        // starts unchecked (issue #100); the group is ignored at Save time
        // when it is unchecked.
        EnsureApplication();
        var dialog = new GrabAddressesDialog(TwoAddresses, new StubContactService());
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

            Assert.NotEqual(true, checkBox!.IsChecked);   // unchecked by default
            Assert.True(combo!.IsEnabled);
            Assert.True(textBox!.IsEnabled);
            Assert.Equal(Visibility.Visible, combo.Visibility);
            Assert.Equal(Visibility.Visible, label!.Visibility);
            Assert.Equal(Visibility.Visible, textBox.Visibility);
        }
        finally { dialog.Close(); }
    }

    [StaFact]
    public void GroupCombo_OffersCreateNewGroup()
    {
        EnsureApplication();
        var dialog = new GrabAddressesDialog(TwoAddresses, new StubContactService());
        try
        {
            dialog.Show();
            dialog.UpdateLayout();
            DoEvents();

            var combo = dialog.FindName("GroupComboBox") as ComboBox;
            Assert.NotNull(combo);
            Assert.NotEmpty(combo!.Items);
            Assert.Contains("Create new group", combo.Items[^1]?.ToString() ?? "");
        }
        finally { dialog.Close(); }
    }

    // ── Modeless close ──────────────────────────────────────────────────────────

    [StaFact]
    public void Escape_ClosesDialog()
    {
        // Modeless windows do not auto-close on Escape (that is ShowDialog-only);
        // the dialog wires Escape to Close() itself.
        EnsureApplication();
        var dialog = new GrabAddressesDialog(TwoAddresses, new StubContactService());
        var closed = false;
        dialog.Closed += (_, _) => closed = true;
        dialog.Show();
        dialog.UpdateLayout();
        DoEvents();

        var source = PresentationSource.FromVisual(dialog);
        Assert.NotNull(source);
        dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source!, 0, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        });
        DoEvents();

        Assert.True(closed, "Escape should close the modeless dialog.");
    }

    // ── Save logic ──────────────────────────────────────────────────────────────

    [StaFact]
    public void Save_WithCreateNewGroup_CreatesGroupAndAddsCheckedContacts()
    {
        EnsureApplication();
        var dir = TempDir();
        var svc = new ContactService(new ProfileContext(dir));
        var dialog = new GrabAddressesDialog(TwoAddresses, svc);
        var closed = false;
        dialog.Closed += (_, _) => closed = true;
        try
        {
            dialog.Show();
            dialog.UpdateLayout();
            DoEvents(); // group load settles — "Create new group" is the only item

            var checkBox = dialog.FindName("AddToGroupCheckBox") as CheckBox;
            var combo    = dialog.FindName("GroupComboBox") as ComboBox;
            var textBox  = dialog.FindName("NewGroupNameBox") as TextBox;
            var save     = dialog.FindName("SaveButton") as Button;
            Assert.NotNull(checkBox);
            Assert.NotNull(combo);
            Assert.NotNull(textBox);
            Assert.NotNull(save);

            checkBox!.IsChecked = true;
            combo!.SelectedIndex = combo.Items.Count - 1; // "Create new group"
            textBox!.Text = "Grabbed Group";

            InvokeButton(save!);
            PumpUntil(() => closed);
            Assert.True(closed, "Save should close the dialog when it completes.");

            var grp = svc.LoadAllGroupsAsync().GetAwaiter().GetResult()
                         .FirstOrDefault(g => g.Name == "Grabbed Group");
            Assert.NotNull(grp);
            Assert.Equal(2, grp!.ResolvedMemberCount);
        }
        finally
        {
            if (!closed) dialog.Close();
            DeleteDir(dir);
        }
    }

    [StaFact]
    public void Save_WithoutAddToGroup_SavesContactsAndIgnoresGroupSelection()
    {
        EnsureApplication();
        var dir = TempDir();
        var svc = new ContactService(new ProfileContext(dir));
        var dialog = new GrabAddressesDialog(TwoAddresses, svc);
        var closed = false;
        dialog.Closed += (_, _) => closed = true;
        try
        {
            dialog.Show();
            dialog.UpdateLayout();
            DoEvents();

            var save = dialog.FindName("SaveButton") as Button;
            Assert.NotNull(save);

            // "Add to group" left unchecked — the combo still has a selection,
            // but it must be ignored.
            InvokeButton(save!);
            PumpUntil(() => closed);
            Assert.True(closed);

            var contacts = svc.SearchContactsAsync("").GetAwaiter().GetResult();
            Assert.Equal(2, contacts.Count);
            Assert.Empty(svc.LoadAllGroupsAsync().GetAwaiter().GetResult());
        }
        finally
        {
            if (!closed) dialog.Close();
            DeleteDir(dir);
        }
    }

    [StaFact]
    public void Save_CreateNewGroupWithEmptyName_DoesNotCloseOrCreateGroup()
    {
        EnsureApplication();
        var dir = TempDir();
        var svc = new ContactService(new ProfileContext(dir));
        var dialog = new GrabAddressesDialog(TwoAddresses, svc);
        var closed = false;
        dialog.Closed += (_, _) => closed = true;
        try
        {
            dialog.Show();
            dialog.UpdateLayout();
            DoEvents();

            var checkBox = dialog.FindName("AddToGroupCheckBox") as CheckBox;
            var combo    = dialog.FindName("GroupComboBox") as ComboBox;
            var save     = dialog.FindName("SaveButton") as Button;
            Assert.NotNull(checkBox);
            Assert.NotNull(combo);
            Assert.NotNull(save);

            checkBox!.IsChecked = true;
            combo!.SelectedIndex = combo.Items.Count - 1; // "Create new group", name left blank

            InvokeButton(save!);
            for (int i = 0; i < 10; i++) DoEvents();

            Assert.False(closed, "Empty new-group name should keep the dialog open.");
            Assert.Empty(svc.LoadAllGroupsAsync().GetAwaiter().GetResult());
        }
        finally
        {
            dialog.Close();
            DeleteDir(dir);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static void InvokeButton(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static void PumpUntil(Func<bool> condition, int maxIterations = 50)
    {
        for (int i = 0; i < maxIterations && !condition(); i++)
            DoEvents();
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

    private static void DoEvents()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
}
