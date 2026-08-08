using System;
using System.Windows;
using System.Windows.Input;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Add-shared-mailbox dialog (#31, spec §6/§7). A leaf single-form window (Address → Parent →
/// Add/Cancel), so it deliberately has no F6 ring and no command palette — there is nothing to jump
/// between and a palette would only duplicate the two buttons (same New-Window-Checklist exception as
/// <see cref="ServerRuleEditorWindow"/>). It has an editable TextBox and can open over the main
/// window's live WebView2, so it is shown modeless (<c>Show()</c>) with Escape/Cancel wired explicitly
/// — the GrabAddresses deadlock lesson. Focus lands on Address; the owner (Account Manager) restores
/// focus to the "Add shared…" button it was launched from when this window closes.
/// </summary>
public partial class AddSharedMailboxWindow : Window
{
    private readonly AddSharedMailboxViewModel _vm;

    public AddSharedMailboxWindow(AddSharedMailboxViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;

        _vm.CancelRequested += OnCancelRequested;
        _vm.SharedMailboxAdded += OnSharedMailboxAdded;
        _vm.AnnouncementRequested += OnAnnouncementRequested;

        Loaded += (_, _) =>
        {
            AddressBox.Focus();
            Keyboard.Focus(AddressBox);
        };
    }

    private void OnCancelRequested() => Close();

    // Close on a successful add; the owner (Account Manager) commits and persists via its own
    // subscription to the same event.
    private void OnSharedMailboxAdded(AccountModel _) => Close();

    private void OnAnnouncementRequested(string text, AnnouncementCategory category)
        => AccessibilityHelper.Announce(this, text, category: category);

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;

        // Modeless: no DialogResult, so Escape doesn't auto-close. Wire it, but step aside when a
        // ComboBox dropdown is open so it can consume Escape itself.
        if (e.Key == Key.Escape && Keyboard.FocusedElement is not System.Windows.Controls.ComboBox { IsDropDownOpen: true })
        {
            Close();
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.CancelRequested -= OnCancelRequested;
        _vm.SharedMailboxAdded -= OnSharedMailboxAdded;
        _vm.AnnouncementRequested -= OnAnnouncementRequested;
        base.OnClosed(e);
    }
}
