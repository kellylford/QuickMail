# QuickMail v0.8.34 Release Notes

## Download

Two options are available for v0.8.34:

| Download | When to use |
|----------|-------------|
| **`QuickMail-win.msi`** — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

This release adds **archiving** — move messages out of your inbox into an Archive folder instead of deleting them, with a dedicated shortcut — and lets you **turn off the delete and archive announcements** on their own so they stop interrupting the screen reader as it reads the next message. If you installed QuickMail from the MSI, this update is delivered automatically.

---

## New: Archive messages

You can now **archive** a message — move it to your account's Archive folder — instead of deleting it. This is for mail you want out of your inbox but kept, not thrown away.

- Press **Ctrl+Shift+M** to archive the selected message. The command is named **Move to Archive** (on the message menu, the context menu, and in the Command Palette), and the shortcut is rebindable in **Settings → Keyboard**. **Delete** still works exactly as before (moves to Trash).
- Archiving works from every view. In the **From**, **To**, and **Conversations** groupings, archiving a group moves the whole group at once — the same way Delete does.
- **Each account archives to its own folder.** QuickMail uses the folder your mail provider marks as the Archive folder automatically, so for most accounts there is nothing to set up. To choose a different folder, activate a folder in the folder tree, open its context menu, and select **Set as Archive Folder** (or **Use Automatic Archive Folder** to go back to the automatic one). There is no single shared archive folder — the choice is per account.
- If an account has no Archive folder and you have not set one, QuickMail tells you so instead of doing nothing.

## New: Turn off delete and archive announcements on their own

When you delete or archive a message, QuickMail announces "Deleting message…" and "1 message deleted." Because those announcements shared the same setting as background loading and sync progress, turning them off also silenced sync progress — and left on, they can interrupt the screen reader as it starts reading the next message in the list.

Delete and archive announcements now have **their own setting**. In **Settings → Screen Reader Announcements**, clear **Announce delete and archive actions** to silence just that chatter while keeping everything else. It is on by default, and the change takes effect immediately. Failure messages (for example, if a delete may not have completed) are still announced even when this is off.

---

## Thank You to Contributors

Thank you, as always, to everyone who contributes to QuickMail through code, bug reports, feature suggestions, and other feedback — including the requests that shaped archiving and the report that the delete announcements were interrupting the reading of the next message.

---

## Reporting Issues

Found a problem or have a suggestion? There are three ways to reach us — pick the one that fits:

1. **Report a Bug → Send** (Help menu, inside QuickMail). Files the report for you anonymously — it includes no email address or other identifying information, so there is no way to follow up with you. **Best when you don't want any follow-up.**
2. **Report a Bug → Copy report and open GitHub** (Help menu). Opens a pre-filled issue that you submit under your own GitHub account, so your GitHub contact information is attached. **Best when you have a GitHub account and want automatic filing plus direct contact.**
3. **Email** [quickmailissues@theideaplace.net](mailto:quickmailissues@theideaplace.net). **Best when you don't mind sending email and want a personal follow-up.**

Full details, including exactly what a report contains (and what it never contains), are in the [Reporting Issues section of the User Guide](https://kellylford.github.io/QuickMail/reporting-issues.html).

---

## Internal

### Per-account message archiving (issue #318, PR #319)

- New `mail.archive` command, titled **Move to Archive** (default **Ctrl+Shift+M**, rebindable, in the Command Palette). Registered in `MainViewModel.RegisterCommands` and overridden in `MainWindow` with the same focus-aware guard as `mail.delete` so multi-selection in the list and group-tree nodes archive the whole selection/group via the controls' own `PreviewKeyDown` (an `IsArchiveGesture` helper). Ctrl+Shift+M was chosen over Alt+Delete, which collides with the common screen-reader "announce cursor position" command.
- `MainViewModel.ArchiveMessagesAsync` mirrors `DeleteMessagesAsync`'s optimistic UI + focus landing, but the server op is a **move** (like `MoveSelectedMessagesToFolderAsync`): folder counts reconcile via `ScheduleFolderCountRefresh` and the account unread total is left intact (archived mail still belongs to the account). Messages are grouped by `(AccountId, FolderName)`, so one call on a From/To/conversation group archives across every account it spans, each to its own Archive folder. Already-archived messages are skipped; accounts with no Archive folder surface guidance rather than a silent no-op.
- Archive folder resolution: explicit per-account override wins, else the server-flagged Archive folder. New `SpecialFolderKind.Archive`, detected in `ImapMailService.GetSpecialFolderKind` (IMAP `\Archive` attribute + name fallback) and `GraphMailService` (well-known `archive` + display-name fallback); Archive is **not** excluded from the All Mail aggregate. New `AccountModel.ArchiveFolderFullName` (nullable, auto-persists to accounts.json), set/cleared from the folder-tree context menu (**Set as Archive Folder** / **Use Automatic Archive Folder**). No global archive folder.
- Gmail has no `\Archive`; pointing an account's archive folder at `[Gmail]/All Mail` archives correctly (the move removes the Inbox label). Tests: command registration + gesture; Graph "Archive" maps to `SpecialFolderKind.Archive`.

### Message-action announcement category (issue #317, PR #320)

- Delete/archive status announcements were emitted as `AnnouncementCategory.Status` (every `StatusText` change is announced by the View), so they rode the same toggle as sync/loading and interrupted the screen reader reading the next message. New `AnnouncementCategory.MessageAction` + `ConfigModel.AnnounceMessageActions` (default on; `config.ini` under `[global]`; honored in `AccessibilityHelper`, applied immediately by `ConfigService.Save → AccessibilityHelper.Configure`). Settings dialog: **Announce delete and archive actions**.
- Status-bar announcements can now carry a category. `MainViewModel.SetStatus(text, category)` sets a one-shot `StatusAnnouncementCategory` and assigns `StatusText`; the View's `PropertyChanged` handler reads it synchronously (WPF raises `PropertyChanged` inline) in `QueueStatusAnnounce`, capturing the category at queue time before the VM resets it. Plain `StatusText = …` stays `Status`. Delete/archive progress/success/cancel route through `MessageAction`; failures and the archive "no Archive folder" guidance stay `Result` so they're heard even when the toggle is off. Reusable — move/copy can adopt `MessageAction` later.
- Tests: `AnnounceMessageActions` default-on + config round-trip; settings-VM load/save round-trip.
