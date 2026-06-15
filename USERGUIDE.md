# QuickMail User Guide

QuickMail is a desktop email client for Windows. It supports multiple IMAP/SMTP email accounts at the same time, offers a unified **All Mail** view across all your accounts, groups messages into conversation threads, and renders HTML emails in a secure reading pane. The whole app is designed to be used entirely from the keyboard if you prefer.

---

## Contents

- [Installing QuickMail](#installing-quickmail)
- [Layout](#layout)
- [Menu bar](#menu-bar)
- [Connecting accounts](#connecting-accounts)
- [Managing accounts](#managing-accounts)
- [Settings](#settings)
- [Reading mail](#reading-mail)
- [Tabs and windows](#tabs-and-windows)
- [Performance and concurrency](#performance-and-concurrency)
- [Virtual folders](#virtual-folders)
- [Conversation view](#conversation-view)
- [From view (by sender)](#from-view-by-sender)
- [To view (by recipient)](#to-view-by-recipient)
- [Filtering messages](#filtering-messages)
- [Flagging messages](#flagging-messages)
- [Sorting messages](#sorting-messages)
- [Mail rules](#mail-rules)
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
- [Command-line options](#command-line-options)

---

## Installing QuickMail

Two download options are available from the [Releases page](https://github.com/kellylford/QuickMail/releases):

| Download | When to use |
|----------|-------------|
| **`quickmail-vX.X.X-setup.exe`** — Windows installer | Recommended for most users. Installs per-user with no elevation required and registers an uninstaller in Windows Settings. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run it directly. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

### WebView2 Runtime

QuickMail uses the **Microsoft Edge WebView2 Runtime** to render HTML messages. It is preinstalled on Windows 11 and most recent Windows 10 systems. If it is missing, the installer downloads and installs it automatically. If you are using the standalone executable and WebView2 is absent, download it from [Microsoft's WebView2 page](https://developer.microsoft.com/microsoft-edge/webview2/).

### Uninstalling

If you used the installer, open **Windows Settings → Apps** and remove QuickMail from there. During uninstall you will be offered the option to delete your data folder (`%AppData%\QuickMail`), which contains your accounts, mail cache, contacts, rules, and saved views. Passwords stored in Windows Credential Manager are never removed automatically.

If you used the standalone executable, simply delete the file. Your data folder at `%AppData%\QuickMail` can be deleted manually if you no longer need it.

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

When the **Reading mode** setting is set to **Tab** (see [Settings](#settings) → Windowing tab), a **tab strip** appears below the toolbar. Each open message occupies a tab. The reading pane is not shown in this mode; message content lives inside the active tab instead. Messages can also be opened in standalone **message windows** using `Ctrl+Enter` or by setting Reading mode to **Window**.

---

## Menu bar

The menu bar at the top of the window provides access to all major features, organized by task:

| Menu | Contains |
|------|----------|
| **File** | New Message, Manage Accounts, Address Book, Settings (`Ctrl+,`), Exit |
| **Message** | Reply, Reply All, Forward, Delete, Empty Trash, Move/Copy to Folder, Grab Addresses |
| **View** | Refresh, View Mode (Messages / Conversations / By Sender / By Recipient), Filter (All / Unread / Read / With Attachments / Replied / Forwarded / To Me), Sort (Newest First / Oldest First / A → Z / Z → A / Most Messages / Fewest Messages), Views (saved views list, Save View, Manage Views, Clear View), Sync Range (7 Days / 30 Days / 6 Months / 1 Year / All Mail), Go to Folder, Search Folders, Command Palette |
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

### Account Properties

Press **Alt+Enter** or right-click an account to view its properties. Properties show:

| Section | Shows |
|---------|-------|
| **Identity** | Display name and email address |
| **Incoming (IMAP)** | Server, port, security method, and username |
| **Outgoing (SMTP)** | Server, port, security method, and username |
| **Authentication** | Authentication method (Password or OAuth2) |
| **Sync** | Cache statistics — messages in local cache, oldest cached message date, sync window setting |

The **Sync** section is useful for understanding what mail is stored locally. If you delete the cache and re-sync, watching the message count grow in this section gives you confidence that the sync is progressing.

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

The **Composing** group on the General tab controls the compose window:

| Setting | What it controls |
|---------|------------------|
| **Automatically save drafts while composing** | On by default. Quietly saves your message as a server draft while you write — see [Automatic draft saving](#automatic-draft-saving). |
| **Auto-save interval** | How often auto-save runs: 30 seconds to 10 minutes (default 2 minutes). |
| **Default compose mode** | The editing mode new messages start in: Plain Text, Markdown, or HTML — see [Compose modes](#compose-modes-plain-text-markdown-and-html). Drafts reopen in the mode they were saved in; templates always reopen in Plain Text. |

### Advanced tab

| Setting | What it controls |
|---------|------------------|
| **Log Format** | How timestamps appear in the log file. **Action first** (default) — message text first, timestamp in brackets at the end of the line; easier to scan since the log is already in chronological order. **Time first** — timestamp at the start of each line, which was the original format. |

### Windowing tab

Controls where messages open when you select them.

| Setting | What it controls |
|---------|------------------|
| **Reading mode** | **Reading Pane** (default) — opens messages in the reading pane inside the main window, one at a time. **Tab** — opens each message in a new tab in the main window's tab strip. **Window** — opens each message in a new standalone window. |
| **Confirm before closing a tab** | When enabled, closing a tab that has unsaved draft changes shows a confirmation dialog before closing. |

Regardless of the Reading mode setting, pressing **Ctrl+Enter** on a message always opens it in a new standalone window.

### Keyboard Shortcuts tab

Assign custom key bindings to any registered command:

1. Select a command from the list.
2. Activate **Set…** to open the shortcut-capture dialog, then press any `Ctrl`, `Shift`, or `Alt` combination.
3. After the combination is captured, the dialog shows **OK**, **Change**, and **Cancel** buttons. If the combination is already assigned to another command, a conflict warning appears inline. Activate **OK** to confirm (reassigning from the conflicting command), **Change** to capture a different combination, or **Cancel** to keep the existing binding without changes.
4. Activate **Restore** to remove a custom binding and return to the command's default.

Custom bindings are saved to `hotkeys.json` in your AppData folder and apply immediately without restarting.

---

## Reading mail

### Refreshing / checking for new mail

- Press **F5** or activate **Refresh** in the toolbar to fetch new messages.
- If a saved view is active, **F5 refreshes the view** — not just the underlying folder. This means you stay in your view and see the latest messages that belong to it.
- QuickMail also checks for new mail automatically in the background while the app is running. When your mail server supports IMAP IDLE, new messages in your inboxes are detected in real time without polling — QuickMail keeps a persistent connection that the server notifies the moment a message arrives. When a new message is detected, a targeted sync runs for that inbox and the message list updates automatically.

### Connection recovery

QuickMail is designed to maintain reliable connections even on unstable networks:

- **Automatic startup retry:** If accounts fail to connect on launch, QuickMail automatically retries up to 3 times with increasing delays (15 seconds, then 30 seconds, then 60 seconds).
- **Connection monitoring:** The app continuously monitors IMAP connections. If a connection drops, QuickMail detects it and automatically reconnects with exponential backoff (30 seconds, 60 seconds, up to 120 seconds).
- **Connection status:** The status bar shows your connection state — "Connecting…" during startup, "N accounts connected" when ready, or an error message if all connections fail.
- **Periodic heartbeat:** Every 10 minutes, QuickMail sends a keep-alive command to prevent idle connection timeouts on slow networks.

If you experience frequent connection errors, check your network, firewall, and mail server settings. QuickMail will keep retrying until the connection is restored.

### Navigating the panes

| Action | How |
|--------|-----|
| Focus the account list | `Ctrl+1` (or `Ctrl+Alt+1` when tabs are open), then use **Up/Down** arrow keys |
| Focus the folder tree | `Ctrl+2` or `Ctrl+Y` (or `Ctrl+Alt+2` when tabs are open), then use **Up/Down** + **Enter** |
| Focus the message list | `Ctrl+3` (or `Ctrl+Alt+3` when tabs are open), then use **Up/Down** to move between messages |
| Focus the tab strip | `Ctrl+Shift+T` (when tab strip is visible) |
| Open a message in the reading pane | Press **Enter** or select the message |
| Move focus into the reading pane | **F6** after a message is open |
| Return focus to the message list | **Escape** or **F6** from the reading pane |
| Cycle focus forward through all panes | **F6** |
| Cycle focus backward | **Shift+F6** |
| Focus the status bar | `Ctrl+9` (when no tabs are open) |
| Navigate status bar regions | **Left/Right** arrow keys |
| Activate a clickable status bar region | **Enter** or **Space** |

**When tabs are open:** `Ctrl+1` through `Ctrl+8` jump to tab number N instead of focusing panes. Use `Ctrl+Alt+1`, `Ctrl+Alt+2`, or `Ctrl+Alt+3` to focus the account list, folder tree, or message list at any time regardless of how many tabs are open. `Ctrl+9` jumps to the last tab (rather than focusing the status bar) when tabs are open.

### Folder tree shortcut (Ctrl+2 / Ctrl+Y)

Press `Ctrl+2` or `Ctrl+Y` to move focus to the main folder tree (when no tabs are open). Use **Up/Down** to choose a folder, **Right/Left** to expand or collapse account and folder nodes, and **Enter** to open the selected folder.

### Search folders (Ctrl+Shift+F)

Press `Ctrl+Shift+F` or activate **View > Search Folders...** to open the flat folder list. Focus starts on the folder list, so you can use **Up/Down** and **Enter** immediately. Press `/` to move to the search field, type to filter folders, and press **Enter** to open the selected folder.

### Load More (Ctrl+M)

The message list shows the 100 most recent messages by default. Activate **Load More** at the bottom of the list, or press `Ctrl+M`, to load the next 100.

### All Mail

At the top of the folder list is an **All Mail** entry. Selecting it shows messages from all folders across all your accounts, sorted newest-first. This is a virtual view — no messages are moved or copied.

For more about virtual folders, including the per-account All Mail folders, see [Virtual folders](#virtual-folders) below.

### Selecting multiple messages

Hold **Shift** and press **Up** or **Down** to extend your selection one message at a time. For larger selections:

| Shortcut | What it does |
|----------|-------------|
| **Ctrl+A** | Select all messages in the current folder |
| **Ctrl+Shift+Home** | Extend selection from the current message to the first message in the list |
| **Ctrl+Shift+End** | Extend selection from the current message to the last message in the list |

Once you have multiple messages selected you can act on all of them at once — for example, pressing **Delete** removes them all.

## Tabs and windows

QuickMail can open messages in three different ways, controlled by the **Reading mode** setting in **Settings → Windowing**:

- **Reading Pane** (default) — one message at a time in the reading pane on the right side of the main window. This is the same behavior as earlier versions.
- **Tab** — each message you open gets its own tab in a strip below the toolbar. Multiple messages can be open at the same time.
- **Window** — each message opens in its own standalone window. You can move these windows to different monitors.

Regardless of the setting, pressing **Ctrl+Enter** on a message always opens it in a new standalone window.

### The tab strip

When Reading mode is **Tab**, a tab strip appears below the main toolbar. Each open message appears as a tab showing the message subject. The currently active tab is highlighted.

**Opening and closing tabs:**

| Action | How |
|--------|-----|
| Open a message in a new tab | Press **Enter** on a message in the list |
| Open a message in a new window instead | Press **Ctrl+Enter** |
| Close the active tab | Press **Ctrl+W**, or activate the close button (✕) on the tab |
| Close all other tabs | Use the **Close Other Tabs** command in the command palette |

`Ctrl+W` works regardless of where keyboard focus is — including from inside the message body. In **Reading Pane** mode, `Ctrl+W` closes the reading pane instead of a tab.

**Navigating tabs:**

| Shortcut | Action |
|----------|--------|
| `Ctrl+Tab` | Move to the next tab |
| `Ctrl+Shift+Tab` | Move to the previous tab |
| `Ctrl+1` … `Ctrl+8` | Jump to tab number N (1 = first, 8 = eighth) |
| `Ctrl+9` | Jump to the last tab |
| `Ctrl+Shift+T` | Move keyboard focus to the tab strip itself |
| `Ctrl+Shift+`` (backtick)` | Open the tab list overlay |

When the tab strip has focus, use **Left/Right** arrows to navigate. Each tab has two keyboard stops: the tab header (press **Enter** or **Space** to activate it) and its close button — **✕** (press **Enter** or **Space** to close the tab). Pressing **Right** from a tab header moves to that tab's close button; pressing **Right** again moves to the next tab's header.

**Tab list overlay (`Ctrl+Shift+\``):**

The tab list overlay shows all open tabs in a list. Use **Up/Down** arrows to move between tabs, **Enter** to activate the selected tab, and **Escape** to close the overlay without changing the active tab.

**Moving and reordering tabs:**

You can drag a tab header to a different position. Keyboard users can use **Move Tab Left** and **Move Tab Right** from the command palette (`Ctrl+Shift+P`).

**Promoting a tab to a window:**

To move a tab out of the main window and into its own standalone window, use the **Move Tab to New Window** command in the command palette. The message keeps its content — nothing is reloaded.

### Message windows

A message window is a standalone window that shows a single message. It has a full menu bar (**File**, **Message**, **Navigate**), a toolbar with common actions, and a status bar showing the message's position in the folder (for example, "Message 3 of 47").

**Opening a message window:**

- Press **Ctrl+Enter** on any message in the message list — this always opens a new window regardless of the Reading mode setting.
- Set Reading mode to **Window** in Settings → Windowing, then press **Enter** on any message.
- Use **Move Tab to New Window** from the command palette when in Tab mode.

**Acting on a message from its window:**

All the mail actions you'd use from the main window are available directly in the message window — via the menu bar, the toolbar, keyboard shortcuts, or the command palette:

| Shortcut | Action |
|----------|--------|
| `Ctrl+R` | Reply |
| `Ctrl+Shift+R` | Reply All |
| `Ctrl+F` | Forward |
| `Delete` | Delete (closes the window after deleting) |
| `Ctrl+Q` | Mark as Read |
| `Ctrl+Shift+G` | Grab Addresses from Message |

**Navigating inside a message window:**

| Shortcut | Action |
|----------|--------|
| `F6` / `Shift+F6` | Cycle focus: toolbar → header fields → message body |
| `Alt+Left` | Go to the previous message |
| `Alt+Right` | Go to the next message |
| `Ctrl+Shift+P` | Open the command palette for this window |
| `Ctrl+W` | Close the window |
| `Escape` | Close the window |

The command palette includes all message actions (Reply, Reply All, Forward, Delete, Mark as Read, Grab Addresses) as well as navigation commands (Previous Message, Next Message, Move to Main Window, Close Window).

**Moving a message back to the main window:**

Activate the **Move to Main Window** button in the toolbar, or choose **Navigate → Move to Main Window** from the menu bar, or use the command palette. The message opens as a new tab in the main window's tab strip and the standalone window closes.

**When a message window closes:**

Focus returns to the message in the main window's message list that was selected when the window was opened.

---

## Performance and concurrency

QuickMail uses a small IMAP connection pool for each account. Message opening, background sync, preview fetching, attachment downloads, and move/copy/delete actions lease separate connections when one is available, so opening a message should not fail just because another IMAP command is already running.

By default, QuickMail uses up to **6 simultaneous IMAP connections per account**. Foreground work such as opening a message or downloading an attachment gets reserved capacity; background sync and preview fetching are limited below the full pool. Advanced users can change the limit in `%AppData%\QuickMail\config.ini` with `MaxImapConnectionsPerAccount`; changes take effect after restarting QuickMail.

**Connection health:** Each account has a dedicated watcher that monitors connection health. If a connection fails, the watcher automatically attempts to reconnect. The connection state is reflected in the status bar and connection errors are announced to screen readers. See [Connection recovery](#connection-recovery) above for details.

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
| **All Flagged Mail** | All flagged messages across every account, sorted newest-first — see [Flagging messages](#flagging-messages) |

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
- Press **Shift+,** (less-than) to jump to the first (newest) message in the current thread. Press **Shift+.** (greater-than) to jump to the last (oldest) message. If the thread is collapsed, it expands automatically first.

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
| **Flagged** | Messages that have any flag set — see [Flagging messages](#flagging-messages) |
| **With Attachments** | Messages that have at least one attachment |
| **Replied** | Messages you have replied to |
| **Forwarded** | Messages you have forwarded |
| **To Me** | Messages where one of your account addresses appears in the To field, excluding mailing list messages |

**Applying a filter:**
- Open **View → Filter** and choose an option, or
- Open the command palette (`Ctrl+Shift+P`) and type the filter name (e.g. "unread").

**Clearing a filter:**
- Open **View → Filter → Show All**, or run **Show All Messages** from the command palette.

The active filter is shown in the window title bar. Navigating to a different folder automatically clears the filter.

Filter commands can be assigned custom keyboard shortcuts in **File → Settings → Keyboard Shortcuts**.

---

## Flagging messages

A **flag** marks a message as requiring action or follow-up. One press of `K` flags a message; another press unflags it. Flags you set in QuickMail are synced to your mail server, so they appear in other mail apps (Outlook, Thunderbird, mobile apps) that share the same account.

### The built-in flag

QuickMail includes one built-in flag named **Flagged**, shown in amber. This is the flag the `K` key applies by default, and it maps directly to the standard IMAP `\Flagged` flag — called "starred" in some apps. Any messages you have already flagged in another mail app appear as flagged in QuickMail after the next sync.

### Toggling a flag

With a message selected in the message list:

- Press `K` to flag the message. The flag indicator appears on the row and the screen reader announces `"Flagged: Flagged."`
- Press `K` again to unflag. The indicator disappears and the screen reader announces `"Unflagged."`

If the message already has a named flag applied, pressing `K` removes that flag (it does not replace it with the default flag).

### Picking a specific named flag

If you have created named flags (see [Named flags and the Flag Manager](#named-flags-and-the-flag-manager) below), press `Ctrl+Shift+K` to open the flag picker. The picker lists all your defined flags plus a **Clear flag** option. Use **Up/Down** arrows to choose, then press **Enter** to apply and close. Press **Escape** to close without making a change.

Only one flag can be set on a message at a time. Applying a different flag replaces the current one.

### Flagging a conversation or group

In Conversations, From, or To view, you can flag all messages in a group at once. Select the group row (not an individual message inside it) and press `K`. The screen reader announces how many messages were flagged — for example, `"Flagged 4 messages: Flagged."`

### Named flags and the Flag Manager

Beyond the built-in flag, you can create up to 20 **named flags** with custom colors — for example, "Urgent" (red), "Waiting" (yellow), or "Done" (green).

**Opening the Flag Manager:**

Open the command palette (`Ctrl+Shift+P`) and type "Manage Flags," then press **Enter**.

The Flag Manager shows your flag list on the left and an edit panel on the right. From here you can:

| Action | How |
|--------|-----|
| Add a new flag | Activate **Add Flag** |
| Rename a flag | Select a flag, edit the **Flag Name** field |
| Change the color | Select a flag, then choose from the color buttons in the edit panel |
| Reorder flags | Activate **Move Up** or **Move Down** |
| Delete a flag | Select a flag, then activate **Delete Flag** — the built-in flag cannot be deleted |
| Set the K default | Select a flag and activate **Set as K default** to make `K` apply that flag |

Press **Escape** or use the command palette (`Ctrl+Shift+P` → "Close") to close the Flag Manager.

Named flag definitions are stored in `flags.json` in your data folder and are local to your QuickMail profile.

**Changing which flag K applies:**

By default `K` applies the built-in "Flagged" flag. To change this, open the Flag Manager, select a named flag, and activate **Set as K default**. `K` now applies that flag. This setting persists across restarts.

### Filtering to flagged messages

Open **View → Filter → Flagged** (or use the command palette and search for "flagged") to show only messages that have any flag set. The active filter is shown in the window title bar.

To return to the full message list, open **View → Filter → Show All** or run **Show All Messages** from the command palette.

Pressing `K` to unflag a message while the Flagged filter is active removes the message from the list immediately, since it no longer matches the filter.

### All Flagged Mail

The folder tree includes an **All Flagged Mail** virtual folder that shows every flagged message from all your accounts in one place, sorted newest-first. This is useful for a daily review of everything you have marked for follow-up.

Select **All Flagged Mail** in the folder tree to open it. Like other virtual folders it is a read-only view — no messages are moved or copied.

### Saved views with the Flagged filter

When you save a view with the **Flagged** filter active, that filter is captured as part of the view. Applying the view later restores the Flagged filter automatically. Assign a keyboard shortcut to the view in the View Manager to jump to your flagged messages instantly.

### Server sync

- **IMAP accounts:** Flagging a message sets the standard `\Flagged` flag on the server immediately. Other mail apps using the same account see the flag on their next sync. Flags set by those other apps appear in QuickMail on your next refresh (`F5`).
- **Microsoft 365 (Graph) accounts:** Flagging sets the `followUpFlag` on the message via the Graph API — the same flag Outlook shows as "Flag for follow-up."

### Screen reader experience

When you navigate to a flagged message, the screen reader announces the flag name before the read status — for example: *"Urgent. Unread. Kelly Ford. Budget review. …"*

To turn off this announcement, open **File → Settings → General** and uncheck **Announce flag status** in the Screen Reader Announcements section. The visual flag indicator remains; only the spoken announcement is suppressed.

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

## Mail rules

Mail rules let you define automatic actions that run on incoming messages as they arrive during background sync. For example, you can automatically move newsletters to a folder, mark messages from your manager as unread, or delete spam.

Rules run **locally on your machine** — no data is sent to any server for rule processing. They fire during the normal sync cycle, so messages are acted on within seconds of arrival.

### Opening the Rules Manager

- Open **Message → Rules…** from the menu bar, or
- Press **Ctrl+Shift+L**, or
- Open the command palette (`Ctrl+Shift+P`) and type "Manage Rules".

The Rules Manager shows your rule list on the left and the editor for the selected rule on the right.

### Creating a rule

1. In the Rules Manager, activate **New Rule**.
2. Enter a **Rule Name**.
3. Choose which **Account** the rule applies to ("All accounts" or a specific one).
4. Check the condition fields you want to use (**From**, **To**, **Subject**, **Body**) and enter the text to match. All checked conditions must match for the rule to fire.
5. Optionally check **Has attachments** to only match messages with attachments.
6. Choose an **Action**:
   - **Mark as read** — marks matching messages as read
   - **Mark as unread** — marks matching messages as unread
   - **Move to folder** — moves matching messages to a folder you choose
   - **Delete** — moves matching messages to Trash
7. If you chose **Move to folder**, activate **Choose Folder…** to pick the destination folder from your folder tree.
8. Activate **Save**.

### Creating a rule from a message

You can quickly create a rule based on a message you're looking at:

1. Select a message in the message list.
2. Right-click (or press **Shift+F10**) and choose **Create Rule from Message…**, or press **Ctrl+Shift+T**.
3. The Rules Manager opens with the sender and subject pre-filled. Choose an action and save.

### Testing a rule

Before saving, you can test a rule against the messages currently shown in your message list:

1. Select the rule in the Rules Manager.
2. Activate **Test Rule**.
3. The status bar shows how many messages would match — for example, "Rule would match 3 of 50 selected messages."

### Enabling and disabling rules

Uncheck the **Enabled** checkbox on any rule to temporarily disable it. Disabled rules are skipped during sync but kept in your rule list for later use.

### Deleting a rule

Select a rule in the list and activate **Delete**, or press the **Delete** key. You'll be asked to confirm before the rule is removed.

### Rules status bar

The status bar shows a summary of your rules — how many are active, how many are disabled, and when they last ran. Press **Enter** or **Space** on the Rules button in the status bar to open the Rules Manager.

### Status bar navigation

The status bar has four regions that you can explore with the keyboard:

| Region | Shows | Interactive |
|--------|-------|-------------|
| **Status** | Message count and sync state — e.g., "7,326 messages. Synced 9:48 AM" or "Syncing… (5 of 20 folders). Never synced" | No |
| **Connection** | Connection state — "Connecting…" (during startup), "N accounts connected" (ready), or an error message | No |
| **Rules** | Summary of active/disabled rules and last run time | Yes — **Enter** or **Space** opens the Rules Manager |
| **Sync progress** | A progress bar shown during mail sync | No |

**Status region details:**
- **Message count:** Total messages in the current view or folder.
- **Sync state:** One of:
  - **"Synced HH:MM"** — Last successful sync time (e.g., "Synced 9:48 AM")
  - **"Never synced"** — No sync has completed yet (shown on first launch before sync finishes)
  - **"In progress"** — Sync is currently running
  - **During sync:** "Syncing… (X of Y folders)" shows folder-by-folder progress

**Navigation:**
- Press `Ctrl+9` or cycle through panes with **F6** to reach the status bar.
- Use **Left** and **Right** arrow keys to move between regions.
- Press **Tab** to exit the status bar and move to the next pane.
- The Sync progress region only appears while mail is syncing and is skipped when hidden.

**Screen reader usage:**
- Screen readers that support reading a status bar directly can usually do so with a dedicated keyboard command — consult your screen reader's documentation for details.
- When reading the full status bar, you hear all text in the Status region together: "7,326 messages. Synced 9:48 AM"
- When navigating with arrow keys to the Status region, the entire Status content is available for screen reader text navigation.

---

## Saved views

A **saved view** is a named snapshot of your current working context — it bundles a folder or virtual folder (or multiple folders), a display mode (Messages, Conversations, From, or To), a filter (All, Unread, etc.), a sort order, and an optional day limit into a single item you can jump back to instantly.

When a view is active, its name appears in the window title bar instead of the folder name.

### Creating a view

Views can be created from any folder, including virtual folders (All Mail, All Inboxes, All Sent, etc.).

1. Navigate to the folder (or virtual folder) you want and set the mode, filter, and sort you prefer.
2. Open **View → Views → Save View…**. The **Save View** dialog opens with a new view already created from your current state, and focus on the **Set…** shortcut button.
3. **(Optional)** Assign a keyboard shortcut: activate **Set…**, press your desired key combination, then activate **OK**.
4. Choose a day limit: **Show all messages** is checked by default (no limit). Uncheck it and type a number of days to restrict what the view shows — for example, `7` for the last week. The **Name** field updates automatically as you change the day setting.
5. The **Name** field is pre-filled with an auto-generated name that reflects the folder and day scope — for example, *"Inbox, last 7 days"* or *"All Inboxes, Conversations, all days"*. Edit it if you prefer a different name.
6. Activate **Save View**.

You can also create a new view while in the View Manager (opened via **View → Views → Manage Views…**) by activating **Save As New** or, when no view is selected, **Create New View from Current State**.

### Views from virtual folders

You can save a view while **All Mail**, **All Inboxes**, **All Drafts**, **All Sent**, or **All Trash** is selected. The view remembers which virtual folder it was created from. When you apply the view later, QuickMail loads the same virtual folder with the stored mode, filter, and sort — not just a fixed folder on disk.

### Applying a view

Once you have saved views, you can reach them in four ways:

- **View menu** — Open **View → Views** and press the view name. The active view has a checkmark next to it. The current keyboard shortcut (if any) is shown next to each item.
- **Folder tree** — A **Views** group appears at the top of the folder tree. Expand it and choose a view name to apply it.
- **Command palette** — Press **Ctrl+Shift+P** and type part of the view's name. Views appear in the **Views** category.
- **View Manager** — Select a view in the list and press **Apply View** to activate it and close the dialog.

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
| **Right — Edit panel** | Edit shortcut, day limit, default flag, and name; also shows the view's saved folders and settings |

**Buttons in the right panel:**

| Button | Action |
|--------|--------|
| **Apply View** | Activates the selected view immediately and closes the dialog |
| **Edit** | Opens the edit fields for the selected view |
| **Save** | Overwrites the selected view with the current app state (mode, filter, sort, day limit, and current folder) |
| **Save As New** | Creates a new view from the current state |
| **Delete** | Permanently removes the selected view |

### Day limit

A view can optionally restrict how many days of mail are shown. This is useful for high-volume folders where you only care about recent messages — for example, a view of your busiest inbox showing only the last seven days.

**Setting a day limit:**

1. Open the view for editing in the View Manager (select it and activate **Edit**).
2. Check **Show all messages** to remove any day limit. Uncheck it and type a number of days — for example, `7` for the last week, `30` for the last month.
3. Activate **Save**.

When creating a new view via **Save View…**, the day limit is set before the name field so the auto-generated name already reflects your choice.

**How it works:**

- Only messages received within the last N days appear in the message list. Older messages remain cached locally and reappear as soon as you clear the view or navigate to the folder directly.
- The day limit is shown in the view's settings summary — for example: *"Messages · All · Newest First · Last 7 days"*.
- The limit stacks with the other view filters. A view with "Unread, last 7 days" shows only unread messages from the past week.
- The limit has no effect on syncing. QuickMail still fetches and stores mail according to your sync range (see Settings); the day limit only controls which of the already-cached messages are displayed when the view is active.

### Keyboard shortcuts for views

1. Select a view in the View Manager and activate **Edit** if not already editing.
2. Activate **Set…** next to the **Keyboard Shortcut** field.
3. Press the key combination you want (must include at least one modifier: Ctrl, Shift, or Alt). After the combination is captured, the dialog shows **OK**, **Change**, and **Cancel**. Activate **OK** to confirm.
4. Activate **Save** to apply.

The shortcut also appears in **File → Settings → Keyboard Shortcuts** and can be changed there as well. Both places stay in sync — changing a shortcut in one place updates the other.

If the combination is already assigned to another view or command, a conflict warning appears inline in the capture dialog. Activate **OK** to reassign (the previous binding is cleared), or **Change** to choose a different combination.

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

- Open **File → Address Book** (or press `Ctrl+Shift+B` from the main window) to view, search, and manage all your saved contacts.
- Search for a contact by typing in the search field — matches appear for both name and email address.
- Click on any contact to view its details in the edit fields below.
- To add a contact manually, type in the **Name** and **Email** fields and press **Enter** or click **Add**.
- To edit a contact's name, select it in the list, edit the **Name** field, and click **Add** to save the changes.
- To select all contacts in the list, press **Ctrl+A** while the contact list has focus.
- To delete a contact, select it in the list and press **Delete**. To delete multiple contacts, select them with **Ctrl+A** and press **Delete**.

### Opening the address book from a compose window

Press `Ctrl+Shift+B` while composing a message to open the Address Book without leaving the compose window. When opened this way, three buttons appear at the top of the dialog:

| Button | Shortcut | Action |
|--------|----------|--------|
| **To** | Alt+T | Insert the selected contact into the To field |
| **Cc** | Alt+C | Insert the selected contact into the Cc field |
| **Bcc** | Alt+B | Insert the selected contact into the Bcc field |

Search for a contact, select them in the list, then press the button for the field you want. The dialog stays open after each insertion so you can add several contacts before closing.

### Grab addresses from a message

While reading a message, you can quickly save the sender, recipients, and reply-to addresses to your address book:

- Open **Message → Grab Addresses from Message** (or press `Ctrl+Shift+G`).
- A dialog appears showing all addresses found in the message (From, To, Cc).
- The focus starts on the first address in the list so you can immediately interact with the checkboxes.
- All addresses are checked by default. Use **Space** or **Up/Down** arrows to navigate and toggle selections.
- Click **Save** to add the selected addresses to your address book.

### Autocomplete in compose

When composing a message, as you type in the **To**, **Cc**, or **Bcc** fields, matching contacts from your address book appear in a dropdown suggestion list:

- Type at least one character to see suggestions. The number of matches is announced.
- Press **Down arrow** to move into the suggestion list.
- Use **Up/Down** arrows to select a contact, then press **Enter** or **Tab** to confirm it.
- Press **Escape** to dismiss the suggestions without selecting.
- Press **Up arrow** on the first item to return focus to the text field.

Contacts are sorted by how recently they were used, so your most-contacted people appear first.

### Contact groups

The Address Book window has a second tab called **Groups** that lets you bundle contacts into named groups for faster addressing. Groups are stored locally as a separate JSON file in your AppData folder and never leave your machine.

**Opening the Groups tab**

- In the Address Book window, select the **Groups** tab at the top of the window, or press **Ctrl+G** to jump to the groups list.
- Press **Ctrl+Shift+M** to open a dedicated **Group Manager** window that focuses on managing groups (rename, add or remove members, delete) without the address book around it.

**Creating a group**

Press **Ctrl+Shift+N** from anywhere in the address book, or activate the **New** button on the Groups tab. A **Name** field appears above the buttons. Type the group name and press **Enter** to create it. Press **Escape** to dismiss the name field without creating a group.

The new group appears in the list and is selected automatically.

To rename a group, select it and press **F2** (or activate the **Rename** button). The same name field appears pre-filled with the current name. Edit and press **Enter** to save, or **Escape** to cancel.

**Adding contacts to a group**

From the **Contacts** tab, select a contact and press **Shift+F10** (or the **Apps** key) to open the context menu, then choose the group to add the contact to.

You can also manage group membership in the **Group Manager** window (**Ctrl+Shift+M** or the **Manage…** button on the Groups tab). Select a group on the left, then press **Enter** on a contact in the candidate list to add or remove them — pressing **Enter** on a contact that is already a member removes them, so the action always toggles.

**Inserting a group into a message**

The quickest way is to type the group name directly in a **To**, **Cc**, or **Bcc** field while composing. The autocomplete list shows matching groups above individual contacts. Select a group from the list and press **Enter** — every member is inserted at once and the screen reader announces the count.

You can also use the Address Book:

1. Switch to the **Groups** tab and select a group.
2. Press **Alt+T**, **Alt+C**, or **Alt+B** — or activate the **To**, **Cc**, or **Bcc** buttons. Every member is inserted in order of recency. The screen reader announces the final count.

If the address book was opened from the main window (not from a compose window), a new compose window opens automatically to receive the addresses.

**Deleting groups**

Select a group and press **Delete** (or activate the **Delete** button). A confirmation appears. Deleting a group does **not** delete the contacts in it.

**Keyboard reference for the Address Book window**

| Action | Shortcut |
|--------|----------|
| Switch to Groups tab / return to Contacts tab | `Ctrl+G` |
| New group (shows name field) | `Ctrl+Shift+N` |
| Rename selected group | `F2` |
| Delete selected group | `Delete` |
| Add selected contact to a group | `Shift+F10` or `Apps` key (opens context menu) |
| Open Group Manager | `Ctrl+Shift+M` |
| Insert group into To field | `Alt+T` |
| Insert group into Cc field | `Alt+C` |
| Insert group into Bcc field | `Alt+B` |

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

### The compose window

From top to bottom, the compose window contains:

1. A **menu bar** (File, Edit, View, Format, Tools) — press **Alt** or **F10** to reach it.
2. A **formatting toolbar**, shown in Markdown and HTML modes.
3. The **From, To, Cc, Bcc, and Subject** fields.
4. The **attachment** controls and list.
5. The **message body** editor.
6. A **status row** with the compose mode selector, the auto-save status, the send status, and the Send / Insert Template / Save as Template / Save Draft / Cancel buttons.

The window title leads with your subject and the editing mode — for example, "Lunch Friday - HTML - QuickMail" — so the taskbar and Alt+Tab identify each message. Until you enter a subject, the title shows the kind of message instead ("New Message", "Reply", "Draft").

### Compose menu bar

Every compose action is available from the menu bar, with its keyboard shortcut shown next to the item:

| Menu | Contains |
|------|----------|
| **File** | Send, Save Draft, Insert Template, Save as Template, Add Attachments, Close |
| **Edit** | Undo, Redo, Cut, Copy, Paste, Select All, Next/Previous Misspelling |
| **View** | Plain Text / Markdown / HTML mode (the active mode is checked), Preview |
| **Format** | Bold, Italic, Underline, Strikethrough, Headings 1–3, Bullet/Numbered List, Insert Link, Clear Formatting, Announce Formatting, Show Formatting |
| **Tools** | Address Book, Check Addresses, Toggle Spelling Announcements, Command Palette |

Format menu items are unavailable in Plain Text mode and shown grayed. Opening the Format menu in Plain Text mode announces why: formatting requires Markdown or HTML mode. Menu items whose action cannot apply right now — like Cut with nothing selected — are also grayed, following standard Windows behavior.

### Compose modes: Plain Text, Markdown, and HTML

Every message can be written in one of three modes:

- **Plain Text** (the default) — no formatting; exactly what you type is sent.
- **Markdown** — write Markdown syntax in the regular text editor (for example `**bold**`, `# Heading`, `- list item`). When the message is sent, QuickMail renders the Markdown to formatted HTML. Recipients see the formatting; recipients whose mail app prefers plain text see your readable Markdown source.
- **HTML** — a rich text editor with real formatting. For screen reader users this is a native edit control, not an embedded web page: your normal edit cursor works exactly as in any text field, with no virtual cursor or browse mode.

**Switching modes:**

| Method | How |
|--------|-----|
| Keyboard | `Ctrl+Shift+1` (Plain Text), `Ctrl+Shift+2` (Markdown), `Ctrl+Shift+3` (HTML) |
| Menu | **View → Plain Text / Markdown / HTML** — the active mode is checked |
| Status row | The compose mode selector at the bottom left of the window |

Your content converts automatically when you switch — Markdown becomes formatted content when entering HTML mode, formatted content becomes Markdown syntax when leaving it. Switching down to Plain Text discards formatting, so QuickMail asks for confirmation first. Each switch is confirmed aloud ("Switched to Markdown mode.").

The mode new messages start in is set under **Settings → General → Composing → Default compose mode**. Drafts and templates always reopen in Plain Text regardless of this setting.

### What Markdown supports — and what survives a mode switch

QuickMail's Markdown understands headings (`#` through `######`), bold, italic, strikethrough (`~~`), inline code and fenced code blocks (including a language name after the opening fence), bullet, numbered, and nested lists, links, bare web addresses (linked automatically), images (`![description](address)`), block quotes, horizontal rules (`---`), and tables in the pipe format (`| A | B |`). Raw HTML typed into Markdown is never rendered — it stays visible as text, so pasted markup cannot smuggle active content into your message.

Task-list syntax (`- [x] done`) is kept as literal text rather than rendered as checkboxes: checkbox form controls are stripped by most mail apps and are not accessible in email, so the bracket text — which reads naturally with screen readers — is sent instead.

Switching Markdown → HTML mode and back returns exactly what you wrote: headings keep their levels, tables keep their header rows and column alignment, images keep their description (alt text) and address, code blocks keep their language, quotes keep their paragraph structure, and link addresses are preserved character for character. An image appears in the HTML editor as its description text, so you can read and refine the description like any other content.

### The HTML that recipients receive

Messages composed in Markdown or HTML mode are sent as a complete, valid HTML document built for accessibility: it declares the message language, carries your subject as the document title, preserves image descriptions, and marks table header cells so screen readers announce column headers while moving through a table. A plain text version always accompanies it for mail apps that prefer text.

### Formatting (Markdown and HTML modes)

The same formatting commands work in both rich modes. In HTML mode they apply real formatting; in Markdown mode they insert the equivalent Markdown syntax at the cursor or around the selected text. Every command confirms its result aloud — "Bold on", "Heading 2", "Bullet list off":

| Command | Shortcut | In Markdown mode inserts |
|---------|----------|--------------------------|
| Bold | `Ctrl+B` | `**` around the selection |
| Italic | `Ctrl+I` | `*` around the selection |
| Underline | `Ctrl+U` | HTML mode only — see note below |
| Strikethrough | `Ctrl+Shift+X` | `~~` around the selection |
| Heading 1 / 2 / 3 | `Ctrl+Alt+1/2/3` | `#` / `##` / `###` at the start of the line |
| Bullet list | `Ctrl+Shift+L` | `- ` at the start of each selected line |
| Numbered list | `Ctrl+Shift+N` | `1. `, `2. `, … at the start of each selected line |
| Indent list item | `Tab` (cursor on a list item) | Adds 2 leading spaces (increases level) |
| Dedent list item | `Shift+Tab` (cursor on a list item) | Removes 2 leading spaces (decreases level) |
| Insert link | `Ctrl+L` | `[text](address)` via a small dialog |
| Clear formatting | `Ctrl+Space` | Removes markers and prefixes from the selection |

Tips:

- With text selected, a command wraps (or unwraps) the selection. With no selection, it inserts an empty marker pair and places the cursor inside, ready to type — invoke it again to toggle off.
- Applying the same heading level twice returns the line to normal text.
- Markdown has no underline syntax, so underline works in HTML mode only. Invoking it in Markdown explains this aloud.
- When the cursor is inside a list item, **Tab** increases the indent level and **Shift+Tab** decreases it. In HTML mode, **Shift+Tab** on a top-level list item removes it from the list. On a non-list line, Tab moves focus between fields as usual.
- The same commands are on the **Format** menu and the formatting toolbar.

### Checking formatting at the cursor

Two commands report the formatting in effect where your cursor is. Both work in Markdown and HTML modes:

- **Announce Formatting (`Ctrl+T`)** speaks a one-line summary — for example, "Heading 2. Bold on, Italic off, Underline off, Strikethrough off."
- **Show Formatting (`Ctrl+Shift+T`)** opens a small window listing the same facts one per row — the block type first ("Heading 2"), then each attribute ("Bold on", "Italic off", …). Arrow through the entries at your own pace; press **Escape** or **Enter** to close and return to the editor exactly where you were.

### Preview window (F8)

In Markdown or HTML mode, press **F8** to open a preview window that renders your message as formatted HTML — the same output your recipient will see. The preview window is fully focusable: screen readers switch into browse mode so you can read and navigate the rendered content just like any webpage.

This is especially useful in HTML mode, where the editor is a rich text editor and screen readers read it in edit mode. The preview lets you hear the message as a recipient would — as a fully-rendered web page.

Links in the preview open in your default browser. Press **Escape** or **Ctrl+W** to close the preview and return focus to the compose editor. Pressing **F8** again while the preview is open also closes it.

The preview is a snapshot of your message at the moment you open it. To refresh it with later edits, close and reopen with **F8**.

### Address fields (To, Cc, Bcc)

The **To**, **Cc**, and **Bcc** fields keep each confirmed address separate. When you finish typing an address it becomes its own button — showing the contact's name, or the email address if no name is known. Each button can be navigated to, removed, or copied independently. Multiple addresses appear as a row of buttons before the text cursor.

**Entering an address:**

- Type an address or name and press **Tab**, **Enter**, **comma (,)**, or **semicolon (;)** to confirm it.
- Or type a few characters and select a contact from the autocomplete dropdown that appears (see [Autocomplete in compose](#autocomplete-in-compose)).

**Navigating addresses with the keyboard:**

| Key | Action |
|-----|--------|
| **Left / Right arrow** | Move focus between addresses |
| **Right arrow** on the last address | Move focus back to the text input |
| **Left arrow** at the start of the text input | Move focus to the last address |
| **Ctrl+A** | Select all address chips in the field |
| **Delete** or **Backspace** (with chips selected) | Remove all selected addresses |
| **Delete** or **Backspace** on a single focused chip | Remove that address |
| **Backspace** in an empty text input | Remove the last address |
| **Ctrl+C** (with chips selected) | Copy all selected addresses to the clipboard |
| **Ctrl+C** on a single focused chip | Copy that address to the clipboard |

**Context menu (right-click or Shift+F10 on an address):**

| Option | Action |
|--------|--------|
| **Copy Address** | Copies the full address (name and email) to the clipboard |
| **Add to Address Book** | Saves the contact silently — no dialog unless the address is already in the book |
| **Remove** | Removes this address from the field |

**Checking addresses (Ctrl+K):**

Press `Ctrl+K` or open the compose Command Palette and search for **Check Addresses** to validate every address in all three fields at once:

- Addresses that cannot be validated are highlighted in red, and their accessible name changes to begin with "Unrecognized:" so screen readers convey the problem without relying on colour alone.
- Bare names with no @ sign are looked up in your address book. If a single match is found, the address is automatically resolved to the full name and email.
- A summary is announced when the check is complete — for example, "3 addresses checked. All valid."

**Screen reader behavior:**

- Each address button's accessible name is the full RFC address — for example, "Kelly Ford &lt;kelly@example.com&gt;". If the address failed validation, the name begins with "Unrecognized:".
- When you Tab into a field that already has addresses, those addresses are announced immediately so you are not left wondering whether the field is empty.

### Spell check

QuickMail supports spelling error detection and announcement while composing messages. A few settings and key commands control how the experience works.

#### The default experience

By default you will not hear about spelling errors as you are typing. You will hear about them when navigating through text with cursor keys, or using **F7** and **Shift+F7** to jump from spelling issue to spelling issue.

When a spelling issue is encountered, you will hear the incorrect spelling and three possible replacements. With focus on the error, press **Alt+1**, **Alt+2**, or **Alt+3** to select one of the three replacements you heard. You can also press **Shift+F10** to bring up a context menu with additional spelling suggestions and an option to ignore the word. After you hear a spelling error announced, you can use your screen reader's Say Line command to read the word in context. To have the spelling error and suggestions repeated, press **Alt+F7**.

#### Adjusting the experience

If the defaults for the spell checking experience are not to your liking, you can make adjustments in QuickMail settings from the main QuickMail window, not the compose window. Open **File → Settings** and select the **General** tab. In the **Screen Reader Announcements** section you will find:

- **Announce spelling errors when typing** — turn this on to hear about spelling errors while you type. Announcements are held until you pause so you hear the complete word, not a partial one.
- **Announce spelling errors while navigating** — turn this off if you do not want to hear about spelling errors as you move through the message body with cursor keys. F7 and Shift+F7 always announce regardless of this setting.
- **Announce spelling suggestions** — when on, up to three replacement suggestions are spoken alongside the misspelled word. Turn this off if you prefer to hear only the misspelled word and use Alt+1/2/3 or Shift+F10 yourself.
- **Announce formatting while navigating in HTML compose** — when on (the default), moving the caret to a paragraph with a different block type in HTML mode announces what it is — for example, moving onto a heading announces "Heading 2" without pressing `Ctrl+T`. Turn this off if you prefer to check formatting on demand only. This setting has no effect in Plain Text or Markdown mode.

Mix and match these settings for the experience you want.

#### Quickly toggling announcements while composing

If you want to temporarily silence spelling announcements without leaving the compose window, open the compose Command Palette with **Ctrl+Shift+P** and search for **Toggle Spelling Announcements**. This flips the "announce while navigating" setting on or off immediately and QuickMail confirms the change aloud. F7 and Shift+F7 navigation always announces regardless of this toggle.

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
| Ctrl+Enter | Send the message (alternate shortcut) |
| Ctrl+S | Save as draft |
| Ctrl+K | Check addresses |
| Alt+U | Jump to the Subject field |
| Alt+M | Jump to the From (account) field |
| Alt+Y | Jump to the message body |
| Ctrl+Shift+1 / 2 / 3 | Switch to Plain Text / Markdown / HTML mode |
| Ctrl+B / Ctrl+I / Ctrl+U | Bold / Italic / Underline (rich modes) |
| Ctrl+Shift+X | Strikethrough (rich modes) |
| Ctrl+Alt+1 / 2 / 3 | Heading 1 / 2 / 3 (rich modes) |
| Ctrl+Shift+L / Ctrl+Shift+N | Bullet list / Numbered list (rich modes) |
| Tab (on a list item) | Indent list item — increase level |
| Shift+Tab (on a list item) | Dedent list item — decrease level |
| Ctrl+L | Insert link (rich modes) |
| Ctrl+Space | Clear formatting (rich modes) |
| Ctrl+T | Announce formatting at the cursor (rich modes) |
| Ctrl+Shift+T | Show formatting at the cursor in a list (rich modes) |
| F8 | Open or close the preview window (Markdown and HTML modes) |
| F7 | Jump to next misspelling |
| Shift+F7 | Jump to previous misspelling |
| Alt+F7 | Repeat spelling announcement for current word |
| Alt+1/2/3 | Replace misspelling with 1st/2nd/3rd suggestion |
| Ctrl+Shift+P | Open command palette |
| Ctrl+Shift+A | Add file attachments |
| Ctrl+Shift+B | Open Address Book (insert contacts into To, Cc, or Bcc) |
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

### Automatic draft saving

QuickMail also saves your message as a draft automatically while you compose. This is on by default, every 2 minutes, and is designed to stay out of your way:

- A successful auto-save quietly updates the status row ("Auto-saved 3:42 PM") with **no announcement**, so your writing is never interrupted.
- If an auto-save fails, it is announced **once** — "Auto-save failed. Your draft is not saved to the server." — and then stays quiet until a save succeeds again.
- To hear when the last auto-save happened, open the compose command palette (**Ctrl+Shift+P**) and run **Announce Last Auto-Save**.
- Auto-save skips messages you have not changed, completely empty messages, and template editing. Like manual saves, each auto-save replaces the previous server draft.

Turn auto-save off or change the interval (30 seconds to 10 minutes) under **Settings → General → Composing**.

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

**Note:** drafts always reopen in Plain Text mode, regardless of the mode they were written in. The editing mode is not yet stored with the draft on the server.

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

## Viewing properties (Alt+Enter)

**Alt+Enter** is the Windows standard shortcut for "Properties." In QuickMail it opens a read-only Properties dialog for whatever is currently selected.

| What has focus | What opens |
|----------------|------------|
| A message in the message list | Message headers, storage details, and format/attachment summary |
| A conversation header (Conversations view) | Subject, message count, unread count, participants, and date range |
| A sender group header (From view) | Sender, message count, unread count, most recent date, and newest subject |
| A recipient group header (To view) | Recipient, message count, unread count, most recent date, and newest subject |
| A folder in the folder tree | Folder name, path, account, and message counts |
| An account in the account list | Server settings and authentication method (no password shown) |
| A contact in the address book | Display name, email address, group memberships, and last-used date |
| A group in the address book | Group name, member count, and a list of members |
| An attachment in the reading pane | File name, MIME type, and size |

In grouped views (Conversations, From, To), Alt+Enter on a group header row shows group-level properties. To see properties for an individual message inside a group, expand the group, navigate to the message, and press Alt+Enter.

### Navigating the Properties dialog

- The dialog opens with focus on the properties list. Use **Up/Down arrows** to move through all rows. Section names appear as focusable entries within the list and are announced when you navigate to them.
- Press **Tab** to move to the **Copy all** and **Close** buttons.
- Press **Enter** or **Ctrl+C** on a selected row to copy it to the clipboard. Section header rows copy just the name; data rows copy "Field: Value". A screen reader announcement confirms the copy.
- Press **Ctrl+A** to select all rows; pressing **Enter** or **Ctrl+C** then copies all selected rows at once.
- Activate **Copy all** to copy every property as formatted plain text organized by section.
- Press **Escape** or activate **Close** to close the dialog. Focus returns to where it was before.

### Screen reader experience

Each data row is announced as a single "Field: Value" phrase — for example, "From: Alice Smith &lt;alice@example.com&gt;" — without reading column headers separately. Section header rows are announced by name (for example, "Headers") as you navigate to them.

### What is not shown

Account properties show the authentication method ("OAuth2" or "Password, stored in Windows Credential Manager") but never display a password or token. Raw message headers are available in Message Properties if they were cached when the message was opened; the section is hidden otherwise.

---

## Deleting messages

- Select one or more messages and press **Delete**. Use **Shift+Up/Down** to select multiple.
- Messages are moved to the **Trash** folder. They are not permanently deleted.
- To permanently delete everything in Trash, activate **Empty Trash** in the toolbar or press `Ctrl+Shift+E`. A confirmation dialog shows how many messages will be deleted before anything is removed.

To skip the confirmation, open **File → Settings**, select the **General** tab, and turn off **Confirm before emptying trash** in the **Mail Actions** group.

---

## Security

- **Passwords** are stored in **Windows Credential Manager** — never in any file on disk. They are protected by your Windows login.
- **OAuth2 tokens** (used for Outlook.com accounts) are encrypted with Windows DPAPI and stored locally. Your Microsoft password is never seen or stored by QuickMail.
- **HTML emails** are displayed in a sandboxed WebView2 component with a strict Content Security Policy. Scripts, embedded objects, remote images, external frames, and forms are blocked or stripped, so malicious email content cannot run on your machine.

---

## Keyboard shortcut reference

### Main window

#### Pane navigation

| Shortcut | Action |
|----------|--------|
| Ctrl+1 | Focus account list (jumps to tab 1 when tabs are open) |
| Ctrl+2 / Ctrl+Y | Focus folder tree (jumps to tab 2 when tabs are open) |
| Ctrl+3 | Focus message list / conversation tree (jumps to tab 3 when tabs are open) |
| Ctrl+4 … Ctrl+8 | Jump to tab N (when tabs are open) |
| Ctrl+9 | Jump to last tab (when tabs are open) / focus status bar (when no tabs) |
| Ctrl+Alt+1 | Focus account list (always works, even when tabs are open) |
| Ctrl+Alt+2 | Focus folder tree (always works) |
| Ctrl+Alt+3 | Focus message list (always works) |
| Ctrl+Shift+T | Focus tab strip (when tab strip is visible) |
| Ctrl+0 | Focus toolbar |
| F6 | Cycle focus forward through panes |
| Shift+F6 | Cycle focus backward through panes |
| Left/Right | Navigate status bar regions (when status bar is focused) |
| Enter / Space | Activate status bar button (Rules) |

#### Tab navigation

| Shortcut | Action |
|----------|--------|
| Ctrl+Tab | Next tab |
| Ctrl+Shift+Tab | Previous tab |
| Ctrl+W | Close active tab (Tab mode) / Close reading pane (Reading Pane mode) |
| Ctrl+Shift+` | Open tab list overlay |

#### Mail actions

| Shortcut | Action |
|----------|--------|
| F5 | Refresh current folder |
| Ctrl+N | New message |
| Ctrl+R | Reply |
| Ctrl+Shift+R | Reply All |
| Ctrl+F | Forward |
| Ctrl+Enter | Open selected message in a new window |
| Delete | Delete selected message(s) |
| K | Toggle flag on selected message or group |
| Ctrl+Shift+K | Open flag picker |
| Ctrl+Shift+L | Manage Rules |
| Ctrl+Shift+T | Create Rule from Message |
| Ctrl+Shift+V | Cycle view mode (Messages / From / To / Conversations) |
| Ctrl+Shift+C | Toggle conversation view |
| Ctrl+Shift+F | Search folders |
| Ctrl+Shift+P | Open command palette |
| Ctrl+, | Open Settings |
| Ctrl+M | Load more messages |
| Ctrl+Shift+E | Empty Trash |
| Ctrl+Shift+B | Open Address Book |
| Ctrl+Shift+G | Grab addresses from open message |
| Ctrl+A | Select all messages in the message list |
| Ctrl+Shift+Home | Extend selection to the first message in the list |
| Ctrl+Shift+End | Extend selection to the last message in the list |
| Shift+Up / Shift+Down | Extend message selection one item at a time |
| Shift+, (< ) | Jump to the first (newest) message in the current group — grouped views only |
| Shift+. (> ) | Jump to the last (oldest) message in the current group — grouped views only |
| Shift+F10 | Open context menu for focused item |
| Escape | Close reading pane |
| Alt+Enter | View Properties for the selected item (message, group, folder, account, contact, or attachment) |

### Message window

| Shortcut | Action |
|----------|--------|
| F6 / Shift+F6 | Cycle focus: toolbar → header fields → message body |
| Alt+Left | Previous message |
| Alt+Right | Next message |
| Ctrl+Shift+P | Open command palette |
| Ctrl+W | Close window |
| Escape | Close window |

### Compose window

| Shortcut | Action |
|----------|--------|
| Alt+S | Send |
| Ctrl+Enter | Send (alternate shortcut) |
| Ctrl+S | Save draft |
| Ctrl+K | Check addresses |
| Alt+U | Jump to Subject field |
| Alt+M | Jump to From (account) field |
| Alt+Y | Jump to message body |
| Alt or F10 | Activate the menu bar |
| Ctrl+Shift+1 / 2 / 3 | Plain Text / Markdown / HTML mode |
| Ctrl+B / Ctrl+I / Ctrl+U | Bold / Italic / Underline (rich modes) |
| Ctrl+Shift+X | Strikethrough (rich modes) |
| Ctrl+Alt+1 / 2 / 3 | Heading 1 / 2 / 3 (rich modes) |
| Ctrl+Shift+L / Ctrl+Shift+N | Bullet / Numbered list (rich modes) |
| Tab (on a list item) | Indent list item — increase level |
| Shift+Tab (on a list item) | Dedent list item — decrease level |
| Ctrl+L | Insert link (rich modes) |
| Ctrl+Space | Clear formatting (rich modes) |
| Ctrl+T | Announce formatting at the cursor (rich modes) |
| Ctrl+Shift+T | Show formatting at the cursor in a list (rich modes) |
| F8 | Open or close the preview window (Markdown and HTML modes) |
| Left / Right arrow (on an address) | Move between addresses |
| Delete / Backspace (on an address) | Remove focused address |
| Delete / Backspace (with chips selected) | Remove all selected addresses |
| Ctrl+A (in address field) | Select all address chips |
| Ctrl+C (with chips selected) | Copy all selected addresses |
| Ctrl+C (on an address) | Copy address to clipboard |
| F7 | Jump to next misspelling |
| Shift+F7 | Jump to previous misspelling |
| Alt+F7 | Repeat spelling announcement |
| Alt+1 / Alt+2 / Alt+3 | Replace misspelling with suggestion |
| Ctrl+Shift+A | Add file attachments |
| Ctrl+Shift+B | Open Address Book (insert contacts into To, Cc, or Bcc) |
| Ctrl+V | Paste files from clipboard as attachments |
| Delete (in attachment list) | Remove selected attachment |
| Ctrl+Shift+P | Open compose command palette |
| Tab | Move between fields |
| Escape | Close window (prompts if there are unsaved changes) |

---

## Data storage

QuickMail keeps all its files under `%AppData%\QuickMail\` (typically `C:\Users\<YourName>\AppData\Roaming\QuickMail\`). You can point QuickMail at a different directory — for example, a folder on OneDrive — using the `--profileDir` command-line option (see [Command-line options](#command-line-options)).

| File / folder | Contents |
|---------------|----------|
| `accounts.json` | Account configuration — server addresses, ports, display names. No passwords. |
| `contacts.json` | Address book contacts — display names and email addresses (human-readable JSON) |
| `flags.json` | Named flag definitions — colors and names (human-readable JSON) |
| `rules.json` | Mail rules — conditions and actions for automatic message processing |
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
| `DefaultComposeMode` | `plain` / `markdown` / `html` | `plain` | The editing mode new compose windows start in. Drafts and templates always reopen in plain text. Also available in Settings → General → Composing. |
| `AutoSaveDrafts` | `on` / `off` | `on` | Automatically save the message as a server draft while composing. Also available in Settings → General → Composing. |
| `AutoSaveIntervalSeconds` | `30`–`600` | `120` | Seconds between automatic draft saves. Also available in Settings → General → Composing. |
| `AnnounceFlagStatus` | `true` / `false` | `true` | When on, the flag name is announced before the read status when navigating flagged messages. Also available in Settings → General → Screen Reader Announcements. |

### `[account:<guid>]` overrides

You can override `PreviewLines` for a specific account by adding a section with that account's GUID (visible in `accounts.json`):

```ini
[account:xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx]
PreviewLines = 1
# Show fewer preview lines for this account only.
```

Changes to `config.ini` take effect the next time you start QuickMail.

---

## Command-line options

QuickMail accepts the following options on the command line:

| Option | Description |
|--------|-------------|
| `--profileDir <path>` | Use `<path>` as the profile directory instead of the default `%AppData%\QuickMail`. All data files (accounts, mail cache, config, contacts, views, and log) are read from and written to this directory. The directory is created automatically if it does not already exist. |
| `--online` | Run in fully online mode. Every folder selection fetches messages live from IMAP. Nothing is read from or written to the local SQLite cache (`mail.db`). Useful for testing or when you want a fresh view without any cached data. IDLE push notification and background sync are also disabled in this mode. |
| `--help` | Show a summary of available options and exit. Also accepted as `-h`, `-help`, or `/?`. |
| `/debug` | Write verbose diagnostic output to `quickmail.log` in the profile directory. |

### Using profiles

The `--profileDir` option lets you run QuickMail with a completely separate set of accounts and mail data. This is useful if you want to keep work and personal mail in separate profiles, or if you want your data stored in a synced folder such as OneDrive.

**Example — store data on OneDrive:**

```
QuickMail.exe --profileDir "C:\Users\YourName\OneDrive\QuickMail"
```

**Example — run a second profile:**

```
QuickMail.exe --profileDir "C:\Users\YourName\AppData\Roaming\QuickMail-Work"
```

Each profile is fully independent. Passwords are stored in Windows Credential Manager (system-wide) and are shared across profiles — you do not need to re-enter them when switching.
