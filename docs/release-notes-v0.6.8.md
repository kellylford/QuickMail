# QuickMail v0.6.8 Release Notes

## New Features

### Contact groups

The Address Book now includes a **Groups** tab alongside the existing flat contact list. Groups let you bundle contacts under a name and insert every member into a To, Cc, or Bcc field in a single step. All group data is stored locally in a separate JSON file in your AppData folder; nothing leaves your machine.

**Opening the Groups tab**

- In the Address Book window, choose the **Groups** tab at the top, or press **Ctrl+G** to jump straight to the groups list.
- For a focused, dialog-only experience, press **Ctrl+Shift+M** to open the **Group Manager** window. It has the same create / rename / delete / add-member / remove-member actions but without the contacts list around it.

**Creating a group**

1. In the **Groups** tab, type a name in the **New group** text box at the bottom.
2. Press **Enter** or click **New**. The new group appears in the list and is selected automatically.

**Adding contacts to a group**

In the Address Book window:

1. Switch to the **Contacts** tab and select the contact you want to add.
2. Switch back to the **Groups** tab, select the target group, and press **Enter** on the group (or use the dedicated flow).

The member list on the right refreshes and the screen reader announces "Added {name} to {group}".

In the **Group Manager** window (`Ctrl+Shift+M`):

1. Select a group on the left.
2. Select a contact in the **Available contacts** list and press **Enter** to add them, or **Delete** to remove a member from the middle list.

**Inserting a group into a message**

When a compose window is open, the Address Book window can insert every member of a group into the To, Cc, or Bcc field in one operation:

1. Open the Address Book from the compose window with **Ctrl+Shift+B**.
2. Switch to the **Groups** tab and select a group.
3. Choose **To**, **Cc**, or **Bcc** from the field buttons. Every member is inserted, sorted by recency of use. The screen reader announces the final count, including any members that were silently skipped because their contact was deleted.

**Renaming, deleting, and missing contacts**

- **F2** renames the selected group; **Delete** removes it after a confirmation prompt. Deleting a group does **not** delete the contacts that were in it.
- If a contact referenced by a group is later deleted, the group keeps the member ID and the right pane shows a "N members (M missing)" annotation so you can see when cleanup is needed.

**Keyboard reference for the Address Book window**

| Action | Shortcut |
|--------|----------|
| Switch to Contacts tab | `Ctrl+1` |
| Switch to Groups tab / focus groups pane | `Ctrl+G` |
| New group | `Ctrl+Shift+N` |
| Rename group | `F2` |
| Delete group | `Delete` |
| Open Group Manager | `Ctrl+Shift+M` |

---

## Bug Fixes

- **Group / contact concurrency** — Group operations (create, rename, add member, remove member) and contact operations now share a single load lock, so a group write cannot tear or block a concurrent contact write. The same lock also covers corrupt-file recovery on first load.

## Internal

- `IContactService` gained eight group methods (`LoadAllGroupsAsync`, `CreateGroupAsync`, `RenameGroupAsync`, `DeleteGroupAsync`, `AddMemberAsync`, `RemoveMemberAsync`, `ListGroupsForContactAsync`, `TouchGroupAsync`). `StubContactService` implements the same surface for tests.
- `ContactService` writes groups to a new `groups.json` file (separate from `contacts.json`) using the same atomic temp-then-rename pattern. A corrupt `groups.json` is renamed to `groups.json.bak-{timestamp}` and treated as empty, matching the recovery behaviour already used for `views.json`, `rules.json`, and `templates.json`.
- 32 new tests across `ContactServiceGroupTests` (18), `AddressBookViewModelGroupTests` (8), and `GroupManagerViewModelTests` (6) cover group CRUD, membership, missing-contact handling, atomic write, corrupt-file recovery, and concurrent group/contact writes. Total test count is now 376, all green.
