# QuickMail User Guide

QuickMail is a keyboard-centric Windows email client for IMAP accounts. All features are reachable from the keyboard; no mouse is required.

---

## Contents

- [System Requirements](#system-requirements)
- [Adding Accounts](#adding-accounts)
- [Main Window](#main-window)
- [Reading Mail](#reading-mail)
- [Composing Mail](#composing-mail)
- [Address Book](#address-book)
- [Grab Addresses from a Message](#grab-addresses-from-a-message)
- [Flags](#flags)
- [Mail Rules](#mail-rules)
- [Saved Views](#saved-views)
- [Settings](#settings)
- [Screen Reader Announcements](#screen-reader-announcements)
- [Keyboard Shortcuts](#keyboard-shortcuts)

---

## System Requirements

- Windows 10 (1703 or later) or Windows 11
- Microsoft Edge WebView2 Runtime (the installer checks for this automatically)
- An IMAP/SMTP email account (Gmail, Outlook.com, Microsoft 365, or any standard IMAP provider)

---

## Adding Accounts

Open **Settings → Accounts** (`Ctrl+,` then navigate to Accounts) or press **Ctrl+Shift+A** from the main window. Press **Add Account**.

### Standard IMAP/SMTP

Choose **Standard IMAP/SMTP** as the account type and fill in:

- **Display name** — shown as the "From" name on outgoing mail
- **Email address**
- **Password**
- **IMAP server**, port, and whether to use SSL
- **SMTP server**, port, and whether to use SSL

Press **Verify** to test the connection before saving.

### Microsoft Account (Outlook.com / Microsoft 365)

Choose **Microsoft OAuth**. The server fields fill in automatically. Press **Sign in with Microsoft** — your browser opens to a Microsoft sign-in page. Sign in and grant QuickMail permission. Switch back to QuickMail and press **Save**.

### Gmail (Google Account)

Choose **Google OAuth**. Press **Sign in with Google** — your browser opens to a Google sign-in page. Sign in and grant QuickMail permission to read and send mail. Switch back to QuickMail and press **Save**. Server settings fill in automatically.

Your credential is stored in Windows Credential Manager and refreshes automatically. You are not prompted to sign in again unless you revoke access from your Google account settings.

### Managing Accounts

Open **Settings → Accounts** to rename, edit, or remove an account, or to sign out of an OAuth account. Removing an account does not delete mail from the server.

---

## Main Window

The main window has four panes reachable by pressing **F6** to cycle forward or **Shift+F6** to cycle backward:

1. **Account list** — your email accounts
2. **Folder tree** — folders for the selected account, or all accounts in unified view
3. **Message list** — messages in the selected folder
4. **Reading pane** — the currently selected message

You can also jump directly to any pane:

| Shortcut | Destination |
|----------|-------------|
| `Ctrl+1` | Account list |
| `Ctrl+2` | Folder tree |
| `Ctrl+3` | Message list |
| `Ctrl+9` | Status bar |

### Unified Inbox

Select **All Inboxes** at the top of the folder tree to see messages from all accounts merged into one list, sorted by date.

### Message List Views

Press `Ctrl+Shift+V` (or use the **View** menu) to switch how messages are grouped:

- **Messages** — flat list, newest first
- **Conversations** — messages grouped by thread
- **From** — messages grouped by sender
- **To** — messages grouped by recipient

### Searching

Press `Ctrl+Shift+S` to open the search box. Type your query and press Enter. Results appear in the message list. Press Escape to clear the search and return to the full folder.

### Searching Folders

Press `Ctrl+Shift+F` to search folder names. Type to filter the tree, press Enter to navigate to the matching folder.

### Refreshing

Press **F5** to manually refresh the current folder.

### Command Palette

Press **Ctrl+Shift+P** to open the command palette. Type any part of a command name to find it. Press Enter to run it. This is the fastest way to discover and run any action in the app.

### Keyboard Customization

Open **Settings → Keyboard** to reassign any shortcut to a different key. Type in the field for a command to capture a new key combination. Changes take effect immediately and survive restarts.

---

## Reading Mail

### Opening a Message

Press Enter on a message in the list to open it in the reading pane (or in a new tab or window, depending on your **Reading Mode** setting in **Settings → General**).

Press **Ctrl+Enter** to open a message in a new tab regardless of the Reading Mode setting.

### Reading Pane

The reading pane renders HTML messages with WebView2. Links open in your default browser. Images from remote sources are blocked by default.

Press **F6** or **Shift+F6** to move between the reading pane and other panes.

### Message Windows

When Reading Mode is set to **Window**, messages open in a separate window. Each window has a full menu bar (**File, Message, Navigate**), a toolbar, and a command palette. Shortcuts work the same as in the main window:

| Shortcut | Action |
|----------|--------|
| `Ctrl+R` | Reply |
| `Ctrl+Shift+R` | Reply All |
| `Ctrl+F` | Forward |
| `Delete` | Delete |
| `Ctrl+Q` | Mark as Read |
| `Ctrl+Shift+G` | Grab Addresses |

Deleting a message from its window closes the window and returns focus to the originating position in the message list.

### Message Properties

Press **Alt+Enter** on any message to open a properties window showing sender, recipients, date, size, and flags.

### Marking as Read

Press `Ctrl+Q` to mark the selected message or messages as read. Messages are also marked read automatically when you open them (configurable in **Settings → General**).

### Deleting Messages

Press **Delete**. Deleted messages go to Trash. Press `Ctrl+Shift+E` to empty the Trash for the selected account.

### Tabs

QuickMail can open messages in tabs, keeping multiple messages visible at once.

| Shortcut | Action |
|----------|--------|
| `Ctrl+Tab` | Next tab |
| `Ctrl+Shift+Tab` | Previous tab |
| `Ctrl+W` | Close tab |
| `Ctrl+Shift+`` ` | Tab list (navigate by name) |
| `Ctrl+Shift+T` | Focus tab strip |

---

## Composing Mail

### Opening a Compose Window

| Shortcut | Action |
|----------|--------|
| `Ctrl+N` | New message |
| `Ctrl+R` | Reply |
| `Ctrl+Shift+R` | Reply All |
| `Ctrl+F` | Forward |

### Compose Panes

Press **F6** to cycle between the address fields (To, Cc, Bcc), the subject, and the message body.

### Address Autocomplete

Start typing a name or address in To, Cc, or Bcc. QuickMail searches your address book and recent contacts. Arrow down to choose a suggestion; press Enter or Tab to accept. Press Escape to dismiss without accepting.

### Editing Modes

Every compose window offers three modes, switchable at any time with `Ctrl+Shift+1/2/3` or the **View** menu:

| Mode | Shortcut | Description |
|------|----------|-------------|
| Plain Text | `Ctrl+Shift+1` | Unformatted text |
| Markdown | `Ctrl+Shift+2` | Write Markdown; sent as formatted HTML |
| HTML | `Ctrl+Shift+3` | Rich text editor with real formatting |

Switching from a rich mode to Plain Text asks for confirmation because formatting would be lost.

Messages composed in Markdown or HTML are sent with both an HTML part and a plain text part.

### Formatting (Markdown and HTML Modes)

| Command | Shortcut |
|---------|----------|
| Bold | `Ctrl+B` |
| Italic | `Ctrl+I` |
| Underline (HTML only) | `Ctrl+U` |
| Strikethrough | `Ctrl+Shift+X` |
| Heading 1 / 2 / 3 | `Ctrl+Alt+1` / `Ctrl+Alt+2` / `Ctrl+Alt+3` |
| Bullet list | `Ctrl+Shift+L` |
| Numbered list | `Ctrl+Shift+N` |
| Insert link | `Ctrl+L` |
| Clear formatting | `Ctrl+Space` |
| Announce formatting at cursor | `Ctrl+T` |
| Show formatting in browsable list | `Ctrl+Shift+T` |

**Nested lists:** In a list, press **Tab** to indent an item (creating a sub-list); press **Shift+Tab** to dedent.

### Checking Formatting (HTML Mode)

- **`Ctrl+T`** — announces a one-line summary: "Heading 2. Bold on, Italic off, Underline off."
- **`Ctrl+Shift+T`** — opens a small window listing the same details one per row. Arrow through them; press Escape or Enter to close.

### Preview (Markdown and HTML Modes)

Press **F8** to open a rendered preview in a separate window. The preview is fully focusable, so you can browse the formatted output exactly as a recipient would. Links open in your default browser. Press **Escape** or **Ctrl+W** to close the preview.

### Spell Check

| Shortcut | Action |
|----------|--------|
| `F7` | Jump to next misspelling |
| `Shift+F7` | Jump to previous misspelling |

Spell check wraps from end to beginning (and beginning to end for Shift+F7) so it always finds misspellings wherever the cursor starts.

### Attachments

Attach files with `Ctrl+Shift+A` in the compose window, by pasting files from the clipboard (`Ctrl+V`), or by dragging and dropping them onto the window. A screen reader announces how many files were attached.

### Sending

| Shortcut | Action |
|----------|--------|
| `Alt+S` or `Ctrl+Enter` | Send |

### Auto-Save Drafts

QuickMail saves your compose as a draft automatically every 2 minutes (on by default). A quiet status line in the compose window shows "Auto-saved 3:42 PM" after each save — no announcement interrupts your writing. If a save fails, it is announced once. You can check the last auto-save time from the command palette: **Ctrl+Shift+P → Announce Last Auto-Save**.

Control auto-save in **Settings → General → Composing**: turn it off, change the interval (30 seconds to 10 minutes), and set the default compose mode for new messages.

### Forwarding with Attachments

When forwarding a message that has attachments, QuickMail opens an **Include Attachments** dialog before downloading. All attachments are checked by default. Arrow between files and press Space to toggle individual ones. Press Tab to reach Forward (include checked files) or Cancel.

---

## Address Book

Press **Ctrl+Shift+B** to open the address book.

The address book lists everyone you have sent mail to or explicitly added. You can search by name or address, edit contact details, and organize contacts into groups.

### Groups

Groups let you write to multiple people with a single address. Select a group in the address book and press Enter to expand it and see its members. To compose to a group, type the group name in the To or Cc field and select it from autocomplete.

### Managing Groups

Open the address book and use the **Groups** pane to create, rename, and delete groups, and to add or remove members.

---

## Grab Addresses from a Message

When reading a message with many recipients, you can save all of them to your address book — and optionally add them to a group — in one step.

1. Open a message and press **Ctrl+Shift+G**.
2. The **Add to Address Book** window lists every address from the message (From, To, and Cc), all checked by default. Uncheck any you do not want.
3. **To add contacts only:** press **Save** (or Enter).
4. **To add contacts to a group:** check **Add to group**, then:
   - Choose an existing group from the **Group** combo box, or
   - Choose **Create new group** and type a name in the **New group name** field.
   - Press **Save**.
5. Press **Cancel** or **Escape** to close without saving.

Tab moves through the address list (one Tab stop for the whole list — arrow keys move between individual checkboxes), then to **Add to group**, then to the group combo, then to the name field, then to Save and Cancel.

---

## Flags

Flags mark messages for follow-up and sync across all mail clients.

### Basic Flagging

Press **K** to toggle the default flag on the selected message. Press **K** again to clear it. A screen reader announces the result: "Flagged: Urgent." or "Unflagged."

In Conversations, From, or To view, pressing **K** on a group row flags every message in the group.

### Named Flags

Press **Ctrl+Shift+K** to open the flag picker. Arrow to a flag and press Enter to apply it. A **Clear flag** option at the bottom removes the current flag.

### Creating and Managing Flags

Open the **Flag Manager** from the command palette (**Ctrl+Shift+P → Manage Flags**). You can create up to 20 named flags, each with an optional color. Use **Set as K default** to make any flag the one that **K** applies.

### Filtering by Flag

Open the View menu or the filter combo box and choose **Flagged** to see only flagged messages in the current folder.

### All Flagged Mail

The **All Flagged Mail** virtual folder in the folder tree aggregates flagged messages across all accounts.

### Flag Accessibility

The flag name is announced before the read status when you navigate to a flagged message — for example, "Urgent. Unread. Kelly Ford. Budget deadline." This makes it immediately clear a message needs attention. You can turn this off in **Settings → General → Accessibility → Announce flag status**.

---

## Mail Rules

Mail rules run automatically when mail arrives and can move, flag, mark as read, or delete messages based on sender, recipient, subject, or other criteria.

Open the **Rules Manager** from the **Tools** menu or the command palette. Each rule has:

- **Conditions** — criteria the message must match (any or all)
- **Actions** — what to do when conditions are met (move to folder, flag, mark read, delete)
- **Active** toggle — turn a rule on or off without deleting it

Rules run in order. Drag to reorder, or use the Move Up / Move Down commands.

---

## Saved Views

A saved view is a named filter you can return to instantly — for example, "Unread from work" or "Flagged in the last 7 days."

Open the **View Manager** from the **View** menu or the command palette. Create a view by choosing a folder (or All Inboxes), a message filter, and optionally a date limit. Assign a hotkey to jump to it directly.

Press the assigned hotkey from anywhere in the main window to switch to that view immediately.

---

## Settings

Press **Ctrl+,** to open Settings.

### General

- **Reading Mode** — Reading Pane, Tab, or Window
- **Mark messages read** — automatically on open, or manually only
- **Default compose mode** — Plain Text, Markdown, or HTML
- **Auto-save drafts** — on/off and interval

### Accounts

Add, edit, or remove accounts. Sign out of OAuth accounts.

### Keyboard

Reassign shortcuts for any registered command.

### Screen Reader Announcements

Control which categories of announcements QuickMail makes:

| Setting | What it controls |
|---------|-----------------|
| Custom Announcements | Master on/off for all programmatic announcements |
| Announce hints | Instructional tips ("Press Escape to return") |
| Announce status | Background progress (sync, loading, connection state) |
| Announce results | Action outcomes (messages moved, addresses saved, flag changes) |
| Announce formatting while navigating | Block type announced when caret enters a new paragraph type in HTML compose |
| Announce flag status | Flag name prepended to message row when navigating the list |

All settings default to on except **Announce flag status** and **Announce spelling while typing** (off by default). Turn off **Custom Announcements** to silence everything at once; turn it back on to restore your individual preferences.

---

## Screen Reader Announcements

QuickMail uses UIA Notification events (the correct API for desktop screen readers on Windows 10 and later) rather than ARIA live regions, which only work in web browsers.

Every announcement is optional and controlled by the settings above. No custom screen reader scripting is required; the app works out of the box with any screen reader.

---

## Keyboard Shortcuts

### Main Window

| Shortcut | Action |
|----------|--------|
| `F6` / `Shift+F6` | Cycle panes forward / backward |
| `Ctrl+1` | Focus account list |
| `Ctrl+2` | Focus folder tree |
| `Ctrl+3` | Focus message list |
| `Ctrl+9` | Focus status bar |
| `Ctrl+Shift+P` | Command palette |
| `Ctrl+N` | New message |
| `Ctrl+R` | Reply |
| `Ctrl+Shift+R` | Reply All |
| `Ctrl+F` | Forward |
| `Delete` | Delete |
| `Ctrl+Q` | Mark as Read |
| `Ctrl+A` | Select all messages (message list) |
| `Alt+Enter` | Message properties |
| `F5` | Refresh |
| `Ctrl+Shift+E` | Empty Trash |
| `K` | Toggle flag |
| `Ctrl+Shift+K` | Pick flag |
| `Ctrl+Shift+S` | Search messages |
| `Ctrl+Shift+F` | Search folders |
| `Ctrl+Shift+V` | View menu |
| `Ctrl+Shift+G` | Grab Addresses from Message |
| `Ctrl+Shift+B` | Address Book |
| `Ctrl+,` | Settings |
| `F1` | User Guide |
| `Shift+,` | First message in group |
| `Shift+.` | Last message in group |

### Tabs

| Shortcut | Action |
|----------|--------|
| `Ctrl+Enter` | Open message in new tab |
| `Ctrl+Tab` | Next tab |
| `Ctrl+Shift+Tab` | Previous tab |
| `Ctrl+W` | Close tab |
| `Ctrl+Shift+T` | Focus tab strip |
| `Ctrl+Shift+`` ` | Tab list |

### Compose Window

| Shortcut | Action |
|----------|--------|
| `F6` / `Shift+F6` | Cycle between address fields, subject, and body |
| `Alt+S` or `Ctrl+Enter` | Send |
| `Ctrl+Shift+1/2/3` | Switch to Plain Text / Markdown / HTML mode |
| `F7` / `Shift+F7` | Next / previous misspelling |
| `F8` | Open preview (Markdown and HTML) |
| `Ctrl+B` | Bold |
| `Ctrl+I` | Italic |
| `Ctrl+U` | Underline (HTML only) |
| `Ctrl+Shift+X` | Strikethrough |
| `Ctrl+Alt+1/2/3` | Heading 1 / 2 / 3 |
| `Ctrl+Shift+L` | Bullet list |
| `Ctrl+Shift+N` | Numbered list |
| `Ctrl+L` | Insert link |
| `Ctrl+Space` | Clear formatting |
| `Ctrl+T` | Announce formatting at cursor |
| `Ctrl+Shift+T` | Show formatting in browsable list |
| `Ctrl+Shift+A` | Add attachment |
| `Ctrl+Shift+P` | Command palette |
| `Escape` | Close window (when no menu or dropdown is open) |
