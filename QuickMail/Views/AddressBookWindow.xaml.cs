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
        // The group-related commands are registered here so they appear in the
        // address book's own command palette (Ctrl+Shift+P) and can be rebound
        // from the Settings dialog.  They follow the same pattern as main-window
        // commands in MainWindow.OnLoaded.

        // Ctrl+Shift+N: switch to Groups tab and show the name entry area in create mode.
        _registry.Register(new CommandDefinition(
            id: "contacts.createGroup", category: "Contacts", title: "New Group",
            defaultKey: Key.N, defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift,
            execute: () =>
            {
                MainTabs.SelectedIndex = 1;
                _vm.SelectedGroup = null;  // ensure create-new mode
                ShowGroupNameEntry(null);
            }));

        // Delete deletes a group only when the Groups list itself has focus.
        // When focus is in the group-members list, Delete there removes a member
        // and the GroupMembersList_PreviewKeyDown handler takes care of it.
        _registry.Register(new CommandDefinition(
            id: "contacts.deleteGroup", category: "Contacts", title: "Delete Group",
            defaultKey: Key.Delete, defaultModifiers: ModifierKeys.None,
            execute: () => _vm.DeleteGroupCommand.Execute(null),
            isAvailable: () => _vm.HasSelectedGroup && GroupsList.IsKeyboardFocusWithin));

        // F2 / Rename: show the name entry area pre-filled with the current name.
        _registry.Register(new CommandDefinition(
            id: "contacts.renameGroup", category: "Contacts", title: "Rename Group",
            defaultKey: Key.F2, defaultModifiers: ModifierKeys.None,
            execute: () =>
            {
                if (_vm.SelectedGroup is { } g)
                    ShowGroupNameEntry(g.Name);
            },
            isAvailable: () => _vm.HasSelectedGroup));

        _registry.Register(new CommandDefinition(
            id: "contacts.focusGroupsPane", category: "Contacts", title: "Focus Groups Pane",
            defaultKey: Key.G, defaultModifiers: ModifierKeys.Control,
            execute: FocusGroupsPane));

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

    private void ManageGroupsButton_Click(object sender, RoutedEventArgs e) => OpenGroupManager();

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
            // If the name entry panel is open, Escape dismisses it without
            // closing the whole window. A second Escape then closes.
            if (GroupNameEntryPanel.Visibility == Visibility.Visible)
            {
                HideGroupNameEntry();
                e.Handled = true;
                return;
            }
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

        // Alt+T / Alt+C / Alt+B — insert into the chosen field. On the Contacts
        // tab the button access keys (_To/_Cc/_Bcc) handle this automatically.
        // On the Groups tab those access keys are not present, so we intercept
        // here and route to the group-insert commands instead. This means the
        // shortcuts work regardless of which tab is active.
        if (e.Key == Key.System && Keyboard.Modifiers == ModifierKeys.Alt
            && MainTabs.SelectedIndex == 1)
        {
            if (e.SystemKey == Key.T && _vm.HasSelectedGroup)
            {
                _vm.AddGroupToToCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (e.SystemKey == Key.C && _vm.HasSelectedGroup)
            {
                _vm.AddGroupToCcCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (e.SystemKey == Key.B && _vm.HasSelectedGroup)
            {
                _vm.AddGroupToBccCommand.Execute(null);
                e.Handled = true;
                return;
            }
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

    private void ContactList_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        // Suppress the context menu when no contact is selected.
        if (_vm.SelectedContact is null)
        {
            e.Handled = true;
            return;
        }

        var menu = ContactList.ContextMenu!;
        menu.Items.Clear();

        if (_vm.Groups.Count == 0)
        {
            menu.Items.Add(new System.Windows.Controls.MenuItem
            {
                Header = "No groups — create one on the Groups tab",
                IsEnabled = false
            });
        }
        else
        {
            foreach (var group in _vm.Groups)
            {
                var item = new System.Windows.Controls.MenuItem { Header = $"Add to \"{group.Name}\"" };
                var g = group;
                item.Click += (_, _) => _vm.AddContactToGroupCommand.Execute(g);
                menu.Items.Add(item);
            }
        }
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
            // NewGroupName is kept in sync by the TwoWay binding, so the VM
            // already has the current text. Run the command, then hide the entry.
            if (_vm.SelectedGroup is not null)
                _vm.RenameGroupCommand.Execute(null);
            else
                _vm.CreateGroupCommand.Execute(null);
            HideGroupNameEntry();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HideGroupNameEntry();
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

        if (e.Key == Key.F2 && _vm.SelectedGroup is { } g)
        {
            ShowGroupNameEntry(g.Name);
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
        // If the name entry panel was open, dismiss it before switching.
        if (GroupNameEntryPanel.Visibility == Visibility.Visible)
        {
            GroupNameEntryPanel.Visibility = Visibility.Collapsed;
            _vm.NewGroupName = string.Empty;
        }
        MainTabs.SelectedIndex = 1;
        if (_vm.Groups.Count == 0)
            NewGroupButton.Focus();
        else
            GroupsList.Focus();
        OnAnnouncement("Groups tab", AnnouncementCategory.Status);
    }

    // ── Group name entry helpers ─────────────────────────────────────────────

    /// <summary>
    /// Shows the name entry panel and focuses the text box. If <paramref name="prefill"/>
    /// is supplied the text is pre-filled and fully selected (rename mode); otherwise
    /// the box is cleared (create mode).
    /// </summary>
    private void ShowGroupNameEntry(string? prefill)
    {
        GroupNameEntryPanel.Visibility = Visibility.Visible;
        if (prefill is not null)
        {
            _vm.NewGroupName = prefill;
            NewGroupNameBox.SelectAll();
        }
        else
        {
            _vm.NewGroupName = string.Empty;
        }
        NewGroupNameBox.Focus();
        OnAnnouncement(
            prefill is null
                ? "Type a new group name and press Enter. Press Escape to cancel."
                : $"Rename \"{prefill}\". Type a new name and press Enter. Press Escape to cancel.",
            AnnouncementCategory.Hint);
    }

    private void HideGroupNameEntry()
    {
        GroupNameEntryPanel.Visibility = Visibility.Collapsed;
        _vm.NewGroupName = string.Empty;
        GroupsList.Focus();
    }

    private void NewGroupButton_Click(object sender, RoutedEventArgs e)
    {
        _vm.SelectedGroup = null;  // ensure create-new mode
        ShowGroupNameEntry(null);
    }

    private void RenameGroupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedGroup is { } g)
            ShowGroupNameEntry(g.Name);
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
