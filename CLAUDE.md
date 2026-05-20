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
dotnet test QuickMail.Tests/QuickMail.Tests.csproj -c Release --filter "FullyQualifiedName~ClassName"
```

Test types in `QuickMail.Tests/`:

- **ViewModelConstructionTests** — VM instantiation with stub services (catches init crashes)
- **XamlParseTests** — XAML loads without `XamlParseException` (requires STA thread via `[StaFact]`)
- **LocalStoreServiceTests** — SQLite round-trip tests
- **SettingsViewModelTests** — settings persistence and hotkey binding logic

- **ViewModelConstructionTests**: VM instantiation with stub services.
- **XamlParseTests**: XAML loads without `XamlParseException` (STA thread).
- **LocalStoreServiceTests**: SQLite round-trip tests.
- **SettingsViewModelTests**: hotkey/settings behavior.

All tests use `StubServices.cs` stub implementations to avoid real network and credential calls.

## Architecture

### Service Layer

**App.xaml.cs** is the manual DI composition root — no container. Services are wired in `OnStartup` in dependency order:
`AccountService` → `CredentialService` → `OAuthService` → `ImapService` → `SmtpService` → `ConfigService` → `LocalStoreService` → `ContactService` → `SyncService` → `CommandRegistry` → `MainViewModel` → `MainWindow`. Pass `/debug` on startup to enable verbose file logging.

**ImapService** uses MailKit and leases `ImapClient` instances from a bounded per-account pool. Foreground operations (message open, attachment download, mutating user actions) use foreground leases. Background work (sync, UID checks, polling, preview fetches, prefetch) uses background leases capped below the full pool so sync cannot starve interactive work. `MaxImapConnectionsPerAccount` defaults to 6 and is clamped to 1-15.

**LocalStoreService** (SQLite via `Microsoft.Data.Sqlite`) caches messages in `%APPDATA%\QuickMail\mail.db` with WAL journaling. It stores `MessageSummary` rows for list panes and `MessageDetail` rows for body/attachment metadata, and handles column-addition migrations at startup.

**SyncService** runs background IMAP sync. It raises `FolderSynced` and `MessagesRemoved` events; `MainViewModel` subscribes and merges new data into observable collections. UI is populated from the SQLite cache immediately, then background sync fills gaps.

**OAuthService** wraps MSAL (`Microsoft.Identity.Client`) for Microsoft 365 / Outlook OAuth2. Token refresh is handled automatically; passwords for OAuth accounts are not stored in Credential Manager.

**ConfigService** reads/writes `%APPDATA%\QuickMail\config.ini` (INI format, human-editable) and `hotkeys.json` (JSON). Settings include `PreviewLines`, `ShowMessageStatus`, `ViewMode`, `SyncDays`, `InitialSyncCount`, with optional per-account `[account:{guid}]` overrides. Results are cached after first load.

**ContactService** stores the address book in `%APPDATA%\QuickMail\contacts.json`. Upserts by email address (case-insensitive); `SearchContactsAsync` returns up to 10 results ordered by `LastUsedTicks`. Contacts are auto-upserted when mail is sent.

**CommandRegistry** holds all `CommandDefinition` instances (id, category, title, default gesture, execute action). Commands are registered in `MainWindow`'s constructor. `FindByGesture()` checks user overrides from `hotkeys.json` first, then falls back to defaults. The command palette (`Ctrl+P`) uses `CommandPaletteViewModel` to display and invoke them.

**Static utilities** (no DI required):
- `ConversationBuilder` — groups messages by normalized subject (strips all leading Re:/Fwd: chains); used for Conversations view mode
- `SenderGroupBuilder` — groups messages by From or To; used for From/To view modes
- `MimeMessageBuilder` — builds a `MimeMessage` from `ComposeModel` + `AccountModel`; shared by `SmtpService` and `ImapService` (draft saving)
- `AddressParser` — splits comma/semicolon-delimited address strings into `MailboxAddress` objects

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
- **Screen reader references**: Do not name a specific screen reader product (NVDA, JAWS, VoiceOver, Narrator, etc.) in documentation, release notes, commit messages, or UI text unless the content is specific to that product. Use the generic term "screen readers" instead.

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

## Keyboard Shortcuts — Enforced

Every user-facing keyboard shortcut **must** be registered in `CommandRegistry` via `_registry.Register(new CommandDefinition(...))` in `MainWindow.xaml.cs`. This is not optional: registration is what makes the shortcut appear in the **keyboard customizations** dialog and in the **Command Palette**.

### Rules

- **Register first, hardcode never.** Do not add a raw `if (modifiers == ... && key == ...)` block in `PreviewKeyDown` for a new action. Register the command with `defaultKey` / `defaultModifiers` and let the registry dispatch it.
- **Two exceptions** are allowed to remain hardcoded (they are framework-level, not user actions):
  - `Ctrl+Shift+P` — opens the Command Palette itself (cannot dispatch through the palette)
  - Navigation shortcuts `Ctrl+0–3`, `Ctrl+9`, `Ctrl+Y`, `F6` — focus-only pane jumps with no associated command title
- **`InputGestureText` in menus** must match the registered default key, e.g. `InputGestureText="Ctrl+Shift+F"`.
- **Category** must be one of: `View`, `Mail`, `Account`, `Contacts`, `Settings`, `Help`.

### Adding a new shortcut — checklist

1. `_registry.Register(new CommandDefinition(id: "category.name", category: "…", title: "…", execute: MyMethod, defaultKey: Key.X, defaultModifiers: ModifierKeys.Control));`
2. Add a menu item with matching `InputGestureText` if applicable.
3. Do **not** add a duplicate hardcoded branch in `PreviewKeyDown`.

### Registered shortcut table (MainWindow)

| Key | Command ID | Title |
|---|---|---|
| Ctrl+0 | *(hardcoded)* | Focus toolbar |
| Ctrl+1 | *(hardcoded)* | Focus account list |
| Ctrl+2 / Ctrl+Y | `view.focusFolders` | Focus Folder Tree |
| Ctrl+3 | *(hardcoded)* | Focus message list |
| Ctrl+9 | `view.focusStatusBar` | Focus Status Bar |
| F6 / Shift+F6 | *(hardcoded)* | Cycle panes |
| Escape | *(hardcoded)* | Close reading pane |
| Ctrl+Shift+P | *(hardcoded)* | Command Palette |
| Ctrl+N | `mail.new` | New Message |
| Ctrl+R | `mail.reply` | Reply |
| Ctrl+Shift+R | `mail.replyAll` | Reply All |
| Ctrl+F | `mail.forward` | Forward |
| Delete | `mail.delete` | Delete |
| F5 | `mail.refresh` | Refresh |
| Ctrl+Shift+E | `mail.emptyTrash` | Empty Trash |
| Ctrl+Shift+V | `view.openViewMenu` | Open View Menu |
| Ctrl+Shift+F | `view.searchFolders` | Search Folders… |
| Ctrl+Shift+S | `view.search` | Search Messages… |
| Ctrl+Shift+G | `contacts.grabAddresses` | Grab Addresses from Message |
| Ctrl+Shift+B | `contacts.openAddressBook` | Address Book |
| F1 | `help.userGuide` | Open User Guide |


## Dependencies

| Package | Version | Purpose |
|---|---|---|
| MailKit | 4.16.0 | IMAP + SMTP protocol |
| CommunityToolkit.Mvvm | 8.4.2 | ObservableProperty, RelayCommand |
| Microsoft.Data.Sqlite | 10.0.7 | Local message cache |
| Microsoft.Web.WebView2 | 1.0.3912.50 | HTML email rendering |
| Microsoft.Identity.Client | 4.84.0 | OAuth2 (Microsoft 365) |
| AdysTech.CredentialManager | 3.1.0 | Windows Credential Manager |
