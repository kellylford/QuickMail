# Changelog

## v0.7.0

### New Features

- **Tabs and windows** — Messages can now open in a reading pane (original behavior), a tab, or a standalone window. Controlled by the new **Reading mode** setting in **Settings → Windowing**. Tab mode adds a strip below the toolbar with `Ctrl+Tab`/`Ctrl+Shift+Tab` navigation, `Ctrl+1`–`9` tab jumping, `Ctrl+W` to close, and a tab list overlay (`Ctrl+Shift+\``). Window mode opens each message in its own window with Previous/Next navigation, F6 focus cycling, and a command palette (`Ctrl+Shift+P`).
- **Always-reliable pane shortcuts** — `Ctrl+Alt+1`–`4` focus the account list, folder tree, message list, and tab strip regardless of whether tabs are open, complementing the existing `Ctrl+1`–`3` shortcuts.

### Accessibility

- Tab strip, message window, and Settings dialog radio groups all follow proper screen reader conventions. See full release notes for detail.

### Bug Fixes

- Multiple tab strip, message window, and reading pane bugs fixed. See full release notes for detail.

---

## v0.6.9

### New Features

- **Alt+Enter View Properties** — pressing Alt+Enter opens a read-only Properties dialog for the focused item: a message, conversation group, sender/recipient group, folder, account, contact, address book group, or attachment. Group headers in Conversations, From, and To views show group-level properties (subject, participants, date range for conversations; sender/count for sender and recipient groups) rather than falling back to a message.

### Accessibility fixes

- **Properties dialog is one continuous list** — all fields and section headers are in a single arrow-navigable list with no separate focus stops between sections. Section headers are focusable entries. Ctrl+A selects all rows for bulk copy.
- **Properties hint respects hint suppression** — the copy-row instruction is delivered through the hint system and silenced when hints are turned off in Settings.

### Bug Fixes

- **Alt+Enter from command palette** — now shows properties for the correct item instead of doing nothing.
- **Alt+Enter on folder tree** — now reads the live focused folder rather than the last committed selection.

---

## v0.6.8

### New Features

- **Contact groups** — The Address Book window now has a **Groups** tab alongside the flat contact list. Create, rename, and delete groups; add and remove members; insert the whole group into To/Cc/Bcc with a single action. The Group Manager dialog (`Ctrl+Shift+M`) is a focused view of the same operations. Group data is stored locally in a separate `groups.json`; nothing leaves the machine.

### Bug Fixes

- **Group / contact concurrency** — Group operations and contact operations now share a single load lock, so a group write cannot tear or block a concurrent contact write.

## v0.6.7

### New Features

- **To Me filter** — New filter (View → Filter → To Me) shows only messages where one of your configured account addresses appears in the To field. Mailing list messages are excluded via `List-Id` header detection, with a retroactive domain-pattern backfill for existing database rows.
- **Selection keyboard shortcuts** — Ctrl+A selects all messages in the list; Ctrl+Shift+Home/End extend selection to the first or last message. Ctrl+A also works in the Address Book (select all contacts) and in compose address fields (select all chips).
- **Mark as Read (Ctrl+Q)** — Marks the selected message, group, or entire folder as read without opening it.
- **Action-first log format** — New option in Settings → Advanced puts the message before the timestamp in quickmail.log, making each line readable from the start.

### Accessibility fixes

- **Status bar** — Screen readers no longer announce the status text multiple times when focus moves to the status bar (Ctrl+9). Content is now announced exactly once. The persistent "Press Escape to return to message list" hint has been removed from the status text; it is delivered as a one-time announcement immediately after a message opens.

---

## v0.6.3

### Accessibility

