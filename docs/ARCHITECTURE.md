# Architecture

## Service Layer

**App.xaml.cs** is the manual DI composition root — no container. Services are wired in `OnStartup` in dependency order:
`ProfileContext` → `AccountService` → `CredentialService` → `OAuthService` → `ImapService` → `SmtpService` → `ConfigService` → `LocalStoreService` → `ContactService` → `TemplateService` → `RuleService` → `SyncService` → `ViewService` → `CommandRegistry` → `MainViewModel` → `MainWindow`.

Every service has a matching interface in `Services/I*.cs`, making them fully substitutable in tests.

**ImapService** uses MailKit and leases `ImapClient` instances from a bounded per-account pool. Foreground operations (message open, attachment download, mutating user actions) use foreground leases. Background work (sync, UID checks, polling, preview fetches, prefetch) uses background leases capped below the full pool so sync cannot starve interactive work. `MaxImapConnectionsPerAccount` defaults to 6 and is clamped to 1-15.

**LocalStoreService** (SQLite via `Microsoft.Data.Sqlite`) caches messages in `mail.db` with WAL journaling. It stores `MessageSummary` rows for list panes and `MessageDetail` rows for body/attachment metadata, and handles column-addition migrations at startup. Schema version is tracked via `PRAGMA user_version` so data migrations run exactly once.

**SyncService** runs background IMAP sync. It raises `FolderSynced`, `MessagesRemoved`, and `RulesApplied` events; `MainViewModel` subscribes and merges new data into observable collections. UI is populated from the SQLite cache immediately, then background sync fills gaps.

**OAuthService** wraps MSAL (`Microsoft.Identity.Client`) for Microsoft 365 / Outlook OAuth2. Token refresh is handled automatically; passwords for OAuth accounts are not stored in Credential Manager.

**ConfigService** reads/writes `config.ini` (INI format, human-editable) and `hotkeys.json` (JSON). Settings include `PreviewLines`, `ShowMessageStatus`, `ViewMode`, `SyncDays`, `InitialSyncCount`, with optional per-account `[account:{guid}]` overrides. Results are cached after first load.

**ContactService** stores the address book in `contacts.json` and contact groups in `groups.json`. Both files share a single `SemaphoreSlim` load lock so concurrent group and contact writes cannot tear. Upserts by email address (case-insensitive); `SearchContactsAsync` returns up to 10 results ordered by `LastUsedTicks`. Contacts are auto-upserted when mail is sent.

Groups (`GroupModel`) are flat, local-only, and keyed by an incrementing integer `Id` with a list of `MemberContactIds`. `LoadAllGroupsAsync` returns groups sorted by `LastUsedTicks` descending; `TouchGroupAsync` bumps the timestamp so a freshly-inserted group sorts to the top. The model exposes computed `ResolvedMemberCount` and `MissingContactCount` (both `[JsonIgnore]`) and a human-readable `Display` ("Name, N members" / "(M missing)" / "Empty group") so the UI does not have to recompute them. The full group surface is on `IContactService`: `LoadAllGroupsAsync`, `CreateGroupAsync`, `RenameGroupAsync`, `DeleteGroupAsync`, `AddMemberAsync`, `RemoveMemberAsync`, `ListGroupsForContactAsync`, `TouchGroupAsync`. A corrupt `groups.json` is renamed to `groups.json.bak-{timestamp}` and treated as empty, matching the recovery behaviour used for views, rules, and templates.

**RuleService** loads/saves mail rules from `rules.json`. Each `MailRule` has AND-combined conditions (FromContains, ToContains, SubjectContains, BodyContains, MustHaveAttachments) and one action (MarkAsRead, MarkAsUnread, MoveToFolder, Delete). `ApplyRulesAsync` is called by `SyncService` on incoming messages; `ApplyRulesToExistingAsync` runs on demand against the full cache. Rules can be scoped to one account via `AccountId` (null = all accounts). File writes use an atomic temp-then-rename pattern.

**ViewService** persists `SavedView` objects to `views.json`. A `SavedView` bundles one or more folders with a view mode, filter, sort order, optional `Hotkey` gesture string, `IsDefault` flag, and optional `DaysOfMail` limit. File is written atomically.

**TemplateService** persists `MessageTemplate` objects to `templates.json`. Thread-safe via `SemaphoreSlim`; writes use atomic temp-then-rename. Templates are ordered alphabetically by title on load.

**CommandRegistry** holds all `CommandDefinition` instances (id, category, title, default gesture, execute action). Commands are registered in `MainWindow`'s constructor. `FindByGesture()` checks user overrides from `hotkeys.json` first, then falls back to defaults. The command palette (`Ctrl+P`) uses `CommandPaletteViewModel` to display and invoke them.

**ProfileContext** resolves the data directory for the process. All services that write files derive their paths from it. Supports `--profileDir <path>` CLI override for alternate profiles (e.g. during testing). All file-writing services take `ProfileContext` in their constructor — do not accept raw `string` paths.

