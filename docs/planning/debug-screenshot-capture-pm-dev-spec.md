# Debug Screenshot Capture — PM & Dev Specification

> Status: **Draft / exploratory.** Not committed to the roadmap. Written up so the idea is captured with enough rigor to build later if we decide to. This is a developer/diagnostic tool, not a shipping end-user feature.

---

## Section 1: Executive Summary

QuickMail has no built-in way to capture what its UI actually looks like as the user moves through it. Visual quirks — the stray access-key underscores in issue #168, oddly wrapped subjects, redundant folder counts — are exactly the kind of thing a sighted spot-check or an AI image pass catches easily, but that a screen-reader-first developer can walk past for months. This spec adds an opt-in, **debug-only** capability that saves a PNG each time a new window (or a major new in-window surface) appears, into an easy-to-find folder in the profile directory. The developer can then run those images through an external AI to look for visual anomalies. The feature is deliberately hard to leave on by accident: it exists only in `/debug` builds, must be switched on manually each session, never persists, and shouts its presence in every window title bar while active.

**Motivating anecdote (from the person who requested it):** "I likely already have this going on — I'm on a Copilot+ PC and Recall captures screens — but there is no public API to reach those images or bulk-export them. I want a first-class, local, exportable version scoped to QuickMail."

---

## Section 2: User Problem & Opportunity

### 2.1 Current state (verified)

| Surface | Today | Pain | Who feels it |
|---|---|---|---|
| Visual QA | No screenshot capability of any kind in the app | Visual regressions are invisible to a screen-reader-first workflow until a sighted user or external AI happens to look | The developer (Kelly), screen-reader users |
| `/debug` mode | `App.xaml.cs:60` sets `LogService.DebugMode = true`; only effect today is verbose text logging to `quickmail.log` | Debug mode captures *words*, never *pixels* | Developer diagnosing UI issues |
| Copilot+ Recall | OS-level screen history exists but has no public read/export API | Cannot programmatically or in bulk pull QuickMail-only frames for analysis | Developer |
| Issue #168 class of bug | Literal underscores shipped and lingered; caught only when an external tester and an external AI looked at screenshots | Slow, luck-dependent discovery of visual defects | Everyone downstream of a visual bug |

**Verification anchors:** `/debug` handling — `App.xaml.cs:60–62`; debug gate — `LogService.DebugMode` (`Services/LogService.cs:31`, used at `LogService.cs:49` and `:109`); window inventory — 27 `Window` subclasses under `QuickMail/Views/`; main-window title — computed `MainViewModel.WindowTitle` (`ViewModels/MainViewModel.cs:482`, invalidated via several `[NotifyPropertyChangedFor(nameof(WindowTitle))]` attributes).

### 2.2 Target personas

- **Kelly (developer, primary).** Screen-reader user building the app. Wants a local, exportable stream of "here is what each screen looked like" to feed to an external AI for visual-quirk review, without depending on Recall or a sighted helper.
- **A sighted contributor doing a visual pass.** Turns it on, clicks through the app, ends up with a labeled folder of every screen to skim.
- **A future automated visual-diff harness (v2, out of scope now).** Could consume the same folder to diff releases.

### 2.3 Why now

Not urgent. The #168 episode simply proved the value: a screenshot pass found in minutes what months of screen-reader use did not. Capturing the design now means it can be built quickly if the itch returns.

---

## Section 3: Design Principles

1. **Impossible to enable by accident, obvious when enabled.** Debug-only + manual per-session opt-in + non-persistent + a title-bar warning on every open window.
2. **Local and private by construction.** Images are written to disk only. QuickMail never transmits them anywhere. The developer runs their own external analysis.
3. **Silent and non-intrusive while running.** Capturing must never steal focus, flash the screen, block the UI thread, or announce itself per-shot. The only ongoing signal is the title-bar warning.
4. **Capture structural "new UI," not every twitch.** A new window or major surface is worth a frame; every keystroke, focus move, or message-to-message navigation is not.
5. **Low blast radius in the codebase.** One central hook, not edits scattered across 27 window files. Zero behavior change when disabled or in non-debug builds.

---

## Section 4: Feature Scope & Acceptance Criteria

