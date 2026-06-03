using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class AddressBookWindow : Window
{
    private readonly AddressBookViewModel _vm;
    private readonly CommandRegistry _registry = new();

    public AddressBookWindow(AddressBookViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;

        // Register the address-book-specific commands.  These are per-window so
        // the main window's registry isn't polluted with commands that are only
        // meaningful while the address book is open.  The palette inside the
        // address book picks them up automatically.
        RegisterCommands();

        Loaded += async (_, _) =>
        {
            // Subscribe BEFORE LoadAsync so announcements and confirmations
            // raised during the first refresh are captured. (The address book
            // is non-modal, so a confirmation dialog shown during load would
            // block the load until dismissed — this matches the existing
            // single-shot behavior of the contact-delete flow.)
            vm.AnnouncementRequested += OnAnnouncement;
            vm.ConfirmRequested     += OnConfirm;
            await vm.LoadAsync();
            SearchBox.Focus();
        };
        Closed += (_, _) =>
        {
            // Always pair += with -= so the VM can be GC'd if the window is
            // short-lived (e.g. unit tests).
            vm.AnnouncementRequested -= OnAnnouncement;
            vm.ConfirmRequested     -= OnConfirm;
        };

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.SelectedContact))
            {
                if (vm.SelectedContact != null)
                {
                    vm.NewName = vm.SelectedContact.DisplayName;
                    vm.NewEmail = vm.SelectedContact.EmailAddress;
                }
                else
                {
                    vm.NewName = string.Empty;
                    vm.NewEmail = string.Empty;
                }
            }
        };
    }

    private void RegisterCommands()
    {
        // The five group-related commands are registered here so they appear in
        // the address book's own command palette (Ctrl+Shift+P) and can be
        // rebound from the Settings dialog.  They follow the same pattern as
        // main-window commands in MainWindow.OnLoaded.
        _registry.Register(new CommandDefinition(
            id: "contacts.createGroup", category: "Contacts", title: "New Group",
            defaultKey: Key.N, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift,
            execute: () => _vm.CreateGroupCommand.Execute(null)));

        _registry.Register(new CommandDefinition(
            id: "contacts.deleteGroup", category: "Contacts", title: "Delete Group",
            defaultKey: Key.Delete, defaultModifiers: ModifierKeys.None,
            execute: () => _vm.DeleteGroupCommand.Execute(null),
            isAvailable: () => _vm.HasSelectedGroup));

        _registry.Register(new CommandDefinition(
            id: "contacts.renameGroup", category: "Contacts", title: "Rename Group",
            defaultKey: Key.F2, defaultModifiers: ModifierKeys.None,
            execute: () => _vm.RenameGroupCommand.Execute(null),
            isAvailable: () => _vm.HasSelectedGroup));

        _registry.Register(new CommandDefinition(
            id: "contacts.focusGroupsPane", category: "Contacts", title: "Focus Groups Pane",
            defaultKey: Key.G, defaultModifiers: ModifierKeys.Control,
            execute: () => { MainTabs.SelectedIndex = 1; GroupsList.Focus(); }));

        _registry.Register(new CommandDefinition(
            id: "contacts.manageGroups", category: "Contacts", title: "Manage Groups…",
            defaultKey: Key.M, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift,
            execute: OpenGroupManager));
    }

    // Opens the standalone Group Manager dialog using the same IContactService
    // the address book is using (so the shared _loadLock is honored).
    private void OpenGroupManager()
    {
        var groupVm = new GroupManagerViewModel(_vm.ContactService);
        var dialog  = new GroupManagerWindow(groupVm) { Owner = this };
        dialog.ShowDialog();
        // Refresh the address book so any edits made in the dialog are visible.
        // LoadAsync is safe to call multiple times.
        _ = _vm.LoadAsync();
    }

    // ── Event sinks ──────────────────────────────────────────────────────────

    private void OnAnnouncement(string text, AnnouncementCategory category) =>
        AccessibilityHelper.Announce(this, text, category: category);

    private Task<bool> OnConfirm(string title, string body) =>
        Task.FromResult(
            MessageBox.Show(this, body, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            == MessageBoxResult.Yes);

    // ── Keyboard wiring ──────────────────────────────────────────────────────

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Framework-level shortcut. The user-facing Ctrl+G / Delete / F2 are
        // handled in the relevant control's PreviewKeyDown so they only fire
        // when that control has focus. Registered shortcuts are dispatched via
        // the per-window CommandRegistry above.
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+P — open the address book's own command palette. Hardcoded
        // (like the main window) because the palette is the entry point and
        // cannot itself be dispatched from the registry.
        if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            OpenCommandPalette();
            e.Handled = true;
            return;
        }

        // Dispatch any registered command whose key/modifiers match.
        var key = e.Key == Key.System ? e.SystemKey
            : e.Key == Key.ImeProcessed ? e.ImeProcessedKey
            : e.Key;
        var cmd = _registry.FindByGesture(key, Keyboard.Modifiers);
        if (cmd != null && (cmd.IsAvailable?.Invoke() ?? true))
        {
            cmd.Execute();
            e.Handled = true;
        }
    }

    private void OpenCommandPalette()
    {
        var previousFocus = Keyboard.FocusedElement as IInputElement;
        var palette = new CommandPaletteWindow(_registry) { Owner = this };
        palette.ShowDialog();
        (previousFocus ?? SearchBox).Focus();
    }

    private async void ContactList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+G switches to the Groups tab. The registered command in
        // _registry.FindByGesture runs at the window level, but ListView
        // handles Ctrl+G as its built-in "go to first row" gesture in a
        // tunneling handler, so it never reaches the registry. Handle it
        // here so it works regardless of which list has focus.
        if (e.Key == Key.G && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            FocusGroupsPane();
            return;
        }

        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            ContactList.SelectAll();
            OnAnnouncement(
                $"{ContactList.SelectedItems.Count} contact{(ContactList.SelectedItems.Count == 1 ? "" : "s")} selected.",
                AnnouncementCategory.Result);
            return;
        }

        if (e.Key == Key.Delete && ContactList.SelectedItems.Count > 0)
        {
            await DeleteSelectedContactsAsync();
            e.Handled = true;
        }
    }

    private void NewEmailBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            _vm.AddContactCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void NewGroupNameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            _vm.NewGroupName = NewGroupNameBox.Text;
            if (_vm.SelectedGroup is not null)
                _vm.RenameGroupCommand.Execute(null);
            else
                _vm.CreateGroupCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void GroupsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+G toggles back to the Contacts tab and focuses the search box.
        if (e.Key == Key.G && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            MainTabs.SelectedIndex = 0;
            SearchBox.Focus();
            OnAnnouncement("Contacts tab", AnnouncementCategory.Status);
            return;
        }

        if (e.Key == Key.F2)
        {
            // F2 moves focus to the rename textbox and pre-selects the name.
            NewGroupNameBox.Focus();
            NewGroupNameBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _vm.SelectedGroup is not null)
        {
            _vm.DeleteGroupCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void GroupMembersList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.G && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            FocusGroupsPane();
            return;
        }

        if (e.Key == Key.Delete && GroupMembersList.SelectedItem is ContactModel c)
        {
            _vm.RemoveContactFromGroupCommand.Execute(c);
            e.Handled = true;
        }
    }

    private void FocusGroupsPane()
    {
        MainTabs.SelectedIndex = 1;
        GroupsList.Focus();
        OnAnnouncement("Groups tab", AnnouncementCategory.Status);
    }

    private void NewNameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // If user edits the name field and a contact is selected, deselect it to allow adding new contact
        if (_vm.SelectedContact != null && NewNameBox.Text != _vm.SelectedContact.DisplayName)
            _vm.SelectedContact = null;
    }

    private void NewEmailBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // If user edits the email field and a contact is selected, deselect it to allow adding new contact
        if (_vm.SelectedContact != null && NewEmailBox.Text != _vm.SelectedContact.EmailAddress)
            _vm.SelectedContact = null;
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (ContactList.SelectedItems.Count == 0) return;
        await DeleteSelectedContactsAsync();
    }

    private async Task DeleteSelectedContactsAsync()
    {
        var selected = ContactList.SelectedItems.OfType<ContactModel>().ToList();
        if (selected.Count == 0) return;

        var prompt = selected.Count == 1
            ? $"Delete {selected[0].Display}?"
            : $"Delete {selected.Count} contacts?";
        var result = MessageBox.Show(prompt, "Confirm Delete",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
            await _vm.DeleteMultipleAsync(selected);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
