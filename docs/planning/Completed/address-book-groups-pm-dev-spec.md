# Address Book Groups — PM & Dev Specification

**Status:** Implemented
**Date:** June 2, 2026
**Target:** Phase 5 (Power User) — Groups feature
**Crew:** Delta (PM → Dev Lead → Test Enforcer)

> Combined PM + Dev spec. **Sections 1–6 are the PM portion** (problem, users, scope, UX, accessibility). **Sections 7–12 are the Dev portion** (architecture, data model, API, view model, views, implementation phases). **Sections 13+** are shared (success metrics, open questions, file/test tables, appendices).

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [User Problem & Opportunity](#2-user-problem--opportunity)
3. [Personas & Use Cases](#3-personas--use-cases)
4. [Competitive Landscape](#4-competitive-landscape)
5. [Design Principles](#5-design-principles)
6. [Feature Scope & Acceptance Criteria](#6-feature-scope--acceptance-criteria)
7. [Data Model](#7-data-model)
8. [Service Layer (IContactService Extension)](#8-service-layer-icontactservice-extension)
9. [Persistence & Migration](#9-persistence--migration)
10. [ViewModels](#10-viewmodels)
11. [Views (XAML + Code-Behind)](#11-views-xaml--code-behind)
12. [Compose Integration](#12-compose-integration)
13. [Command Registry & Shortcuts](#13-command-registry--shortcuts)
14. [Accessibility (WCAG 2.2)](#14-accessibility-wcag-22)
15. [Implementation Phases](#15-implementation-phases)
16. [Success Metrics](#16-success-metrics)
17. [Open Questions & Risks](#17-open-questions--risks)
18. [Files to Create](#18-files-to-create)
19. [Files to Modify](#19-files-to-modify)
20. [Tests to Add](#20-tests-to-add)
21. [Appendix A — Sample JSON Shape](#appendix-a--sample-json-shape)
22. [Appendix B — Sample User Flows](#appendix-b--sample-user-flows)

---

## 1. Executive Summary

The address book today (see [AddressBookViewModel.cs](../../QuickMail/ViewModels/AddressBookViewModel.cs) and [AddressBookWindow.xaml](../../QuickMail/Views/AddressBookWindow.xaml)) is a flat list of individual contacts. Users who repeatedly mail the same set of people (project teams, family groups, customer lists) must type every address, or pick them one at a time from the address book, every time they compose.

**Groups** let users name a collection of existing addresses (e.g. "Family", "Book club", "Work team") and treat that collection as a single addressable unit. When a user picks a group in any address field (To / Cc / Bcc) the group expands into all of its member addresses, formatted identically to individually-picked contacts. A contact can belong to many groups, and groups can be added, renamed, deleted, and have members added or removed at any time — without affecting the underlying contacts.

This feature is the natural next step beyond the current "Grab Addresses from Message" flow (Ctrl+Shift+G) and the standalone Address Book (Ctrl+Shift+B). It is keyboard-first, screen-reader friendly, and stored locally in a small, human-readable JSON file (no schema migration of the existing `contacts.json` required).

---

## 2. User Problem & Opportunity

### 2.1 Current state

- The address book holds a single flat list of `ContactModel` rows (id, display name, email, last-used timestamp).
- Compose window opens the address book with `Ctrl+Shift+B`; the user can pick one contact and click **To / Cc / Bcc** to insert it. Multi-pick exists but there is no way to bundle contacts that the user *always* sends to together.
- There is no concept of "send to a saved set of people" anywhere in the product.

### 2.2 Opportunity

A small, focused groups feature covers a high-frequency workflow (mailing a recurring set of people) without the cost of a full contact-management overhaul. It also sets the stage for later features (e.g. group-aware rules, group import/export) without requiring them now.

### 2.3 Non-goals (deliberately out of scope for v1)

- **Nested groups** (a group containing another group). Flat only.
- **Distribution-list semantics on the IMAP server** (no `imap.metoo` or X-LIST). Groups are local-only.
- **Server-side contact sync** (CardDAV, Google Contacts, Microsoft People). Local JSON only.
- **Group-based mail rules.** Rules continue to operate on `From`/`To`/`Subject`/etc. text matches.
- **Importing / exporting groups** as vCards or CSV. Plain JSON.
- **Sharing groups between profiles.** A group lives in the active profile's directory.

---

## 3. Personas & Use Cases

| Persona | Need | Use case |
|---|---|---|
| **Power keyboard user** (Alex) | "I mail my 7-person project team daily. I should type 'team' once." | Creates a group, picks it from the address book, expands to all 7 addresses. |
| **Screen reader user** (Pat) | "I need to know what's in a group before I send to it." | Groups display member count and list on focus; insertion announces a per-member confirmation. |
| **Privacy-conscious user** (Sam) | "I want groups to live locally, not on a server I don't control." | Groups live in `%APPDATA%\QuickMail\groups.json`; no network call ever. |
| **Family user** (Riley) | "My kid's contact is in two groups — school and family." | One contact, many group memberships; deleting the group does not delete the contact. |

**Primary use cases**

1. **Create a group** from the address book window.
2. **Add existing contacts to a group** (search → check, or right-click → "Add to group…").
3. **Pick a group** while composing a message — it expands into all of its members in the chosen field.
4. **Rename a group.**
5. **Remove a member from a group** without deleting the contact.
6. **Delete a group** (members are preserved as plain contacts).
7. **View group members** (read-only count + list, with an "Open in address book" link per member).

---

## 4. Competitive Landscape

| Product | Group concept | Strengths | Weaknesses |
|---|---|---|---|
| **Microsoft Outlook (People)** | Contact Lists | Integrates with mail rules, calendar invites. | Cloud-tied; nested groups; many clicks to manage. |
| **Thunderbird** | Mailing Lists (Mork) | Local file, simple. | Mork format is opaque; CLI / address book tab only. |
| **Gmail** | Labels (approximation) | Familiar; tag-based. | Not addressable; can't be picked as a recipient. |
| **Apple Mail** | Groups in Contacts | Tight OS integration. | macOS-only; no keyboard shortcut for "pick group". |
| **QuickMail (current)** | None | N/A | Repeated multi-address entry. |

**QuickMail positioning.** A flat, keyboard-first, screen-reader-friendly group list. One shortcut to pick, one shortcut to manage, all data on disk, no IMAP-server-side state. Comparable in spirit to Thunderbird mailing lists but with a modern, accessible UI and no Mork.

---

## 5. Design Principles

1. **Flat, not nested.** A group contains contacts only — never other groups. Eliminates cycles, simplifies the picker, and matches user mental models.
2. **Local and private.** Group data lives in `%APPDATA%\QuickMail\groups.json` (or the active `--profileDir` override). Never written anywhere else.
3. **Keyboard-first.** Every group action has a registered command and a default shortcut. Mouse is optional.
4. **Group deletion is non-destructive.** Deleting a group does **not** delete its member contacts.
5. **A contact can be in many groups.** Membership is a many-to-many relation, not a foreign key on the contact row.
6. **Screen-reader accessible by default.** Every group has an `AutomationProperties.Name` that includes the member count ("Project team, 7 members"). All announcements go through `AccessibilityHelper.Announce()` with an `AnnouncementCategory`.
7. **No schema migration of `contacts.json`.** Groups are an additive, separate file. Existing contact data is untouched.

---

## 6. Feature Scope & Acceptance Criteria

### 6.1 In scope (v1)

- [ ] Groups stored in `%APPDATA%\QuickMail\groups.json` (separate file from `contacts.json`).
- [ ] Data model: `GroupModel { Id, Name, MemberContactIds[], LastUsedTicks }`.
- [ ] CRUD on groups: create, rename, delete, list.
- [ ] Membership: add contact to group, remove contact from group, list members of group, list groups containing a contact.
- [ ] A contact can be a member of zero, one, or many groups.
- [ ] The address book window shows a **Groups** pane alongside the contacts pane.
- [ ] In compose, the address book picker shows a **Groups** tab in addition to the existing **Contacts** tab; both use the same `To / Cc / Bcc` insert buttons.
- [ ] Picking a group inserts **all** of its member addresses (in `LastUsedTicks`-desc order, deduped) into the chosen field.
- [ ] Insertion is per-member for undo/announce purposes (each address announced individually).
- [ ] Two new commands registered in `CommandRegistry`:
  - `contacts.createGroup` — create a new group (in the address book window).
  - `contacts.manageGroups` — open the Group Manager dialog (rename/delete, manage members).
- [ ] Address book window gets one new shortcut: `Ctrl+G` — focus the Groups pane (registered, customizable).
- [ ] Group Manager dialog gets `F2` to rename, `Delete` to delete with confirmation.
- [ ] All new UI elements have `AutomationProperties.Name` and proper `TabIndex`.
- [ ] Unit tests for the service, ViewModel, and the address-pick expansion logic.
- [ ] No new dependencies. JSON storage reuses `System.Text.Json`.

### 6.2 Out of scope (v1)

- [ ] Nested groups.
- [ ] Group-aware mail rules.
- [ ] Group import/export.
- [ ] Group avatars / colors.
- [ ] Server-side (IMAP/CARDDAV) sync.

### 6.3 Acceptance criteria — UX

- [ ] Creating a group takes ≤ 2 keystrokes from the address book window (`Ctrl+Shift+N` opens "New Group"; type name; Enter).
- [ ] Adding a contact to a group from the contacts pane takes ≤ 3 keystrokes (`Space` opens the "Add to group…" menu; `Enter` on a group; Esc to close).
- [ ] Picking a group from the compose address book inserts all of its members and announces "Inserted N addresses from group 'X'".
- [ ] Deleting a group with 12 members shows confirmation "Delete group 'X'? Its 12 members will not be deleted. Press Enter to confirm."
- [ ] Renaming a group in-place (`F2`) announces the new name on commit.
- [ ] All announcements routed through `AccessibilityHelper.Announce()` with a `category` argument (see §14).

---

## 7. Data Model

### 7.1 New types

````csharp
// filepath: QuickMail/Models/GroupModel.cs
namespace QuickMail.Models;

public class GroupModel
{
    public int    Id             { get; set; }
    public string Name           { get; set; } = string.Empty;
    public List<int> MemberContactIds { get; set; } = new();
    public long   LastUsedTicks  { get; set; }

    /// <summary>
    /// Count of members after resolving MemberContactIds against the live contact list.
    /// Computed by the service; persisted as null (System.Text.Json default) so
    /// the file stays compact and the value is always recomputed on load.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int ResolvedMemberCount { get; set; }

    /// <summary>
    /// Display string for the group list. "Project Team, 7 members" /
    /// "Empty group" / "3 members (1 missing contact)".
    /// </summary>
    public string Display(int liveContactCount) =>
        ResolvedMemberCount == 0
            ? "Empty group"
            : MemberContactIds.Count == ResolvedMemberCount
                ? $"{Name}, {ResolvedMemberCount} member{(ResolvedMemberCount == 1 ? "" : "s")}"
                : $"{Name}, {ResolvedMemberCount} members ({MemberContactIds.Count - ResolvedMemberCount} missing)";
}
````

> `LastUsedTicks` is updated whenever a group is picked in the address book, and used to sort the groups list (most-recently-used first), matching the existing contact sort order in `ContactService.SearchContactsAsync`.

### 7.2 Existing `ContactModel` is unchanged

We do **not** add a `GroupIds` property to `ContactModel`. The relation is held on the `GroupModel` side, which keeps the existing `contacts.json` schema and `IContactService` contract intact. `ListGroupsForContactAsync(int contactId)` is computed by `ContactService`/`GroupService` on demand.

### 7.3 Why a separate file (not a `[Groups]` section in `contacts.json`)?

- Independent backup / git-diff story. Users can sync just contacts, or just groups.
- Avoids touching the existing `ContactService` upsert path (which would otherwise need a write lock for group membership changes).
- Lets us add group-level features later (e.g. per-group color) without invalidating the contact cache.

---

## 8. Service Layer (IContactService Extension)

We extend `IContactService` with the group API. (See §9.3 for why we keep a single service rather than a new `IGroupService`.)

````csharp
// filepath: QuickMail/Services/IContactService.cs
// ...existing code...
public interface IContactService
{
    // ...existing contact methods unchanged...

    // ── Groups ───────────────────────────────────────────────────────────

    /// <summary>Loads all groups. ResolvedMemberCount is set on each result.</summary>
    Task<List<GroupModel>> LoadAllGroupsAsync();

    /// <summary>Creates a new group with the given name. Returns the assigned Id.</summary>
    Task<int> CreateGroupAsync(string name);

    /// <summary>Renames a group. Throws if id not found.</summary>
    Task RenameGroupAsync(int id, string newName);

    /// <summary>Deletes a group. Contacts referenced by MemberContactIds are not touched.</summary>
    Task DeleteGroupAsync(int id);

    /// <summary>Adds a contact to a group. Idempotent.</summary>
    Task AddMemberAsync(int groupId, int contactId);

    /// <summary>Removes a contact from a group. Idempotent.</summary>
    Task RemoveMemberAsync(int groupId, int contactId);

    /// <summary>
    /// Returns the group ids that contain the given contact.
    /// Used by the contact-details popover to show "Member of: …".
    /// </summary>
    Task<List<int>> ListGroupsForContactAsync(int contactId);

    /// <summary>
    /// Bumps LastUsedTicks on a group (called when the user picks it in compose).
    /// Keeps the picker sorted by recency, matching the contact sort.
    /// </summary>
    Task TouchGroupAsync(int groupId);
}
````

### 8.1 Implementation notes

- Reuse the existing `ProfileContext.ProfileDir` for the file path: `Path.Combine(profile.ProfileDir, "groups.json")`.
- Reuse the same `SemaphoreSlim` / `EnsureLoadedAsync` pattern as `ContactService`. (Refactor opportunity: extract a small generic `JsonFileStore<T>` helper, but only if it doesn't grow the diff — see §17.2.)
- All write methods use the **temp-then-rename** atomic write pattern already used in `ContactService`, `TemplateService`, `RuleService`, and `ViewService`.
- `ResolvedMemberCount` is **never persisted**. It's recomputed in `LoadAllGroupsAsync` by intersecting `MemberContactIds` with the in-memory contact list.
- `ListGroupsForContactAsync` is O(groups × avgMembers) in the worst case; in practice both are small. If profiling shows it matters later, add an inverted index when a group is written.

### 8.2 Concurrency

`ContactService` already serializes all writes behind `_loadLock`. Group operations must take the same lock, because the picker dialog will call `ContactService.LoadAllContactsAsync` and `ContactService.LoadAllGroupsAsync` interleaved with `ContactService.UpsertContactAsync` from the address book "Add" form. A single `SemaphoreSlim(1,1)` covers both files cheaply; using two locks would require careful ordering to avoid deadlock.

### 8.3 Why a single `IContactService` (not a new `IGroupService`)?

- The two files are tightly coupled: every group operation needs to resolve member ids against the contact list.
- The existing tests already wire up `IContactService` in `StubServices.cs` and `MakeServices()`. Adding a second service doubles the stubbing surface.
- One service, one lock, one file-write story. If groups later grow a feature (per-group color, group-aware rules) that has no contact dependency, splitting into a separate `IGroupService` becomes a focused refactor.

---

## 9. Persistence & Migration

### 9.1 File format

`groups.json` — pretty-printed UTF-8 JSON. See [Appendix A](#appendix-a--sample-json-shape) for a full example.

### 9.2 Atomic write

Reuse the pattern from `ContactService.SaveAllAsync`:

````csharp
// filepath: QuickMail/Services/ContactService.cs (new private helper)
private async Task WriteGroupsAtomicallyAsync(List<GroupModel> groups)
{
    var json = JsonSerializer.Serialize(groups, _jsonOptions);
    var tmp  = _groupsFilePath + ".tmp";
    await File.WriteAllTextAsync(tmp, json, Encoding.UTF8);
    // File.Move with overwrite is atomic on NTFS; same call used by other services.
    File.Move(tmp, _groupsFilePath, overwrite: true);
}
````

`_jsonOptions` is the existing `JsonSerializerOptions { WriteIndented = true }` field already used for contacts.

### 9.3 Schema migration

There is **no** schema migration of `contacts.json`. `groups.json` is new.

If `groups.json` is missing on first read (e.g. existing user upgrading), `LoadAllGroupsAsync` returns an empty list and the next write creates the file. No `PRAGMA` / `user_version` equivalent is needed because JSON files have no schema version to track.

If `groups.json` is corrupt (e.g. truncated by a crash mid-write — impossible with the atomic-rename pattern, but defensively), the service should:

1. Log the error via `LogService.Error`.
2. Rename the bad file to `groups.json.bak-{timestamp}`.
3. Return an empty list and continue.
4. Announce a one-time `AnnouncementCategory.Status` message: "Address book groups file was unreadable; starting fresh. A backup was saved."

---

## 10. ViewModels

### 10.1 `AddressBookViewModel` (existing — extended)

Add a `Groups` observable collection and a `SelectedGroup` property. Keep the existing `FilteredContacts` collection and the `AddToTo/Cc/Bcc` commands. New commands: `CreateGroupAsync`, `DeleteGroupAsync`, `AddContactToGroupAsync`, `RemoveContactFromGroupAsync`.

````csharp
// filepath: QuickMail/ViewModels/AddressBookViewModel.cs
// ...existing code...
public partial class AddressBookViewModel : ObservableObject
{
    // ...existing fields and properties...

    /// <summary>All groups, sorted by LastUsedTicks descending.</summary>
    public ObservableCollection<GroupModel> Groups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedGroup))]
    private GroupModel? _selectedGroup;

    public bool HasSelectedGroup => SelectedGroup is not null;

    /// <summary>
    /// Members of the currently selected group, resolved against _allContacts.
    /// Rebuilt whenever SelectedGroup or _allContacts changes.
    /// </summary>
    public ObservableCollection<ContactModel> SelectedGroupMembers { get; } = [];

    partial void OnSelectedGroupChanged(GroupModel? value) => RebuildSelectedGroupMembers();

    [RelayCommand]
    private async Task CreateGroupAsync()
    {
        var name = NewGroupName?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        var id = await _contactService.CreateGroupAsync(name);
        NewGroupName = string.Empty;
        await ReloadGroupsAsync();
        SelectedGroup = Groups.FirstOrDefault(g => g.Id == id);
        AccessibilityHelper.Announce(
            $"Group '{name}' created",
            AnnouncementCategory.Result);
    }

    [RelayCommand]
    private async Task DeleteGroupAsync()
    {
        if (SelectedGroup is not { } g) return;
        // Confirmation is requested via event; see §10.3.
        var confirmed = await RequestConfirmAsync(
            $"Delete group '{g.Name}'? " +
            $"Its {g.ResolvedMemberCount} member{(g.ResolvedMemberCount == 1 ? "" : "s")} will not be deleted.");
        if (!confirmed) return;
        await _contactService.DeleteGroupAsync(g.Id);
        await ReloadGroupsAsync();
        SelectedGroup = null;
        AccessibilityHelper.Announce("Group deleted", AnnouncementCategory.Result);
    }

    [RelayCommand]
    private async Task AddContactToGroupAsync(GroupModel targetGroup)
    {
        if (SelectedContact is not { } c) return;
        await _contactService.AddMemberAsync(targetGroup.Id, c.Id);
        if (SelectedGroup?.Id == targetGroup.Id) RebuildSelectedGroupMembers();
        await ReloadGroupsAsync();
        AccessibilityHelper.Announce(
            $"Added {c.Display} to {targetGroup.Name}",
            AnnouncementCategory.Result);
    }

    [RelayCommand]
    private async Task RemoveContactFromGroupAsync(ContactModel contact)
    {
        if (SelectedGroup is not { } g) return;
        await _contactService.RemoveMemberAsync(g.Id, contact.Id);
        RebuildSelectedGroupMembers();
        await ReloadGroupsAsync();
        AccessibilityHelper.Announce(
            $"Removed {contact.Display} from {g.Name}",
            AnnouncementCategory.Result);
    }

    /// <summary>
    /// Inserts all members of the selected group into the chosen field.
    /// Used when the user activates a group row and presses To / Cc / Bcc.
    /// </summary>
    private void InsertGroup(Action<ContactModel> inserter)
    {
        if (SelectedGroup is not { } g) return;
        var ordered = SelectedGroupMembers
            .OrderByDescending(c => c.LastUsedTicks)
            .ToList();
        foreach (var c in ordered) inserter(c);
        AccessibilityHelper.Announce(
            $"Inserted {ordered.Count} address{(ordered.Count == 1 ? "" : "es")} from group '{g.Name}'",
            AnnouncementCategory.Result);
        _ = _contactService.TouchGroupAsync(g.Id);
    }

    [RelayCommand] private void AddGroupToTo()   => InsertGroup(_toInsertAction   ?? (_ => { }));
    [RelayCommand] private void AddGroupToCc()   => InsertGroup(_ccInsertAction   ?? (_ => { }));
    [RelayCommand] private void AddGroupToBcc()  => InsertGroup(_bccInsertAction  ?? (_ => { }));

    private async Task ReloadGroupsAsync()
    {
        Groups.Clear();
        foreach (var g in await _contactService.LoadAllGroupsAsync())
            Groups.Add(g);
    }

    private void RebuildSelectedGroupMembers()
    {
        SelectedGroupMembers.Clear();
        if (SelectedGroup is not { } g) return;
        var byId = _allContacts.ToDictionary(c => c.Id);
        foreach (var id in g.MemberContactIds)
            if (byId.TryGetValue(id, out var c))
                SelectedGroupMembers.Add(c);
    }
}
````

### 10.2 New `GroupManagerViewModel` (dialog)

Used by the standalone Group Manager dialog (`Ctrl+Shift+M` from the address book, optional — see §17.1).

````csharp
// filepath: QuickMail/ViewModels/GroupManagerViewModel.cs
namespace QuickMail.ViewModels;

public partial class GroupManagerViewModel : ObservableObject
{
    private readonly IContactService _contactService;
    public GroupManagerViewModel(IContactService contactService, List<ContactModel> allContacts)
    {
        _contactService = contactService;
        // Build a contact picker list (searchable, multi-select) for "Add members…".
        ContactCandidates = new(allContacts);
    }

    public ObservableCollection<GroupModel> Groups { get; } = [];
    public ObservableCollection<ContactModel> ContactCandidates { get; }

    [ObservableProperty] private GroupModel? _selectedGroup;
    [ObservableProperty] private string _newName = string.Empty;

    partial void OnSelectedGroupChanged(GroupModel? value) =>
        OnPropertyChanged(nameof(SelectedGroupMemberCount));

    public int SelectedGroupMemberCount => SelectedGroup?.MemberContactIds.Count ?? 0;

    [RelayCommand]
    private async Task RenameAsync()
    {
        if (SelectedGroup is not { } g || string.IsNullOrWhiteSpace(NewName)) return;
        await _contactService.RenameGroupAsync(g.Id, NewName.Trim());
        AccessibilityHelper.Announce($"Renamed group to '{NewName}'", AnnouncementCategory.Result);
        NewName = string.Empty;
    }

    [RelayCommand] private Task DeleteAsync() { /* mirrors AddressBookViewModel.DeleteGroupAsync */ }

    [RelayCommand]
    private async Task AddMembersAsync(IReadOnlyList<ContactModel> picked)
    {
        if (SelectedGroup is not { } g) return;
        foreach (var c in picked) await _contactService.AddMemberAsync(g.Id, c.Id);
        AccessibilityHelper.Announce(
            $"Added {picked.Count} member{(picked.Count == 1 ? "" : "s")} to {g.Name}",
            AnnouncementCategory.Result);
    }

    [RelayCommand]
    private async Task RemoveMemberAsync(ContactModel c)
    {
        if (SelectedGroup is not { } g) return;
        await _contactService.RemoveMemberAsync(g.Id, c.Id);
        AccessibilityHelper.Announce($"Removed {c.Display}", AnnouncementCategory.Result);
    }
}
````

### 10.3 Modal dialog rules

Following the project's enforced **Modal Dialog Rules** (see `CLAUDE.md`):

- The VM exposes a `ConfirmRequested` event; the View (code-behind) subscribes, shows a `MessageBox`, and calls back via a `TaskCompletionSource<bool>`.
- **Never** call `MessageBox.Show` from a ViewModel.
- No `Dispatcher` calls in the VM; no `Window` types referenced.

````csharp
// filepath: QuickMail/ViewModels/AddressBookViewModel.cs (within the VM class)
public event Func<string, string, Task<bool>>? ConfirmRequested;

private Task<bool> RequestConfirmAsync(string body) =>
    ConfirmRequested?.Invoke("Confirm", body) ?? Task.FromResult(false);
````

The View subscribes in `Loaded` and unsubscribes in `Closed` — pairing `+=` with `-=`.

---

## 11. Views (XAML + Code-Behind)

### 11.1 `AddressBookWindow.xaml` — add a Groups tab

Convert the existing single-pane layout into a `TabControl` with two tabs: **Contacts** (existing UI) and **Groups** (new). Both tabs share the same `To / Cc / Bcc` button row.

````xml
<!-- filepath: QuickMail/Views/AddressBookWindow.xaml -->
<!-- ...existing Window root and resources... -->
<TabControl x:Name="MainTabs" TabIndex="0" KeyboardNavigation.TabNavigation="Local">
    <TabItem Header="_Contacts"
             AutomationProperties.Name="Contacts"
             KeyboardNavigation.TabNavigation="Local">
        <!-- existing search box, contact list, and add-contact form go here unchanged -->
    </TabItem>

    <TabItem Header="_Groups"
             AutomationProperties.Name="Groups"
             KeyboardNavigation.TabNavigation="Local">
        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="2*" />
                <ColumnDefinition Width="3*" />
            </Grid.ColumnDefinitions>

            <!-- Left: group list -->
            <DockPanel Grid.Column="0">
                <TextBox x:Name="GroupSearchBox"
                         DockPanel.Dock="Top"
                         TabIndex="10"
                         Text="{Binding SearchText, UpdateSourceTrigger=PropertyChanged}"
                         AutomationProperties.Name="Search groups"
                         KeyDown="GroupSearchBox_KeyDown" />
                <Button DockPanel.Dock="Bottom"
                        Content="New _group"
                        TabIndex="14"
                        Command="{Binding CreateGroupCommand}"
                        AutomationProperties.Name="New group" />
                <ListView x:Name="GroupsList"
                          TabIndex="11"
                          ItemsSource="{Binding Groups}"
                          SelectedItem="{Binding SelectedGroup, Mode=TwoWay}"
                          AutomationProperties.Name="Groups">
                    <ListView.View>
                        <GridView>
                            <GridViewColumn Header="Name" Width="180"
                                            DisplayMemberBinding="{Binding Name}" />
                            <GridViewColumn Header="Members" Width="80"
                                            DisplayMemberBinding="{Binding ResolvedMemberCount}" />
                        </GridView>
                    </ListView.View>
                </ListView>
            </DockPanel>

            <!-- Right: members of selected group -->
            <DockPanel Grid.Column="1">
                <TextBlock DockPanel.Dock="Top"
                           Text="{Binding SelectedGroup.Name, StringFormat='Members of {0}'}"
                           Margin="6" />
                <ListView x:Name="GroupMembersList"
                          TabIndex="12"
                          ItemsSource="{Binding SelectedGroupMembers}"
                          AutomationProperties.Name="Group members">
                    <ListView.View>
                        <GridView>
                            <GridViewColumn Header="Name" DisplayMemberBinding="{Binding DisplayName}" />
                            <GridViewColumn Header="Email" DisplayMemberBinding="{Binding EmailAddress}" />
                        </GridView>
                    </ListView.View>
                </ListView>
            </DockPanel>
        </Grid>
    </TabItem>
</TabControl>

<!-- shared To/Cc/Bcc row (existing) -->
<StackPanel Orientation="Horizontal" ...>
    <Button Content="To"   Command="{Binding AddToToCommand}"   AutomationProperties.Name="Add to To" />
    <Button Content="Cc"   Command="{Binding AddToCcCommand}"   AutomationProperties.Name="Add to Cc" />
    <Button Content="Bcc"  Command="{Binding AddToBccCommand}"  AutomationProperties.Name="Add to Bcc" />
</StackPanel>
````

### 11.2 `AddressBookWindow.xaml.cs` — keyboard wiring

- `Ctrl+Tab` / `Ctrl+Shift+Tab` to switch tabs (WPF default; do not override).
- `Enter` on a group row inserts into the field selected by the **Insert destination** radio row at the top of the Groups tab. (See §17.1 open question — do we add a per-pane destination selector, or use a single shared one?)
- `Delete` on a group calls `vm.DeleteGroupCommand`.
- `F2` on a group opens the rename prompt.
- `Ctrl+N` opens a "New group" text box at the bottom of the group list pane.

The code-behind stays UI-only: focus management, the `ConfirmRequested` event handler, and the `Insert destination` radio button wiring. **No business logic, no service calls.**

### 11.3 `GroupManagerWindow.xaml` — new dialog

Two-pane dialog: groups list on the left, member editor (with a search-as-you-type "Add members" picker) on the right. Reuses the same look-and-feel as `AddressBookWindow` and `ViewManagerWindow`.

---

## 12. Compose Integration

### 12.1 Picking a group

When the address book is opened from the compose window (`compose.openAddressBook`, `Ctrl+Shift+B`):

- The **Groups** tab is enabled and selected by default if the user has any groups, otherwise **Contacts** is shown (current behavior).
- Selecting a group and activating **To / Cc / Bcc** calls the same `ToBox.AddAddress(name, email)` path used for individual contacts, once per member.
- Members are inserted in `LastUsedTicks` descending order, matching the contact search sort, so the most recently used contact is most likely at the bottom of the field (where the caret sits).

````csharp
// filepath: QuickMail/Views/ComposeWindow.xaml.cs (modified OpenAddressBook)
private void OpenAddressBook()
{
    var vm = new AddressBookViewModel(_contactService);
    vm.SetInsertActions(
        toAction:  c => ToBox.AddAddress (c.DisplayName ?? string.Empty, c.EmailAddress),
        ccAction:  c => CcBox.AddAddress (c.DisplayName ?? string.Empty, c.EmailAddress),
        bccAction: c => BccBox.AddAddress(c.DisplayName ?? string.Empty, c.EmailAddress));
    var win = new AddressBookWindow(vm) { Owner = this };
    win.ShowDialog();
}
````

> `vm.SetInsertActions` is reused as-is. The Groups tab's `To / Cc / Bcc` buttons call the same `AddGroupToTo/Cc/Bcc` commands wired in §10.1, which iterate members and call the same insert actions.

### 12.2 What if a member is missing?

If a `MemberContactIds` entry points to a contact that was deleted, `SelectedGroupMembers` silently omits it. The `GroupModel.Display` string still includes the "N missing" suffix. The address book announces: "Group 'X' has N members. 2 contacts are missing and were skipped." when the user activates the group.

### 12.3 Undo

Undo is **per-address** because each `AddAddress` call is independent. The compose window's existing undo stack handles this without changes.

### 12.4 Deduplication

If a group contains two contacts with the same email address (case-insensitive), the second insertion is deduplicated by `TokenizedAddressBox.AddAddress` (existing behavior — see [TokenizedAddressBox.xaml.cs](../../QuickMail/Controls/TokenizedAddressBox.xaml.cs)). The announcement reflects the deduplicated count.

---

## 13. Command Registry & Shortcuts

| Command ID | Category | Title | Default Key | Available when |
|---|---|---|---|---|
| `contacts.createGroup` | Contacts | New Group | `Ctrl+Shift+N` | Address book window focused |
| `contacts.manageGroups` | Contacts | Manage Groups | `Ctrl+Shift+M` | Address book window focused |
| `contacts.focusGroupsPane` | Contacts | Focus Groups Pane | `Ctrl+G` | Address book window focused |
| `contacts.renameGroup` | Contacts | Rename Group | `F2` | Group selected in address book |
| `contacts.deleteGroup` | Contacts | Delete Group | `Delete` | Group selected in address book |

Registration pattern (mirrors the existing `contacts.openAddressBook` registration in [MainWindow.xaml.cs](../../QuickMail/Views/MainWindow.xaml.cs)):

````csharp
// filepath: QuickMail/Views/AddressBookWindow.xaml.cs (constructor)
_registry.Register(new CommandDefinition(
    id:             "contacts.createGroup",
    category:       "Contacts",
    title:          "New Group",
    defaultKey:     Key.N,
    defaultModifiers: ModifierKeys.Control | ModifierKeys.Shift,
    execute:        () => _vm.CreateGroupCommand.Execute(null),
    isAvailable:    () => IsVisible));
````

> All five new commands must be **registered, not hardcoded** in `PreviewKeyDown`. The CLAUDE.md keyboard-shortcut rules apply.

---

## 14. Accessibility (WCAG 2.2)

### 14.1 Automation properties

| Control | `AutomationProperties.Name` |
|---|---|
| Groups tab | `"Groups"` |
| Groups list | `"Groups"` (with `HelpText="Use arrow keys to navigate. Press F2 to rename, Delete to remove."`) |
| Group members list | `"Group members"` |
| "New group" button | `"New group"` |
| Search groups textbox | `"Search groups"` |

### 14.2 Announcements — all routed through `AccessibilityHelper.Announce(category: …)`

| Event | Text | Category |
|---|---|---|
| Group created | `"Group 'X' created"` | `Result` |
| Group deleted | `"Group deleted"` | `Result` |
| Group renamed | `"Renamed group to 'X'"` | `Result` |
| Contact added to group | `"Added <Display> to <GroupName>"` | `Result` |
| Contact removed from group | `"Removed <Display> from <GroupName>"` | `Result` |
| Group picked in compose | `"Inserted N addresses from group 'X'"` | `Result` |
| Group with missing members | `"Group 'X' has N members. M contacts are missing and were skipped."` | `Status` |
| `groups.json` unreadable on startup | `"Address book groups file was unreadable; starting fresh. A backup was saved."` | `Status` |
| Settings toggle confirmation | (only example using `force: true`) | n/a |

No `Hint` announcements are required for v1 — the groups tab uses the same focus/label pattern users already know from the Contacts tab.

### 14.3 Keyboard

- All controls reachable by Tab.
- `Ctrl+G` focuses the Groups pane from anywhere in the address book.
- `Ctrl+1` … `Ctrl+9` shortcuts within the group list jump to the Nth group (matches the existing folder-pane behavior).
- Visible focus indicator (the WPF default focus rectangle is sufficient; do **not** suppress it).
- All destructive actions (`Delete` on a group, `Remove` on a member) require confirmation.

### 14.4 Inclusive language

- Use **"select"** / **"activate"** in any new user-visible strings — never "click".
- Do not name a specific screen reader product. Use "screen readers" generically.

---

## 15. Implementation Phases

### Phase 5.1 — Foundation (data + service)

- Add `GroupModel`.
- Extend `IContactService` with the group methods from §8.
- Implement those methods in `ContactService` (atomic write, `ResolvedMemberCount` recompute, corrupt-file recovery).
- Add `StubContactService` overrides for the new methods (return `Task.CompletedTask` and empty lists).
- **Tests:** `ContactServiceGroupTests` — CRUD, membership, missing-contact handling, atomic write, corrupt-file recovery, `ListGroupsForContactAsync`.
- **No UI yet.** A console-style test or a temporary debug button in the address book can be used to seed fixtures.

### Phase 5.2 — ViewModel

- Extend `AddressBookViewModel` per §10.1.
- Add `GroupManagerViewModel` per §10.2.
- **Tests:** `AddressBookViewModelGroupTests`, `GroupManagerViewModelTests`.

### Phase 5.3 — Views

- Add the **Groups** tab to `AddressBookWindow.xaml`.
- Create `GroupManagerWindow.xaml(.cs)`.
- Wire the new commands in `AddressBookWindow.xaml.cs`.
- **Tests:** `XamlParseTests` — `AddressBookWindow_XamlParsesWithoutException` (updated to include new controls), new test for `GroupManagerWindow_XamlParsesWithoutException`.

### Phase 5.4 — Compose integration

- Update `ComposeWindow.OpenAddressBook` per §12.1.
- **Tests:** `ComposeWindowWithGroupsTests` — open address book with seeded groups, simulate `AddGroupToTo`, assert `ToBox` has the right tokens.

### Phase 5.5 — Polish

- Settings: add a "Show Groups tab by default" toggle (default: off — keep current behavior; on only if user has ≥ 1 group). See §17.1.
- `build.bat smoke` passes.
- Release notes drafted in `docs/release-notes-v0.7.0.md` (or whichever the next version is).
- Update `USERGUIDE.md` with a "Groups" section.

---

## 16. Success Metrics

| Metric | Target | How measured |
|---|---|---|
| Group usage (groups with ≥ 1 contact) | ≥ 30% of active users in the first 30 days | `groups.json` presence + non-empty on a sampled telemetry-free population (we have no telemetry — measured via an opt-in `LogService.Debug` counter behind the `/debug` flag) |
| Compose acceleration | Reduction in average "address field keystrokes per send" | Same opt-in counter |
| Crash rate from group-related code | 0 new crashes | CI test suite |
| Accessibility compliance | 0 screen reader regressions in `XamlParseTests` and manual VoiceOver / NVDA passes | Manual smoke + the `AnnouncementCategory` rules in §14 |

We have no telemetry, so the above is "after-the-fact" reasoning, not "live dashboard". The opt-in debug counter should be approved by the user via the existing Settings dialog before it ships.

---

## 17. Open Questions & Risks

### 17.1 Open questions

1. **Default tab when opening from Compose.** Contacts or Groups if any exist? **Recommendation:** Contacts, with a setting to change it. Users who have many groups can flip it once.
2. **Standalone "Group Manager" dialog vs. inline management in the address book?** **Recommendation:** Both. The address book gets the Groups tab for quick add/pick; the Group Manager dialog handles rename, bulk add, and large-group edits.
3. **Group ordering.** Sort by `LastUsedTicks` desc, alphabetical, or user-customizable? **Recommendation:** `LastUsedTicks` desc for v1, matching the contact sort. Add drag-to-reorder in v2.
4. **Should `Ctrl+G` in the main (non-address-book) window open the Group Manager?** **Recommendation:** No. It conflicts with browser "Find next" muscle memory. `Ctrl+Shift+M` only, from the address book window.

### 17.2 Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Schema drift between `contacts.json` and `groups.json` (e.g. a contact is deleted but its id is still in a group's `MemberContactIds`). | Medium | `ResolvedMemberCount` is recomputed on every load; missing ids are silently skipped; `GroupModel.Display` surfaces the discrepancy. |
| Race between compose pick and address book add. | Low | Single `SemaphoreSlim` covers both. |
| Large groups (1000+ members) make the picker slow. | Very low (no power user has this in tests) | Re-evaluate if reported; an inverted index on `contactId → groupIds` would be a focused optimization. |
| `JsonFileStore<T>` refactor is tempting but expands the diff. | Medium temptation | Explicitly **out of scope** for v1. Land the inline copy in `ContactService`; refactor in a follow-up PR. |
| New commands increase the Command Palette surface. | Low | All five are scoped to the Address Book window via `isAvailable: () => IsVisible`, so they only appear when the address book is open. |

---

## 18. Files to Create

| File | Purpose |
|---|---|
| `QuickMail/Models/GroupModel.cs` | Group data model (§7.1) |
| `QuickMail/ViewModels/GroupManagerViewModel.cs` | Group Manager dialog VM (§10.2) |
| `QuickMail/Views/GroupManagerWindow.xaml` | Group Manager dialog UI (§11.3) |
| `QuickMail/Views/GroupManagerWindow.xaml.cs` | Code-behind, focus + ConfirmRequested wiring only |
| `QuickMail.Tests/ContactServiceGroupTests.cs` | Service CRUD + atomic write + corruption recovery tests |
| `QuickMail.Tests/AddressBookViewModelGroupTests.cs` | VM tests for groups tab |
| `QuickMail.Tests/GroupManagerViewModelTests.cs` | Group Manager dialog VM tests |
| `QuickMail.Tests/ComposeWindowWithGroupsTests.cs` | Compose pick-from-group integration tests |
| `docs/release-notes-v0.7.0.md` | Release notes (one-shot) |
| `USERGUIDE.md` (modified section) | New "Groups" section |

---

## 19. Files to Modify

| File | Change |
|---|---|
| `QuickMail/Services/IContactService.cs` | Add group methods (§8) |
| `QuickMail/Services/ContactService.cs` | Implement group methods; reuse `_loadLock`; add `WriteGroupsAtomicallyAsync` helper |
| `QuickMail.Tests/StubServices.cs` | Stub the new group methods (return empty / `Task.CompletedTask`) |
| `QuickMail/ViewModels/AddressBookViewModel.cs` | Add `Groups`, `SelectedGroup`, `SelectedGroupMembers`, `CreateGroupAsync`, `DeleteGroupAsync`, `AddContactToGroupAsync`, `RemoveContactFromGroupAsync`, `AddGroupToTo/Cc/Bcc` (§10.1) |
| `QuickMail/Views/AddressBookWindow.xaml` | Add Groups tab (§11.1) |
| `QuickMail/Views/AddressBookWindow.xaml.cs` | Wire keyboard for groups; subscribe to `ConfirmRequested`; register 5 new commands (§11.2, §13) |
| `QuickMail/Views/ComposeWindow.xaml.cs` | No change to `OpenAddressBook` — it reuses `vm.SetInsertActions`; pick-from-group is a VM behavior |
| `QuickMail/App.xaml.cs` | No change — `ContactService` is already registered |
| `QuickMail.Tests/ViewModelConstructionTests.cs` | Add `GroupManagerViewModel` construction test |
| `QuickMail.Tests/XamlParseTests.cs` | Add `GroupManagerWindow_XamlParsesWithoutException`; update `AddressBookWindow_XamlParsesWithoutException` if it asserts on control counts |
| `USERGUIDE.md` | Add "Groups" section under "Address book" |
| `CLAUDE.md` | Add `GroupModel` and the new `IContactService` methods to the architecture summary (one-paragraph each) |

---

## 20. Tests to Add

| Test class | Test | Covers |
|---|---|---|
| `ContactServiceGroupTests` | `CreateGroupAsync_AssignsIncrementingId` | §7, §8.1 |
| `ContactServiceGroupTests` | `CreateGroupAsync_RejectsEmptyName` | §8 |
| `ContactServiceGroupTests` | `AddMemberAsync_IsIdempotent` | §8 |
| `ContactServiceGroupTests` | `RemoveMemberAsync_IsIdempotent` | §8 |
| `ContactServiceGroupTests` | `DeleteGroupAsync_DoesNotDeleteContacts` | §5.4 |
| `ContactServiceGroupTests` | `ListGroupsForContactAsync_ReturnsAllGroupsContainingContact` | §8 |
| `ContactServiceGroupTests` | `LoadAllGroupsAsync_SkipsMissingContacts` | §9.3, §12.2 |
| `ContactServiceGroupTests` | `LoadAllGroupsAsync_RecoversFromCorruptFile` | §9.3 |
| `ContactServiceGroupTests` | `Concurrent_GroupAndContactWritesDoNotDeadlock` | §8.2 (run 100 iterations) |
| `AddressBookViewModelGroupTests` | `CreateGroupCommand_PopulatesGroupsCollection` | §10.1 |
| `AddressBookViewModelGroupTests` | `DeleteGroupCommand_RemovesFromGroupsCollection` | §10.1 |
| `AddressBookViewModelGroupTests` | `SelectedGroupMembers_ResolvesAllValidIds` | §10.1 |
| `AddressBookViewModelGroupTests` | `InsertGroup_CallsInsertActionForEachMember` | §10.1, §12.1 |
| `AddressBookViewModelGroupTests` | `InsertGroup_AnnouncesCount` | §14.2 |
| `AddressBookViewModelGroupTests` | `DeleteGroupCommand_RequestsConfirmation` | §10.3 (Modal Dialog Rule) |
| `GroupManagerViewModelTests` | `RenameAsync_UpdatesName` | §10.2 |
| `GroupManagerViewModelTests` | `AddMembersAsync_AddsEach` | §10.2 |
| `GroupManagerViewModelTests` | `RemoveMemberAsync_Removes` | §10.2 |
| `XamlParseTests` | `GroupManagerWindow_XamlParsesWithoutException` | §11.3 |
| `XamlParseTests` | `AddressBookWindow_XamlParsesWithoutException` (updated) | §11.1 |
| `ViewModelConstructionTests` | `GroupManagerViewModel_Constructs` | (sanity) |
| `ComposeWindowWithGroupsTests` | `OpenAddressBook_SeedsGroups_PickInsertsAllMembers` | §12.1, §12.4 |
| `ComposeWindowWithGroupsTests` | `OpenAddressBook_DuplicateEmailInGroup_IsDeduplicated` | §12.4 |

Total: **~23 new tests.** All use `StubServices` and `Xunit.StaFact` (where WPF is involved).

---

## Appendix A — Sample JSON Shape

`%APPDATA%\QuickMail\groups.json` after seeding:

```json
[
  {
    "Id": 1,
    "Name": "Project Team",
    "MemberContactIds": [12, 7, 3, 19, 22],
    "LastUsedTicks": 638372840000000000
  },
  {
    "Id": 2,
    "Name": "Family",
    "MemberContactIds": [4, 5],
    "LastUsedTicks": 638370120000000000
  },
  {
    "Id": 3,
    "Name": "Empty group",
    "MemberContactIds": [],
    "LastUsedTicks": 0
  }
]
```

- `ResolvedMemberCount` is **not** persisted (System.Text.Json default of `[JsonIgnore]`).
- IDs are assigned by `ContactService.CreateGroupAsync` as `max(existing) + 1`, mirroring the contact id strategy.
- Atomic write via temp-then-rename, same as the other services.

---

## Appendix B — Sample User Flows

### B.1 Create a group and add three members

1. Press `Ctrl+Shift+B` to open the Address Book.
2. Press `Ctrl+Tab` to switch to the **Groups** tab. (Or `Ctrl+G` from anywhere in the window.)
3. Press `Ctrl+Shift+N` ("New group"). The "New group" text box at the bottom of the group list is focused. Type `Project Team`, press `Enter`.
4. Announcement: *"Group 'Project Team' created"*. The new group is selected.
5. Press `Ctrl+Tab` to return to the **Contacts** tab. Type `ali` in the search box. Press `Down` to select "Alice <[email protected]>".
6. Press `Ctrl+Shift+N` again — wait, that opens a new group. Instead, press `Space` to open the "Add to group…" picker (or `Apps` key / Shift+F10 for right-click). Choose `Project Team`. Press `Enter`.
7. Announcement: *"Added Alice <[email protected]> to Project Team"*.
8. Repeat for the other two members.

### B.2 Pick a group while composing

1. Compose a new message. Focus is in the **To** field.
2. Press `Ctrl+Shift+B` to open the Address Book. The **Groups** tab is shown (assuming the user has at least one group).
3. `Project Team` is highlighted. Press `Enter`, or press `Alt+T` ("Add to To").
4. Five addresses are inserted into the To field. Announcement: *"Inserted 5 addresses from group 'Project Team'"*.
5. The Address Book remains open so the user can pick more, or press `Escape` to close it.

### B.3 Delete a group

1. In the Address Book, **Groups** tab, select `Family`.
2. Press `Delete`. Confirmation dialog: *"Delete group 'Family'? Its 2 members will not be deleted."* Press `Enter` to confirm.
3. Announcement: *"Group deleted"*. The group is removed from the list. Contacts 4 and 5 remain in the address book.

### B.4 Handle a deleted member gracefully

1. The user deletes "Bob" from the Contacts tab.
2. The user returns to the Groups tab and selects `Project Team` (which used to contain Bob).
3. Announcement on focus: *"Project Team, 4 members (1 missing contact)"*.
4. `Bob` does **not** appear in the right pane. The group itself is **not** deleted; it simply lists 4 members.
5. `groups.json` is **not** rewritten — the stale id is left in place so an undo of the contact delete (if the user uses the Ctrl+Z stack elsewhere) can re-resolve it.

---

*This spec is ready for Dev Lead implementation. The PM portion (§1–6) and the Dev portion (§7–12) are self-contained; review can be done in either order.*
