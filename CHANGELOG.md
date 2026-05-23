# Changelog

## v0.6.1

### New Features

- **IMAP IDLE push** — a dedicated background connection per account watches the INBOX and triggers a targeted sync the moment the server signals new mail. Works in both normal and `--online` modes. No configuration required.
- **Online mode (`--online`)** — new command-line flag for fully live IMAP access with no local SQLite cache.
- **12-hour clock** — message dates now show `9:30A` / `3:45P` for today and `M/d/yyyy` for older messages.

### Bug Fixes

- IDLE push did not work in `--online` mode — the watcher connections were never started.
- Preview text was still shown in the message list when Preview Lines was set to 0 in Settings.

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
