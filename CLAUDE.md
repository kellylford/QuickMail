# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

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

Startup flags: `/debug` enables verbose file logging. `--profileDir <path>` overrides the data directory (default `%APPDATA%\QuickMail`); useful for isolated testing.

## Tests

xUnit 2.9.3 with `Xunit.StaFact` for WPF STA-thread tests.

```bat
dotnet test QuickMail.Tests/QuickMail.Tests.csproj -c Release
dotnet test QuickMail.Tests/QuickMail.Tests.csproj -c Release --filter "FullyQualifiedName~ClassName"
```

All tests use `StubServices.cs` stub implementations to avoid real network and credential calls. Key test classes in `QuickMail.Tests/`:

- **ViewModelConstructionTests** — VM instantiation with stub services (catches init crashes)
- **XamlParseTests** — XAML loads without `XamlParseException` (requires STA thread via `[StaFact]`)
- **LocalStoreServiceTests** — SQLite round-trip tests
- **SettingsViewModelTests** — settings persistence and hotkey binding logic
- **CommandRegistryTests** / **ViewManagerHotkeyIntegrationTests** — command registration and hotkey override
- **RuleServiceTests** / **RulesManagerViewModelTests** — mail rule matching and actions
- **ComposeViewModelReplyTests** / **ComposeViewModelTemplateTests** — compose VM behavior
- **ConversationBuilderTests** / **SenderGroupBuilderTests** — grouping utilities
- **SavedViewsTests** / **ViewManagerViewModelTests** — saved-view persistence and management
- **TemplateServiceTests** / **TemplatePickerViewModelTests** — message template CRUD
- **ProfileContextTests** — profile directory validation
- **IcsModelTests**, **MessageFilterTests**, **TutorialViewModelTests**, **SessionFeaturesTests**, **BatchObservableCollectionTests**

## Architecture

### Service Layer

**App.xaml.cs** is the manual DI composition root — no container. Services are wired in `OnStartup` in dependency order:
`ProfileContext` → `AccountService` → `CredentialService` → `OAuthService` → `ImapService` → `SmtpService` → `ConfigService` → `LocalStoreService` → `ContactService` → `TemplateService` → `RuleService` → `SyncService` → `ViewService` → `CommandRegistry` → `MainViewModel` → `MainWindow`.

Every service has a matching interface in `Services/I*.cs`, making them fully substitutable in tests.

**ImapService** uses MailKit and leases `ImapClient` instances from a bounded per-account pool. Foreground operations (message open, attachment download, mutating user actions) use foreground leases. Background work (sync, UID checks, polling, preview fetches, prefetch) uses background leases capped below the full pool so sync cannot starve interactive work. `MaxImapConnectionsPerAccount` defaults to 6 and is clamped to 1-15.

**LocalStoreService** (SQLite via `Microsoft.Data.Sqlite`) caches messages in `mail.db` with WAL journaling. It stores `MessageSummary` rows for list panes and `MessageDetail` rows for body/attachment metadata, and handles column-addition migrations at startup. Schema version is tracked via `PRAGMA user_version` so data migrations run exactly once.

**SyncService** runs background IMAP sync. It raises `FolderSynced`, `MessagesRemoved`, and `RulesApplied` events; `MainViewModel` subscribes and merges new data into observable collections. UI is populated from the SQLite cache immediately, then background sync fills gaps.

**OAuthService** wraps MSAL (`Microsoft.Identity.Client`) for Microsoft 365 / Outlook OAuth2. Token refresh is handled automatically; passwords for OAuth accounts are not stored in Credential Manager.

**ConfigService** reads/writes `config.ini` (INI format, human-editable) and `hotkeys.json` (JSON). Settings include `PreviewLines`, `ShowMessageStatus`, `ViewMode`, `SyncDays`, `InitialSyncCount`, with optional per-account `[account:{guid}]` overrides. Results are cached after first load.

