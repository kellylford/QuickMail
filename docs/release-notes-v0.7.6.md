# QuickMail v0.7.6 Release Notes

## Download

Two options are available for v0.7.6:

| Download | When to use |
|----------|-------------|
| **`quickmail-v0.7.6-setup.exe`** — Windows installer | Recommended for most users. Installs per-user with no elevation required, checks for the WebView2 Runtime, and registers an uninstaller. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

---

## Bug Fixes

### Adding a new account no longer disconnects existing accounts

Opening the Account Manager and adding a new account no longer causes all existing accounts to appear disconnected. The bug was a race condition in the `AccountReachabilityChanged` event handler: when the Account Manager refreshed the accounts collection, the event handler remained bound to the old account objects. New accounts would never receive their connection status updates and would show as disconnected indefinitely. The handler is now properly unsubscribed and re-subscribed when the accounts collection is refreshed. (#126)

### Virtual folder refresh now reconciles read and flag state

Pressing **F5** in **All Mail**, **All Inboxes**, **Drafts**, **Sent**, **Trash**, or a per-account All Mail view now correctly updates messages that were already in the list. Previously, if you marked a message read or flagged it in another mail client and then pressed F5, the existing row kept its stale state — only newly arrived messages were added. The refresh now updates `IsRead`, reply/forward state, attachments, and flag information on any message that has changed on the server, without disturbing selection or screen reader focus. (#111)

### Microsoft 365 / Exchange: nested folders now appear in the folder tree

When using a Microsoft 365 or Exchange account, folders nested inside other folders (for example, a **Projects** folder inside **Inbox**) now appear correctly in the folder tree. Previously, only top-level folders were fetched — any subfolder was invisible. (#109)

### Microsoft 365 / Exchange: folder picker shows readable paths instead of internal IDs

When moving or copying a message on a Microsoft 365 or Exchange account, the folder picker now shows readable folder paths (for example, **Inbox/Projects/2026**) instead of the opaque internal identifier that Graph uses for folder names. Screen readers were announcing strings like `QM Test — AAMkADRmODc0…`; they now announce the folder name as expected. (#109)

---

## Improvements

### Logging control in Advanced settings

**Settings → Advanced** now has a **QuickMail Logging** section with two options:

- **Enable logging** checkbox — turn logging on or off. Logging is **off by default**. When enabled, QuickMail writes activity to `quickmail.log` in your profile directory (usually `%APPDATA%\QuickMail`, or the path supplied via `--profileDir`). Changes take effect as soon as you select **Save** — no restart required.
- **Delete QuickMail log** button — deletes the current log file after a Yes/No confirmation. If logging is still enabled, a fresh log file is created the next time an activity is logged.

> **Note:** If QuickMail is launched with the `/debug` flag, logging always runs regardless of the **Enable logging** setting. The `/debug` flag is intended for diagnosing problems and overrides this preference so that no diagnostic detail is lost.

### Spelling suggestions are now numbered

When a misspelling is announced, each suggestion is now preceded by its number — for example: "Misspelling: teh. 1: the, 2: then, 3: them." This makes the `Alt+1` / `Alt+2` / `Alt+3` correction shortcuts immediately obvious without having to count suggestions mentally.

A new **Spelling Suggestions Verbosity** setting in **Settings → Screen Reader Announcements** lets you choose between **Numbers with suggestions** (the new default) and **Just suggestions** if you prefer the previous style.

---

## Thank You to Contributors

Thank you to everyone who has contributed to QuickMail through code, bug reports, feature suggestions, and other feedback. Your contributions make the project better for everyone.

---

## Internal

### Virtual folder refresh reconciliation

- `MainViewModel`: replaced the add-only `existingKeys` `HashSet` in all virtual-folder merge paths (All Mail, per-account All Mail, All Inboxes, Drafts, Sent, Trash) with a `Dictionary` keyed on `(MessageId, AccountId, FolderName)`. On collision, a new `ReconcileMessageState` helper copies server-fresh mutable fields (`IsRead`, `IsReplied`, `IsForwarded`, `HasAttachments`, `FlagId`, `FlagName`, `FlagColorHex`, `IsServerFlagged`) onto the existing `ObservableObject` in place, triggering `PropertyChanged` so rows update without disturbing selection or screen reader focus. Genuinely new messages continue to be inserted sorted. The local-store upsert at the end of each path already covered all fetched messages, so cache correctness was unaffected.

### Change-notification extraction (`IChangeNotifier`)

- New `IChangeNotifier` interface (`InboxNewMailDetected`, `AccountReachabilityChanged`, `StartWatchers`, `StopWatchers`) extracted from `IMailService`, so each backend can supply its own notification strategy without the interface coupling.
- `ImapMailService` implements `IChangeNotifier` directly — the IMAP IDLE watcher is bound to the IMAP connection lifecycle (`DisconnectAsync` cancels an account's watcher; `Dispose` stops them all), so extracting it to a standalone class would require two-way event plumbing with more risk. `StartIdleWatchers`/`StopIdleWatchers` renamed to `StartWatchers`/`StopWatchers`.
- New `ChangeNotifierRouter` aggregates per-backend notifiers behind one surface (mirrors `MailServiceRouter` for `IMailService`). `StartWatchers` partitions accounts by `BackendKind` so each notifier only watches accounts it owns; events from all backends are forwarded to a single subscriber set. Microsoft Graph accounts receive no watcher here — Graph delta-poll notification lands in PR 7b.
- `MainViewModel` takes an optional `IChangeNotifier` (production wires the router; tests pass `null`) and subscribes to it instead of to `IMailService`. The `AccountReachabilityChanged` subscription is stored in `_onReachabilityChanged` and explicitly unsubscribed before re-subscribing, preventing handler stacking on repeated `StartBackgroundSyncAsync` calls; `RefreshAccountList` follows the same pattern so account-list refreshes always bind to the current account objects.
- New `InvokeOnUi` helper in `MainViewModel` marshals ThreadPool-fired change-notifier events onto the UI thread before accessing `Accounts` or `_cachedFolders`.
- New `ChangeNotifierRouterTests`: account partitioning, event forwarding, and dispose behaviour (6 tests). Existing `IdleNewMailTests` migrated to drive `IChangeNotifier` directly.

### Microsoft Graph: nested folder fetch and readable picker paths

- `GraphMailService.GetFoldersAsync`: now does a breadth-first descent into `/me/mailFolders/{id}/childFolders` for any folder reporting `childFolderCount > 0`. `/me/mailFolders` alone returns only top-level folders. `childFolderCount` added to the folder DTO (`GraphFolderDto`).
- `MailFolderModel`: new nullable `ParentId` property (null for IMAP accounts, populated for Graph accounts).
- `FolderTreeBuilder`: builds the hierarchy from `ParentId` when any folder in the set carries one (Graph); falls back to the existing separator-path logic for all-IMAP accounts. Well-known folders (Inbox, Drafts, Sent, Deleted, Junk) sort conventionally in both builders via a shared `WellKnownRank`; remaining folders sort alphabetically.
- `FolderPickerWindow`: reconstructs a `DisplayName` path by walking the `ParentId` chain (for example, `Inbox/Projects/2026`) so Graph's opaque `FullName` id is never shown. For all-IMAP accounts the `byId` map is skipped entirely.
- New tests: `GraphMailServiceTests.GetFoldersAsync_DescendsIntoChildFolders_AndSetsParentId`, `FolderTreeBuilderTests` (IMAP separator nesting, Graph parent-id nesting 3 deep, conventional well-known ordering), `FolderPickerPathTests` (IMAP keeps `FullName`; Graph builds display-name path without surfacing the id).