**Static utilities** (no DI required):
- `ConversationBuilder` — groups messages by normalized subject (strips all leading Re:/Fwd: chains); used for Conversations view mode
- `SenderGroupBuilder` — groups messages by From or To; used for From/To view modes
- `MimeMessageBuilder` — builds a `MimeMessage` from `ComposeModel` + `AccountModel`; shared by `SmtpService` and `ImapService` (draft saving)
- `AddressParser` — splits comma/semicolon-delimited address strings into `MailboxAddress` objects
- `MessagePropertiesBuilder`, `FolderPropertiesBuilder`, `AccountPropertiesBuilder`, `ContactPropertiesBuilder`, `GroupPropertiesBuilder`, `AttachmentPropertiesBuilder` — transform model objects into `(title, sections[])` for `PropertiesViewModel`. Pure static functions, no DI, testable without UI. Each takes the relevant model(s) and returns a list of `PropertySection` records containing `PropertyItem` (label/value) rows.
- `FolderTreeBuilder` — builds `FolderTreeNode` hierarchy from a flat `MailFolderModel` list; auto-detects IMAP path separator (`.` or `/`)

## Helpers

**`BatchObservableCollection<T>`** (`Helpers/BatchObservableCollection.cs`) — subclass of `ObservableCollection<T>` that suppresses individual `CollectionChanged` notifications during bulk mutations and fires a single `Reset` when the batch closes. This prevents screen readers from re-announcing the focused item after every insert during background sync. Always use `BeginBatchScope()` in a `using` block (auto-closes on exception); the manual `BeginBatch()` / `EndBatch()` pair is available for explicit `try/finally` sites. Nesting is safe — the outermost scope fires the reset.

## ViewModel State

**MainViewModel** owns application state. Key patterns:

- Cancellation token sources are scoped by activity: connect, folder load, message load, background sync, and prefetch.
- Version stamps discard stale async results after folder/message/view changes.
- View modes: Messages, Conversations, From, and To.
- `OnFolderSynced` and `OnMessagesRemoved` must use key-based lookups, not repeated `Messages.Any()` / `FirstOrDefault()` scans over large All Mail views.
- `_suppressFolderSyncUpdates` silences sync events during startup; `_suppressFilterRebuild` prevents stale filter application during folder transitions.

**AddressBookViewModel** owns the Address Book window's two-tab state (Contacts and Groups). It exposes `Groups: ObservableCollection<GroupModel>` sorted by `LastUsedTicks` descending and a derived `SelectedGroupMembers: ObservableCollection<ContactModel>` rebuilt whenever `SelectedGroup` changes. Confirmation dialogs and screen reader announcements are surfaced via `event Func<string, string, Task<bool>>? ConfirmRequested` and `event Action<string, AnnouncementCategory>? AnnouncementRequested`; the View subscribes and shows the dialog or calls `AccessibilityHelper.Announce` directly. The `InsertGroup(Action<ContactModel>)` private helper iterates `SelectedGroupMembers` in recency order and calls the inserter for each — the Compose window sets those inserters via `SetInsertActions(to, cc, bcc)` so a group can be dropped into any address field with one command. **GroupManagerViewModel** is a sibling VM that powers the standalone `GroupManagerWindow` (`Ctrl+Shift+M`); it shares the same `IContactService` instance and raises `GroupsChanged` for the parent to refresh.

## Runtime Modes

QuickMail has startup flags that alter which services are available. New code that calls any service must account for the mode it may be running in.

| Flag | Effect on services |
|---|---|
| *(normal)* | All services available. SQLite schema fully created. `LocalStoreService` returns cached data. |
| `--online` | SQLite schema is **not created**. `LocalStoreService` methods will throw `SqliteException` on any call. All data must come from the backend (IMAP or Graph). |
| `--profileDir <path>` | All file-based services use the alternate path. No effect on availability. |

**Graph accounts in `--online` mode:** fully supported, no behavioral difference. `GraphMailService` has **no `ILocalStoreService` dependency** — every read goes straight to the Graph REST API, so the uncreated SQLite schema never matters. (`GraphMailServiceTests.GraphMailService_HasNoLocalStoreDependency_SoOnlineModeWorks` guards this structurally.) The local-store-first / backend-fallback pattern below lives in `MainViewModel`, which dispatches the fallback through the `MailServiceRouter` to whichever backend owns the account.

**The standard fetch pattern for message bodies** — used wherever a message detail is needed:

```csharp
MailMessageDetail? detail = null;
try
{
    detail = await _localStore.LoadDetailAsync(accountId, folder, uid);
}
catch { /* local store unavailable (e.g. online mode) — fall through to IMAP */ }

if (detail == null)
    detail = await _imap.GetMessageDetailAsync(accountId, folder, uid, ct);
```

This pattern must use **two separate try/catch scopes** — one for the local store call and one (outer) for the whole method. A single outer catch that covers both the local store and the IMAP call will swallow the local store exception and silently skip the IMAP fallback, leaving the UI blank with no visible error.

- ✅ Inner catch around `LoadDetailAsync` only — falls through to IMAP on any failure.
- ❌ Single outer catch around both calls — IMAP is never reached when local store throws.

When adding any new code that calls `LocalStoreService`, ask: does this work in `--online` mode? If the answer is "it will throw," wrap that call in its own catch with an explicit fallback.

## Virtual Folders

Virtual folders use `FullName` sentinel strings starting with `\x00` to distinguish them from real IMAP folders.

| FullName | Scope |
|---|---|
| `\x00AllMail` | Union of all non-excluded folders across all accounts |
| `\x00AllInboxes` | Inbox only from each account |
| `\x00AllDrafts` / `\x00AllSent` / `\x00AllTrash` | Global drafts/sent/trash |
| `\x00AccountMail:{guid}` | All folders for one account |

Trash, Junk, Sent, and Drafts are excluded from `\x00AllMail` via `folder.ExcludeFromAllMail`. When a virtual folder is selected, `MainViewModel` queries multiple real folders and merges results sorted newest-first.