### 4.1 In scope (v1)

| Feature | Setting / Shortcut | Default | Notes |
|---|---|---|---|
| Screenshot on new window shown | — | — | Class handler on `Window.Loaded`, captured at idle priority |
| Screenshot on major in-window surface | — | — | Explicit `Capture()` calls: reading-pane message rendered, new tab opened, command palette (palette is itself a Window, so auto-covered) |
| Session toggle in Settings | Checkbox "Capture screenshots of new windows (debug)" | **Off**, **not persisted**, **debug-only visibility** | Flips `IScreenshotCaptureService.Enabled` for this session only |
| Title-bar warning | — | Shown on every window while enabled | Suffix e.g. `  — ⚠ SCREENSHOTS ON` |
| Screenshots folder | "Open screenshots folder" button (debug-only, in Settings) | — | `<profileDir>\debug-screenshots\<session>\` |
| Toggle announcements | — | force-announced | Meta-announcement, bypasses user announcement prefs |

### 4.2 Explicitly out of scope (v1)

- **Non-debug builds.** The service is never instantiated and the settings row never appears unless `LogService.DebugMode` is true.
- **Persistence.** The toggle is in-memory; every launch starts Off. Nothing is written to `config.ini`.
- **PII redaction.** Screenshots will contain real email content. No blurring/masking. Documented as the developer's responsibility.
- **In-app AI analysis.** QuickMail only *produces* the images. Running them through an AI happens outside the app.
- **Per-control / per-focus / per-keystroke capture.** No capture on focus change, selection change, or message-to-message navigation.
- **De-duplication by image hash, video/GIF capture, periodic timed capture, occluded/background-window capture.** Deferred or rejected.
- **Cross-session galleries, thumbnails, an in-app viewer.** Use File Explorer.

### 4.3 Acceptance criteria

- With `/debug`, a Settings row appears; without `/debug`, it does not.
- Turning it on: every window title gains the warning suffix within the same tick; a PNG of the current window(s) is produced; a force-announcement is heard.
- Opening any window (dialog, compose, message, address book, etc.) writes one labeled PNG.
- The reading pane's rendered message (WebView2 content) is present in its capture — not a blank rectangle.
- Turning it off (or closing the app) removes the warning suffix / stops capture. Relaunch starts Off.
- With capture Off or in a non-debug build, there is zero measurable behavior change and no new files.

---

## Section 5: Architecture & Technical Decisions

### 5.1 Key architectural decisions

**Decision A — Capture with Win32 `PrintWindow(PW_RENDERFULLCONTENT)`, fall back to `Graphics.CopyFromScreen`.**

**Alternatives:**
1. `RenderTargetBitmap.Render(visual)` — pure WPF, no interop. **Con: cannot capture WebView2.** The reading pane is an out-of-process WebView2 HWND composited in separate "airspace"; `RenderTargetBitmap` renders it as blank/black. Since the reading pane is the single most valuable surface to inspect, this alternative is disqualifying on its own. (This limitation was confirmed first-hand while diagnosing #168 with a `RenderTargetBitmap` probe — it captured buttons and labels fine but would not have captured HTML body content.)
2. `PrintWindow` with `PW_RENDERFULLCONTENT` (0x00000002) — captures the full window including child HWNDs like WebView2 on Windows 10 1903+ / Windows 11, even if partially occluded. **Chosen primary.**
3. `Graphics.CopyFromScreen(window screen rect)` — grabs literal on-screen pixels; always includes WebView2, but captures whatever is on top if the window is occluded. **Chosen fallback** (a freshly shown window is on top, so occlusion is rare at capture time).

**Rationale:** WebView2 fidelity is the whole point. `PrintWindow(PW_RENDERFULLCONTENT)` is the only in-window API that reliably includes it; `CopyFromScreen` is a robust backstop for GPU surfaces that occasionally return black from `PrintWindow`. Detection: if the `PrintWindow` bitmap is entirely one color, retry with `CopyFromScreen`.

**Decision B — Single class-level hook, not per-window edits.**

Register once at startup:
```csharp
EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
    new RoutedEventHandler(OnAnyWindowLoaded));
