using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>
/// Backs the standalone Group Manager dialog (Ctrl+Shift+M from the address
/// book window). Provides a focused, dialog-only place to rename groups,
/// bulk-add members, and remove individual members — the address book
/// Groups tab handles quick add/pick; this dialog handles larger edits.
///
/// Construction is split into two steps:
///   1. <c>new GroupManagerViewModel(contactService)</c>
///   2. <see cref="LoadAsync"/> — fetches contacts and groups; must be called
///      from the View's <c>Loaded</c> handler so the UI is populated.
/// </summary>
public partial class GroupManagerViewModel : ObservableObject
{
    private readonly IContactService _contactService;
    private List<ContactModel> _allContacts = [];

    /// <summary>
    /// Raised for destructive confirmations (delete group) and for close
    /// requests after a successful operation.
    /// </summary>
    public event Func<string, string, Task<bool>>? ConfirmRequested;

    /// <summary>
    /// Raised for screen-reader announcements. The View subscribes and calls
    /// <see cref="QuickMail.Views.AccessibilityHelper.Announce"/> so the VM
    /// stays free of System.Windows types.
    /// </summary>
    public event Action<string, AnnouncementCategory>? AnnouncementRequested;

    /// <summary>
    /// Raised after a successful mutation so the address book window can
    /// refresh its own Groups tab. The dialog does not own the picker UI.
    /// </summary>
    public event Action? GroupsChanged;

    private void Announce(string text, AnnouncementCategory category) =>
        AnnouncementRequested?.Invoke(text, category);

    public GroupManagerViewModel(IContactService contactService)
    {
        _contactService = contactService;
    }

