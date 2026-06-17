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
build.bat installer  # publish + compile Inno Setup installer -> installer/Output/quickmail-v<version>-setup.exe
build.bat smoke      # build + launch for 6s
build.bat clean
```

Or directly: `dotnet run --project QuickMail`.

The `installer` target requires [Inno Setup 6](https://jrsoftware.org/isdl.php) (`ISCC.exe`). See `docs/INSTALLER.md` for installer details.

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

Manual DI root in `App.xaml.cs` — no container. Services wired in `OnStartup`:
`ProfileContext` → `AccountService` → `CredentialService` → `OAuthService` → `ImapService` → `SmtpService` → `ConfigService` → `LocalStoreService` → `ContactService` → `TemplateService` → `RuleService` → `SyncService` → `ViewService` → `CommandRegistry` → `MainViewModel` → `MainWindow`.

Every service has a matching interface in `Services/I*.cs`. See `docs/ARCHITECTURE.md` for full service descriptions, runtime modes, and virtual folder sentinels.

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

### Screen Reader Announcement Infrastructure

All custom screen reader announcements are user-configurable and governed by `ConfigModel` settings (`config.ini`):

**Configuration settings** (in `[config.ini]`, all optional, default to `true` except spelling-while-typing):
- `CustomAnnouncements` — Master on/off switch for all programmatic announcements
- `AnnounceHints` — Instructional tips (e.g. "Press Escape to return")
- `AnnounceStatus` — Background progress (e.g. "Syncing…", "N messages loaded", connection state)
- `AnnounceResults` — Action outcomes (e.g. "3 messages moved", "Delete may not have completed")
- `AnnounceSpellingWhileTyping` — Misspellings during typing (off by default, adds overhead)
- `AnnounceSpellingWhileNavigating` — Misspellings on navigation

**Implementation rules:**
- **All custom announcements must go through `AccessibilityHelper.Announce(text, category, force)`**. Never call `RaiseNotificationEvent` directly.
- **Every call must pass a `category` argument** — maps to config settings above:
  - `AnnouncementCategory.Hint` → respects `AnnounceHints` setting
  - `AnnouncementCategory.Status` → respects `AnnounceStatus` setting (sync progress, loading, connection state)
  - `AnnouncementCategory.Result` → respects `AnnounceResults` setting (search counts, operation confirmations)
- **`force: true`** bypasses config settings — use only for meta-announcements (e.g. "Custom announcements toggled on/off"). All regular content respects user preferences.
- **Do not bake instructional text into `AutomationProperties.Name`** on controls. Keep the name a short identifying label ("Search messages", not "Search messages. Press Tab to move to results."). If the instruction is worth surfacing, deliver it as a `Hint` announce at the moment the control is focused or activated.

**Example**: Sync status updates use `AnnouncementCategory.Status` so users who disable background progress announcements won't hear every folder completion. Message counts at sync end use `AnnouncementCategory.Status` (when `AnnounceStatus` is on) and appear as visual status bar text regardless (for sighted users and always-visible state).

### Screen Reader User Experience — Defer to User Expertise

**Critical principle**: When working on accessibility features, AI should NEVER make claims about how screen readers work or what the user experience is without explicit user guidance. The person using the assistive technology is the expert.

- **Never assume** screen reader behavior based on training data or general knowledge
- **Always ask clarifying questions** about what the user actually hears/experiences rather than explaining it back to them
- **Trust the user's report** of their actual experience — they have the real data from using the technology
- **Document user feedback** accurately — if a user with decades of assistive technology experience reports a problem, that report is authoritative

When accessibility issues arise, the investigation should be user-centric:
1. Ask: "What do you hear/experience right now?"
2. Ask: "What should you hear/experience instead?"
3. Verify the fix matches the user's actual experience, not theoretical expectations

## Modal Dialog Rules — Enforced

These rules prevent a class of crashes (`STATUS_CALLBACK_RETURNED_THREAD_APT_CHANGED`) that
occur when parent-window UI is mutated while a modal dialog's message loop is still running.

### Prefer modeless `Show()` for dialogs launched over a window with a live WebView2 — especially if they contain an editable text field

`ShowDialog()` runs a **nested message loop** on the UI thread. When such a dialog is opened
on top of a window that hosts a live WebView2 (e.g. the reading pane or a `MessageWindow`),
and the dialog contains an editable `TextBox`, focusing that text field with a screen reader
active can **hard-deadlock the UI thread** — the dispatcher freezes and *all* keyboard input
to the app stops. The screen reader's synchronous UIA query, the editable control's text/COM
activation, the out-of-process WebView2 UIA provider, and the nested modal loop combine into
a cross-apartment STA wait that never resolves.

This is the root cause of the GrabAddresses keyboard lockup. The fix was to launch the dialog
with `.Show()` (modeless) instead of `.ShowDialog()`. The compose window — modeless, editable,
and used constantly without issue — is the proof that modeless is the safe pattern.

- ✅ `new GrabAddressesDialog(...) { Owner = this }.Show();` when the dialog has editable text
  and is opened over an open message.
- ❌ `…ShowDialog();` for the same dialog — modal nesting + editable text + WebView2 + screen
  reader = frozen dispatcher.
- A vanilla `TextBox` is **not** the problem (editable boxes work everywhere else in the app,
  all modeless). Do **not** chase exotic "the screen reader redirects focus" theories, disable
  the input-method/TSF context, or remove `Focus()` calls — those are red herrings. The
  differentiator is the **modal nested message loop**, so look there first.

**Modeless trade-off to handle:** a modeless window has no `DialogResult`, so `IsCancel="True"`
and Escape no longer auto-close it. Wire Cancel and Escape to `Close()` explicitly (a
`PreviewKeyDown` Escape handler — guarded so it does not steal Escape from an open ComboBox
dropdown — plus a Cancel `Click` handler). Save handlers should call `Close()` directly.

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

## IDisposable Rules — Enforced

Fixing CA1001 by implementing `IDisposable` is only half the job. Before the PR can merge, verify the full ownership chain:

### Always cancel before disposing a CancellationTokenSource

```csharp
// Wrong — in-flight tasks get ObjectDisposedException instead of OperationCanceledException
public void Dispose() { _cts.Dispose(); }

