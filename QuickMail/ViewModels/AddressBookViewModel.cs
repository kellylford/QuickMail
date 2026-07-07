using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>
/// Backs the Address Book window. Owns the contacts list, the groups list,
/// and the To/Cc/Bcc insert actions. Keeps all business logic out of the
/// view code-behind — view code only handles focus, dialogs, and key wiring.
/// </summary>
public partial class AddressBookViewModel : ObservableObject
{
    private readonly IContactService _contactService;
    private List<ContactModel> _allContacts = [];

    private Action<ContactModel>? _toInsertAction;
    private Action<ContactModel>? _ccInsertAction;
    private Action<ContactModel>? _bccInsertAction;

    /// <summary>
    /// Raised by destructive operations (e.g. delete group) so the View can
    /// show a MessageBox and return the user's choice. The VM never touches
    /// System.Windows types directly.
    /// </summary>
    public event Func<string, string, Task<bool>>? ConfirmRequested;

    /// <summary>
    /// Raised for screen-reader announcements. The View subscribes and calls
    /// <see cref="QuickMail.Views.AccessibilityHelper.Announce"/> so the VM
    /// stays free of System.Windows types. Every call passes a category
    /// argument per the announcement rules in CLAUDE.md.
    /// </summary>
    public event Action<string, AnnouncementCategory>? AnnouncementRequested;

    private void Announce(string text, AnnouncementCategory category) =>
        AnnouncementRequested?.Invoke(text, category);

    public bool HasInsertActions => _toInsertAction != null;

    public void SetInsertActions(
        Action<ContactModel> toAction,
        Action<ContactModel> ccAction,
        Action<ContactModel> bccAction)
    {
        _toInsertAction = toAction;
        _ccInsertAction = ccAction;
        _bccInsertAction = bccAction;
        OnPropertyChanged(nameof(HasInsertActions));
    }

    public AddressBookViewModel(IContactService contactService)
    {
        _contactService = contactService;
        // Exposed so dialogs (e.g. GroupManagerWindow) opened from this address book
        // can re-use the same IContactService instance.
        ContactService = contactService;
    }

    /// <summary>
    /// The IContactService the VM was constructed with. Exposed so dialogs opened
    /// from the address book (e.g. <c>GroupManagerWindow</c>) can share the same
    /// instance — important because the single <c>_loadLock</c> must be shared to
    /// avoid the deadlock risk described in the spec.
    /// </summary>
    public IContactService ContactService { get; }

    public ObservableCollection<ContactModel> FilteredContacts { get; } = [];

    // ── Group state ──────────────────────────────────────────────────────────

    /// <summary>All groups, sorted by LastUsedTicks descending.</summary>
    public ObservableCollection<GroupModel> Groups { get; } = [];

