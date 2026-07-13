# Plain Text View — PM + Dev Spec (Issue #34)

> Status: Draft for implementation. Scope is a small, bounded enhancement, so this spec
> uses the reduced section set the SPEC-TEMPLATE prescribes for enhancements, plus the
> three mandatory sections (keyboard walkthrough, infrastructure changes, out of scope)
> and the shared-component audit.

---

## 1. Executive Summary

QuickMail always renders a message's HTML body when the sender provides one; there is no way
to read a message as plain text. This feature adds a **sticky "Plain Text View" preference**:
when on, every message renders from its original `text/plain` MIME part (falling back to
text extracted from the HTML only when the sender sent no plain-text part). The preference is
toggled by a command (Command Palette + View menu) and by a checkbox in Settings, persists
across restarts, and applies to all three reading surfaces (reading pane, message tab, and the
standalone message window). Default is **off** — zero change for existing users.

## 2. User Problem & Opportunity

### 2.1 Current state (verified)

| Surface | Today | Pain |
|---|---|---|
| Reading pane / tab (`MainWindow.ShowMessageBodyAsync` → `MessageBodyHtmlBuilder.BuildMessageHtml`) | Always prefers `HtmlBody`; only falls back to plain text when the HTML is *too complex* to sanitize (reader mode) or times out | No user control. A user who prefers plain text, or wants to see a message's raw text for a phishing/formatting check, cannot |
| Standalone message window (`MessageWindow.ShowMessageBodyAsync`) | Same builder, same HTML-first behavior | Same |
| Settings | No reading-view preference exists | Nothing to persist a plain-text choice |

