# Startup Improvements — PM + Dev Spec

**GitHub issues:** [#144 Improve QuickMail startup](https://github.com/kellylford/QuickMail/issues/144) (closed — Phase 1)
· [#516 Startup folder as a first-class setting, and a configurable startup sync scope](https://github.com/kellylford/QuickMail/issues/516)
· resolves [#498](https://github.com/kellylford/QuickMail/issues/498)

## Status

| Phase | Description | Status |
|---|---|---|
| 1 | Inbox-first parallel sync in `SyncService` | ✅ Shipped in v0.7.6 |
| 2 | Persist the folder list in SQLite | ✅ #516 |
| 3 | `StartupFolder` config keys | ✅ #516 |
| 4 | Resolve and apply the startup folder in `InitialLoadAsync` | ✅ #516 |
| 5 | Retire `SavedView.IsDefault`, with migration | ✅ #516 |
| 6 | Folder-tree context menu + commands | ✅ #516 |
| 7 | Settings → Startup | ✅ #516 |
| 8 | Startup sync scope | ✅ #516 |

> **This spec was rewritten for #516.** The original Phases 2–4 (a `StartupFolder` limited to
> virtual folders and saved views, applied *after* the saved-view default, chosen from a flat
> ComboBox) were never built and are superseded. Their central assumption was wrong: see §2.

---

## Section 1: Executive Summary

Users kept asking to open QuickMail in a folder of their choosing. The only route was to navigate
there, save a view, open the View Manager, and tick **Default view (applied on startup)** — three
surfaces and a permanent saved-view artifact to express one preference. #516 makes the startup
folder a first-class setting, reachable from the folder tree's context menu or Settings → Startup,
and removes the saved-view mechanism entirely.

The same change lets users cap how much QuickMail syncs at launch, which matters for anyone with
several accounts and a deep folder tree.

---

## Section 2: What the original spec got wrong

The 2026 spec assumed the startup folder was a small config-plus-UI job. It is not, because of one
fact it did not record:

**The folder list was never persisted.** `MainViewModel._cachedFolders` was in-memory only, filled
by `GetFoldersAsync` after connect. So at launch nothing knew which folders existed, let alone which
were Inboxes. That made "open me in All Inboxes" unimplementable before the network came up — which
is exactly why the saved-view default was applied *post-connect* and why users saw All Mail first
and a switch a few seconds later. Issue #57 had already moved that application from post-sync to
post-connect; it shortened the flash without removing it.

It is also why the folder tree at launch showed only the eight virtual aggregates, and the root
cause behind [#451](https://github.com/kellylford/QuickMail/issues/451).

So #516 starts by persisting folder metadata, and everything else builds on it.

Two further corrections to the original text: the settings dialog is `Views/SettingsDialog.xaml`
(there is no `SettingsWindow.xaml`), and its line references were stale by roughly 800 lines.

---

## Section 3: Design principles

1. **The startup folder is applied from cache, before any connect.** CLAUDE.md's startup rule
   requires it, and applying it later is the defect being fixed, not an implementation detail.
2. **One concept, not two.** A startup folder is a folder. Saved views remain what they always
   were — named filters with hotkeys — and no longer double as a startup mechanism.
3. **Set it where you already are.** The folder tree is the primary entry point; Settings is where
   you see and change it.
4. **Falling back is normal, and it is explained.** A startup folder that no longer resolves opens
   All Mail and says so. Never a dialog, never a crash.
5. **Nothing is skipped permanently.** Every sync scope still leaves the periodic sweep visiting
   every folder and the live watchers covering every Inbox.

---

## Section 4: Scope

### In scope

| Feature | Location | Default |
|---|---|---|
| Folder metadata persistence | `Folder` table in `mail.db` | Always on |
| `StartupFolder` / `StartupFolderAccount` / `StartupFolderLabel` | `[global]` in `config.ini` | Empty = All Mail |
| `StartupSyncScope` | `[global]` in `config.ini` | `startupFolder` |
| Set / Clear from the folder tree | `FolderContextMenu` + `folder.setStartupFolder`, `folder.clearStartupFolder` | No default hotkey |
| Settings → Startup | `SettingsDialog.xaml`, sixth tab | — |
| Migration off `SavedView.IsDefault` | `StartupFolderMigration`, run once in `App.OnStartup` | — |

### Out of scope

Per-account startup folders. Launching QuickMail with Windows (#129). Remembering the last folder
used. Per-folder sync opt-in/opt-out. Scoping the *periodic* sweep (#462). The observable-property
half of #451 — persisting folders removes its root cause, but re-stamping already-displayed rows is
a separate change.

---

## Section 5: Architecture

### 5.1 The `Folder` table

`account_id, full_name, display_name, parent_id, kind, exclude_from_all_mail, unread_count,
message_count, sort_order`, primary key `(account_id, full_name)`. Created with
`CREATE TABLE IF NOT EXISTS`, so no `user_version` bump — the same treatment `CalendarEvent` got.
`SuppressUnreadCount` is not stored; it is derived from `Kind`.

Written replace-all per account after each discovery, so a folder deleted or renamed on the server
disappears locally. Read once in `InitialLoadAsync`; purged for unknown accounts there and on
account deletion.

### 5.2 The `_cachedFolders` / `_connectedAccountIds` split — the load-bearing decision

`_cachedFolders` used to mean *"accounts connected this session"*: an account appeared there only
after `GetFoldersAsync` succeeded, and eleven call sites relied on that. Pre-filling it from SQLite
would have made every one of them report "connected" for accounts that never came up — the full
sync firing at unreachable servers, watchers starting for them, the status bar claiming connections
that did not exist, and `AccountsNeedingConnect` no longer reconnecting them.

Connection state therefore has its own home, `_connectedAccountIds`. **Anything asking "did we
connect?" must use the set; anything asking "what folders do we know about?" uses the dictionary.**
Sites moved to the set: the sync guard, the sync account list, the periodic sweep, `WireUpWatchers`,
`IsAccountReady`, the All Mail IMAP fetch phase, `AccountsNeedingConnect`, and the connected-count
labels.

This is the first thing to check if a startup or connection bug appears near this code.

### 5.3 Startup folder resolution

`ConfigModel.StartupFolder` holds one of four things:

| Value | Meaning |
|---|---|
| empty | All Mail (the default, and today's behaviour) |
| `AllInboxes`, `AllMail`, … | A virtual aggregate. Stored without the NUL sentinel prefix — an INI file cannot carry one — matching the `SavedView.VirtualFolderKey` convention. |
| a folder's `FullName` + `StartupFolderAccount` | A real folder. The account is required: folder names collide across accounts, and the pair is what resolves. |
| `view:{guid}` | A migrated multi-folder saved view. **Written only by the migration**; no picker offers or can create it. |

`StartupFolderLabel` is display-only, stored rather than derived because a Microsoft Graph
`FullName` is an opaque server id with nothing human-readable to fall back on.

Anything unresolvable falls back to All Mail with the reason in the status bar and one
`AnnouncementCategory.Status` announce.

`--online` never initializes the local store, so it resolves against config alone
(`ResolveOnlineStartupFolder`) and its background path calls `RefreshAsync` instead of a hardcoded
All Mail fetch. The setting was previously ignored entirely on that path.

### 5.4 Startup sync scope

`SyncAllAccountsAsync` has exactly one caller, so it *is* the startup sync and reads the scope from
config itself — keeping it off `ISyncService` and out of the five test stubs.

`startupFolder` mirrors what the startup folder shows. **Note the consequence:** with no startup
folder configured, that is All Mail, which spans everything, so those users still get the full
sweep. A narrower sync would leave stale rows on the screen they are looking at. The saving is
opted into by choosing a narrower place to start; `inboxes` is there for anyone who wants it
unconditionally.

Cases that cannot be resolved narrowly sync wide: a startup folder that no longer exists (startup
itself falls back to All Mail), an unparseable account id, and `view:{guid}`. Under-syncing what the
user is looking at is the worse failure.

The `SyncProgressChanged` denominator comes from the same filtered set, or the progress announcement
names a total the sync never reaches and "Sync complete" never fires.

---

## Section 6: Keyboard walkthrough

### Path A — set it from the folder tree

1. **Ctrl+1** moves focus to the folder tree.
2. Arrow to the folder. The screen reader announces the folder and its unread count.
3. Press the **Applications** key (or **Shift+F10**). Announced: "Folder actions".
4. Arrow to **Set as Startup Folder**, press **Enter**. The menu closes, focus returns to the
   folder, and a `Result` announcement says "QuickMail will open in Projects." The status bar shows
   the same sentence.

### Path B — set it from Settings

1. **Ctrl+,** opens Settings.
2. **Ctrl+Tab** to the **Startup** tab. Announced: "Startup settings".
3. **Tab** reaches the read-only **Opens in** field, which reads its current value ("All Mail" when
   nothing is configured). It is read-only rather than disabled so Tab still reaches it.
4. **Tab** to **Choose…**, **Enter**. The folder tree picker opens with the current startup folder
   already selected — never nothing.
5. Arrow to the folder, **Enter**. The picker closes; the field updates.
6. **Tab** to the **Startup Sync** radio group: one tab stop, arrows move and select together.
7. **Tab** to **Save**, **Enter**.

### Path C — launching

The app opens with the startup folder selected and its cached messages already listed. No All Mail
flash. The folder tree is fully populated from cache before any connect. Status bar:
"N messages in Projects (cached — syncing…)".

### Path D — the folder is gone

The app opens in All Mail. Status bar and one `Status` announcement: "Startup folder 'Projects' was
not found — showing All Mail." No dialog.

---

## Section 7: Infrastructure changes

- **F6 ring:** unchanged. No new panes.
- **Commands:** `folder.setStartupFolder` and `folder.clearStartupFolder`, category `Mail`, no
  default hotkeys. Both registered in `MainWindow.xaml.cs`.
- **`AutomationProperties.Name` added:** "Startup settings", "Startup folder settings", "Startup
  sync settings", "Choose startup folder", "Clear startup folder", "Set as Startup Folder", "Clear
  Startup Folder", and one per sync-scope radio.
- **Announcements added:** the set/clear/refusal sentences via `Report(...)`
  (`AnnouncementCategory.Result`), and the startup fallback sentence
  (`AnnouncementCategory.Status`). No new announcement categories.
- **Removed:** `SavedView.IsDefault`, `ViewManagerViewModel.EditIsDefault`, the View Manager's
  Options group, and the post-connect `ApplyViewAsync` call in `StartBackgroundSyncAsync`.
- **VM state:** `MainViewModel._connectedAccountIds` is new and is now the authority on connection
  state (§5.2).
- **Test registries touched:** `RadioGroupWiringTests.RadioGroupSites` gains `StartupSyncScope`.
  No new `Selector`-bound type, so `SelectorItemAccessibilityTests` and `TypeAheadWiringTests` are
  unaffected.

---

## Section 8: Tests

| Suite | Covers |
|---|---|
| `FolderStoreTests` | Folder table round-trip, replace-on-save, per-account isolation, purge, idempotent `Initialize` |
| `ConfigServiceSaveTests` | All four keys through a real INI, fresh-config defaults, scope normalisation |
| `MainViewModelStartupTests` | Resolution for each form, every fallback, `--online` |
| `StartupFolderMigrationTests` | Each `IsDefault` shape, malformed JSON, never overwriting an explicit choice |
| `StartupFolderCommandTests` | The context-menu guard, including the aggregates it must accept and the sentinels it must refuse |
| `StartupSyncScopeTests` | Which folders each scope actually visits; the progress denominator |
| `StartupSettingsTests` | Settings round-trip, Choose/Clear, and call-site guards on the picker |

---

## Section 9: Known limitation

The periodic sweep is unscoped: it visits every folder every `MailSyncPollMinutes` (default 5). So
work skipped at launch still happens shortly after. This moves load out of the critical window
rather than removing it. Scoping the sweep is [#462](https://github.com/kellylford/QuickMail/issues/462)
and was deliberately left out of #516.
