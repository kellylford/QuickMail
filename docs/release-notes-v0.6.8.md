# QuickMail v0.6.8 Release Notes

## New Features

### Contact groups

The Address Book now has a **Groups** tab alongside the contacts list. Groups let you bundle any number of contacts under a name, then insert every member into a To, Cc, or Bcc field in a single step — either from the address book itself or by typing the group name in a compose address field and selecting it from the autocomplete list.

All group data is stored locally in `groups.json` in your AppData folder and never leaves your machine. Groups are completely separate from `contacts.json`; adding or removing a group never touches your individual contacts.

**Opening the Groups tab**

- In the Address Book window, choose the **Groups** tab at the top, or press **Ctrl+G** to jump to the groups list from anywhere in the window. Pressing **Ctrl+G** again returns you to the Contacts tab.
- Press **Ctrl+Shift+M** (or activate the **Manage…** button on the Groups tab) to open the standalone **Group Manager** window for heavier editing — renaming, bulk member changes, and deletion — without the contacts list in the way.

**Creating a group**

Press **Ctrl+Shift+N** from anywhere in the Address Book, or activate the **New** button on the Groups tab. A name field appears above the buttons. Type the name and press **Enter**. Press **Escape** to cancel without creating a group.

**Adding contacts to a group**

- From the **Contacts** tab, select a contact, then press **Shift+F10** (or the **Apps** key) to open the context menu and choose which group to add them to.
- In the **Group Manager** window, select a group on the left, then press **Enter** on a candidate in the lower list to add them. Pressing **Enter** again on a contact who is already a member removes them — the action always toggles, so you never get a repeated "Added" announcement.

**Inserting a group into a message**

From the **compose** address fields, type any part of a group name and the autocomplete list shows matching groups (above individual contacts). Selecting a group expands all its members directly into the field. The screen reader announces the count: "Inserted N addresses from group 'X'".

From the **Address Book** window, select a group on the Groups tab and press **Alt+T**, **Alt+C**, or **Alt+B** — or activate the **To**, **Cc**, or **Bcc** buttons. Every member is inserted in order of recency. If opened from the main window (not from a compose window), a new compose window opens automatically to receive the addresses.

**Renaming a group**

Select a group in the list and press **F2**, or activate the **Rename** button. The name field appears pre-filled with the current name. Edit it and press **Enter** to save, or **Escape** to cancel.

**Deleting a group**

Select a group and press **Delete** (or activate the **Delete** button). A confirmation appears. Deleting a group does **not** delete the contacts that were in it.

**Missing contacts**

If a contact referenced by a group is later deleted, the group retains the membership record. The groups list shows "N members (M missing)" so you can see when cleanup is needed. The missing member is silently skipped during insertion and the screen reader notes the skip count.

**Keyboard reference for the Address Book window**

| Action | Shortcut |
|--------|----------|
| Switch to Groups tab / return to Contacts tab | `Ctrl+G` |
| New group (shows name field) | `Ctrl+Shift+N` |
| Rename selected group | `F2` |
| Delete selected group | `Delete` |
| Add selected contact to a group | `Shift+F10` or `Apps` key (context menu) |
| Open Group Manager | `Ctrl+Shift+M` |
| Insert group into To field | `Alt+T` |
| Insert group into Cc field | `Alt+C` |
| Insert group into Bcc field | `Alt+B` |

---

## Bug Fixes

- **Delete key on group members list** — pressing Delete while focus is in the group members list now removes the focused member from the group instead of deleting the entire group.
- **Alt+T / Alt+C / Alt+B** — these shortcuts now work correctly on the Groups tab as well as the Contacts tab, and they work when the address book is opened standalone (not just from a compose window).

## Internal

- `IContactService` gained nine group methods: `LoadAllGroupsAsync`, `CreateGroupAsync`, `RenameGroupAsync`, `DeleteGroupAsync`, `AddMemberAsync`, `RemoveMemberAsync`, `ListGroupsForContactAsync`, `TouchGroupAsync`, and `SearchGroupsAsync`. `StubContactService` implements the same surface for tests.
- `ContactService` writes groups to `groups.json` (separate from `contacts.json`) using the same atomic temp-then-rename pattern. A corrupt `groups.json` is renamed to `groups.json.bak-{timestamp}` and treated as empty, matching the recovery behaviour already used for `views.json`, `rules.json`, and `templates.json`.
- `GroupModel` exposes computed `ResolvedMemberCount` and `MissingContactCount` (`[JsonIgnore]`) so the UI never recomputes them.
- `AddressSuggestion` (internal to `ComposeWindow`) wraps either a `ContactModel` or a `GroupModel`; the autocomplete popup uses a two-line item template (bold name / dimmed secondary text) and sets `AutomationProperties.Name` to the full accessible label on each row.
- 59 new tests added across `ContactServiceGroupTests`, `AddressBookViewModelGroupTests`, `GroupManagerViewModelTests`, `GroupsTabFocusTests`, and `GroupSubtitleConverterTests`. Total test count is now 391, all green.