Verified facts:
- `MailMessageDetail` already carries both `PlainTextBody` (the sender's `text/plain` part) and
  `HtmlBody` (`Models/MailMessageDetail.cs:11-13`). Fidelity is achievable because the original
  plain-text part is already stored — we do not have to synthesize it.
- `MessageBodyHtmlBuilder.BuildMessageHtml(detail, themeCss)` is the single rendering entry point
  for all three surfaces (`Helpers/MessageBodyHtmlBuilder.cs:34`), and it already contains
  `BuildPlainTextHtmlDocument` and `HtmlToText` — the plain-text rendering path exists; it is just
  never chosen by user request.

### 2.2 Target personas

- **Kelly / screen-reader power user** — wants faithful, low-noise text without HTML layout
  quirks; toggles plain text for a cleaner read. Uses the command or a bound hotkey.
- **Security-conscious reader** — wants to see a suspicious message's raw text (unstyled) to
  judge it. Toggles on, reads, toggles off.
- **Low-vision / simple-layout user** — prefers plain reflowed text app-wide; sets the Settings
  checkbox once and forgets it.

## 3. Design Principles

1. **Zero change when off.** Default off; the existing HTML-first path is byte-for-byte unchanged.
2. **Fidelity first.** When on, render the sender's own `text/plain` part verbatim. Only when
   there is no plain-text part do we extract text from the HTML, and we say so with a note.
3. **One preference, every surface.** Reading pane, tab, and standalone window all honor the
   same sticky setting. No per-surface divergence.
4. **Register, don't hardcode.** The toggle is a `CommandRegistry` command (per CLAUDE.md), so it
   appears in the Command Palette and keyboard-customizations dialog.
5. **Respect announcement preferences.** The toggle's spoken confirmation goes through
   `AccessibilityHelper.Announce` with `AnnouncementCategory.Result`.

## 4. Feature Scope & Acceptance Criteria

### 4.1 In scope (v1)

| Feature | Setting / Shortcut | Default | Notes |
|---|---|---|---|
| Sticky plain-text preference | `ConfigModel.ReadAsPlainText` | `false` | Persisted in `config.ini` |
| Toggle command (main window) | `view.togglePlainText` / **Ctrl+Shift+H** | — | Category `View`; re-renders the open message; flips + saves config; announces |
| Toggle command (message window) | `window.togglePlainText` / **Ctrl+Shift+H** | — | Local registry; same behavior for the standalone window |
| View-menu item | "_Plain Text View" (checkable) | unchecked | `IsChecked` bound one-way to `MainViewModel.ReadAsPlainText`; `InputGestureText="Ctrl+Shift+H"` |
| Settings checkbox | "Read messages as plain text" | unchecked | On the General settings tab; round-tripped by `SettingsViewModel` |
| Builder plain-text mode | `BuildMessageHtml(detail, themeCss, forcePlainText)` | `forcePlainText:false` | New optional param; existing callers unaffected |

**Acceptance criteria:**
1. With the setting off, a message with an HTML body renders exactly as it does today.
2. With the setting on, a message that has a `text/plain` part renders that part verbatim
   (subject to the existing 140k-char reader clamp and URL auto-linking), with **no** "simplified"
   note.
3. With the setting on, a message that has *only* HTML renders text extracted from the HTML, with a
   note: "This message has no plain-text version; showing text extracted from the HTML."
4. Toggling via command re-renders the currently open message in place without moving focus out of
   the message body, and announces the new state.
5. The setting persists across app restart.
6. The toggle works identically in reading-pane mode, tab mode, and standalone-window mode.

### 4.2 Explicitly out of scope (v1)

- **No per-message override.** The preference is global/sticky (this was the chosen design). There
  is no "just this one message" transient toggle distinct from the sticky mode.
- **No per-account override.** `ReadAsPlainText` is a single global flag, not in `AccountOverrideConfig`.
- **No "prefer plain text on send/compose" change.** This is strictly a *reading* feature; compose
  mode defaults are untouched.
- **No change to the automatic reader-mode fallback.** Complex/oversized HTML still falls back to
  simplified rendering when the setting is off, exactly as today.
- **No attachment or calendar-card changes.** The event card still prepends as today; only the body
  region changes.

## 5. Architecture & Technical Decisions

### 5.1 Key decisions

**Decision:** Add `bool forcePlainText = false` as a third parameter to
`MessageBodyHtmlBuilder.BuildMessageHtml`. When true, skip the HTML-sanitize branch and render via
`BuildPlainTextHtmlDocument` using `PlainTextBody` (or `HtmlToText(HtmlBody)` when the plain part is
empty).

- **Alternatives:** (a) A separate `BuildPlainTextMessageHtml` method — rejected: duplicates the
  subject/theme/note plumbing already in `BuildMessageHtml`. (b) Have each call site choose the
  document builder — rejected: spreads the fidelity/fallback logic across three files.
- **Rationale:** One optional parameter keeps all four existing callers source-compatible (they
  pass nothing new unless opting in), and centralizes the "plain text part vs HTML-extract + note"
  decision in one place that is already unit-tested (`MessageBodyHtmlBuilderTests`).

**Decision:** Persist the preference as a single global bool `ConfigModel.ReadAsPlainText`, read
live at each render.

- **Rationale:** Matches the sticky + Settings design. The reading pane and standalone window both
  already read `_configService.Load()` at render time for theme CSS, so reading one more flag there
  is consistent and needs no new plumbing.

**Decision:** The main-window toggle command lives in `MainWindow.xaml.cs` `_registry` (window-level
wiring), because executing it must call the View's `RerenderReadingPaneAsync()`. It flips and saves
config, sets `MainViewModel.ReadAsPlainText` (for menu check state), re-renders, and announces.

- **Rationale:** Consistent with existing window-level commands that touch the WebView2
  (`contacts.grabAddresses`, `mail.closeMessage`). Keeps `Dispatcher`/WebView2 out of the VM.

### 5.2 Runtime mode compatibility

The feature reads only `MailMessageDetail` fields already loaded into the open message and the
config flag. It calls **no** `LocalStore…Async` methods itself.

| Mode | Behavior |
|---|---|
| Normal | Works; renders `PlainTextBody` already present on the loaded detail. |
| `--online` | Works unchanged; the detail is fetched from IMAP as today, and `PlainTextBody` is populated by the fetch. No SQLite dependency added. |
| `--profileDir <path>` | Works; config is read from the alternate profile. |

### 5.3 Code reuse

Rendering is already centralized in `MessageBodyHtmlBuilder`. No new duplication is introduced;
this spec removes the risk of divergence by routing all three surfaces through the one new
parameter rather than adding a second plain-text path.

### 5.4 Shared component audit (mandatory)

| Component | File | Other consumers | Change | Risk / mitigation |
|---|---|---|---|---|
| `MessageBodyHtmlBuilder.BuildMessageHtml` | `Helpers/MessageBodyHtmlBuilder.cs` | `MainWindow` (reading pane + tab, 2 call sites), `MessageWindow` (2 call sites) | Add optional `forcePlainText` param | Backward-compatible: default `false` preserves today's behavior. All 4 sites updated to pass the live flag. |
| `ConfigModel` | `Models/ConfigModel.cs` | ConfigService (INI read/write), SettingsViewModel, many | Add `ReadAsPlainText` bool | Additive property with default; INI round-trip must include it (verify in `SettingsViewModelTests`). |
| `MainViewModel.ApplySettings` | `ViewModels/MainViewModel.cs` | Called from `MainWindow.OpenSettings` after the Settings dialog closes | Set `ReadAsPlainText` from cfg | Additive; MainWindow re-renders the reading pane after apply so a Settings change takes effect live. |
| `SettingsViewModel` | `ViewModels/SettingsViewModel.cs` | `SettingsDialog` | Add `ReadAsPlainText` observable; load in ctor, write in `Save` | Additive; mirrors the existing `ShowMessageStatus` pattern. |
| `SettingsDialog.xaml` (General tab) | `Views/SettingsDialog.xaml` | Only the settings dialog | Add one checkbox | No other consumers. |
| `MainWindow.xaml` View menu | `Views/MainWindow.xaml` | — | Add one checkable menu item | No other consumers. |
| `MessageWindow` local registry | `Views/MessageWindow.xaml.cs` | Only that window | Add `window.togglePlainText` command | No other consumers; follows the New Window checklist (palette entry). |

`MailMessageDetail`, `MessageTabViewModel`, and the tab renderer need **no** change — tab mode
already routes through `MainWindow.ShowMessageBodyAsync`, and `IsMessageOpen`/`MessageDetail` are
set in tab mode (`MainWindow.xaml.cs:3975`), so `RerenderReadingPaneAsync` covers it.

## 6. Keyboard Walkthrough (mandatory)

Assume announcements are enabled (`AnnounceResults = true`). "SR:" = screen-reader speech.

### Path A: Toggle on in the reading pane
1. A message with an HTML body is open in the reading pane; focus is in the message body.
2. User presses **Ctrl+Shift+H**. **Expected:** the body re-renders as plain text in place, focus
   stays in the message body (no focus jump), SR: "Plain text view on."
3. User presses **Ctrl+Shift+H** again. **Expected:** body re-renders as the HTML view, focus stays
   in the body, SR: "Plain text view off."

### Path B: Toggle via View menu
1. User opens the View menu (Alt+V). **Expected:** a "Plain Text View" item shows a check mark that
   reflects the current setting.
2. User activates it. **Expected:** setting flips, the open message (if any) re-renders, SR
   announces the menu selection per normal WPF menu behavior; the confirmation announce ("Plain
   text view on/off") also fires.

### Path C: Message with no plain-text part
1. Setting is on. User opens a message that has only an HTML body. **Expected:** body shows text
   extracted from the HTML, preceded by the note "This message has no plain-text version; showing
   text extracted from the HTML." Focus lands in the body as usual; SR reads the note first, then
   the body.

### Path D: Standalone message window
1. A message is open in a standalone `MessageWindow`; focus in the body.
2. User presses **Ctrl+Shift+H** (or opens the window's Command Palette with Ctrl+Shift+P and picks
   "Toggle Plain Text View"). **Expected:** the window's body re-renders per the new state, focus
   stays in the body, SR: "Plain text view on/off." The setting is shared, so the next message
   opened anywhere honors it.

### Path E: Settings checkbox
1. User opens Settings, General tab. Tabs to "Read messages as plain text", presses Space to check
   it, activates Save/OK.
2. **Expected:** dialog closes; the currently open message (if any) re-renders as plain text
   immediately (no restart). Reopening the app later still has the box checked.

### Path F: No message open
1. No message is open. User invokes "Toggle Plain Text View" from the Command Palette.
2. **Expected:** setting flips and is saved, SR: "Plain text view on/off." Nothing to re-render;
   the next opened message uses the new state.

## 7. Accessibility (mandatory)

- **AutomationProperties.Name:** No new named controls beyond the Settings checkbox and menu item,
  both of which get short labels from their `Content`/`Header` ("Read messages as plain text",
  "Plain Text View"). No role words, no hints baked in.
- **Announcements:** One new announce, `AccessibilityHelper.Announce("Plain text view on"/"off",
  AnnouncementCategory.Result)` — it reports the outcome of an explicit user action, so `Result`
  is correct and it respects `AnnounceResults`. No `force:true` (regular content).
- **Screen-reader browse mode:** Unchanged. The plain-text document is the existing
  `BuildPlainTextHtmlDocument` output (a focusable `<body tabindex="0">` with `white-space:pre-wrap`)
  — the same document type already used for reader-mode fallback, so browse behavior is known-good.
- **Focus:** The re-render path used by the toggle (`RerenderReadingPaneAsync` /
  `MessageWindow` re-render) deliberately does **not** move focus, matching the theme-change
  re-render. Focus stays in the body.
- **F6 ring:** No new panes. No change.
- **Menu check state:** Communicated by the menu check mark **and** by the announce — not color.

## 8. Acceptance Walkthrough (run in-app at test time)

### Scenario 1: Happy path, reading pane
Setup: app running, an HTML-bodied message open in the reading pane.
1. Press Ctrl+Shift+H. **Verify:** body becomes plain text; focus still in body; hear "Plain text
   view on."
2. Press Ctrl+Shift+H. **Verify:** body returns to HTML; hear "Plain text view off."

### Scenario 2: Fidelity — plain part vs HTML-extract
1. Setting on. Open a normal multipart message (has text/plain). **Verify:** text matches the
   sender's plain part (line breaks preserved), **no** "simplified"/"no plain-text version" note.
2. Open an HTML-only message (e.g. many marketing mails). **Verify:** text is shown with the "no
   plain-text version" note.

### Scenario 3: Persistence
1. Turn setting on, close app, reopen. **Verify:** newly opened messages render plain text;
   Settings checkbox is checked.

### Scenario 4: All surfaces
1. In tab mode, open a message in a tab, press Ctrl+Shift+H. **Verify:** tab body re-renders plain.
2. In window mode, open a message in a standalone window, press Ctrl+Shift+H. **Verify:** window
   body re-renders plain; open a second window — it honors the sticky setting.

### Scenario 5: Settings live-apply
1. With a message open, open Settings, toggle the checkbox, Save. **Verify:** open message
   re-renders to match without restart.

### Scenario 6: `--online` mode
1. Launch with `--online`, open a message, toggle plain text. **Verify:** works; no blank body.

### Scenario 7: No regression when off
1. Leave the setting off. Open a variety of messages. **Verify:** rendering is identical to before
   this feature (HTML shown, complex HTML still falls back to reader mode).

## 9. Infrastructure Changes (mandatory)

- **F6 ring:** none added/removed.
- **Commands added to `CommandRegistry`:**
  - `view.togglePlainText` — category `View`, title "Toggle Plain Text View", default key
    **Ctrl+Shift+H**, registered in `MainWindow.xaml.cs`.
  - `window.togglePlainText` — category `View`, title "Toggle Plain Text View", default key
    **Ctrl+Shift+H**, registered in `MessageWindow.xaml.cs` local registry.
- **`AutomationProperties.Name` introduced/changed:** none beyond the labeled checkbox and menu
  item (their Content/Header serves as the name).
- **`AccessibilityHelper.Announce` added:** one, `AnnouncementCategory.Result` ("Plain text view
  on/off").
- **VM state:** `MainViewModel.ReadAsPlainText` (`[ObservableProperty]`) added for menu check
  binding and re-render decisions; set in `ApplySettings`. `SettingsViewModel.ReadAsPlainText`
  added for the Settings round-trip.

## 10. Files to Create / Modify

No new files.

| File | Change | Est. |
|---|---|---|
| `Models/ConfigModel.cs` | Add `ReadAsPlainText` bool (default false) | +3 |
| `Services/ConfigService.cs` | Read/write the flag in the INI round-trip | +2–6 |
| `Helpers/MessageBodyHtmlBuilder.cs` | Add `forcePlainText` param + plain-text branch + no-plain-part note | +15 |
| `ViewModels/MainViewModel.cs` | Add `ReadAsPlainText` observable; set it in `ApplySettings` | +6 |
| `Views/MainWindow.xaml.cs` | Register `view.togglePlainText`; execute = flip+save+set VM+re-render+announce; pass flag at the 2 render sites | +30 |
| `Views/MainWindow.xaml` | Add checkable "Plain Text View" View-menu item; re-render after Settings apply (in `OpenSettings`) | +6 |
| `Views/MessageWindow.xaml.cs` | Register `window.togglePlainText`; pass flag at the 2 render sites; re-render + announce | +25 |
| `ViewModels/SettingsViewModel.cs` | Add `ReadAsPlainText` observable; load in ctor; write in `Save` | +4 |
| `Views/SettingsDialog.xaml` | Add checkbox on the General tab | +4 |

## 11. Tests to Add / Update

| Test class | Methods | Coverage |
|---|---|---|
| `MessageBodyHtmlBuilderTests` | `BuildMessageHtml_ForcePlainText_UsesPlainTextPart` (verbatim, no note); `BuildMessageHtml_ForcePlainText_NoPlainPart_ExtractsHtmlWithNote`; `BuildMessageHtml_ForcePlainText_False_UnchangedFromDefault` | Happy path + fallback + regression |
| `SettingsViewModelTests` | Round-trip `ReadAsPlainText` (load reflects config; Save writes it) | Persistence |
| `ConfigService` round-trip (existing suite) | Confirm `ReadAsPlainText` survives write→read | Config persistence |
| `XamlParseTests` | Existing test recompiles MainWindow + SettingsDialog with the new menu item/checkbox (no XamlParseException) | Regression |

## 12. Known Risks & Open Questions

| Risk | Prob. | Impact | Mitigation |
|---|---|---|---|
| Ctrl+Shift+H collides with an existing binding | Low | Minor | Verified against `docs/KEYBOARD-SHORTCUTS.md` — H (with Ctrl+Shift) is free. Registered via registry, so the user can rebind if desired. |
| A message with neither plain nor HTML body shows an empty body | Low | Minor | Plain-text branch handles empty text as today (empty focusable body); no note. |
| Settings change doesn't live-apply to the open message | Low | Minor | `OpenSettings` calls `RerenderReadingPaneAsync()` after `ApplySettings`. Covered by Scenario 5. |
| Toggle moves focus out of the body | Med | Major | Re-render path is the focus-preserving `RerenderReadingPaneAsync`/window re-render, not `ShowMessageBodyAsync`. Covered by Scenario 1. |

Open questions: none. Design (sticky + Settings) is decided; default hotkey proposed as
Ctrl+Shift+H and easily rebindable.

## 13. Implementation Guidance for AI

- The plain-text document builder already exists (`BuildPlainTextHtmlDocument`); reuse it. The only
  new logic is choosing the source text and whether to attach the "no plain-text version" note.
- Read the live flag at render time (config or `MainViewModel.ReadAsPlainText`); do not cache it in
  the builder.
- Follow CLAUDE.md: register the command (no hardcoded `PreviewKeyDown` branch), announce through
  `AccessibilityHelper.Announce` with `AnnouncementCategory.Result`, keep WebView2/Dispatcher out
  of the VM, and give the menu item/checkbox short accessible names.
- Highest-risk acceptance steps: Scenario 1 (focus preserved on toggle), Scenario 2 (fidelity +
  correct note), Scenario 4 (all three surfaces).