// Correct — Cancel is safe to call twice; sends the clean signal before the handle is released
public void Dispose() { _cts.Cancel(); _cts.Dispose(); GC.SuppressFinalize(this); }
```

### Verify Dispose() is actually called

Adding `IDisposable` to a class means nothing unless something calls `Dispose()`. For every new `IDisposable` implementation, identify where `Dispose()` is called before the PR is merged:

- **Services created in `App.xaml.cs`**: store in a private field; call `?.Dispose()` in `OnExit`.
- **ViewModels owned by a Window**: call `vm.Dispose()` in `OnClosed` (not `OnClosing` — the window may still cancel the close and stay open).
- **Process-lifetime singletons where disposal is genuinely a no-op**: suppress CA1001 with a `[SuppressMessage]` explaining why nothing calls Dispose, rather than implementing the interface.

### `[RelayCommand]` methods must stay instance methods

Do not make a `[RelayCommand]` method `static` to satisfy CA1822. Use `#pragma warning disable CA1822` instead. Static relay commands are unidiomatic in CommunityToolkit.Mvvm and lose integration with the owning `ObservableObject` instance.

### `#pragma warning disable` comments must state the real reason

When suppressing a warning, the comment must state *why* the suppression is correct, not just echo the warning text. Two common mistakes found in this codebase:

- ❌ `// instance property required for XAML data binding` — XAML can bind to static properties; the real reason is `[NotifyPropertyChangedFor]` fires `PropertyChanged` on the instance.
- ❌ `// Window subclasses cannot implement IDisposable` — they can; the real reason is WPF does not call `Dispose` on `Window` instances.

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

See `docs/KEYBOARD-SHORTCUTS.md` for the full registered shortcut table and compose window details.

## Accessibility Checklist — Apply to Every XAML Change

Before committing any XAML, verify each of these:

- **`AutomationProperties.Name` is a short label only.** No descriptions, no role names (not "tab", "button", "checkbox"), no keyboard shortcuts, no sentences. The screen reader already announces the role; repeating it doubles the speech. Wrong: `"General settings tab"`. Right: `"General settings"`.
- **Hints and usage instructions go through `AccessibilityHelper.Announce`**, not into `AutomationProperties.Name`. If a keyboard shortcut or usage tip is worth surfacing, deliver it as `AnnouncementCategory.Hint` when the control is first focused — where the user's hint preference applies.
- **Radio button groups have one tab stop.** The container must have `KeyboardNavigation.TabNavigation="Once"` and `KeyboardNavigation.DirectionalNavigation="Cycle"`. All buttons in the group share the same `GroupName`. Individual radio buttons must not each be reachable via Tab.
- **New primary pane controls are in the F6 ring.** Any list, tree, or panel that is a major navigation destination must be added to `CycleFocusAsync` and `GetFocusedPaneIndex` in `MainWindow.xaml.cs`. A control that can receive focus but is not in F6 is stranded.

