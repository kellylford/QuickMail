# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

# QuickMail

WPF desktop email client (.NET 8, C#). Multi-account IMAP/SMTP with unified inbox, keyboard-centric UI.

## Build & Run

```bat
cd QuickMail
build.bat            # debug build
build.bat release    # release build
build.bat run        # debug build + launch
build.bat publish    # self-contained single-file win-x64 → publish/QuickMail.exe
build.bat smoke      # build + launch for 6s (CI smoke test)
build.bat clean
```

Or directly: `dotnet run --project QuickMail`

## Tests

xUnit 2.9.3 with `Xunit.StaFact` for WPF STA-thread tests.

```bat
dotnet test QuickMail.Tests/QuickMail.Tests.csproj -c Release
```

Test types in `QuickMail.Tests/`:
- **ViewModelConstructionTests** — VM instantiation with stub services (catches init crashes)
- **XamlParseTests** — XAML loads without `XamlParseException` (requires STA thread via `[StaFact]`)
- **LocalStoreServiceTests** — SQLite round-trip tests

All tests use `StubServices.cs` stub implementations to avoid real network/DB calls.

## Architecture

### Service Layer

**App.xaml.cs** is the manual DI composition root — no container. Services are wired in `OnStartup` in dependency order:
`AccountService` → `CredentialService` → `OAuthService` → `ImapService` → `SmtpService` → `ConfigService` → `LocalStoreService` → `SyncService` → `CommandRegistry` → `MainViewModel` → `MainWindow`. Pass `/debug` on startup to enable verbose file logging.

**ImapService** keeps one `ImapClient` per account `Guid` in a `ConcurrentDictionary`. Each account has a `SemaphoreSlim` that serializes IMAP operations to prevent concurrent client use. `GetOrReconnectAsync()` transparently reconnects stale clients. `ExecuteWithRetryAsync<T>()` retries once on transient connection errors (`ServiceNotConnectedException`, `ImapProtocolException`, `IOException`, `SocketException`). `OperationCanceledException` is never treated as transient.

**LocalStoreService** (SQLite via `Microsoft.Data.Sqlite`) caches messages in `%APPDATA%\QuickMail\mail.db` with WAL journaling. Two tables: `MessageSummary` (list pane data, indexed by `date_ticks DESC`) and `MessageDetail` (body, attachments JSON). Handles column-addition migrations at startup.

**SyncService** runs background IMAP polling. It raises `FolderSynced` and `MessagesRemoved` events; `MainViewModel` subscribes and merges new data into the observable collections. UI is populated from the SQLite cache immediately on startup (`InitialLoadAsync`), then background sync fills gaps.

**OAuthService** wraps MSAL (`Microsoft.Identity.Client`) for Microsoft 365 / Outlook OAuth2. Token refresh is handled automatically; passwords for OAuth accounts are not stored in Credential Manager.

### ViewModel State

**MainViewModel** owns all application state. Key patterns:

- **Four CancellationTokenSources**: `_connectCts`, `_folderCts`, `_messageCts`, `_bgSyncCts` — cancel only the relevant one when switching context; background sync does not cancel user-initiated loads.
- **Version stamps**: `_folderLoadVersion`, `_conversationRebuildVersion`, etc. — incremented before an async op; stale results from superseded operations are discarded when they complete.
- **View modes**: Messages, Conversations (thread-grouped by normalized subject), From (sender groups), To (recipient groups).

### Virtual Folders

Virtual folders use `FullName` sentinel strings starting with `\x00` to distinguish them from real IMAP folders. `IsVirtualFolder()` checks this prefix.

| FullName | Scope |
|---|---|
| `\x00AllMail` | Union of all non-excluded folders across all accounts |
| `\x00AllInboxes` | Inbox only from each account |
| `\x00AllDrafts` / `\x00AllSent` / `\x00AllTrash` | Global drafts/sent/trash |
| `\x00AccountMail:{guid}` | All folders for one specific account |

Trash, Junk, Sent, and Drafts are excluded from `\x00AllMail` via `folder.ExcludeFromAllMail`. When a virtual folder is selected, `MainViewModel` queries multiple real folders and merges results sorted newest-first.

**`FolderTreeBuilder`** converts the flat IMAP folder list into a display hierarchy; virtual folders are injected at the top level.

## Key Conventions

- **MVVM strictly**: no logic in code-behind except UI-only concerns (keyboard shortcuts, WebView2 navigation)
- **Passwords**: never written to JSON; always round-trip through `CredentialService` (Windows Credential Manager). OAuth accounts bypass this.
- **HTML sandbox**: WebView2 `NavigateToString` with strict CSP — no scripts, no object/embed, no frames
- **Pagination**: messages fetched in batches of 100; "Load More" appends next batch
- **Logging**: `LogService` appends to `%APPDATA%\QuickMail\quickmail.log`; `LogService.Debug()` only writes when `/debug` flag is present

## Keyboard Shortcuts (MainWindow)

| Key | Action |
|---|---|
| Ctrl+0 | Focus account list |
| Ctrl+1 | Focus folder list |
| Ctrl+2 | Focus message list |
| Ctrl+3 | Focus reading pane |
| Ctrl+N | New message |
| Ctrl+Y | Folder picker |
| Delete | Delete selected messages |
| Escape | Close reading pane |

## Dependencies

| Package | Version | Purpose |
|---|---|---|
| MailKit | 4.16.0 | IMAP + SMTP protocol |
| CommunityToolkit.Mvvm | 8.4.2 | ObservableProperty, RelayCommand |
| Microsoft.Web.WebView2 | 1.0.3912.50 | HTML email rendering |
| Microsoft.Data.Sqlite | 10.0.7 | Local message cache |
| Microsoft.Identity.Client | 4.84.0 | OAuth2 (Microsoft 365) |
| AdysTech.CredentialManager | 3.1.0 | Windows Credential Manager |