- **Status bar keyboard navigation** — The status bar now has four named regions navigable with **Left** and **Right** arrow keys: Status, Connection, Rules, and Sync Progress. Press `Ctrl+9` or `F6` to reach the status bar, arrow between regions, and **Tab** to exit. Screen readers announce each region individually as focus moves.
- **Connection status region** — A new status bar region shows the current connection state: "Offline", "Connecting…", "Syncing…", "N accounts connected", or "Connection error". The value updates in real time as accounts connect and sync.
- **Rules status bar region is now a proper button** — The Rules summary in the status bar is now a `Button` (ControlType.Button + InvokePattern) rather than a styled read-only text box. Screen readers correctly announce it as interactive, and **Enter** or **Space** opens the Rules Manager. Previously the element was announced as "edit" and its clickability was invisible to keyboard users.
- **Screen reader status bar commands** — The status bar exposes the UIA StatusBar control pattern, enabling screen readers to read the entire bar with their built-in status-bar commands.

---

## v0.6.2

### New Features

- **Connection status and message counts in the account list** — Each account now shows a status line displaying connection state and unread/total message counts across all folders (e.g., "Connected — 5 unread, 44 total").
- **Unread count badges on folders** — The folder tree now displays an unread count badge (e.g., `(3)`) next to each folder name when there are unread messages.
- **Account-wide message counts** — Message counts now reflect your entire account across all folders, not just the inbox.

### Bug Fixes

- Status bar message count now updates live during searches, filters, and when new mail arrives via IMAP IDLE.
- Sync Range setting in Settings now takes effect immediately without requiring a manual refresh.
- Virtual folders (All Inboxes, All Sent, All Drafts, All Trash) now properly receive new mail in real-time when they are active.
- Folder navigation no longer flashes stale messages from the previous folder before the new folder loads.
- Screen reader announcements no longer duplicate folder names, unread counts, or connection status.

---

## v0.6.1

### New Features

- **IMAP IDLE push** — a dedicated background connection per account watches the INBOX and triggers a targeted sync the moment the server signals new mail. Works in both normal and `--online` modes. No configuration required.
- **Online mode (`--online`)** — new command-line flag for fully live IMAP access with no local SQLite cache.
- **12-hour clock** — message dates now show `9:30A` / `3:45P` for today and `M/d/yyyy` for older messages.

### Bug Fixes

- IDLE push did not work in `--online` mode — the watcher connections were never started.
- Delete and Move to Folder crashed in `--online` mode with "no such table: MessageSummary" — the local store cleanup after an IMAP delete is now skipped when running in online mode.
- Preview text was still shown in the message list when Preview Lines was set to 0 in Settings.
- Preview Lines 1–5 all showed the same amount of preview text — values now map to 100–500 characters (100 per line).

---

## v0.6

### New Features

- **Mail rules** — automatic actions (move, mark read/unread, delete) on incoming messages during sync, with a full Rules Manager dialog (`Ctrl+Shift+L`), per-condition toggles, folder picker integration, test-against-current-messages dry run, and "Create Rule from Message" shortcut (`Ctrl+Shift+T`). Rules run locally; no data leaves the machine.
- **Profile support** — `--profileDir <path>` command-line option stores all data (accounts, mail cache, config, contacts, views, rules, log) in a custom directory. Use it to keep work and personal mail in separate profiles, or to store data on a synced drive. Also adds `--help` to show available options and exit.

### Bug Fixes

- Messages in regular IMAP folders (e.g. INBOX) now appear in real-time as sync completes. Previously only virtual-folder views (All Mail, All Inboxes, etc.) received live sync updates; messages in regular folders required a manual refresh.

---

## v0.5.9

### Accessibility

- Restored automatic focus into the WebView2 message body after opening a message.
- Added a retrying WebView2 host plus DOM focus handoff and UIA notification so NVDA receives a stable "Message body" focus event after heavy HTML rendering.

## v0.5.8

### Message rendering

- Heavy sender HTML is now prepared off the UI thread before it is sent to WebView2.
- Large, deeply table-based, or embedded-image-heavy messages are displayed in a simplified reader mode instead of rendering the full marketing HTML.
- HTML rendering now strips remote images, external resources, scripts, forms, inline event handlers, and oversized styling before display.
- WebView2 body navigation has a timeout so a problematic message cannot hold the interface indefinitely.

## v0.5.7

### Folder tree shortcuts

- `Ctrl+2` and `Ctrl+Y` now focus the main folder tree pane instead of opening the quick-search folder picker.
- Added `Ctrl+Shift+F` / **View > Search Folders...** for the virtualized flat folder list.
- The same folder-tree shortcut handling now works from the WebView2 reading pane.