## New Window Checklist — Apply When Creating Any Window Subclass

Every `Window` subclass requires all of the following before it is committed:

- **F6 / Shift+F6 focus cycle.** Define the logical pane stops (e.g. toolbar, header fields, body). Implement a cycle method and handle `F6` / `Shift+F6` in `PreviewKeyDown`.
- **WebView2 F6 relay.** If the window contains a WebView2, the injected JS keydown script must relay `F6` and `Shift+F6` as postMessages back to WPF, exactly as `MainWindow` does. Without this, F6 pressed inside the body is swallowed and the cycle breaks.
- **Command palette.** Wire `Ctrl+Shift+P` to open a local command palette containing all window-scoped actions (close, navigate, move, etc.). Follow the pattern in `ComposeWindow.xaml.cs`. Actions that have no default hotkey still belong in the palette so users can discover them.
- **Cancellation token.** Any async load must use a `CancellationTokenSource` field. Cancel and dispose it in `OnClosing`. Re-create it at the start of each new load so navigating away cancels in-flight fetches.
- **Focus restoration on close.** Capture the originating focused element (or its index) before the window opens. When the window closes, explicitly return focus to that position. WPF's default return-to-owner behaviour is not reliable for virtualised list items.

## Feature Checklist — Apply Before Committing Any New Feature

Before a feature branch is committed:

1. **Exercise every entry point.** A message-opening feature must be tested from the flat message list, the conversations tree, and the from/to group trees. A feature that only works from one view is incomplete.
2. **Exercise every configured mode.** If a feature has a mode setting (e.g. ReadingPane / Tab / Window), verify it works correctly in every mode before committing.
3. **Keyboard-only walkthrough.** Perform the full user journey using only the keyboard — Tab, arrow keys, Enter, Escape, F6. If focus is lost or stranded at any point, it is a bug, not a follow-up.
4. **No silent empty state from caught exceptions.** A `catch` block that swallows an exception and leaves the UI blank is never acceptable. If a primary data source fails (e.g. SQLite unavailable in `--online` mode), the catch must fall through to a visible fallback (e.g. IMAP fetch) or surface an error. Catch blocks that silently return convert failures into debugging marathons. See the **standard fetch pattern** in `docs/ARCHITECTURE.md` — the local store and IMAP calls must be in separate catch scopes so a local store failure does not prevent the IMAP fallback from running.
5. **Test in `--online` mode** for any feature that calls `LocalStoreService`. Run with `--online` and verify the feature works correctly from IMAP alone. Features that only pass in normal mode are incomplete.
6. **If the feature affects startup state, verify it activates before the user sees content.** Any feature that influences what the user sees or hears at launch (default view, folder selection, announcement text, connection status, etc.) must be applied in `InitialLoadAsync`, not deferred to the end of `StartBackgroundSyncAsync`. Deferring to post-sync means the user sees a different state for 20–40 seconds before the feature takes effect.

## Spec Writing Requirements

When AI generates a spec from a conceptual directive, the spec is not ready for implementation until it includes all three of these sections.

### Keyboard walkthrough

A numbered step-by-step sequence showing exactly what the user does and what they hear or see, for each distinct mode or path. Example:

1. User presses Enter on a message. Screen reader announces: "Opening message."
2. A window appears with focus on the message body. Screen reader announces: "Message body. [Subject]."
3. User presses F6. Focus moves to the toolbar. Screen reader announces: "Toolbar."
4. User presses Escape. Window closes. Focus returns to the originating message in the list.

This forces every interaction to be explicitly designed before any code is written. A gap in the walkthrough means a missing design decision — not something to be resolved during coding.

### Infrastructure changes

Explicitly list every change to shared infrastructure:
- Which panes are added to or removed from the F6 ring
- Which commands are added to `CommandRegistry`, with category and whether a default key is assigned
- Which `AutomationProperties.Name` values are introduced or changed
- Which `AccessibilityHelper.Announce` calls are added, with category (Hint/Status/Result). **Reminder**: announcements in each category are gated by user configuration (`AnnounceHints`, `AnnounceStatus`, `AnnounceResults`) — specs must explicitly choose the category so implementation respects user preferences.
- Whether VM state properties (e.g. `IsMessageOpen`) need updating to reflect the new feature

### Out of scope

Explicitly state what the feature does not do. This surfaces assumptions that need a design decision and prevents scope creep. If something is deferred, say so — do not leave it implicit.
