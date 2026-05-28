# QuickMail v0.6.6 Release Notes

## New Features

### Address fields now keep each address separate

The **To**, **Cc**, and **Bcc** fields in the compose window now keep each confirmed address as its own button. Type an address and press **Tab**, **Enter**, **comma**, or **semicolon** to confirm it — or select a contact from the autocomplete dropdown. Multiple addresses appear as a row of buttons before the text input cursor. Each one can be navigated to, removed, or copied independently.

**Keyboard navigation:**

- **Left / Right arrow** — move focus between addresses
- **Right arrow** on the last address — move back to the text input
- **Left arrow** at the start of the text input — move to the last address
- **Delete** or **Backspace** on a focused address — remove that address
- **Backspace** in an empty text input — remove the last address
- **Ctrl+C** on a focused address — copy the full address to the clipboard

**Context menu (right-click or Shift+F10 on an address):**

- **Copy Address** — copies the full name and email to the clipboard
- **Add to Address Book** — saves the contact silently with no dialog (shows a message if the address is already saved)
- **Remove** — removes this address from the field

**Check Addresses (Ctrl+K):** Validates every address in all three fields. Addresses that cannot be validated are highlighted in red and their accessible name changes to begin with "Unrecognized:" so screen readers convey the problem without relying on colour alone. Bare names with no @ sign are looked up in your address book — if a single match is found, the address resolves to the full name and email automatically. A summary is announced when the check completes.

**Screen reader behavior:** Each address button's accessible name is the full RFC address (for example, "Kelly Ford &lt;kelly@example.com&gt;"). When you Tab into a field that already has addresses, those addresses are announced immediately so you are not left wondering whether the field is empty.

---

### Open Address Book from the compose window

You can now open the Address Book directly from the compose window with **Ctrl+Shift+B**. When opened this way, a row of three buttons appears at the top of the dialog — **To** (Alt+T), **Cc** (Alt+C), and **Bcc** (Alt+B) — so you can search for a contact and insert them into whichever field you choose. The dialog stays open after each insertion, so you can add several contacts in one session before closing.

When the Address Book is opened from the main window the insert buttons are not shown and the dialog works as before.

---

### Group boundary navigation in grouped views

Two new keyboard shortcuts let you jump to the start or end of a group in Conversations, By Sender, and By Recipient views:

| Shortcut | Action |
|----------|--------|
| **Shift+,** (less-than key) | Jump to the first (newest) message in the current group |
| **Shift+.** (greater-than key) | Jump to the last (oldest) message in the current group |

If the group is collapsed when you press either key, it expands automatically before moving focus. In the flat Messages view the keys do nothing. Both shortcuts are remappable via **File → Settings → Keyboard Shortcuts** and appear in the command palette.

---

### Ctrl+Enter as an alternate send shortcut

You can now send a message from the compose window with **Ctrl+Enter** in addition to the existing **Alt+S**. This is useful if you prefer not to lift your hands from the main keyboard area to reach Alt+S.

---

### Confirmation before emptying trash

**Empty Trash** (`Ctrl+Shift+E`) now shows a confirmation dialog before permanently deleting messages. The dialog reports exactly how many messages will be removed so you know what you are about to do.

If you prefer not to see the confirmation, open **File → Settings**, select the **General** tab, and turn off **Confirm before emptying trash** in the **Mail Actions** group.

---

### Junk folder support

QuickMail now recognises and tracks the **Junk** (spam) folder as a distinct special folder alongside Inbox, Drafts, Sent, and Trash. If your mail server has a dedicated Junk folder, it is no longer lumped together with Trash in QuickMail's folder handling.

---

### About dialog

**Help → About QuickMail** opens an About dialog showing the application version and a link to the MIT License on GitHub. The same dialog is accessible from the Command Palette (**Ctrl+Shift+P**) — search for **About QuickMail**.

---

## Bug Fixes

- **Reading pane closed by incoming mail** — When new mail arrived while you were reading a message, the reading pane appeared to close and focus jumped back to the message list. QuickMail now leaves focus in the reading pane when new mail arrives.

- **Trash messages reappear after Empty Trash** — If you emptied trash and then changed the sort order or applied a filter, previously deleted messages could briefly reappear in the message list. QuickMail now clears the cached message count for each account's Trash folder immediately after a successful empty, so the list stays correct.

- **To, Cc, and Bcc fields not named correctly for screen readers** — The To, Cc, and Bcc fields in the compose window now report their names correctly to screen readers and braille displays. Previously the field names were either missing or announced twice when tabbing between fields.

- **Keyboard shortcuts announced as part of control names** — Keyboard shortcut text (for example "Alt+S" or "Ctrl+Shift+A") was embedded directly in the accessible names of buttons and fields throughout the compose window and toolbar, causing screen readers to read it as part of the control's identity on every focus. Shortcuts are now exposed through the correct UIA AcceleratorKey property, where screen readers can surface them on request rather than announcing them automatically every time focus arrives.
