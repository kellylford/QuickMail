# QuickMail v0.6.9 Release Notes

## Download

Two options are available for v0.6.9:

| Download | When to use |
|----------|-------------|
| **`quickmail-v0.6.9-setup.exe`** — Windows installer | Recommended for most users. Installs per-user with no elevation required, checks for the WebView2 Runtime, and registers an uninstaller. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

The installer requires the **Microsoft Edge WebView2 Runtime** to render HTML messages. It is preinstalled on Windows 11 and most recent Windows 10 systems; the installer downloads and installs it automatically when missing.

### Uninstalling

Uninstall from **Windows Settings → Apps**. The uninstaller gives you a choice: remove the app only, or remove the app and all your local data — the accounts configuration, mail cache, contacts, rules, templates, and saved views stored in `%AppData%\QuickMail`. Passwords in Windows Credential Manager are never touched.

Uninstalling QuickMail does not affect your mail on the server. Nothing on the server is ever changed unless you take an explicit action in the app. The only time QuickMail permanently deletes messages is when you choose **Empty Trash**.

---

## New Features

### View Properties (Alt+Enter)

Pressing **Alt+Enter** anywhere in QuickMail opens a read-only **Properties** dialog for whatever is currently selected — the same shortcut Windows uses for properties everywhere.

**What opens depends on where focus is:**

| Focus location | Properties shown |
|----------------|-----------------|
| A message in the message list | From, To, Cc, Reply-To, Subject, Date, Message-ID; account, folder, and IMAP UID; format and attachment count |
| A conversation header (Conversations view) | Subject, message count, unread count, all participants, newest and oldest message dates |
| A sender group header (From view) | Sender, message count, unread count, most recent date, and newest subject |
| A recipient group header (To view) | Recipient, message count, unread count, most recent date, and newest subject |
| A folder in the folder tree | Folder name, full path, account, and message counts |
| An account in the account list | Display name, email address, IMAP and SMTP server details, authentication method |
| A contact in the address book | Display name, email address, group memberships, and last-used date |
| A group in the address book | Group name, member count, missing contacts, and a list of all members |
| An attachment in the reading pane | File name, MIME type, and size |

**In grouped views (Conversations, From, To):** pressing Alt+Enter on a group header row shows properties for the group itself. To see properties for an individual message inside a group, expand the group, navigate to the message, and press Alt+Enter.

**Navigating the Properties dialog:**

The dialog contains a single properties list. Arrow keys move continuously through all rows, including section headers that appear as named entries in the list. Tab moves to the Copy all and Close buttons.

- **Enter** or **Ctrl+C** on a selected row copies it to the clipboard. Section headers copy just the name; data rows copy "Field: Value".
- **Ctrl+A** selects all rows. Pressing **Enter** or **Ctrl+C** then copies all selected rows at once.
- **Copy all** copies every property as structured plain text organized by section.
- **Escape** or **Close** dismisses the dialog and returns focus to where it was.

---

## Accessibility

- **Properties list is one continuous list** — all rows and section headers are in a single list navigable with arrow keys, so there are no separate focus stops between sections.
- **Section headers are focusable** — section names (for example, "Headers", "Storage") appear as entries in the list and are announced by name when focused.
- **Properties hint respects hint suppression** — the "Press Enter or Ctrl+C to copy" instruction is delivered through the app's hint system and is silenced when you have turned hints off in Settings.

---

## Bug Fixes

- **Alt+Enter from the command palette** — pressing Alt+Enter when the command palette had focus silently did nothing; it now correctly shows properties for the previously focused item.
- **Alt+Enter on the folder tree** — if you pressed Alt+Enter immediately after arrowing to a folder (before pressing Enter to commit the selection), properties for the wrong folder could be shown; focus is now read directly from the tree's live selection.
- **Alt+Enter on a conversation or sender group header** — previously fell back to showing properties for the first message in the group instead of group-level properties.

---

## Internal

- `ConversationPropertiesBuilder` and `SenderGroupPropertiesBuilder` added alongside the existing context builders.
- `PropertiesViewModel` now exposes a flat `IReadOnlyList<FlatRow>` (with interleaved header rows) rather than split `FieldSections`/`SubListSections` collections.
- `PropertyItemNameConverter` updated to produce `"Label"` for header rows and `"Label: Value"` for data rows.
- 49 new tests added. Total test count is now 440, all green.
