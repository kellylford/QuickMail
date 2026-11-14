# QuickMail — Comprehensive Code Review

**Date:** May 16, 2026
**Version:** 0.5.1
**Reviewer:** Automated code review
**Scope:** Full codebase (Models, Services, ViewModels, Views, Tests, Build)

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [Architecture & Design](#architecture--design)
3. [Code Quality by Layer](#code-quality-by-layer)
   - [Models](#models)
   - [Services](#services)
   - [ViewModels](#viewmodels)
   - [Views](#views)
   - [Tests](#tests)
4. [Security Review](#security-review)
5. [Performance & Resource Management](#performance--resource-management)
6. [Accessibility](#accessibility)
7. [Build & Deployment](#build--deployment)
8. [Risk Assessment](#risk-assessment)
9. [Recommendations](#recommendations)

---

## Executive Summary

QuickMail is a well-architected WPF desktop email client targeting .NET 8. The codebase demonstrates strong adherence to MVVM patterns, thoughtful separation of concerns, and impressive attention to accessibility. The project is production-quality for a v0.5 release, with a solid foundation that can scale.

**Overall Grade: B+ (Strong)**

| Dimension | Grade | Notes |
|-----------|-------|-------|
| Architecture | A- | Clean MVVM, good DI, well-defined interfaces |
| Code Quality | B+ | Consistent style, good comments, some duplication |
| Security | B | Credential Manager is good; OAuth client ID embedded |
| Testing | B- | Good smoke/XAML tests; no service-level unit tests |
| Accessibility | A | UIA notifications, screen-reader announcements, focus management |
| Performance | B+ | SQLite caching, IMAP pooling, background sync; some N+1 patterns |

---

## Architecture & Design

### Strengths

1. **Clean MVVM separation.** Views contain zero business logic. ViewModels use `CommunityToolkit.Mvvm` source generators (`[ObservableProperty]`, `[RelayCommand]`) which eliminates boilerplate. The code-behind files handle only UI-specific concerns: keyboard shortcuts, focus management, drag-and-drop, and WebView2 initialization.

2. **Well-defined service interfaces.** Every service (`IImapService`, `ISmtpService`, `IAccountService`, `ICredentialService`, `ILocalStoreService`, `ISyncService`, `IConfigService`, `ICommandRegistry`, `IOAuthService`) has a corresponding interface, enabling testability via stubs.

3. **Manual DI composition root.** `App.xaml.cs` wires all dependencies explicitly — no magic DI container. This is pragmatic for a desktop app of this size and makes the dependency graph trivially auditable.

4. **Command Registry pattern.** The `CommandRegistry` / `CommandDefinition` system is elegant. It decouples keyboard shortcut binding from ViewModels, supports user-customizable hotkeys via `hotkeys.json`, and powers the Command Palette (Ctrl+Shift+P). This is a sophisticated feature for a v0.5 app.

5. **Cancellation discipline.** Three separate `CancellationTokenSource` instances (`_connectCts`, `_folderCts`, `_messageCts`) prevent cross-contamination between operations. Version stamps (`_folderLoadVersion`, `_conversationRebuildVersion`, etc.) prevent stale async results from corrupting state.

### Concerns

1. **`MainViewModel` is too large.** At ~1,200+ lines, it handles account management, folder navigation, message loading, view-mode switching, sync orchestration, command registration, and preview extraction. Consider extracting:
   - A `FolderNavigationService` for folder tree management
   - A `MessageLoadService` for the fetch/cache/display pipeline
   - A `ViewModeManager` for conversation/sender/to-group rebuilds

2. **`MainWindow.xaml.cs` is also large (~800+ lines).** The focus-management logic (type-ahead, tree navigation, `LandOn*` methods) is complex and duplicated across conversation/sender/to-group trees. Consider extracting a `TreeViewFocusHelper` or `MessagePanelFocusManager`.

3. **No event aggregator or messenger.** ViewModels communicate via plain C# events (`ComposeRequested`, `ManageAccountsRequested`, etc.). This works but creates tight coupling between `MainViewModel` and `MainWindow`. Consider `WeakReferenceMessenger` from CommunityToolkit.Mvvm for cross-cutting concerns.

4. **Virtual folder sentinels are fragile.** The `\x00AllMail` sentinel pattern relies on null-byte-prefixed strings that no real IMAP folder would have. While clever, it's implicit magic. A dedicated `VirtualFolder : MailFolderModel` subclass or a `FolderKind` enum would be more explicit and type-safe.

---

## Code Quality by Layer

### Models

**Strengths:**
- Clean POCOs / observable objects with clear property names
- `MailMessageSummary` uses `[ObservableProperty]` with `[NotifyPropertyChangedFor]` chains correctly
- `AttachmentModel` includes a comprehensive MIME type mapping
- `ConversationGroup` and `SenderGroup` implement `INotifyPropertyChanged` manually for `IsExpanded` (required for TwoWay TreeView binding)
- Good use of computed properties (`StatusDisplay`, `DateDisplay`, `AutomationName`)

**Issues:**
- **`MailMessageDetail` inherits from `MailMessageSummary` but shadows `To`.** The base class has `To` as a simple string; the derived class re-declares it. This is confusing and could cause serialization issues. The base `To` should be removed or the derived class should use `new` explicitly.
- **`ConfigModel` uses magic strings for `ViewMode`** (`"messages"`, `"conversations"`, `"from"`, `"to"`) alongside a proper `ViewMode` enum in the ViewModel. The config should serialize the enum directly or use a consistent mapping layer.
- **`AccountOverrideConfig` is referenced but not defined** in the models directory. It appears to be defined elsewhere or is a missing file.

### Services

#### `ImapService` (largest service, ~900 lines)

**Strengths:**
- **Client pool pattern.** `ConcurrentDictionary<Guid, ImapClient>` with per-account `SemaphoreSlim` serializes operations correctly.
- **Retry with reconnect.** `ExecuteWithRetryAsync<T>` catches transient errors (`ServiceNotConnectedException`, `ImapProtocolException`, `IOException`, `SocketException`), discards the stale client, reconnects, and retries once. This is production-grade.
- **`NoOpAsync` with stale detection.** Silently discards dead connections so the next operation gets a fresh client.
- **Proper folder open/close in `finally` blocks.** Every `folder.OpenAsync()` is paired with `folder.CloseAsync()` in a `finally`.
- **IMAP PREVIEW extension support.** Requests `PreviewText` in fetch items; falls back to body-part download only when the server doesn't support it.
- **Attachment extraction.** `CollectAttachments` correctly skips `multipart/alternative` and `multipart/related`, only collecting explicit attachments and non-text body parts with filenames.

**Issues:**
- **Duplicate address-parsing logic.** `SmtpService.AddAddresses` and `ImapService.AppendDraftAsync`'s local `AddAddresses` function do the same thing with slightly different implementations. Extract to a shared `AddressParser` utility.
- **Duplicate MIME message building.** `SmtpService.SendAsync` and `ImapService.AppendDraftAsync` both build `MimeMessage` objects from `ComposeModel`. Extract to a `MimeMessageBuilder`.
- **`GetMessagesSinceAsync` has a hardcoded 500-message initial fetch.** This should be configurable or at least defined as a named constant.
- **`GetMessageDetailAsync` opens the folder `ReadWrite`** even when only reading (it does add the `Seen` flag, which justifies it, but the flag addition could be a separate call).
- **`SummaryToModel` is a large mapping method.** Consider AutoMapper or a dedicated factory class.

#### `LocalStoreService`

**Strengths:**
- **SQLite with WAL mode.** Good choice for concurrent reads during writes.
- **Schema migrations.** `RunMigration` with try/catch for idempotent column additions is pragmatic for a desktop app.
- **Backfill logic.** The `to_addr` backfill from `MessageDetail` to `MessageSummary` on initialization is thoughtful.
- **Upsert pattern.** `INSERT ... ON CONFLICT DO UPDATE` with the `preview_text` CASE preserves existing previews when the incoming value is empty.
- **Attachment metadata as JSON.** Storing only metadata (not content bytes) in the detail table is correct; content is fetched on demand.

**Issues:**
- **No database versioning.** The migration approach (try/catch on ALTER TABLE) works but is fragile. A `PRAGMA user_version`-based migration system would be more robust.
- **`ReadSummariesAsync` doesn't read `has_attachments`.** The column exists (added via migration) but isn't loaded. The `HasAttachments` property on `MailMessageSummary` is set separately in `SelectMessageAsync`.
- **`Open()` and `OpenAsync()` are not used consistently.** Some callers use the sync version, others async. For consistency, all DB access should be async.
- **No connection pooling or lifetime management.** Each operation opens a new connection. For a desktop app this is fine, but under heavy background sync it could create pressure.

#### `SyncService`

**Strengths:**
- **Two-phase sync.** Phase 1: fetch new messages via `GetMessagesSinceAsync`. Phase 2: detect remote deletions by comparing local UIDs to server UIDs.
- **Preview fetch is deferred and fire-and-forget.** `FetchAllPreviewsAsync` runs after all folder syncs complete, preventing IMAP command interleaving on the shared client.
- **Proper dispatcher marshaling.** `Application.Current.Dispatcher.InvokeAsync` for all UI updates from background threads.

**Issues:**
- **`SyncAllAccountsAsync` iterates accounts sequentially.** For multiple accounts, this could be slow. Consider `Parallel.ForEachAsync` with per-account semaphores (already in place in `ImapService`).
- **The `previewJobs` list captures `incoming` references** that are also being mutated by `FetchAndApplyPreviewsAsync`. This is safe because previews are fetched after sync, but the shared reference is subtle.

#### `ConfigService`

**Strengths:**
- **INI file format with comments.** User-editable, human-friendly.
- **Hotkeys in separate JSON file.** Clean separation of simple settings from structured data.
- **Caching with `_cached` field.** Avoids re-parsing on every access.
- **Default value generation on first run.** `Save` is called when no config file exists.

**Issues:**
- **INI parsing is hand-rolled.** While functional, it doesn't handle edge cases like quoted values, escaped characters, or inline comments. Consider a library like `Microsoft.Extensions.Configuration.Ini`.
- **`Save` writes hotkeys.json only when non-empty, and deletes it when empty.** This is a policy decision that could surprise users who manually create the file.

#### `CredentialService`

**Strengths:**
- **Windows Credential Manager via `AdysTech.CredentialManager`.** Industry-standard for desktop apps. No plaintext passwords on disk.
- **Proper key namespacing** (`QuickMail:{accountId}`).

**Issues:**
- **`GetPassword` tries `SecurePassword` then falls back to `Password`.** The fallback to plaintext `Password` property is a potential security concern if the credential was stored by an older version.

#### `OAuthService`

**Strengths:**
- **MSAL with DPAPI-encrypted token cache.** Tokens survive app restarts securely.
- **Silent token acquisition with interactive fallback.** Standard OAuth 2.0 flow for native apps.
- **System browser for interactive auth** (not embedded WebView). Better security and user experience.

**Issues:**
- **Client ID is hardcoded** (`bcdc84f1-d37c-4581-b14a-a01f7b3a1312`). This is standard for public clients but means the app is tied to a specific Azure AD registration. Consider making it configurable for enterprise deployments.
- **`RegisterTokenCache` uses `.GetAwaiter().GetResult()`** in a constructor. This is a sync-over-async anti-pattern that could deadlock in some synchronization contexts. Use an async factory pattern (`CreateAsync`) instead.

#### `LogService`

**Strengths:**
- **Static class with simple API.** `Log(string)`, `Log(string, Exception)`, `Debug(string)`.
- **Fail-safe.** All operations wrapped in try/catch to prevent logging from crashing the app.
- **Debug mode gated by command-line flag.** Keeps production logs clean.

**Issues:**
- **No log rotation or size management.** The log file grows indefinitely. Add a simple size check and rotation.
- **Static mutable state (`DebugMode`).** Thread-safe in practice (set once at startup) but not ideal.

### ViewModels

#### `MainViewModel`

**Strengths:**
- **Comprehensive observable properties** with correct `[NotifyPropertyChangedFor]` chains.
- **`WindowTitle` computed property** reflects current context (message subject, folder, account).
- **`InitialLoadAsync` / `StartBackgroundSyncAsync` separation.** Cache is shown immediately; network sync happens in background.
- **`FetchAllMailAsync` two-phase design.** Phase 1: cache. Phase 2: incremental IMAP. Handles the "To view needs recipient repair" edge case.
- **`ScheduleConversationRebuild` / `ScheduleSenderGroupRebuild` / `ScheduleToGroupRebuild`** with version stamps prevent stale background results from overwriting newer foreground results.
- **`InsertMessageSorted` binary search insertion** for O(log n) insertion into the sorted message list.

**Issues:**
- **Too many responsibilities** (as noted in Architecture).
- **`AnnounceLoadingProgressAsync` polls every 10 seconds.** This is a minor resource drain. Consider event-driven announcements instead.
- **`FetchVirtualAsync` is referenced but not shown in the provided code.** It may be defined elsewhere or is a missing method.
- **`ExtractPreview` is referenced but not shown.** Same concern.

#### `ComposeViewModel`

**Strengths:**
- **Dirty tracking** via `On*Changed` partial methods. Clean and automatic.
- **Draft save/update/delete lifecycle.** `SaveDraftAsync` finds the Drafts folder, appends, and tracks the UID for subsequent updates. `SendAsync` deletes the draft after successful send.
- **Attachment size limit enforcement** (25 MB).
- **Dangerous file extension warning** before opening executable attachments.
- **Factory methods** (`CreateReply`, `CreateForward`, etc.) for composing from existing messages.

**Issues:**
- **`Seed` method is called after construction.** This is a two-phase initialization pattern. Consider a factory method that constructs and seeds in one step.
- **`AddAttachmentFromPath` reads entire file into memory synchronously.** Large attachments could cause UI freezes. Use async file I/O.

#### `AccountManagerViewModel` / `AddAccountViewModel`

**Strengths:**
- **Clean form-to-model mapping.** Working copy pattern prevents direct mutation of the selected account until save.
- **OAuth auto-configuration.** Switching to OAuth2Microsoft auto-fills Outlook server settings.
- **Connection testing.** `TestConnectionAsync` connects and immediately disconnects.

**Issues:**
- **Significant duplication between `AccountManagerViewModel` and `AddAccountViewModel`.** Both have identical properties (`ImapHost`, `ImapPort`, `SmtpHost`, etc.), `AuthTypeIndex`, `IsPasswordAuth`/`IsOAuth2`, and `SignInMicrosoftAsync`/`TestConnectionAsync` commands. Extract a base class or compose a shared `AccountEditorViewModel`.

### Views

#### `MainWindow.xaml`

**Strengths:**
- **Well-structured 3-pane layout** with `GridSplitter`.
- **Comprehensive context menus** for messages, conversations, sender groups, to-groups, and folders.
- **Proper `AutomationProperties.Name`** on all interactive elements.
- **`KeyboardNavigation.TabNavigation="Once"`** on each pane for correct tab-stop behavior.
- **Virtualizing TreeViews** with `VirtualizingPanel.VirtualizationMode="Recycling"` for performance.
- **Status column visibility** controlled by `BoolToColumnWidthConverter` (0 width when hidden).

**Issues:**
- **Context menus are defined in `Window.Resources`** rather than in a separate resource dictionary. This makes the XAML file very long (~800 lines).
- **The `MessageContextMenu` uses `PlacementTarget.DataContext`** which is fragile. The `Tag` setter on `TreeViewItem` container style is a workaround for this.

#### `MainWindow.xaml.cs`

**Strengths:**
- **WebView2 initialization** with strict settings (no dev tools, no default context menus).
- **JavaScript-to-WPF bridge** for Escape, F6, Shift+Tab relay from the WebView2 to WPF focus management.
- **Type-ahead search** for message list, folder tree, and conversation/sender trees.
- **Focus restoration** after collection changes (sync, delete, view-mode switch).
- **Debug focus tracing** (gated by `/debug` flag).

**Issues:**
- **Too large** (as noted in Architecture). The type-ahead and focus-management logic is duplicated across multiple tree views.
- **`TryGetTypeAheadKeyText` is duplicated** between `MainWindow.xaml.cs` and `FolderPickerWindow.xaml.cs`.
- **`GetVisibleTreeNodes` is duplicated** between `MainWindow.xaml.cs` and `FolderPickerWindow.xaml.cs`.
- **`SelectTreeViewNode` is duplicated** between `MainWindow.xaml.cs` and `FolderPickerWindow.xaml.cs`.
- **`FindAndExpandPath` is duplicated** between `MainWindow.xaml.cs` and `FolderPickerWindow.xaml.cs`.

### Tests

**Strengths:**
- **ViewModel construction smoke tests.** Verify all ViewModels can be constructed without exceptions.
- **XAML parse tests.** Verify every Window's XAML compiles and loads without `XamlParseException`.
- **`LocalStoreService` integration test.** Verifies the `To` field round-trips through SQLite.
- **Comprehensive stub implementations.** `StubServices.cs` provides no-op implementations of every service interface.
- **`StaFact` for WPF tests.** Correctly handles STA thread requirement.

**Issues:**
- **No unit tests for `ImapService`.** The most complex service has zero tests. Mocking `MailKit` is challenging but possible with `IMailFolder` interfaces.
- **No unit tests for `SyncService`.** The sync logic (UID comparison, deletion detection) is untested.
- **No unit tests for `ConversationBuilder` or `SenderGroupBuilder`.** These pure functions are the easiest to test and should have comprehensive tests.
- **No unit tests for `ConfigService` parsing.** The INI parser has no test coverage.
- **No unit tests for `CommandRegistry`.** Gesture resolution and override logic is untested.
- **Only one `LocalStoreService` test.** The SQLite layer needs more coverage: upsert conflicts, deletion, preview updates, detail loading, migration scenarios.
- **No integration tests with a real IMAP server.** Consider a test container (e.g., `greenmail`, `docker-mailserver`) for CI integration tests.

---

## Security Review

### What's Good

1. **Windows Credential Manager for passwords.** No plaintext passwords in `accounts.json`.
2. **OAuth 2.0 with MSAL.** Industry-standard authentication for Microsoft accounts.
3. **DPAPI-encrypted token cache.** OAuth refresh tokens are stored securely.
4. **WebView2 CSP.** The HTML rendering sandbox has strict Content Security Policy (no scripts, no object/embed, no frames).
5. **Certificate validation enabled by default.** `ImapAcceptInvalidCert` and `SmtpAcceptInvalidCert` are opt-in.
6. **Dangerous attachment detection.** `.exe`, `.bat`, `.cmd`, `.ps1`, etc. trigger a warning before opening.

### Concerns

1. **Hardcoded OAuth client ID.** `bcdc84f1-d37c-4581-b14a-a01f7b3a1312` is embedded in the binary. This is standard for public native apps (it's not a secret), but it means anyone can extract it and impersonate the app in phishing attacks. The redirect URI is `http://localhost` which mitigates this somewhat.

2. **Password passed as `string` throughout.** `string? password` parameters appear in `ConnectAsync`, `SendAsync`, and `AppendDraftAsync`. .NET strings are immutable but stay in memory until GC. Consider `SecureString` (though its utility is debated in modern .NET).

3. **No certificate pinning.** The app trusts any certificate signed by a system-trusted CA. For IMAP/SMTP, this is standard practice, but `ImapAcceptInvalidCert` / `SmtpAcceptInvalidCert` being available as a user setting is a risk if users enable it without understanding the implications.

4. **`accounts.json` contains server addresses and usernames.** While passwords are in Credential Manager, the configuration file still reveals which email services a user connects to and their usernames. File permissions on `%APPDATA%\QuickMail` are the only protection.

5. **Temporary attachment files.** `OpenComposeAttachment` writes to `%TEMP%\QuickMail` and opens with `UseShellExecute`. These files persist until manually cleaned. Consider deleting after the process exits or using a temp file with `FileOptions.DeleteOnClose`.

---

## Performance & Resource Management

### What's Good

1. **SQLite cache.** Avoids re-downloading message summaries on every launch.
2. **IMAP client pooling.** One connection per account, reused across operations.
3. **Background sync.** Non-blocking; messages trickle in via events.
4. **Virtualizing TreeViews.** `VirtualizingPanel.IsVirtualizing="True"` with `Recycling` mode.
5. **Binary search insertion.** `InsertMessageSorted` is O(log n) for finding the insertion point.
6. **Preview fetch batching.** Only fetches previews for messages the server didn't fill via IMAP PREVIEW, and limits to 100 per folder.

### Concerns

1. **`OnFolderSynced` does linear search for deduplication.** `Messages.Any(e => e.UniqueId == msg.UniqueId && ...)` is O(n) per incoming message. For large mailboxes with frequent syncs, this could be slow. Use a `HashSet<(uint, Guid, string)>` for O(1) lookup.

2. **`OnMessagesRemoved` also does linear search.** Same issue.

3. **`FetchAllMailAsync` loads all summaries into memory.** For users with 100,000+ messages, this could cause high memory usage. Consider pagination or virtualized data sources.

4. **`ConversationBuilder.Build` materializes all groups.** For large mailboxes, the LINQ `GroupBy` + `OrderByDescending` chain creates many intermediate collections.

5. **`ScheduleConversationRebuild` copies the entire `Messages` list** (`Messages.ToList()`). This is necessary for thread safety but doubles memory for the snapshot duration.

6. **`SyncService` iterates folders sequentially per account.** For accounts with many folders, this extends sync time.

---

## Accessibility

QuickMail's accessibility implementation is **exceptional** for a v0.5 desktop app.

### Strengths

1. **UIA Notification events.** `AccessibilityHelper.Announce` uses `RaiseNotificationEvent` (the correct API for desktop screen readers, as opposed to `LiveSetting` which only works in browsers).

2. **Comprehensive `AutomationProperties.Name`** on all interactive elements: toolbar buttons, list items, tree view items, context menus, text boxes.

3. **`AutomationProperties.IsColumnHeader`** on GridView columns.

4. **Multi-binding automation names.** Message list items announce: read status, from, subject, preview, date. Conversation groups announce: subject, count, sender, preview, date.

5. **Status bar announcements.** Every `StatusText` change triggers a screen-reader announcement.

6. **Focus management.** `Ctrl+0` through `Ctrl+3` jump to panes. `F6` cycles focus. Arrow keys navigate within panes. Enter triggers actions without moving focus.

7. **True WPF TreeView for folders.** Screen readers correctly announce level, expanded/collapsed state.

8. **Type-ahead search** in message list, folder tree, and conversation/sender trees.

9. **Keyboard-accessible context menus** with access keys (underscored letters).

10. **Focus visual style.** Custom dashed-rectangle focus indicator applied globally.

### Minor Issues

- **The `StatusTextBoxStyle` template** hides the default TextBox chrome. While visually clean, it may confuse users who expect a standard text input appearance.

---

## Build & Deployment

### Strengths

1. **Simple `build.bat`** with build, run, publish, clean, and smoke-test targets.
2. **Self-contained single-file publish** (`PublishSingleFile`, `SelfContained`, `win-x64`).
3. **ReadyToRun enabled** for faster startup.
4. **Smoke test** launches the app and verifies it stays alive for 6 seconds.

### Issues

1. **No CI/CD pipeline.** The smoke test is manual. A GitHub Actions workflow that runs `dotnet test` and the smoke test on push would catch regressions.
2. **Version is hardcoded in `.csproj`.** Consider using `GitVersion` or `MinVer` for automatic versioning from git tags.
3. **No signing.** The published executable is not code-signed, which will trigger SmartScreen warnings on Windows.

---

## Risk Assessment

| Risk | Severity | Likelihood | Mitigation |
|------|----------|------------|------------|
| `MainViewModel` becomes unmaintainable | Medium | High | Refactor into smaller services |
| Stale cache shows deleted messages | Low | Low | SyncService handles remote deletion detection |
| IMAP connection leaks | Low | Low | `Dispose` cleans up all clients; `using` patterns in operations |
| Large mailbox memory pressure | Medium | Medium | Add pagination; virtualize data sources |
| OAuth token theft via malware | Medium | Low | Tokens are DPAPI-encrypted; scope is limited to IMAP/SMTP |
| SQLite corruption | Low | Low | WAL mode is resilient; no critical data (cache only) |
| XAML parse errors after refactoring | Low | Medium | XAML parse tests catch these |

---

## Recommendations

### High Priority

1. **Add unit tests for `ConversationBuilder`, `SenderGroupBuilder`, `CommandRegistry`, and `ConfigService`.** These are pure functions with no external dependencies — the easiest and highest-value tests to write.

2. ~~**Extract duplicated code:**~~ ✅ **Completed** (PR #10, DeepSeek4 branch, merged May 17 2026)
   - ✅ `AddressParser` — new static utility in `Services/AddressParser.cs`; both `SmtpService` and `ImapService.AppendDraftAsync` now delegate to `AddressParser.AddAddresses`. Normalised to `MailboxAddress.TryParse` (silent-skip on invalid addresses).
   - ✅ `MimeMessageBuilder` — new static class in `Services/MimeMessageBuilder.cs`; builds a `MimeMessage` from `ComposeModel` + `AccountModel`. Accepts an optional `userAgent` string so `SmtpService` can set the `User-Agent` header while `ImapService` (draft save) omits it.
   - ✅ `TreeViewFocusHelper` — new static class in `Views/TreeViewFocusHelper.cs`; consolidates `GetVisibleTreeNodes`, `SelectTreeViewNode`, `TryGetTypeAheadKeyText`, `FindFolderTreeNode`, `FindAndExpandFolderPath`, and `FoldersMatch`. **Note:** after merge, `MainWindow.FolderMatches` was reverted to strict `AccountId` equality (the helper's `Guid.Empty` wildcard is correct for `FolderPickerWindow` but would allow cross-account folder collisions in the main window).
   - ✅ `AccountEditorViewModel` — new abstract base class in `ViewModels/AccountEditorViewModel.cs`; holds all shared form fields, auth-type helpers, and the `SignInMicrosoft` / `TestConnection` commands. Both `AccountManagerViewModel` and `AddAccountViewModel` now derive from it, eliminating ~200 lines of duplication.

3. **Fix `MailMessageDetail.To` shadowing `MailMessageSummary.To`.** Remove `To` from the base class or use explicit `new` in the derived class.

4. **Add O(1) deduplication in `OnFolderSynced`.** Replace linear `Messages.Any(...)` with a `HashSet` lookup.

### Medium Priority

5. **Refactor `MainViewModel`** into smaller focused services:
   - `FolderNavigationService`
   - `MessageLoadService`
   - `ViewModeManager`

6. **Add database versioning** (`PRAGMA user_version`) instead of try/catch migrations.

7. **Add log rotation** to `LogService`.

8. **Make `OAuthService` use an async factory** instead of `.GetAwaiter().GetResult()` in the constructor.

9. **Add CI/CD pipeline** (GitHub Actions) for build + test on push.

10. **Add more `LocalStoreService` tests:** upsert conflicts, deletion, preview updates, detail loading.

### Low Priority

11. **Consider `WeakReferenceMessenger`** for cross-ViewModel communication.

12. ~~**Make the initial sync message count (500) configurable.**~~ ✅ **Completed** (PR #10, DeepSeek4 branch, merged May 17 2026)
    - `InitialSyncCount` property added to `ConfigModel` (default: 500; set to 0 to fetch all messages).
    - `ConfigService` updated to parse and write `InitialSyncCount` in `config.ini`.
    - `IImapService.GetMessagesSinceAsync` signature updated with an `int initialCount` parameter; `ImapService`, `SyncService`, `MainViewModel`, and `StubServices` all updated accordingly.
    - **Bonus fix:** `SyncService.SyncFolderAsync` now uses `GetMessagesSinceDateAsync` when `maxUid == 0` and `SyncDays > 0`, so an initial sync respects the date window instead of falling back to the count-based fetch unconditionally.

13. **Add code signing** to the publish workflow.

14. **Consider `Microsoft.Extensions.Configuration.Ini`** for INI parsing.

15. **Add pagination for very large mailboxes** (>100,000 messages).

16. **Clean up temporary attachment files** after the opened process exits.

---

## Conclusion

QuickMail is a well-crafted desktop email client with a solid architectural foundation. The codebase demonstrates mature engineering practices: clean MVVM separation, proper async/await patterns, thoughtful error handling, and exceptional accessibility. The primary areas for improvement are test coverage (especially for pure business logic), reducing code duplication, and breaking up the large `MainViewModel` and `MainWindow` code-behind. With these improvements, QuickMail is well-positioned for a 1.0 release.
