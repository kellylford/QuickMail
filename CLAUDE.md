# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with this repository.

# QuickMail

WPF desktop email client (.NET 8, C#). Multi-account IMAP/SMTP with unified inbox, keyboard-centric UI, local SQLite cache, and WebView2 reading pane.

## Build & Run

```bat
build.bat            # debug build
build.bat release    # release build
build.bat run        # debug build + launch
build.bat publish    # self-contained single-file win-x64 -> publish/QuickMail.exe
build.bat smoke      # build + launch for 6s
build.bat clean
```

Or directly: `dotnet run --project QuickMail`.

## Tests

xUnit 2.9.3 with `Xunit.StaFact` for WPF STA-thread tests.

```bat
dotnet test QuickMail.Tests/QuickMail.Tests.csproj -c Release
```

Test types in `QuickMail.Tests/`:

- **ViewModelConstructionTests**: VM instantiation with stub services.
- **XamlParseTests**: XAML loads without `XamlParseException` (STA thread).
- **LocalStoreServiceTests**: SQLite round-trip tests.
- **SettingsViewModelTests**: hotkey/settings behavior.

All tests use `StubServices.cs` stub implementations to avoid real network and credential calls.

## Architecture

### Service Layer

**App.xaml.cs** is the manual DI composition root. Services are wired in `OnStartup` in dependency order:
`AccountService` -> `CredentialService` -> `OAuthService` -> `ConfigService` -> `ImapService` -> `SmtpService` -> `LocalStoreService` -> `SyncService` -> `CommandRegistry` -> `MainViewModel` -> `MainWindow`. Pass `/debug` on startup to enable verbose file logging.

**ImapService** uses MailKit and leases `ImapClient` instances from a bounded per-account pool. Foreground operations (message open, attachment download, mutating user actions) use foreground leases. Background work (sync, UID checks, polling, preview fetches, prefetch) uses background leases capped below the full pool so sync cannot starve interactive work. `MaxImapConnectionsPerAccount` defaults to 6 and is clamped to 1-15.

**LocalStoreService** (SQLite via `Microsoft.Data.Sqlite`) caches messages in `%APPDATA%\QuickMail\mail.db` with WAL journaling. It stores `MessageSummary` rows for list panes and `MessageDetail` rows for body/attachment metadata, and handles column-addition migrations at startup.

**SyncService** runs background IMAP sync. It raises `FolderSynced` and `MessagesRemoved` events; `MainViewModel` subscribes and merges new data into observable collections. UI is populated from the SQLite cache immediately, then background sync fills gaps.

**OAuthService** wraps MSAL (`Microsoft.Identity.Client`) for Microsoft 365 / Outlook OAuth2. Token refresh is handled automatically; passwords for OAuth accounts are not stored in Credential Manager.

### ViewModel State

**MainViewModel** owns application state. Key patterns:

- Cancellation token sources are scoped by activity: connect, folder load, message load, background sync, and prefetch.
- Version stamps discard stale async results after folder/message/view changes.
- View modes: Messages, Conversations, From, and To.
- `OnFolderSynced` and `OnMessagesRemoved` must use key-based lookups, not repeated `Messages.Any()` / `FirstOrDefault()` scans over large All Mail views.

### Virtual Folders

Virtual folders use `FullName` sentinel strings starting with `\x00` to distinguish them from real IMAP folders.

| FullName | Scope |
|---|---|
| `\x00AllMail` | Union of all non-excluded folders across all accounts |
| `\x00AllInboxes` | Inbox only from each account |
| `\x00AllDrafts` / `\x00AllSent` / `\x00AllTrash` | Global drafts/sent/trash |
| `\x00AccountMail:{guid}` | All folders for one account |

Trash, Junk, Sent, and Drafts are excluded from `\x00AllMail` via `folder.ExcludeFromAllMail`. When a virtual folder is selected, `MainViewModel` queries multiple real folders and merges results sorted newest-first.

## Key Conventions

- **MVVM strictly**: no business logic in code-behind. Code-behind is limited to UI-only concerns such as focus, keyboard routing, dialogs requested by the VM, and WebView2 host behavior.
- **Passwords**: never written to JSON; always use `CredentialService` (Windows Credential Manager). OAuth accounts bypass password storage.
- **HTML sandbox**: WebView2 `NavigateToString` uses strict CSP. Scripts, objects, frames, forms, remote images, and active handlers are blocked or stripped.
- **Heavy HTML rendering**: build reading-pane HTML off the UI thread; large/table-heavy messages use simplified reader mode before `NavigateToString`.
- **Plain-text links**: render http/https/mailto text as links, and open clicked links in the default browser rather than inside the reading pane.
- **Folder picker**: `FolderPickerWindow` is a flat virtualized list. It opens with focus on the folder list; `/` (forward slash) moves focus to search.
- **Logging**: `LogService` appends to `%APPDATA%\QuickMail\quickmail.log`; `LogService.Debug()` writes only when `/debug` is present. Avoid logging credentials or unnecessary PII.
- **Inclusive language in documentation and UI text**: Use verbs like "activate", "select", "choose", or "press" instead of "click".

## MVVM Rules — Enforced

These rules apply to every change. Violations must be corrected before a PR can merge.

### ViewModels must not touch the View layer

- **No `MessageBox`, `Window`, or any `System.Windows` UI type in a ViewModel.** Confirmation dialogs, alerts, and window navigation must be requested via an event or callback that the View subscribes to.
  - ✅ `public event Action? ConfirmDeleteRequested;` — raise it from the VM; the View shows the dialog and calls back.
  - ❌ `MessageBox.Show("Delete?", ...)` inside a ViewModel method.
- **No direct references to controls** (`TextBox`, `ListBox`, `Button`, etc.) in a ViewModel. Expose properties and commands; let bindings do the wiring.
- **No `Dispatcher` calls in a ViewModel.** If you need to marshal to the UI thread, use `Application.Current.Dispatcher` only in Views or services, never in a VM.

### Code-behind must not duplicate bindings

- If a control is already bound two-way to a VM property, **do not also set that control's value directly in code-behind**. Pick one path: either the binding, or explicit code-behind assignment — not both.
  - ✅ `vm.NewName = contact.DisplayName;` — updates the VM; the binding propagates to the TextBox.
  - ❌ `NewNameBox.Text = contact.DisplayName;` when `NewNameBox` is already bound to `vm.NewName`.

### Code-behind is allowed only for UI-only concerns

Permitted in `.xaml.cs`:
- Keyboard shortcut wiring (`PreviewKeyDown`, `KeyDown`)
- Focus management (`element.Focus()`, `Keyboard.Focus()`)
- WebView2 navigation and CSP setup
- Subscribing to VM events and showing dialogs in response
- Animation or visual-state transitions that have no business logic

Not permitted in `.xaml.cs`:
- Business logic, data transformation, or validation
- Direct calls to services (`ImapService`, `ContactService`, etc.)
- State decisions ("if account has unread messages, do X")

### Async event handlers in Views

- `async void` event handlers are acceptable **only** in Views (code-behind) for fire-and-forget UI reactions.
- When an `async void` handler calls a service that may be slow (e.g. autocomplete search), use a `CancellationTokenSource` field — cancel and replace it on each invocation so stale results from a superseded call never overwrite fresher results.

## Keyboard Shortcuts (MainWindow)

| Key | Action |
|---|---|
| Ctrl+0 | Focus toolbar |
| Ctrl+1 | Focus account list |
| Ctrl+2 / Ctrl+Y | Focus folder tree |
| Ctrl+3 | Focus message list / conversation tree |
| Ctrl+9 | Focus status bar |
| F6 / Shift+F6 | Cycle panes |
| Ctrl+N | New message |
| Delete | Delete selected messages |
| Escape | Close reading pane |

## Dependencies

| Package | Version | Purpose |
|---|---|---|
| MailKit | 4.16.0 | IMAP + SMTP protocol |
| CommunityToolkit.Mvvm | 8.4.2 | ObservableProperty, RelayCommand |
| Microsoft.Data.Sqlite | 10.0.7 | Local message cache |
| Microsoft.Web.WebView2 | 1.0.3912.50 | HTML email rendering |
| Microsoft.Identity.Client | 4.84.0 | OAuth2 (Microsoft 365) |
| AdysTech.CredentialManager | 3.1.0 | Windows Credential Manager |
