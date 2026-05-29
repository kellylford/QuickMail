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

**In the Address Book:** Press **Ctrl+A** while the contact list has focus to select all visible contacts.

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
