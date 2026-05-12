# Changelog

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
