# QuickMail User Guide

QuickMail is a desktop email client for Windows. It supports multiple IMAP/SMTP email accounts at the same time, offers a unified **All Mail** view across all your accounts, groups messages into conversation threads, and renders HTML emails in a secure reading pane. The whole app is designed to be used entirely from the keyboard if you prefer.

---

## Contents

- [Layout](#layout)
- [Menu bar](#menu-bar)
- [Connecting accounts](#connecting-accounts)
- [Managing accounts](#managing-accounts)
- [Reading mail](#reading-mail)
- [Virtual folders](#virtual-folders)
- [Conversation view](#conversation-view)
- [From view (by sender)](#from-view-by-sender)
- [To view (by recipient)](#to-view-by-recipient)
- [Address book](#address-book)
- [Command palette](#command-palette)
- [Context menus](#context-menus)
- [Composing messages](#composing-messages)
- [Drafts](#drafts)
- [Deleting messages](#deleting-messages)
- [Security](#security)
- [Keyboard shortcut reference](#keyboard-shortcut-reference)
- [Data storage](#data-storage)
- [Configuration file](#configuration-file)

---

## Layout

QuickMail uses a three-pane layout:

| Pane | Location | What it shows |
|------|----------|---------------|
| **Account list** | Left, top | All your configured email accounts |
| **Folder list** | Left, bottom | Folders for the selected account (Inbox, Drafts, Sent, Trash, etc.) |
| **Message list** | Centre | Messages in the selected folder |
| **Reading pane** | Right / bottom | The full body of the selected message, rendered as HTML when available |

A toolbar at the top provides buttons for the most common actions.

---

## Menu bar

The menu bar at the top of the window provides access to all major features, organized by task:

| Menu | Contains |
|------|----------|
| **File** | New Message, Manage Accounts, Address Book, Exit |
| **Message** | Reply, Reply All, Forward, Delete, Empty Trash, Move/Copy to Folder, Grab Addresses |
| **View** | Refresh, View Mode (Messages / Conversations / By Sender / By Recipient), Sync Range (7 Days / 30 Days / 6 Months / 1 Year / All Mail), Go to Folder, Command Palette |
| **Help** | User Guide |

All menu items show their keyboard shortcuts for quick reference. You can also press **Alt** or **F10** to activate the menu bar if you prefer keyboard-only navigation.

---

## Connecting accounts

### Standard IMAP/SMTP accounts (password-based)

This covers most email providers: Gmail (with app passwords), Fastmail, Zoho, self-hosted servers, and so on.

1. Activate **Accounts** in the toolbar.
2. In the Account Manager dialog, activate **Add Account**.
3. Fill in the following fields:

   | Field | What to enter |
   |-------|---------------|
   | **Display Name** | The name shown in the From field when you send mail |
   | **Email Address** | Your full email address |
   | **Authentication** | Select **Password** |
   | **IMAP Host / Port** | Your provider's incoming mail server (e.g. `imap.gmail.com`, port `993`) |
   | **SMTP Host / Port** | Your provider's outgoing mail server (e.g. `smtp.gmail.com`, port `587`) |
   | **Password** | Your account password (stored securely — see [Security](#security)) |

4. Activate **Test Connection** to check that everything is correct.
5. Activate **Save**.

### Outlook.com / Microsoft personal accounts (OAuth2)

Outlook.com requires **OAuth2 / Modern Authentication**. A plain password will not work.

> **Before adding the account**, make sure IMAP is enabled for your Outlook.com mailbox.
> Go to **[Outlook.com Settings → Mail → Sync email → POP and IMAP](https://outlook.live.com/mail/0/options/mail/accounts/popImap)** and turn IMAP on.
> Microsoft's help article: https://support.microsoft.com/en-us/office/pop-imap-and-smtp-settings-for-outlook-com-d088b986-291d-42b8-9564-9c414e2aa040

1. Activate **Accounts** in the toolbar, then activate **Add Account**.
2. Enter your **Display Name** and **Email Address**.
3. Change **Authentication** to **Microsoft OAuth2 (Outlook.com)**.
   - The IMAP and SMTP server fields fill in automatically with the correct Outlook.com values.
4. Activate **Sign in with Microsoft**. A browser window opens.
5. Sign in with your Microsoft account and grant the requested permissions. The browser window closes automatically when done.
6. Activate **Save**.

Your Microsoft password is never seen or stored by QuickMail — only an encrypted OAuth2 token is kept locally.

---

## Managing accounts

- Open **Accounts** from the toolbar to see all configured accounts.
- Select an account in the list to edit its settings.
- Activate **Remove Account** to delete an account. You will be asked to confirm before anything is removed.
- Changes take effect after you activate **Save** and close the dialog.

---

## Reading mail

### Refreshing / checking for new mail

- Press **F5** or activate **Refresh** in the toolbar to fetch new messages for the current folder.
- QuickMail also checks for new mail automatically in the background while the app is running.

### Navigating the panes

| Action | How |
|--------|-----|
| Focus the account list | `Ctrl+1`, then use **Up/Down** arrow keys |
| Focus the folder list | `Ctrl+2`, then use **Up/Down** + **Enter** to open a folder |
| Focus the message list | `Ctrl+3`, then use **Up/Down** to move between messages |
| Open a message in the reading pane | Press **Enter** or select the message |
| Move focus into the reading pane | `Ctrl+4` or **F6** |
| Return focus to the message list | **Escape** or **F6** from the reading pane |
| Cycle focus forward through all panes | **F6** |
| Cycle focus backward | **Shift+F6** |

### Quick folder picker (Ctrl+Y)

Press `Ctrl+Y` to open a searchable list of every folder across all your accounts. Start typing to filter the list, then press **Enter** to jump straight to that folder.

### Load More (Ctrl+M)

The message list shows the 100 most recent messages by default. Activate **Load More** at the bottom of the list, or press `Ctrl+M`, to load the next 100.

### All Mail

At the top of the folder list is an **All Mail** entry. Selecting it shows messages from all folders across all your accounts, sorted newest-first. This is a virtual view — no messages are moved or copied.

For more about virtual folders, including the per-account All Mail folders, see [Virtual folders](#virtual-folders) below.

### Selecting multiple messages

Hold **Shift** and press **Up** or **Down** in the message list to extend your selection. You can then act on all selected messages at once (for example, pressing **Delete** removes them all).

---

## Virtual folders

The top of the folder list contains a group of **virtual folders** that aggregate messages across all your accounts. They are read-only views — no mail is moved or copied.

| Virtual folder | What it shows |
|----------------|---------------|
| **All Mail** | Every non-excluded message across all accounts and folders, sorted newest-first |
| **All Inboxes** | Inbox messages from every account |
| **All Drafts** | Draft messages from every account |
| **All Sent** | Sent messages from every account |
| **All Trash** | Deleted messages from every account |

Each account also has its own **All Mail — {Account Name}** entry directly under that account in the folder tree. Selecting it shows all mail for that account only, without mixing in messages from other accounts. These per-account folders also appear in the **Ctrl+Y** folder picker.

---

## Conversation view

Conversation view groups related messages by subject into threaded trees, so you can follow a back-and-forth exchange in one place.

**Toggle conversation view:**
- Activate the **Conversation** button in the toolbar, or
- Press `Ctrl+Shift+C`.

When conversation view is on:

- Each row in the message list represents a thread rather than a single message.
- Select the arrow next to a thread (or press **Right** arrow when the thread is selected) to expand it and see the individual messages inside.
- **Reply**, **Forward**, and **Delete** always act on the specific message selected within the thread.

---

## From view (by sender)

From view groups messages by the sender's name and address, so you can see at a glance who has written to you and how many messages you have from each person.

**Toggle From view:**
- Open the **View** menu and choose **From**, or
- Press **Ctrl+Shift+V** to cycle through view modes.

When From view is on:

- Each row in the message list represents a sender group rather than a single message.
- Select the arrow next to a group (or press **Right** arrow) to expand it and read individual messages inside.
- Press **Delete** on a group to delete all messages from that sender at once.

---

## To view (by recipient)

To view groups messages by the recipient address. This is useful for folders that receive mail sent to several different addresses — for example, a shared mailbox or an alias.

Toggling and navigation work the same way as [From view](#from-view-by-sender). Press **Ctrl+Shift+V** to cycle between Messages, From, To, and Conversations.

---

## Address book

QuickMail includes a built-in address book for storing email addresses and display names. Contacts can be added manually or imported directly from your messages. The address book is stored as a human-readable JSON file in your AppData folder alongside other QuickMail settings.

### Managing the address book

- Open **File → Address Book** (or press `Ctrl+Shift+B`) to view, search, and manage all your saved contacts.
- Search for a contact by typing in the search field — matches appear for both name and email address.
- Click on any contact to view its details in the edit fields below.
- To add a contact manually, type in the **Name** and **Email** fields and press **Enter** or click **Add**.
- To edit a contact's name, select it in the list, edit the **Name** field, and click **Add** to save the changes.
- To delete a contact, select it in the list and click **Delete** (or press **Delete**).

### Grab addresses from a message

While reading a message, you can quickly save the sender, recipients, and reply-to addresses to your address book:

- Open **Message → Grab Addresses from Message** (or press `Ctrl+Shift+G`).
- A dialog appears showing all addresses found in the message (From, To, Cc).
- The focus starts on the first address in the list so you can immediately interact with the checkboxes.
- All addresses are checked by default. Use **Space** or **Up/Down** arrows to navigate and toggle selections.
- Click **Save** to add the selected addresses to your address book.

### Autocomplete in compose

When composing a message, as you type in the **To**, **Cc**, or **Bcc** fields, matching contacts from your address book appear in a dropdown suggestion list:

- Type at least one character to see suggestions.
- Press **Down arrow** to move into the suggestion list.
- Use **Up/Down** arrows to select a contact, then press **Enter** or **Tab** to insert it.
- Press **Escape** to dismiss the suggestions without selecting.
- Press **Up arrow** on the first item to return focus to the text field.

Contacts are sorted by how recently they were used, so your most-contacted people appear first.

**Address separator:** When inserting an address from the address book, QuickMail detects which separator (comma or semicolon) you've been using in the field and inserts the new address with the same separator. Both formats are supported: `address1, address2` or `address1; address2`.

---

## Command palette

Press **Ctrl+Shift+P** to open the command palette. Type any part of a command name and press **Enter** (or click) to run it — no need to remember every shortcut.

- All actions are searchable, including folder navigation, compose, delete, view switching, and account management.
- Press **Escape** to close without running a command. Focus returns to where it was before.

---

## Context menus

Right-click (or press **Shift+F10**) anywhere in the message list, folder tree, sender groups, or conversation groups to open a context menu with relevant actions.

Common actions available through context menus:

- Reply, Reply All, Forward
- Delete
- Mark as Read / Mark as Unread
- Move to Folder

---

## Composing messages

### New message

- Press `Ctrl+N`, or activate **New** in the toolbar.
- The compose window opens. If you have more than one account, the **From** field lets you choose which account to send from.

### Spell check

The message body has spell checking enabled. Misspelled words are underlined as you type. Right-click a word (or press **Shift+F10**) for suggested corrections.

### Reply, Reply All, and Forward

With a message selected in the list or open in the reading pane:

| Action | Shortcut |
|--------|----------|
| Reply | `Ctrl+R` |
| Reply All | `Ctrl+Shift+R` |
| Forward | `Ctrl+F` |

These are also available as buttons in the toolbar.

### Compose window shortcuts

| Shortcut | Action |
|----------|--------|
| Alt+S | Send the message |
| Ctrl+S | Save as draft |
| Alt+U | Jump to the Subject field |
| Alt+M | Jump to the From (account) field |
| Ctrl+Shift+A | Add file attachments |
| Ctrl+V | Paste files from clipboard as attachments |
| Delete (in attachment list) | Remove selected attachment |
| Escape | Close the window (prompts to save if there are unsaved changes) |
| Tab | Move between fields |

**Bcc** is included as a field for blind-copy recipients.

---

## Drafts

Drafts are saved to the **Drafts folder on your mail server**, so they are accessible from any device or email app.

### Saving a draft

- Press `Ctrl+S` or activate **Save Draft** in the compose window at any time.
- The draft is uploaded to the server immediately. The status bar shows **"Draft saved."** when it is done.
- You can save as many times as you like — each save replaces the previous version.

### Closing with unsaved changes

If you close the compose window after making changes that have not been sent or saved, QuickMail will ask:

- **Yes** — save as a draft and close
- **No** — discard the changes and close
- **Cancel** — go back to the compose window

### Re-opening a draft

1. Go to the **Drafts** folder in the folder list.
2. Select the draft in the message list.
3. Select the draft or press **Enter** — the compose window opens with the draft pre-loaded.
4. Make your edits, then send with `Alt+S` or save again with `Ctrl+S`.

When you send a draft, it is automatically removed from the Drafts folder.

---

## Attachments

### Adding attachments when composing

- **File dialog** — Press `Ctrl+Shift+A` or activate **Add Files…** to open a file picker. You can select multiple files at once.
- **Drag-and-drop** — Drag one or more files from File Explorer and drop them anywhere on the compose window.
- **Clipboard paste** — Copy files in File Explorer (`Ctrl+C`), then press `Ctrl+V` in the compose window. Only file copies are intercepted this way; text paste still works normally.

The attachment list appears below the **Add Files** button once files are added. It shows the file name, and a tooltip shows the size. A summary below the list shows the total count and size against the 25 MB limit.

To remove an attachment: select it in the list and press `Delete`, or right-click (or `Shift+F10`) and choose **Remove**.

To preview an attachment before sending: right-click it and choose **Open**. A security warning is shown for executable file types (`.exe`, `.bat`, etc.).

Attachments are saved with drafts and restored when you re-open a draft.

### Forwarded messages

When you forward a message that has attachments, QuickMail automatically downloads and includes them in the compose window. The status bar shows **"Preparing forward…"** while this happens.

### Reading attachments in received messages

If a message has attachments, they appear as a list below the date in the reading pane. Tab to the attachment list; use Up/Down to move between items.

Right-click an attachment (or press `Shift+F10`) for options:

| Option | Action |
|--------|--------|
| Save… | Download and save the selected attachment |
| Save All… | Download and save all attachments to a folder you choose |
| Open | Download and open the attachment with its default application |

A security warning is shown before opening executable file types.

---

## Deleting messages

- Select one or more messages and press **Delete**. Use **Shift+Up/Down** to select multiple.
- Messages are moved to the **Trash** folder. They are not permanently deleted.
- To permanently delete everything in Trash, activate **Empty Trash** in the toolbar or press `Ctrl+Shift+E`.

---

## Security

- **Passwords** are stored in **Windows Credential Manager** — never in any file on disk. They are protected by your Windows login.
- **OAuth2 tokens** (used for Outlook.com accounts) are encrypted with Windows DPAPI and stored locally. Your Microsoft password is never seen or stored by QuickMail.
- **HTML emails** are displayed in a sandboxed WebView2 component with a strict Content Security Policy. Scripts, embedded objects, and external frames are all blocked, so malicious email content cannot run on your machine.

---

## Keyboard shortcut reference

### Main window

| Shortcut | Action |
|----------|--------|
| Ctrl+1 | Focus account list |
| Ctrl+2 | Focus folder list |
| Ctrl+3 | Focus message list / conversation tree |
| Ctrl+4 | Focus reading pane |
| Ctrl+0 | Focus toolbar |
| F6 | Cycle focus forward through panes |
| Shift+F6 | Cycle focus backward through panes |
| F5 | Refresh current folder |
| Ctrl+N | New message |
| Ctrl+R | Reply |
| Ctrl+Shift+R | Reply All |
| Ctrl+F | Forward |
| Delete | Delete selected message(s) |
| Ctrl+Y | Open folder picker |
| Ctrl+Shift+V | Cycle view mode (Messages / From / To / Conversations) |
| Ctrl+Shift+C | Toggle conversation view |
| Ctrl+Shift+P | Open command palette |
| Ctrl+M | Load more messages |
| Ctrl+Shift+E | Empty Trash |
| Ctrl+Shift+B | Open Address Book |
| Ctrl+Shift+G | Grab addresses from open message |
| Shift+Up / Shift+Down | Extend message selection |
| Shift+F10 | Open context menu for focused item |
| Escape | Close reading pane |

### Compose window

| Shortcut | Action |
|----------|--------|
| Alt+S | Send |
| Ctrl+S | Save draft |
| Alt+U | Jump to Subject field |
| Alt+M | Jump to From (account) field |
| Ctrl+Shift+A | Add file attachments |
| Ctrl+V | Paste files from clipboard as attachments |
| Delete (in attachment list) | Remove selected attachment |
| Tab | Move between fields |
| Escape | Close window (prompts if there are unsaved changes) |

---

## Data storage

QuickMail keeps all its files under `%AppData%\QuickMail\` (typically `C:\Users\<YourName>\AppData\Roaming\QuickMail\`):

| File / folder | Contents |
|---------------|----------|
| `accounts.json` | Account configuration — server addresses, ports, display names. No passwords. |
| `contacts.json` | Address book contacts — display names and email addresses (human-readable JSON) |
| `config.ini` | Optional settings file — see [Configuration file](#configuration-file) below. |
| `mail.db` | Local message cache (SQLite database) |
| `msal.cache` | Encrypted OAuth2 token cache (Microsoft accounts only) |
| `quickmail.log` | Application log |

---

## Configuration file

Some settings do not yet have a UI. You can control them by editing `%AppData%\QuickMail\config.ini` in any text editor. The file is created with defaults the first time QuickMail runs.

Lines starting with `#` are comments and are ignored. The file uses a simple `key = value` format inside named sections.

### `[global]` settings

| Setting | Values | Default | What it does |
|---------|--------|---------|--------------|
| `PreviewLines` | `0`–`5` | `3` | Number of body-preview lines shown under each subject in the message list. Set to `0` to hide previews entirely. |
| `ShowMessageStatus` | `true` / `false` | `true` | Show or hide the read/unread status indicator column in the message list. |
| `ConversationView` | `true` / `false` | `false` | Start with conversation threading on. |
| `SyncDays` | integer ≥ `0` | `30` | How many days back to look for messages when syncing. Set to `0` to fetch all messages (may be slow on large mailboxes). |
| `InitialSyncCount` | integer ≥ `0` | `500` | Maximum number of messages fetched per folder on the very first sync of a newly connected account. |

### `[account:<guid>]` overrides

You can override `PreviewLines` for a specific account by adding a section with that account's GUID (visible in `accounts.json`):

```ini
[account:xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx]
PreviewLines = 1
# Show fewer preview lines for this account only.
```

Changes to `config.ini` take effect the next time you start QuickMail.