**ContactService** stores the address book in `contacts.json`. Upserts by email address (case-insensitive); `SearchContactsAsync` returns up to 10 results ordered by `LastUsedTicks`. Contacts are auto-upserted when mail is sent.

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
- `FolderTreeBuilder` — builds `FolderTreeNode` hierarchy from a flat `MailFolderModel` list; auto-detects IMAP path separator (`.` or `/`)

### Helpers

**`BatchObservableCollection<T>`** (`Helpers/BatchObservableCollection.cs`) — subclass of `ObservableCollection<T>` that suppresses individual `CollectionChanged` notifications during bulk mutations and fires a single `Reset` when the batch closes. This prevents screen readers from re-announcing the focused item after every insert during background sync. Always use `BeginBatchScope()` in a `using` block (auto-closes on exception); the manual `BeginBatch()` / `EndBatch()` pair is available for explicit `try/finally` sites. Nesting is safe — the outermost scope fires the reset.

### ViewModel State

**MainViewModel** owns application state. Key patterns:

- Cancellation token sources are scoped by activity: connect, folder load, message load, background sync, and prefetch.
- Version stamps discard stale async results after folder/message/view changes.
- View modes: Messages, Conversations, From, and To.
- `OnFolderSynced` and `OnMessagesRemoved` must use key-based lookups, not repeated `Messages.Any()` / `FirstOrDefault()` scans over large All Mail views.
- `_suppressFolderSyncUpdates` silences sync events during startup; `_suppressFilterRebuild` prevents stale filter application during folder transitions.

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
- **Logging**: `LogService` appends to `quickmail.log`; `LogService.Debug()` writes only when `/debug` is present. Avoid logging credentials or unnecessary PII.
- **Inclusive language in documentation and UI text**: Use verbs like "activate", "select", "choose", or "press" instead of "click".
- **Screen reader references**: Do not name a specific screen reader product (NVDA, JAWS, VoiceOver, Narrator, etc.) in documentation, release notes, commit messages, or UI text unless the content is specific to that product. Use the generic term "screen readers" instead.
- **Programmatic screen reader announcements**: All custom announcements must go through `AccessibilityHelper.Announce()`. Never call `RaiseNotificationEvent` directly. Every call must pass a `category` argument — choose the most specific one:
  - `AnnouncementCategory.Hint` — instructional text the user will already know after initial familiarization (e.g. "Press Escape to return to the list"). Users can silence these in Settings.
  - `AnnouncementCategory.Status` — background progress the user didn't explicitly trigger (e.g. sync counts, connection state). Users can silence these in Settings.
  - `AnnouncementCategory.Result` — direct outcome of a user action (e.g. "3 messages found", "Moved to Archive"). This is the default parameter value.
  - Use `force: true` only for feedback about the announcement system itself (the toggle confirmation), so it is always heard regardless of settings.
  - Do not bake instructional text into `AutomationProperties.Name` on controls. Keep the name a short identifying label ("Search messages", not "Search messages. Press Tab to move to results."). If the instruction is worth surfacing, deliver it as a `Hint` announce at the moment the control is focused or activated.

## Modal Dialog Rules — Enforced

These rules prevent a class of crashes (`STATUS_CALLBACK_RETURNED_THREAD_APT_CHANGED`) that
occur when parent-window UI is mutated while a modal dialog's message loop is still running.

### Never fire ViewsChanged (or any event that triggers UpdateSavedViews / RebuildViewsMenu) while a modal dialog is open

`ShowDialog()` blocks the caller but runs a nested message loop.  If code inside the dialog
fires an event that causes the **parent window** to rebuild its menu, re-query the folder tree,
or otherwise touch WPF objects — all while the dialog's loop is still active — the UI thread
enters a re-entrant state that violates COM apartment rules and crashes the app.

- ✅ Fire `ViewsChanged` **after** `dialog.ShowDialog()` returns (the dialog is gone, its message loop is dead).
- ✅ If you need the parent to sync state, set a flag or let the caller call `UpdateSavedViews()` post-close.
- ❌ `vmVm.ViewsChanged += (_, _) => _vm.UpdateSavedViews();` before `dialog.ShowDialog()` — this crashes on Delete, Save, or any in-dialog operation that raises the event.
- ❌ Calling `ViewsChanged?.Invoke(...)` from `OnClosing` — the message loop is still unwinding.

