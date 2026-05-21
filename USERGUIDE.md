# QuickMail User Guide

QuickMail is a desktop email client for Windows. It supports multiple IMAP/SMTP email accounts at the same time, offers a unified **All Mail** view across all your accounts, groups messages into conversation threads, and renders HTML emails in a secure reading pane. The whole app is designed to be used entirely from the keyboard if you prefer.

---

## Contents

- [Layout](#layout)
- [Menu bar](#menu-bar)
- [Connecting accounts](#connecting-accounts)
- [Managing accounts](#managing-accounts)
- [Settings](#settings)
- [Reading mail](#reading-mail)
- [Performance and concurrency](#performance-and-concurrency)
- [Virtual folders](#virtual-folders)
- [Conversation view](#conversation-view)
- [From view (by sender)](#from-view-by-sender)
- [To view (by recipient)](#to-view-by-recipient)
- [Filtering messages](#filtering-messages)
- [Sorting messages](#sorting-messages)
- [Saved views](#saved-views)
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
| **File** | New Message, Manage Accounts, Address Book, Settings (`Ctrl+,`), Exit |
| **Message** | Reply, Reply All, Forward, Delete, Empty Trash, Move/Copy to Folder, Grab Addresses |
| **View** | Refresh, View Mode (Messages / Conversations / By Sender / By Recipient), Filter (All / Unread / Read / With Attachments / Replied / Forwarded), Sort (Newest First / Oldest First / A → Z / Z → A / Most Messages / Fewest Messages), Views (saved views list, Save View, Manage Views, Clear View), Sync Range (7 Days / 30 Days / 6 Months / 1 Year / All Mail), Go to Folder, Search Folders, Command Palette |
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
- The default account is marked with **— default** in the account list.
- Activate **Remove Account** to delete an account. You will be asked to confirm before anything is removed.
- Changes take effect after you activate **Save** and close the dialog.

---

## Settings

Open **File → Settings** (or press `Ctrl+,`) to change application-wide preferences and keyboard shortcuts.

### General tab

| Setting | What it controls |
|---------|------------------|
| **View Mode** | Default message grouping when the app starts (Messages, Conversations, By Sender, By Recipient) |
| **Sync Days** | How many days back to fetch messages on sync. `0` means all mail. |
| **Preview Lines** | Number of body-preview lines shown under each subject in the message list (0–5). |
| **Show Message Status** | Whether the read/unread/replied/forwarded status indicator appears in the message list. |
| **Initial Sync Count** | Maximum messages fetched per folder on the first sync of a newly connected account. |

### Keyboard Shortcuts tab

Assign custom key bindings to any registered command:

1. Select a command from the list.
2. Activate **Set…** to open the key-capture dialog, then press any `Ctrl`, `Shift`, or `Alt` combination.
3. If the combination is already in use, a conflict dialog identifies the clash so you can choose another key or cancel.
4. Activate **Restore** to remove a custom binding and return to the command's default.

Custom bindings are saved to `hotkeys.json` in your AppData folder and apply immediately without restarting.

---

## Reading mail

### Refreshing / checking for new mail

- Press **F5** or activate **Refresh** in the toolbar to fetch new messages.
- If a saved view is active, **F5 refreshes the view** — not just the underlying folder. This means you stay in your view and see the latest messages that belong to it.
- QuickMail also checks for new mail automatically in the background while the app is running.

### Navigating the panes

| Action | How |
|--------|-----|
| Focus the account list | `Ctrl+1`, then use **Up/Down** arrow keys |
| Focus the folder tree | `Ctrl+2` or `Ctrl+Y`, then use **Up/Down** + **Enter** |
| Focus the message list | `Ctrl+3`, then use **Up/Down** to move between messages |
| Open a message in the reading pane | Press **Enter** or select the message |
| Move focus into the reading pane | **F6** after a message is open |
| Return focus to the message list | **Escape** or **F6** from the reading pane |
| Cycle focus forward through all panes | **F6** |
| Cycle focus backward | **Shift+F6** |
| Focus the status bar | `Ctrl+9` |

### Folder tree shortcut (Ctrl+2 / Ctrl+Y)

Press `Ctrl+2` or `Ctrl+Y` to move focus to the main folder tree. Use **Up/Down** to choose a folder, **Right/Left** to expand or collapse account and folder nodes, and **Enter** to open the selected folder.

### Search folders (Ctrl+Shift+F)

Press `Ctrl+Shift+F` or activate **View > Search Folders...** to open the flat folder list. Focus starts on the folder list, so you can use **Up/Down** and **Enter** immediately. Press `/` to move to the search field, type to filter folders, and press **Enter** to open the selected folder.

### Load More (Ctrl+M)

The message list shows the 100 most recent messages by default. Activate **Load More** at the bottom of the list, or press `Ctrl+M`, to load the next 100.

### All Mail

At the top of the folder list is an **All Mail** entry. Selecting it shows messages from all folders across all your accounts, sorted newest-first. This is a virtual view — no messages are moved or copied.

For more about virtual folders, including the per-account All Mail folders, see [Virtual folders](#virtual-folders) below.

### Selecting multiple messages

Hold **Shift** and press **Up** or **Down** in the message list to extend your selection. You can then act on all selected messages at once (for example, pressing **Delete** removes them all).

## Performance and concurrency

QuickMail uses a small IMAP connection pool for each account. Message opening, background sync, preview fetching, attachment downloads, and move/copy/delete actions lease separate connections when one is available, so opening a message should not fail just because another IMAP command is already running.

By default, QuickMail uses up to **6 simultaneous IMAP connections per account**. Foreground work such as opening a message or downloading an attachment gets reserved capacity; background sync and preview fetching are limited below the full pool. Advanced users can change the limit in `%AppData%\QuickMail\config.ini` with `MaxImapConnectionsPerAccount`; changes take effect after restarting QuickMail.

Some marketing or financial messages contain very large HTML layouts. QuickMail may show those messages in a simplified reader mode so the reading pane remains responsive.

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

Each account also has its own **All Mail — {Account Name}** entry directly under that account in the folder tree. Selecting it shows all mail for that account only, without mixing in messages from other accounts. These per-account folders also appear in the **Go to Folder** picker.

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

## Filtering messages

The **View → Filter** submenu (and the command palette) let you narrow the message list to a specific subset without navigating away from the current folder.

| Filter | Shows |
|--------|-------|
| **Show All** | All messages (no filter active) |
| **Unread** | Messages that have not been read, replied to, or forwarded |
| **Read** | Messages that have been marked as read |
| **With Attachments** | Messages that have at least one attachment |
| **Replied** | Messages you have replied to |
| **Forwarded** | Messages you have forwarded |

**Applying a filter:**
- Open **View → Filter** and choose an option, or
- Open the command palette (`Ctrl+Shift+P`) and type the filter name (e.g. "unread").

**Clearing a filter:**
- Open **View → Filter → Show All**, or run **Show All Messages** from the command palette.

The active filter is shown in the window title bar. Navigating to a different folder automatically clears the filter.

Filter commands can be assigned custom keyboard shortcuts in **File → Settings → Keyboard Shortcuts**.

---

## Sorting messages

The **View → Sort** submenu controls the order in which messages or groups are displayed. The selected sort is saved and restored when you reopen the app.

| Sort | Orders by |
|------|-----------|
| **Newest First** *(default)* | Most recent message date first |
| **Oldest First** | Oldest message date first |
| **A → Z** | Subject or sender name, ascending |
| **Z → A** | Subject or sender name, descending |
| **Most Messages** | Groups with the most messages first — grouped views only |
| **Fewest Messages** | Groups with the fewest messages first — grouped views only |

**Most Messages** and **Fewest Messages** only apply when a grouped view is active (Conversations, By Sender, or By Recipient) and are greyed out in the flat Messages view. They let you instantly see, for example, who you have the most correspondence with.

**Applying a sort:**
- Open **View → Sort** and choose an option.

**Clearing a sort:**
- Open **View → Sort → Newest First** to return to the default.

---

## Saved views

A **saved view** is a named snapshot of your current working context — it bundles a folder or virtual folder (or multiple folders), a display mode (Messages, Conversations, From, or To), a filter (All, Unread, etc.), and a sort order into a single item you can jump back to instantly.

When a view is active, its name appears in the window title bar instead of the folder name.

### Creating a view

Views can be created from any folder, including virtual folders (All Mail, All Inboxes, All Sent, etc.).

1. Navigate to the folder (or virtual folder) you want and set the mode, filter, and sort you prefer.
2. Open **View → Views → Save View…**. The **View Manager** dialog opens.
3. Press **Create New View from Current State**. A view is created with an auto-generated name like *"All Inboxes, Conversations"* or *"The Idea Place Inbox, From Unread"*.
4. Edit the **Name** field if you want a different name.
5. Press **Save**.

### Views from virtual folders

You can save a view while **All Mail**, **All Inboxes**, **All Drafts**, **All Sent**, or **All Trash** is selected. The view remembers which virtual folder it was created from. When you apply the view later, QuickMail loads the same virtual folder with the stored mode, filter, and sort — not just a fixed folder on disk.

### Applying a view

Once you have saved views, you can reach them in three ways:

- **View menu** — Open **View → Views** and press the view name. The current keyboard shortcut (if any) is shown next to each item.
- **Folder tree** — A **Views** group appears at the top of the folder tree. Expand it and choose a view name to apply it.
- **Command palette** — Press **Ctrl+Shift+P** and type part of the view's name. Views appear in the **Views** category.

While a view is active:

- The window title shows the view name.
- Pressing **F5** refreshes the view — not just the underlying folder. You stay inside the view and see the latest messages that belong to it.

### Clearing a view

A view stays active until you explicitly dismiss it or navigate to a different folder.

- **Explicit dismiss** — Open **View → Views → Clear View** to deactivate the current view and return to normal folder navigation. The folder that was selected when the view was applied remains selected.
- **Navigating away** — Selecting any folder in the folder tree deactivates the current view and navigates to that folder.

### Managing views

Open **View → Views → Manage Views…** (or press the keyboard shortcut you assigned) to open the **View Manager**.

The dialog has two panels:

| Area | What it does |
|------|-------------|
| **Left — Saved Views list** | Choose a view to select it for editing |
| **Right — Edit panel** | Edit name, shortcut, default flag, and see the view's saved folders and settings |

**Buttons in the right panel:**

| Button | Action |
|--------|--------|
| **Save** | Overwrites the selected view with the current app state (mode, filter, sort, and current folder) |
| **Save As New** | Creates a new view from the current state |
| **Delete** | Permanently removes the selected view |

### Keyboard shortcuts for views

1. Select a view in the View Manager.
2. Press **Set…** next to the **Keyboard Shortcut** field.
3. Press the key combination you want (must include at least one modifier: Ctrl, Shift, or Alt).
4. Press **Save** to apply.

The shortcut also appears in **File → Settings → Keyboard Shortcuts** and can be changed there as well. Both places stay in sync — changing a shortcut in one place updates the other.

If you assign a shortcut that is already used by another view, QuickMail asks whether to reassign it; the previous view loses its shortcut.

### Multi-folder views

A single view can span more than one folder:

1. Open the View Manager and select an existing view.
2. Navigate to a different folder in the main window.
3. Press **Save**. QuickMail detects the folder change and asks:
   - **Yes** — replace the existing folder with the new one.
   - **No** — add the new folder alongside the existing one (multi-folder view).
   - **Cancel** — keep the view unchanged.

When a view has multiple folders, the folder tree shows a **ViewName — All** child node (loads messages from every constituent folder at once) plus one node per individual folder.

### Default view

Check **Default view (applied on startup)** in the View Manager and press **Save** to mark a view as the default. QuickMail applies it automatically after the initial sync completes each time it starts.

Only one view can be the default; marking a new view as default automatically clears the flag on any previously-default view.

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
- **HTML emails** are displayed in a sandboxed WebView2 component with a strict Content Security Policy. Scripts, embedded objects, remote images, external frames, and forms are blocked or stripped, so malicious email content cannot run on your machine.

---

## Keyboard shortcut reference

### Main window

| Shortcut | Action |
|----------|--------|
| Ctrl+1 | Focus account list |
| Ctrl+2 / Ctrl+Y | Focus folder tree |
| Ctrl+3 | Focus message list / conversation tree |
| Ctrl+0 | Focus toolbar |
| Ctrl+9 | Focus status bar |
| F6 | Cycle focus forward through panes |
| Shift+F6 | Cycle focus backward through panes |
| F5 | Refresh current folder |
| Ctrl+N | New message |
| Ctrl+R | Reply |
| Ctrl+Shift+R | Reply All |
| Ctrl+F | Forward |
| Delete | Delete selected message(s) |
| Ctrl+Shift+V | Cycle view mode (Messages / From / To / Conversations) |
| Ctrl+Shift+C | Toggle conversation view |
| Ctrl+Shift+F | Search folders |
| Ctrl+Shift+P | Open command palette |
| Ctrl+, | Open Settings |
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
| `SyncDays` | integer >= `0` | `30` | How many days back to look for messages when syncing. Set to `0` to fetch all messages (may be slow on large mailboxes). |
| `InitialSyncCount` | integer >= `0` | `500` | Maximum number of messages fetched per folder on the very first sync of a newly connected account. |
| `MaxImapConnectionsPerAccount` | `1`–`15` | `6` | Maximum simultaneous IMAP connections QuickMail may open for each account. Background work is capped below this value so opening messages keeps reserved capacity. Higher values can make large accounts more responsive, but only raise this if your mail provider allows it. |

### `[account:<guid>]` overrides

You can override `PreviewLines` for a specific account by adding a section with that account's GUID (visible in `accounts.json`):

```ini
[account:xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx]
PreviewLines = 1
# Show fewer preview lines for this account only.
```

Changes to `config.ini` take effect the next time you start QuickMail.