## v0.5.6

### Folder picker shortcuts

- `Ctrl+2` and `Ctrl+Y` both opened the same folder picker.
- Added WebView2 shortcut relay so both shortcuts work while focus is in the reading pane.
- Added feedback when folders are still loading instead of silently ignoring the shortcut.

## v0.5.5

### Folder navigation and Inbox loading

- Changed the primary folder picker shortcut to `Ctrl+2`.
- Folder selection now shows cached folder messages immediately and refreshes from IMAP in the background, so opening Inbox does not wait on Gmail before returning control to the UI.
- Foreground folder summary fetches no longer request IMAP preview text; background sync still fills previews when available.

## v0.5.4

### Folder picker

- Replaced the `Ctrl+Y` folder picker tree with a virtualized searchable list so opening the picker does not realize a large Gmail folder tree on the UI thread.
- The folder list is focused when the picker opens; press `/` to move focus to the search field, type to filter folders, and press Enter to open the selected folder.

## v0.5.3

### IMAP responsiveness

- Raised the default IMAP connection limit from 3 to 6 simultaneous connections per account.
- Added foreground/background IMAP leasing: background sync, polling, UID checks, and preview fetching are capped below the full pool so message opening and attachment downloads keep reserved connection capacity.
- Increased the supported `MaxImapConnectionsPerAccount` range to 1-15.

## v0.5.2

### IMAP concurrency

- Added a bounded IMAP connection pool per account so background sync, preview fetching, message opening, attachment downloads, and move/copy/delete operations do not reuse the same busy MailKit client.
- Message opening now allows a newer selection to cancel and supersede an older body load, preventing stale or slow loads from blocking quick navigation.
- Added `MaxImapConnectionsPerAccount` to `config.ini` with a default of 3 and a supported range of 1-10.

### Documentation

- Updated the README, user guide, and project notes for pooled IMAP connections and the current keyboard shortcuts.

## v0.5.1

### Attachments

- You can now send attachments when composing a message. Use the **Add Attachment** button, drag files onto the compose window, or paste from the clipboard.
- Received messages show their attachments in the reading pane. Click an attachment to open it or use **Save As** to save it to disk.
- A security warning is shown before opening file types that are commonly used to deliver malware (`.exe`, `.bat`, `.ps1`, etc.).
- Attachment presence is indicated in the message list so you can tell at a glance which messages have files.

### Drafts

- **Save Draft** (Ctrl+S) saves your in-progress message to the Drafts folder on the server so it is accessible from any device.
- Drafts are automatically removed from the server when you send the message.
- The compose window now shows a clear saved/unsaved state in the title bar.

### Microsoft / Outlook accounts

- Microsoft 365 and Outlook.com accounts can now be added using OAuth2 — no app password required. QuickMail will open a browser login page and handle the token exchange automatically.
- Fixed a bug that could cause OAuth2 re-authentication to fail after a token refresh.

### Conversation view

- Messages can be grouped by conversation (thread). Conversation grouping is **off by default** and can be toggled from the View menu.
- Read/unread status is now shown with a dedicated label in the message list for better accessibility.

### Accessibility

- Screen readers now receive live announcements (via UIA) when the message list is loading, when a folder is empty, and when messages are deleted.
- Arrow key navigation is properly contained within each list pane — keys no longer bleed through to adjacent panes.
- Keyboard shortcut fixes: Escape now correctly closes the reading pane without closing the main window.

### User Guide

- A built-in **User Guide** is now available from the Help menu or by pressing **F1**. It opens the guide on GitHub in your default browser.
- The guide now includes full documentation for `config.ini` settings.

### Other fixes

- Fixed a crash that could occur when closing a dialog under certain timing conditions.
- Fixed the window title not always reflecting the currently open folder or message.
- Shift+click now selects a range of messages in the message list.
- The authentication method selector in the account setup dialog is now a drop-down instead of free text, making valid options clearer.
- The app reconnects automatically when the IMAP connection drops and you refresh.