```
`Loaded` is a routed event, so one registration fires for **all** 27 window types without touching their files. The handler schedules the actual capture at `DispatcherPriority.Background`/`ApplicationIdle` so layout and first paint have completed. (`ContentRendered` would be ideal but is a plain CLR event and cannot be class-handled; idle-after-Loaded is the equivalent that keeps the hook central.)

**Alternatives:** a common `Window` base class (touches 27 files, risky), or a Win32 CBT hook (overkill, fragile). Rejected.

**Decision C — Debug-only service, no-op object when disabled.**

`App.OnStartup` constructs a real `ScreenshotCaptureService` only when `LogService.DebugMode` is true; otherwise the DI root wires a `NullScreenshotCaptureService` (all methods no-op, `Enabled` always false, and the Settings row bound to `IsDebug=false` collapses). No branching sprinkled through callers.

**Decision D — Session-only toggle lives on the service, never in `ConfigModel`/`config.ini`.**

The Settings checkbox binds to a plain `SettingsViewModel` property that forwards to `IScreenshotCaptureService.Enabled`. It is intentionally **not** added to `ConfigModel`, so `ConfigService` read/write is untouched and the value cannot persist. (Contrast with the announcement settings, which *are* persisted — this one deliberately is not.)

**Decision E — Title-bar warning.**

The service exposes `bool Enabled` and an `EnabledChanged` event. On change, it walks `Application.Current.Windows` and applies/removes the suffix; the `Window.Loaded` class handler applies it to any window opened while enabled. For `MainWindow`, whose `Title` is bound to the computed `MainViewModel.WindowTitle`, the suffix is incorporated in the getter (guarded by the service flag) so a `WindowTitle` refresh doesn't drop it; static-title windows get the suffix appended directly.

### 5.2 Runtime mode compatibility

| Mode | Effect |
|---|---|
| Normal (no `/debug`) | Service not created; feature invisible; zero cost |
| `/debug` | Feature available, still Off until toggled |
| `--online` | No interaction — capture is pure UI, never touches `LocalStoreService` |
| `--profileDir <path>` | Screenshots go under the **active** profile dir, so isolated test profiles keep their own capture folders |

### 5.3 Code reuse and duplication risks

- Capture + save logic lives once in `ScreenshotCaptureService`. No duplication.
- Title-suffix logic lives once in the service (plus the one guarded branch in `WindowTitle`). Avoid re-implementing per window.

### 5.4 Shared component audit (mandatory)

| Component | File | Other consumers | Change needed | Risk / mitigation |
|---|---|---|---|---|
| `App` DI root | `App.xaml.cs` | Owns every service | Construct `ScreenshotCaptureService` (or Null) in `OnStartup`, dispose in `OnExit`, register the `Window.Loaded` class handler | Low. Guarded by `LogService.DebugMode`. Null object when off. |
| `MainViewModel.WindowTitle` | `ViewModels/MainViewModel.cs:482` | Bound to `MainWindow` `Title` | Append warning suffix when service enabled | Suffix only when enabled; existing title logic unchanged otherwise. Verify title still correct with capture Off. |
| `SettingsDialog` + `SettingsViewModel` | `Views/SettingsDialog.xaml`, VM | All app settings | Add one debug-only checkbox + "Open folder" button, bound to service, **not** to `ConfigModel` | Row `Visibility` collapses when not debug. `ConfigService` round-trip unaffected (new value never enters config). Verify `SettingsViewModelTests` still green. |
| All 27 `Window` subclasses | `Views/*.xaml.cs` | — | **None** (central class handler) | The point of Decision B: no per-file edits, so no per-window regression surface. |
| `ProfileContext` | `Services/ProfileContext.cs` | Path resolution across app | Read the active profile dir to place `debug-screenshots\` | Read-only use; no change to `ProfileContext` itself. |

**Summary:** This feature modifies `App.xaml.cs`, `MainViewModel.WindowTitle`, and the Settings dialog/VM, and adds one new service (+ null variant). It touches no window code-behind and no `ConfigModel`/`ConfigService`, so persistence and per-window behavior are unaffected.

---

## Section 6: Keyboard Walkthrough (Mandatory)

### Path: Enable capture (debug build)

1. User launches `QuickMail.exe /debug`, opens **Settings** (existing path). **Expected:** In the settings list there is a new row group, e.g. "Diagnostics (debug)". Focus can Tab to a checkbox "Capture screenshots of new windows". Screen reader announces "Capture screenshots of new windows, check box, not checked."
2. User presses Space to check it. **Expected:** Checkbox becomes checked. A force-announcement is heard: "Screenshot capture on. QuickMail is saving screen images to disk this session." Every open window's title bar now ends with " — ⚠ SCREENSHOTS ON" (the main window's title updates immediately).
3. User Tabs to the "Open screenshots folder" button and presses Enter. **Expected:** File Explorer opens on `…\debug-screenshots\<session>\`. Focus behavior of Settings is otherwise unchanged.
4. User closes Settings and navigates the app normally. **Expected:** No per-action announcements, no flashing, no focus theft. Opening any window silently adds a PNG to the folder.

### Path: A window is captured

1. With capture on, user presses Enter on a message (Window mode) / opens Compose / opens the Address Book. **Expected:** The window opens and behaves exactly as normal. Within ~1 second a file `NNNN-<WindowName>.png` appears in the session folder. No sound, no focus change attributable to capture.
2. User reads a message in the reading pane. **Expected:** When the message finishes rendering, one `NNNN-ReadingPane-<subject-slug>.png` is written and the HTML body is visible in it (not blank).

### Path: Disable / end session

1. User returns to Settings and unchecks the box. **Expected:** Force-announcement "Screenshot capture off." All title bars drop the warning suffix. No further files are written.
2. User quits and relaunches (with `/debug`). **Expected:** The checkbox is **unchecked** again (non-persistent). No warning suffix anywhere until re-enabled.

### Path: Non-debug build

1. User launches without `/debug`, opens Settings. **Expected:** The Diagnostics row is **absent**. Nothing captures; no folder is created.

---

## Section 7: Accessibility Checklist (Mandatory)

- **AutomationProperties.Name** — One new checkbox: "Capture screenshots of new windows" (short label, no role/hint baked in). One new button: "Open screenshots folder". Both debug-only.
- **AnnouncementCategory** — Toggle on/off is a **meta-announcement** about a diagnostic mode, so it uses `AccessibilityHelper.Announce(..., AnnouncementCategory.Status, force: true)` (bypasses user announcement prefs, exactly like the "Custom announcements toggled" meta-announcement pattern in the codebase). **No per-capture announcement** — capturing is silent by principle #3.
- **Screen reader browse mode** — No WebView2 interaction added; the reading pane is only *read* (pixels), never re-focused.
- **Focus restoration** — The capture path never changes focus. The "Open folder" button is a normal button; Settings focus flow is unchanged.
- **F6 ring** — No new panes. No F6 changes.
- **Checkbox / radio groups** — One standalone checkbox; not a radio group.
- **Color-only information** — The title-bar warning uses text ("SCREENSHOTS ON"), not color alone; the ⚠ glyph is decorative, the words carry the meaning.

**Answer:** Introduces no new panes, no WebView2 browsing, one debug-only checkbox and one button. Announcements: on/off as `Status` + `force:true`. No F6 changes. Capture is silent.

---

## Section 8: Acceptance Walkthrough (Mandatory)

### Scenario: Enable and capture (debug)

**Setup:** Launch `QuickMail.exe /debug`, main window open.

1. Open Settings, find and check "Capture screenshots of new windows". **Verify:** Checkbox checks; force-announcement heard; main window title now ends with the warning suffix.
2. Press "Open screenshots folder". **Verify:** Explorer opens the session folder (may be empty or contain a MainWindow shot).
3. Open Compose (Ctrl+N). **Verify:** Within ~1s a `*-ComposeWindow.png` appears and shows the compose form.
4. Select a message so the reading pane renders it. **Verify:** A `*-ReadingPane-*.png` appears and the **email body text is visible** in the image (WebView2 captured, not blank).
5. Uncheck the setting. **Verify:** Force-announcement "off"; suffix removed from all titles; no new files when opening another window.

### Scenario: Non-persistence & non-debug

1. With capture on, quit and relaunch with `/debug`. **Verify:** Checkbox is unchecked; no suffix.
2. Relaunch **without** `/debug`, open Settings. **Verify:** The Diagnostics row is absent; no `debug-screenshots` folder is created during the session.

### Scenario: No-regression on shared components

1. In a **non-debug** build, exercise Settings normally and save. **Verify:** Settings persist as before; `config.ini` contains no screenshot key. (`SettingsViewModelTests` green.)
2. With capture **off**, confirm main-window title is exactly the normal computed title (no stray suffix).

### Scenario: Edge cases

1. Open and close many windows rapidly. **Verify:** No UI stall; files are written without blocking navigation (capture runs off the UI thread after grabbing pixels); a per-window/label debounce prevents duplicate frames < ~750 ms apart.
2. Let a session exceed the safety cap (e.g., 500 images). **Verify:** Capture stops writing and logs one `LogService.Debug` line; the app keeps running normally.

---

## Section 9: Success Metrics

- **Behavioral:** After a click-through of the app with capture on, the session folder contains one legible PNG per distinct window/surface, correctly labeled.
- **Fidelity:** Reading-pane captures include rendered HTML body content.
- **Safety:** Feature is unreachable without `/debug`; never persists; every active window is visibly flagged.
- **No regressions:** Settings tests unchanged; `config.ini` unchanged; title correct when disabled; no measurable overhead when off.
- **Utility:** The developer can select-all in the folder and hand the images to an external AI in one step (the original goal — a Recall replacement scoped to QuickMail).

---

## Section 10: Implementation Phases

### Phase 1: Service + capture engine (headless)
**Goal:** `IScreenshotCaptureService` / `ScreenshotCaptureService` / `NullScreenshotCaptureService` exist; `Capture(Window, label)` writes a correct PNG (incl. WebView2) to the session folder; `Enabled`, `EnabledChanged`, session-folder creation, debounce, and the safety cap all work.
**Deliverables:** `Services/IScreenshotCaptureService.cs`, `Services/ScreenshotCaptureService.cs` (Win32 `PrintWindow`/`CopyFromScreen` interop), null variant; DI wiring in `App.xaml.cs`; `IDisposable` (cancel/flush any background save on exit).
**Tests:** `ScreenshotCaptureServiceTests` — path/foldering, filename slugging, debounce logic, cap logic (capture the pixel-grab behind a seam so tests don't need a real HWND).
**Risk:** `PrintWindow` returns black for some GPU surfaces → fallback to `CopyFromScreen`; verify on the WebView2 reading pane specifically. **Duration:** 3–5 h.

### Phase 2: Central window hook + in-window capture points
**Goal:** One `Window.Loaded` class handler captures every window at idle; explicit `Capture()` calls added for reading-pane render-complete and new-tab-open.
**Deliverables:** class-handler registration in `App.xaml.cs`; ~2–3 explicit call sites (reading-pane `NavigationCompleted`, tab open).
**Tests:** `XamlParseTests` unaffected; a headless test that raising `Loaded` on a stub window triggers one capture (via the service seam).
**Risk:** Loaded firing before WebView2 paints → idle-priority schedule + the render-complete explicit call. **Duration:** 2–3 h.

### Phase 3: Settings toggle + title-bar warning + announcements
**Goal:** Debug-only checkbox and "Open folder" button in Settings; toggling flips the service and force-announces; warning suffix appears/disappears across all windows including `MainWindow`.
**Deliverables:** `SettingsDialog.xaml` (+VM) rows guarded by an `IsDebug` flag; suffix logic in the service + guarded branch in `MainViewModel.WindowTitle`.
**Tests:** `SettingsViewModelTests` (toggle forwards to service; nothing written to config); title-suffix unit test on `WindowTitle`.
**Risk:** `WindowTitle` recompute dropping the suffix → incorporate in getter. **Duration:** 2–3 h.

---

## Section 11: Files to Create / Modify

### Create
| File | Purpose | Lines (est.) |
|---|---|---|
| `Services/IScreenshotCaptureService.cs` | Interface (`Enabled`, `EnabledChanged`, `Capture`, `OpenFolder`) | 20–30 |
| `Services/ScreenshotCaptureService.cs` | Capture engine, foldering, debounce, cap, Win32 interop | 200–300 |
| `Services/NullScreenshotCaptureService.cs` | No-op for non-debug | 20–30 |
| `docs/…` acceptance notes (optional) | — | — |

### Modify
| File | Changes | Lines (est.) |
|---|---|---|
| `App.xaml.cs` | Construct service (debug vs null), dispose on exit, register `Window.Loaded` class handler | +25 |
| `ViewModels/MainViewModel.cs` | Append warning suffix in `WindowTitle` when enabled | +8 |
| `ViewModels/SettingsViewModel.cs` | `IsDebug` flag + `ScreenshotCaptureEnabled` passthrough + `OpenScreenshotsFolderCommand` | +25 |
| `Views/SettingsDialog.xaml` | Debug-only Diagnostics row (checkbox + button) | +20 |
| Reading-pane render-complete + tab-open sites | Explicit `Capture()` calls | +6 |

---

## Section 12: Tests to Add

| Test Class | Test Methods | Coverage |
|---|---|---|
| `ScreenshotCaptureServiceTests` | Session folder created under active profile; filename slug is filesystem-safe; debounce suppresses < 750 ms repeats; cap stops after N; `Enabled=false` writes nothing | Happy path + guards |
| `SettingsViewModelScreenshotTests` | Toggle forwards to service; `IsDebug=false` hides row; no `config.ini` key written | Setting behavior + persistence guard |
| `MainViewModelTitleTests` | Suffix present iff service enabled; base title otherwise | Title logic |

---

## Section 13: Known Risks & Open Questions

### 13.1 Risks
| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| `PrintWindow` returns black for WebView2 on some GPUs | Medium | Major (defeats the purpose) | `CopyFromScreen` fallback on all-one-color detection; test on the reading pane explicitly (Phase 1 exit gate) |
| Capture blocks/stutters the UI thread | Medium | Major | Grab pixels fast on UI thread, encode+save on a background task; debounce; idle-priority scheduling |
| Screenshots leak PII if the folder is shared | High (inherent) | Major | Debug-only, session-only, title-bar warning, local-only, never transmitted; documented as developer responsibility |
| Disk fills during a long session | Low | Minor | Per-session cap (e.g., 500) + one debug log line when hit |
| Title suffix dropped on `WindowTitle` recompute | Medium | Minor | Incorporate suffix in the getter, not by external mutation |

### 13.2 Open questions (decide before build)
- **Trigger breadth:** v1 = new windows + reading-pane render + tab open. Confirm we do **not** also want view-mode/filter changes captured. *(Proposed: leave them out; revisit if the folder feels sparse.)*
- **Suffix wording:** " — ⚠ SCREENSHOTS ON" vs " [CAPTURING SCREENSHOTS]". *(Proposed: the former; decide at build.)*
- **Cap value:** 500 images/session — right ballpark? *(Proposed: 500, configurable constant.)*
- **Manual capture hotkey:** a debug-only "capture now" key (e.g., a registered command) would help grab transient states (open menus, tooltips) that never raise `Loaded`. *(Proposed: defer to v2 to keep v1 tight; note it as the most likely first follow-up.)*

---

## Section 15: Implementation Guidance for AI

- **Latitude:** You choose the exact interop shape (`PrintWindow` P/Invoke signatures, `System.Drawing` vs `Windows.Graphics.Capture`). `System.Drawing.Common` + GDI `PrintWindow`/`CopyFromScreen` is the low-dependency path and is fine for a debug tool; do not add a heavy capture dependency.
- **Normative constraints:** Silent capture (no per-shot announcement, no focus change) and the debug-only gate are non-negotiable — they are the safety story. If `PrintWindow` cannot capture WebView2 in your environment, stop and confirm the `CopyFromScreen` fallback approach before proceeding, because WebView2 fidelity is the feature's reason to exist.
- **Do not** add anything to `ConfigModel`/`ConfigService` — non-persistence is deliberate.
- **After build:** the highest-risk acceptance steps are §8 Scenario 1 step 4 (WebView2 body visible in the capture) and §8 Scenario "Non-persistence" (toggle resets on relaunch). Exercise those first.
