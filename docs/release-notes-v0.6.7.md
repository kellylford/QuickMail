# QuickMail v0.6.7 Release Notes

## New Features

### Selection keyboard shortcuts

Several standard Windows selection shortcuts that were previously missing are now implemented.

**In the message list:**

| Shortcut | What it does |
|----------|-------------|
| **Ctrl+A** | Select all messages in the current folder |
| **Ctrl+Shift+Home** | Extend the current selection to the first message in the list |
| **Ctrl+Shift+End** | Extend the current selection to the last message in the list |

These compose naturally with the existing **Shift+Up/Down** selection: after pressing **Ctrl+Shift+Home**, pressing **Shift+Down** shrinks the selection from the top, and after **Ctrl+Shift+End**, **Shift+Up** shrinks from the bottom.

**Ctrl+A** is also registered in the Command Palette as **Select All Messages** and can be remapped in **File → Settings → Keyboard Shortcuts**. It is context-sensitive: it only fires when the message list has focus, so pressing Ctrl+A in the search box or any text field still selects the text in that field as expected.

**In the Address Book:** Press **Ctrl+A** while the contact list has focus to select all visible contacts. Press **Delete** to remove all selected contacts (a single confirmation prompt shows the count before anything is deleted).

**In Compose address fields (To/Cc/Bcc):** Press **Ctrl+A** to select all address chips in the field. Selected chips are highlighted. Press **Delete** or **Backspace** to remove them all, or **Ctrl+C** to copy all their addresses. Any arrow-key navigation or click clears the selection. When the field has typed text (not yet committed as a chip), **Ctrl+A** selects that text as usual.

---

### Mark as Read (Ctrl+Q)

A new **Mark as Read** command, bound to **Ctrl+Q** by default, marks the current selection as read without opening it. The command is context-sensitive:

| Context | What gets marked |
|---------|-----------------|
| A single message is selected | That message |
| A group header is selected (Conversations, By Sender, or By Recipient view) | Every message in the group |
| The folder tree has focus | Every message loaded in the current folder or virtual folder |

The shortcut is remappable via **File → Settings → Keyboard Shortcuts** and appears in the Command Palette (**Ctrl+Shift+P**).

---

### To Me filter

A new **To Me** filter shows only messages where one of your configured account addresses appears directly in the **To** field. It is available in **View → Filter → To Me** and in the Command Palette.

Mailing list messages are excluded even when the list software copies your address into the To field alongside the list address. Detection uses the standard `List-Id` header that all compliant mailing list managers send. Messages already in your database are retroactively identified using a domain pattern backfill (groups.io, Freelists, Listserv, Mailman, Yahoo Groups, Google Groups) that runs automatically on first launch after updating.

The filter can be saved as part of a named view and assigned a custom keyboard shortcut in **File → Settings → Keyboard Shortcuts**.

---

### Action-first log format

The **Advanced** tab in **File → Settings** now includes a **Log Format** option controlling how lines are written to `quickmail.log`.

**Action first** (the new default) puts the message before the timestamp:

```
Sync completed: 42 messages  [2024-11-15 14:23:45.123]
```

**Time first** preserves the original format with the timestamp at the start:

```
2024-11-15 14:23:45.123  Sync completed: 42 messages
```

Because the log is already in chronological order, the timestamp at the start added no navigation value — it just pushed the information you actually wanted to the right. Action first makes each line readable from the beginning, which is significantly better when reviewing the log with a screen reader. The setting takes effect immediately when you save; no restart is needed.

---

## Accessibility fixes

### Status bar no longer reads content multiple times

Pressing **Ctrl+9** to move focus to the status bar previously caused screen readers to announce the status text four times: once as the container label, once as the TextBox name, once as the TextBox value, and once as the screen reader's own region-entry announcement.

The status bar regions now use focusable text elements (ControlType.Text) instead of read-only text boxes (ControlType.Edit). A text element has no separate Value property, so the content is announced exactly once when focus arrives. The status bar container no longer carries a redundant name that duplicated the content announcement.

The "Press Escape to return to message list" hint that appeared in the status text after opening a message has also been removed from the status bar. That hint was already being delivered as a one-time screen reader announcement immediately after the message loaded — having it persist in the status bar meant it was repeated every time focus visited the bar.