This was the root cause of two separate crashes: Escape in the Save View dialog, and Delete/Save in the View Manager.

### Event subscriptions on dialog VMs must be cleaned up

If you subscribe to a VM event before `ShowDialog()`, unsubscribe after — even if the VM is
short-lived — to prevent ghost callbacks if the object graph is retained longer than expected.

```csharp
void OnChanged(object? s, EventArgs e) { ... }
vmVm.SomeEvent += OnChanged;
dialog.ShowDialog();
vmVm.SomeEvent -= OnChanged;   // always pair += with -=
```

### XAML element names in tests must use `as` + `Assert.NotNull`

`window.FindName("ElementName")` returns `null` if the element is renamed or removed from XAML.
A direct cast `(Button)null` silently produces a null reference; `.Visibility` then throws
`NullReferenceException` with no indication of which element is missing.

- ✅ `var btn = window.FindName("MyButton") as Button; Assert.NotNull(btn);`
- ❌ `var btn = (Button)window.FindName("MyButton");`  — crashes instead of failing cleanly

### Data validation at entry points, not exit points

Virtual folder sentinel strings (`FullName` starting with `\x00`) must be excluded when saving
view state.  Use `IsRealImapFolder()` in `ViewManagerViewModel` at the point where a folder
**enters** a saved view.  Defensive guards in load/fetch code are belt-and-suspenders only;
they should never be the primary protection.

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
| Ctrl+Q | `mail.markRead` | Mark as Read |
| F5 | `mail.refresh` | Refresh |
| Ctrl+Shift+E | `mail.emptyTrash` | Empty Trash |
| Ctrl+Shift+V | `view.openViewMenu` | Open View Menu |
| Ctrl+Shift+F | `view.searchFolders` | Search Folders… |
| Ctrl+Shift+S | `view.search` | Search Messages… |
| Ctrl+Shift+G | `contacts.grabAddresses` | Grab Addresses from Message |
| Ctrl+Shift+B | `contacts.openAddressBook` | Address Book |
| F1 | `help.userGuide` | Open User Guide |
| *(unassigned)* | `settings.toggleCustomAnnouncements` | Toggle Custom Announcements |
| Ctrl+A | `mail.selectAll` | Select All Messages (message list focus only) |
| Shift+, | `mail.jumpToFirstInGroup` | First Message in Group |
| Shift+. | `mail.jumpToLastInGroup` | Last Message in Group |
| *(unassigned)* | `mail.acceptInvite` | Accept Invitation |
| *(unassigned)* | `mail.declineInvite` | Decline Invitation |
| *(unassigned)* | `mail.tentativeInvite` | Tentatively Accept Invitation |
| *(unassigned)* | `help.keyboardTutorial` | Keyboard Tutorial |

**Compose window shortcuts** (Alt+S, Ctrl+Enter, Ctrl+S, Ctrl+Shift+A, F7, Shift+F7, Alt+Y, Alt+U, Alt+M, Escape) are registered or hardcoded in `ComposeWindow.xaml.cs`. Registry-based ones appear in the compose window's command palette (`Ctrl+Shift+P`) but are **not** user-customisable via the Settings dialog. The main window's `CommandRegistry` and `hotkeys.json` do not include compose commands. `Ctrl+Enter` is hardcoded (like `Ctrl+Shift+P`) as a second send gesture so it does not create a duplicate "Send Message" entry in the palette.


## Dependencies

| Package | Version | Purpose |
|---|---|---|
| MailKit | 4.16.0 | IMAP + SMTP protocol |
| CommunityToolkit.Mvvm | 8.4.2 | ObservableProperty, RelayCommand |
| Microsoft.Data.Sqlite | 10.0.7 | Local message cache |
| Microsoft.Web.WebView2 | 1.0.3912.50 | HTML email rendering |
| Microsoft.Identity.Client | 4.84.0 | OAuth2 (Microsoft 365) |
| AdysTech.CredentialManager | 3.1.0 | Windows Credential Manager |