    public ObservableCollection<GroupModel>        Groups             { get; } = [];
    public ObservableCollection<ContactModel>      GroupMembers       { get; } = [];
    public ObservableCollection<ContactModel>      ContactCandidates  { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedGroup))]
    [NotifyPropertyChangedFor(nameof(SelectedGroupName))]
    [NotifyPropertyChangedFor(nameof(SelectedGroupMemberCount))]
    private GroupModel? _selectedGroup;

    public bool HasSelectedGroup => SelectedGroup is not null;
    public string SelectedGroupName => SelectedGroup?.Name ?? string.Empty;
    public int    SelectedGroupMemberCount => SelectedGroup?.MemberContactIds.Count ?? 0;

    [ObservableProperty]
    private string _newName = string.Empty;

    [ObservableProperty]
    private string _candidateSearch = string.Empty;

    partial void OnSelectedGroupChanged(GroupModel? value) => RebuildMembers();

    partial void OnCandidateSearchChanged(string value) => ApplyCandidateFilter();

    public async Task LoadAsync()
    {
        _allContacts = await _contactService.LoadAllContactsAsync();
        await ReloadGroupsAsync();
        ContactCandidates.Clear();
        foreach (var c in _allContacts.OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase))
            ContactCandidates.Add(c);
    }

    [RelayCommand]
    private async Task RenameSelectedAsync()
    {
        if (SelectedGroup is not { } g) return;
        var newName = NewName?.Trim();
        if (string.IsNullOrEmpty(newName) || newName == g.Name) return;
        try
        {
            await _contactService.RenameGroupAsync(g.Id, newName);
        }
        catch (ArgumentException) { return; }
        NewName = string.Empty;
        await ReloadGroupsAsync();
        SelectedGroup = Groups.FirstOrDefault(x => x.Id == g.Id);
        Announce($"Renamed group to '{newName}'", AnnouncementCategory.Result);
        GroupsChanged?.Invoke();
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedGroup is not { } g) return;
        var memberCount = g.ResolvedMemberCount;
        var body = memberCount switch
        {
            0 => $"Delete group '{g.Name}'? It is empty.",
            1 => $"Delete group '{g.Name}'? Its 1 member will not be deleted.",
            _ => $"Delete group '{g.Name}'? Its {memberCount} members will not be deleted.",
        };
        var ok = await RequestConfirmAsync("Confirm", body);
        if (!ok) return;
        await _contactService.DeleteGroupAsync(g.Id);
        await ReloadGroupsAsync();
        SelectedGroup = null;
        Announce("Group deleted", AnnouncementCategory.Result);
        GroupsChanged?.Invoke();
    }

    [RelayCommand]
    private async Task AddMemberAsync(ContactModel? contact)
    {
        if (SelectedGroup is not { } g || contact is null) return;
        await _contactService.AddMemberAsync(g.Id, contact.Id);
        await ReloadGroupsAsync();
        SelectedGroup = Groups.FirstOrDefault(x => x.Id == g.Id);
        Announce($"Added {contact.Display} to {g.Name}", AnnouncementCategory.Result);
        GroupsChanged?.Invoke();
    }

    [RelayCommand]
    private async Task RemoveMemberAsync(ContactModel? contact)
    {
        if (SelectedGroup is not { } g || contact is null) return;
        await _contactService.RemoveMemberAsync(g.Id, contact.Id);
        await ReloadGroupsAsync();
        SelectedGroup = Groups.FirstOrDefault(x => x.Id == g.Id);
        Announce($"Removed {contact.Display} from {g.Name}", AnnouncementCategory.Result);
        GroupsChanged?.Invoke();
    }

    /// <summary>
    /// Adds the contact if they are not yet a member of the selected group;
    /// removes them if they already are. This is the primary "Enter on a
    /// candidate" action so pressing Enter twice on the same contact cleanly
    /// undoes the first press instead of silently repeating the announcement.
    /// </summary>
    [RelayCommand]
    private async Task ToggleMemberAsync(ContactModel? contact)
    {
        if (SelectedGroup is not { } g || contact is null) return;
        if (g.MemberContactIds.Contains(contact.Id))
        {
            await _contactService.RemoveMemberAsync(g.Id, contact.Id);
            await ReloadGroupsAsync();
            SelectedGroup = Groups.FirstOrDefault(x => x.Id == g.Id);
            Announce($"Removed {contact.Display} from {g.Name}", AnnouncementCategory.Result);
        }
        else
        {
            await _contactService.AddMemberAsync(g.Id, contact.Id);
            await ReloadGroupsAsync();
            SelectedGroup = Groups.FirstOrDefault(x => x.Id == g.Id);
            Announce($"Added {contact.Display} to {g.Name}", AnnouncementCategory.Result);
        }
        GroupsChanged?.Invoke();
    }

    [RelayCommand]
    private async Task CreateGroupAsync()
    {
        var name = NewName?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        int id;
        try { id = await _contactService.CreateGroupAsync(name); }
        catch (ArgumentException) { return; }
        NewName = string.Empty;
        await ReloadGroupsAsync();
        SelectedGroup = Groups.FirstOrDefault(x => x.Id == id);
        Announce($"Group '{name}' created", AnnouncementCategory.Result);
        GroupsChanged?.Invoke();
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private async Task ReloadGroupsAsync()
    {
        Groups.Clear();
        foreach (var g in await _contactService.LoadAllGroupsAsync())
            Groups.Add(g);
    }

    private void RebuildMembers()
    {
        GroupMembers.Clear();
        if (SelectedGroup is not { } g) return;
        var byId = _allContacts.ToDictionary(c => c.Id);
        var members = g.MemberContactIds
            .Where(id => byId.ContainsKey(id))
            .Select(id => byId[id])
            .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var m in members) GroupMembers.Add(m);
    }

    private void ApplyCandidateFilter()
    {
        // Re-populate the candidate list (kept simple — only a few hundred
        // contacts expected in practice). SelectedGroup is unchanged.
        var selectedId = SelectedGroup?.Id;
        ContactCandidates.Clear();
        var q = CandidateSearch?.Trim() ?? string.Empty;
        var matches = string.IsNullOrEmpty(q)
            ? _allContacts
            : _allContacts.Where(c =>
                c.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.EmailAddress.Contains(q, StringComparison.OrdinalIgnoreCase));
        foreach (var c in matches.OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase))
            ContactCandidates.Add(c);
        // Refresh selection (the user may have just renamed; the id is the same).
        if (selectedId is int id)
        {
            SelectedGroup = Groups.FirstOrDefault(g => g.Id == id);
        }
    }

    private Task<bool> RequestConfirmAsync(string title, string body) =>
        ConfirmRequested?.Invoke(title, body) ?? Task.FromResult(false);
}