    /// <summary>
    /// Members of the currently selected group, resolved against the live
    /// contact list. Rebuilt whenever SelectedGroup changes (or contacts
    /// change) so the right pane always shows what would actually be inserted.
    /// </summary>
    public ObservableCollection<ContactModel> SelectedGroupMembers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedContact))]
    [NotifyPropertyChangedFor(nameof(CanEditContact))]
    [NotifyPropertyChangedFor(nameof(CanDeleteContact))]
    private ContactModel? _selectedContact;

    public bool HasSelectedContact => SelectedContact != null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedGroup))]
    private GroupModel? _selectedGroup;

    public bool HasSelectedGroup => SelectedGroup is not null;

    [ObservableProperty]
    private string _searchText = string.Empty;

    // ── Contact editing ──────────────────────────────────────────────────────

    [ObservableProperty]
    private string _editName = string.Empty;

    [ObservableProperty]
    private string _editEmail = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContactReadOnly))]
    [NotifyPropertyChangedFor(nameof(CanEditContact))]
    [NotifyPropertyChangedFor(nameof(CanDeleteContact))]
    private bool _isEditingContact;

    private enum ContactEditMode { None, Adding, Editing }
    private ContactEditMode _contactEditMode = ContactEditMode.None;
    private int _editingContactId;

    public bool IsContactReadOnly => !IsEditingContact;
    public bool CanEditContact    => HasSelectedContact && !IsEditingContact;
    public bool CanDeleteContact  => HasSelectedContact && !IsEditingContact;

    [ObservableProperty]
    private string _contactError = string.Empty;

    partial void OnSelectedContactChanged(ContactModel? value)
    {
        if (!IsEditingContact)
        {
            EditName     = value?.DisplayName  ?? string.Empty;
            EditEmail    = value?.EmailAddress ?? string.Empty;
            ContactError = string.Empty;
        }
    }

    // ── Group editing ────────────────────────────────────────────────────────

    /// <summary>
    /// Name typed into the "New group" textbox at the bottom of the Groups
    /// tab. Cleared after a successful create or rename.
    /// </summary>
    [ObservableProperty]
    private string _newGroupName = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplyFilter(value);

    partial void OnSelectedGroupChanged(GroupModel? value) => RebuildSelectedGroupMembers();

    [RelayCommand]
    public async Task LoadAsync()
    {
        _allContacts = await _contactService.LoadAllContactsAsync();
        ApplyFilter(SearchText);
        await ReloadGroupsAsync();
        RebuildSelectedGroupMembers();
    }

    [RelayCommand]
    private void BeginAddContact()
    {
        EditName           = string.Empty;
        EditEmail          = string.Empty;
        ContactError       = string.Empty;
        IsEditingContact   = true;
        _contactEditMode   = ContactEditMode.Adding;
        Announce("Add contact", AnnouncementCategory.Result);
    }

    [RelayCommand]
    private void BeginEditContact()
    {
        if (SelectedContact is not { } c) return;
        _editingContactId = c.Id;
        IsEditingContact  = true;
        _contactEditMode  = ContactEditMode.Editing;
        ContactError      = string.Empty;
        Announce($"Edit {c.DisplayName}", AnnouncementCategory.Result);
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditingContact = false;
        _contactEditMode = ContactEditMode.None;
        if (SelectedContact is { } c)
        {
            EditName  = c.DisplayName;
            EditEmail = c.EmailAddress;
        }
        else
        {
            EditName  = string.Empty;
            EditEmail = string.Empty;
        }
        ContactError = string.Empty;
        Announce("Edit cancelled", AnnouncementCategory.Result);
    }

    [RelayCommand]
    private async Task SaveContactAsync()
    {
        if (string.IsNullOrWhiteSpace(EditEmail))
        {
            ContactError = "Email address required";
            return;
        }

        if (_contactEditMode == ContactEditMode.Adding)
        {
            await _contactService.UpsertContactAsync(new ContactModel
            {
                DisplayName   = EditName.Trim(),
                EmailAddress  = EditEmail.Trim(),
                LastUsedTicks = DateTimeOffset.UtcNow.UtcTicks,
            });
            var addedName = EditName.Trim();
            IsEditingContact = false;
            _contactEditMode = ContactEditMode.None;
            await LoadAsync();
            // Select the newly added contact by email
            var added = _allContacts.FirstOrDefault(c =>
                c.EmailAddress.Equals(EditEmail.Trim(), StringComparison.OrdinalIgnoreCase));
            if (added is not null) SelectedContact = added;
            Announce($"{addedName} added", AnnouncementCategory.Result);
        }
        else if (_contactEditMode == ContactEditMode.Editing)
        {
            var success = await _contactService.UpdateContactAsync(
                _editingContactId,
                EditName.Trim(),
                EditEmail.Trim());

            if (!success)
            {
                ContactError = "Email address is already used by another contact";
                Announce("Email address is already used by another contact", AnnouncementCategory.Result);
                return;
            }

            var updatedName = EditName.Trim();
            IsEditingContact = false;
            _contactEditMode = ContactEditMode.None;
            await LoadAsync();
            // Restore selection by id
            SelectedContact = _allContacts.FirstOrDefault(c => c.Id == _editingContactId);
            Announce($"{updatedName} updated", AnnouncementCategory.Result);
        }
    }

    [RelayCommand]
    private void AddToTo() { if (SelectedContact is { } c) _toInsertAction?.Invoke(c); }

    [RelayCommand]
    private void AddToCc() { if (SelectedContact is { } c) _ccInsertAction?.Invoke(c); }

    [RelayCommand]
    private void AddToBcc() { if (SelectedContact is { } c) _bccInsertAction?.Invoke(c); }

    // ── Group commands ───────────────────────────────────────────────────────

    [RelayCommand]
    private async Task CreateGroupAsync()
    {
        var name = NewGroupName?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        int id;
        try
        {
            id = await _contactService.CreateGroupAsync(name);
        }
        catch (DuplicateGroupNameException)
        {
            Announce($"A group named '{name}' already exists. Choose a different name.", AnnouncementCategory.Result);
            return;
        }
        catch (ArgumentException)
        {
            // Empty / whitespace name. The "New group" text box already
            // prevents this on Enter, but be defensive in case the command
            // is invoked programmatically.
            return;
        }
        NewGroupName = string.Empty;
        await ReloadGroupsAsync();
        var created = Groups.FirstOrDefault(g => g.Id == id);
        if (created is not null) SelectedGroup = created;
        Announce(
            $"Group '{name}' created",
            AnnouncementCategory.Result);
    }

    [RelayCommand]
    private async Task DeleteGroupAsync()
    {
        if (SelectedGroup is not { } g) return;
        var memberCount = g.ResolvedMemberCount;
        var body = memberCount switch
        {
            0 => $"Delete group '{g.Name}'? It is empty.",
            1 => $"Delete group '{g.Name}'? Its 1 member will not be deleted.",
            _ => $"Delete group '{g.Name}'? Its {memberCount} members will not be deleted.",
        };
        var confirmed = await RequestConfirmAsync("Confirm", body);
        if (!confirmed) return;
        await _contactService.DeleteGroupAsync(g.Id);
        var deletedName = g.Name;
        await ReloadGroupsAsync();
        SelectedGroup = null;
        Announce(
            $"Group '{deletedName}' deleted",
            AnnouncementCategory.Result);
    }

    [RelayCommand]
    private async Task RenameGroupAsync()
    {
        if (SelectedGroup is not { } g) return;
        var newName = NewGroupName?.Trim();
        if (string.IsNullOrEmpty(newName) || newName == g.Name) return;
        try
        {
            await _contactService.RenameGroupAsync(g.Id, newName);
        }
        catch (DuplicateGroupNameException)
        {
            Announce($"A group named '{newName}' already exists. Choose a different name.", AnnouncementCategory.Result);
            return;
        }
        NewGroupName = string.Empty;
        await ReloadGroupsAsync();
        var renamed = Groups.FirstOrDefault(x => x.Id == g.Id);
        if (renamed is not null) SelectedGroup = renamed;
        Announce(
            $"Renamed group to '{newName}'",
            AnnouncementCategory.Result);
    }

    [RelayCommand]
    public async Task AddContactToGroupAsync(GroupModel? targetGroup)
    {
        if (SelectedContact is not { } c || targetGroup is null) return;
        await _contactService.AddMemberAsync(targetGroup.Id, c.Id);
        await ReloadGroupsAsync();
        // If the user is adding to the group they currently have selected,
        // refresh the members pane so they see the new member appear.
        if (SelectedGroup?.Id == targetGroup.Id)
        {
            SelectedGroup = Groups.FirstOrDefault(x => x.Id == targetGroup.Id);
            RebuildSelectedGroupMembers();
        }
        Announce(
            $"Added {c.Display} to {targetGroup.Name}",
            AnnouncementCategory.Result);
    }

    [RelayCommand]
    public async Task RemoveContactFromGroupAsync(ContactModel? contact)
    {
        if (SelectedGroup is not { } g || contact is null) return;
        await _contactService.RemoveMemberAsync(g.Id, contact.Id);
        await ReloadGroupsAsync();
        // Preserve selection across the reload.
        SelectedGroup = Groups.FirstOrDefault(x => x.Id == g.Id);
        RebuildSelectedGroupMembers();
        Announce(
            $"Removed {contact.Display} from {g.Name}",
            AnnouncementCategory.Result);
    }

    /// <summary>
    /// Inserts all members of the selected group into the chosen field. The
    /// actual insert happens through the <c>Action&lt;ContactModel&gt;</c>
    /// set by the Compose window via <see cref="SetInsertActions"/>; this
    /// command just iterates the resolved members and calls the action for
    /// each one. The screen reader is told the final count, including any
    /// missing members that were silently skipped.
    /// </summary>
    private void InsertGroup(Action<ContactModel> inserter)
    {
        if (SelectedGroup is not { } g) return;
        var ordered = SelectedGroupMembers
            .OrderByDescending(c => c.LastUsedTicks)
            .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var c in ordered) inserter(c);
        // Fire-and-forget: touch the group so it sorts to the top next time.
        _ = _contactService.TouchGroupAsync(g.Id);
        Announce(
            ordered.Count == 1
                ? $"Inserted 1 address from group '{g.Name}'"
                : $"Inserted {ordered.Count} addresses from group '{g.Name}'",
            AnnouncementCategory.Result);
    }

    [RelayCommand] private void AddGroupToTo()  => InsertGroup(c => _toInsertAction?.Invoke(c));
    [RelayCommand] private void AddGroupToCc()  => InsertGroup(c => _ccInsertAction?.Invoke(c));
    [RelayCommand] private void AddGroupToBcc() => InsertGroup(c => _bccInsertAction?.Invoke(c));

    // ── Bulk contact ops ─────────────────────────────────────────────────────

    public async Task DeleteConfirmedAsync()
    {
        if (SelectedContact is not { } contact) return;
        await _contactService.DeleteContactAsync(contact.Id);
        _allContacts.Remove(contact);
        FilteredContacts.Remove(contact);
        SelectedContact = null;
        // A deleted contact may have been a member of groups. The group
        // MemberContactIds are intentionally left intact on disk (see
        // ContactService.DeleteContactAsync rationale), but the visible
        // ResolvedMemberCount on the right pane needs to drop. Re-fetch.
        await ReloadGroupsAsync();
    }

    public async Task DeleteMultipleAsync(IReadOnlyList<ContactModel> contacts)
    {
        foreach (var contact in contacts)
            await _contactService.DeleteContactAsync(contact.Id);
        foreach (var contact in contacts)
        {
            _allContacts.Remove(contact);
            FilteredContacts.Remove(contact);
        }
        if (contacts.Contains(SelectedContact))
            SelectedContact = null;
        await ReloadGroupsAsync();
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private async Task ReloadGroupsAsync()
    {
        // Use SetProperty via the field directly (CommunityToolkit's MVVMTK0034
        // warning) so the OnSelectedGroupChanged rebuild is suppressed — we'll
        // call RebuildSelectedGroupMembers() explicitly at the end to avoid
        // building the member list twice when Groups are merely refreshed.
        var selectedId = SelectedGroup?.Id;
        Groups.Clear();
        foreach (var g in await _contactService.LoadAllGroupsAsync())
            Groups.Add(g);
        // Restore selection if the group still exists.
        GroupModel? match = null;
        if (selectedId is int id)
            match = Groups.FirstOrDefault(g => g.Id == id);
        if (!ReferenceEquals(SelectedGroup, match))
            SelectedGroup = match;
    }

    private void RebuildSelectedGroupMembers()
    {
        SelectedGroupMembers.Clear();
        if (SelectedGroup is not { } g) return;
        var byId = _allContacts.ToDictionary(c => c.Id);
        // Stable order: by display name for predictability (LastUsedTicks
        // ordering happens at insert time, not display time, so the list
        // doesn't shuffle as the user clicks).
        var members = g.MemberContactIds
            .Where(id => byId.ContainsKey(id))
            .Select(id => byId[id])
            .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var m in members) SelectedGroupMembers.Add(m);

        if (g.MissingContactCount > 0)
        {
            Announce(
                $"{g.Name}, {g.ResolvedMemberCount} member{(g.ResolvedMemberCount == 1 ? "" : "s")}. " +
                $"{g.MissingContactCount} contact{(g.MissingContactCount == 1 ? " is" : "s are")} missing and were skipped.",
                AnnouncementCategory.Status);
        }
    }

    private void ApplyFilter(string query)
    {
        FilteredContacts.Clear();
        var q = query.Trim();
        var matches = string.IsNullOrEmpty(q)
            ? _allContacts
            : _allContacts.Where(c =>
                c.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.EmailAddress.Contains(q, StringComparison.OrdinalIgnoreCase));
        foreach (var c in matches)
            FilteredContacts.Add(c);
    }

    private Task<bool> RequestConfirmAsync(string title, string body) =>
        ConfirmRequested?.Invoke(title, body) ?? Task.FromResult(false);

    /// <summary>
    /// Raised when a Properties dialog should be shown. The View subscribes and
    /// calls new PropertiesWindow(vm).ShowDialog().
    /// </summary>
    public event Action<PropertiesViewModel>? PropertiesRequested;

    [RelayCommand]
    private async Task ShowPropertiesAsync()
    {
        if (SelectedContact is { } contact)
        {
            var groupIds   = await _contactService.ListGroupsForContactAsync(contact.Id);
            var allGroups  = await _contactService.LoadAllGroupsAsync();
            var groupNames = allGroups
                .Where(g => groupIds.Contains(g.Id))
                .Select(g => g.Name)
                .ToList();
            var (title, sections) = ContactPropertiesBuilder.Build(contact, groupNames);
            PropertiesRequested?.Invoke(new PropertiesViewModel(title, sections));
        }
        else if (SelectedGroup is { } group)
        {
            var members   = SelectedGroupMembers.ToList();
            var (title, sections) = GroupPropertiesBuilder.Build(group, members);
            PropertiesRequested?.Invoke(new PropertiesViewModel(title, sections));
        }
    }
}
