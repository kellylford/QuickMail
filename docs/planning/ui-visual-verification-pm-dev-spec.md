# AI-Driven Visual Verification & UI Testing Harness — PM & Dev Specification

> Status: **Draft / exploratory.** A developer/CI tooling capability, not a shipping end-user feature. Written to be implementable as-is.
>
> **Tracking:** [Issue #180](https://github.com/kellylford/QuickMail/issues/180)
>
> **Depends on:** [Issue #175 — Debug Screenshot Capture](https://github.com/kellylford/QuickMail/issues/175) (`docs/planning/debug-screenshot-capture-pm-dev-spec.md`) provides the pixel-capture engine this harness drives. This spec is the **seeding + driving + orchestration + review** layer on top of it.

---

## Section 1: Executive Summary

QuickMail is built screen-reader-first by a developer who cannot see the screen. That workflow is excellent for accessibility and blind to a whole class of defects: a reading pane that renders blank, controls that overlap or lose their template, text clipped at 175% scale, a theme whose colors never applied, mojibake, a stray access-key underscore (#168). #175 lets a **human** turn on screenshotting and click around. This spec removes the human: it defines a harness that **seeds a deterministic fake mailbox in the app's own on-disk format, launches the real app straight to each major UI surface with the network turned off, captures a screenshot of each, and hands the images to an AI that flags anything obviously broken** — across every theme and text scale.

The result is a repeatable command (and a CI job / Claude Code workflow) that answers, without a sighted person: *"Does every major screen still render correctly in every theme?"* It does **not** replace the screen-reader acceptance walkthrough — it covers the orthogonal visual channel the developer cannot self-check.

**Design spine:** fidelity by *reuse* (write fixtures through the app's real persistence code; capture through #175), determinism by *construction* (fixed IDs, fixed clock, suppressed live state), and safety by *construction* (isolated throwaway profile, network hard-off, debug-gated hooks that are never user-reachable).

---

## Section 2: User Problem & Opportunity

### 2.1 Current state (verified)

| Surface | Today | Pain |
|---|---|---|
| Visual QA | None. #175 (capture) is drafted but manual and human-driven | Visual regressions invisible to a screen-reader-first workflow until a sighted human or external AI happens to look |
| Test data | `QuickMail.Tests` uses `StubServices` in-memory stubs; no way to produce a **real, populated `mail.db` + `accounts.json`** the shipping app will open | Cannot launch the actual app against realistic content without a live account and manual sync |
| Launch targeting | App always boots to its normal startup flow (`App.OnStartup` → `MainWindow.Show()`), then background-syncs | No way to say "open straight to the Theme Manager in Parchment Dark at 150% and screenshot it" |
| Isolation | `--profileDir <path>` exists and fully isolates file state | Good foundation — but credentials live in Windows Credential Manager and the app still tries to connect |

**Verification anchors:** profile file layout — `accounts.json` (`AccountService.cs:20`), `mail.db` (`LocalStoreService.cs:20`), `config.ini`/`hotkeys.json` (`ConfigService.cs:41–42`), plus `contacts.json`, `groups.json`, `flags.json`, `rules.json`, `views.json`, `templates.json`, `custom.lex`, `themes/`. Write API — `LocalStoreService.UpsertSummariesAsync` (`:270`), `UpsertDetailAsync` (`:547`), `UpsertCalendarEventAsync` (`:764`). Account shape — `Models/AccountModel.cs`. Startup + flags — `App.OnStartup` (`App.xaml.cs:25`), `--profileDir`/`--online`/`--feature`/`/debug` parsing (`App.xaml.cs:35–67, 199–202`; `ProfileContext.ParseProfileDir` `:33`), `MainWindow.Show()` (`App.xaml.cs:162`). Cache-first startup — the local store is read before `StartBackgroundSyncAsync` runs, so seeded content renders before any connection.

### 2.2 Target personas

- **Kelly (developer, primary).** Runs one command before merging a UI change and gets back "these screens are fine; this one looks broken — here's the image." No sighted helper, no Recall dependency.
- **CI (secondary).** A Windows job runs the harness on every PR touching `Views/`, `Styles/`, or `Controls/` and posts an AI verdict + the images as artifacts.
- **A future baseline-diff harness (v2).** Consumes the same run folder to diff a PR's images against a committed baseline.

### 2.3 Why now

We just merged theming (#179): six themes, retemplated controls, text scaling to 200%, a WebView2 reading pane that re-renders per theme. That is the largest **visual** surface change in the app's history and exactly what a screen-reader-first developer cannot self-verify. The blast radius justifies building the safety net now.

---

## Section 3: Design Principles

1. **Fidelity by reuse, not reimplementation.** Fixtures are written by calling the app's own `LocalStoreService`/`AccountService`; screenshots are taken by #175's capture service. Nothing hand-parses or hand-emits the DB format, so schema migrations (the `MessageSummary` table already carries 8 `ALTER TABLE` migrations) can never desync the fixtures.
2. **Determinism by construction.** Fixed GUIDs, a fixed injected clock, fixed message bodies, and suppression of every non-deterministic UI element (relative "today/yesterday" dates, the update-check banner, live sync status, animation) so a surface's image is byte-reproducible run to run and therefore diffable.
3. **Safety by construction.** The harness always runs against a **throwaway `--profileDir`**; automation mode **hard-disables all network** (IMAP/SMTP/Graph/OAuth/update check) so it can never touch a real mailbox or send mail; the launch hooks are **debug-gated and never user-reachable** (no menu item, no command, no palette entry).
4. **Real pixels, real app.** This is not a headless render. WPF + the out-of-process WebView2 reading pane must actually paint to be captured, so the harness launches the **shipping executable** on a real (or virtual) desktop. It is an integration harness, not a unit test.
5. **One surface per process (v1).** Each probe launches, drives to exactly one surface, captures, and exits. Clean state, crash isolation, and trivial determinism beat the efficiency of a single long-lived driven session (which is a v2 optimization).
6. **Catch "broken," not "different" (v1).** The AI review answers *"is this screen obviously broken?"* — blank pane, unstyled/overlapping controls, clipped/overlapping text, wrong theme colors, an error dialog, missing content. Pixel-exact regression diffing is a separate v2 concern.
7. **Complement, never replace, the AT walkthrough.** The harness owns the visual channel only. Screen-reader usability remains the developer's judgment; nothing here claims to validate it.

---

## Section 4: Feature Scope & Acceptance Criteria

### 4.1 In scope (v1)

| Capability | Shape | Notes |
|---|---|---|
| **Fixture generator** | `QuickMail.Fixtures` console tool: `dotnet run --project tools/QuickMail.Fixtures -- --out <dir> [--set default]` | Writes a complete isolated profile (accounts.json + mail.db + supporting JSON) via the app's real services |
| **Deterministic dataset** | A curated "default" fixture set | One account; Inbox + Sent + a custom folder; messages covering: read/unread, flagged, with-attachment, HTML body, plain body, very long subject, mailing-list, a calendar invite; plus seeded contacts, a flag definition, a saved view, a rule, a template |
| **Automation launch mode** | `--ui-probe <surface>[;<surface>…]` (implies `/debug` + network-off) | Drives to each surface, captures, exits with code 0/non-0 |
| **Surface router** | Maps a surface name → the existing code that opens it | `inbox`, `reading-pane`, `compose`, `settings-appearance`, `theme-manager`, `address-book`, `rules`, `saved-views`, `calendar`, `command-palette`, `folder-picker` |
| **Matrix knobs** | `--theme <id>`, `--text-scale <pct>`, `--capture-dir <path>` | Applied before the surface is shown |
| **Settle-then-capture** | Idle + WebView2 `NavigationCompleted` gate before the shot | Reuses #175's capture; adds a deterministic "surface ready" signal |
| **Orchestrator** | `scripts/ui-probe.ps1` (and an optional Claude Code workflow) | Seeds one profile, loops the probe plan launching the exe per entry, collects PNGs into a run folder, invokes the reviewer |
| **AI review** | Structured per-image checklist → PASS/FAIL/UNSURE + reason + image path | Claude vision (or any vision model); emits a Markdown/JSON report |

### 4.2 Explicitly out of scope (v1)

- **Replacing the screen-reader acceptance walkthrough.** Visual only.
- **Pixel-perfect regression / baseline diffing.** v2. v1 detects *broken*, not *changed*.
- **Real accounts, real network, real send.** Automation mode forbids all of it.
- **Exhaustive DPI/multi-monitor/resolution coverage.** v1 fixes one canonical resolution + DPI (e.g. 1920×1080 @ 100% Windows scaling); the app's own text-scale knob provides the scaling axis.
- **Every dialog and transient (tooltips, open menus, drag states).** v1 covers the major surfaces in §4.1; transient states that never reach a stable "settled" frame are deferred.
- **High Contrast rendering fidelity.** HC intentionally hands rendering to Windows (#179); capturing it is possible but low-value for *our* code and deferred.
- **Performance/timing assertions.** Not a perf harness.

### 4.3 Acceptance criteria

- Running the fixture generator into an empty dir yields a profile that, when the **shipping app** is launched with `--profileDir <that dir>` (no other flags), opens to a **populated inbox** with the seeded messages — no crash, no "add an account" empty state, and no network access required.
- `QuickMail.exe --ui-probe theme-manager --theme dark --text-scale 150 --profileDir <fixture> --capture-dir <run>` produces exactly one PNG of the Theme Manager, dark theme, 150% text, and exits 0 without contacting any server.
- The reading-pane probe's PNG shows the **rendered HTML message body** (WebView2 captured, not a blank rectangle).
- The orchestrator run over the default probe plan (surfaces × {Parchment, Parchment Dark} at 100% + a 150% pass) produces one labeled PNG per entry and an AI report naming any FAIL with its image.
- With no `--ui-probe`/`/debug`, none of the hooks exist: no new menu/command/endpoint, and a normal launch is byte-for-byte unchanged in behavior.
- The harness never writes outside its `--profileDir`/`--capture-dir` and never opens a socket in automation mode (verifiable: run with a network sniffer or a `--online`-off assertion).

---

## Section 5: Architecture & Technical Decisions

### 5.1 The three components

```
┌────────────────────┐   writes    ┌──────────────────────┐   launches   ┌───────────────────┐
│ Fixture Generator  │───────────▶ │ Isolated profile dir │ ───────────▶ │ QuickMail.exe     │
│ (real LocalStore/  │ accounts.json│ mail.db, *.json,     │ --profileDir │  --ui-probe <s>   │
│  AccountService)   │  + mail.db   │ themes/ …            │ --capture-dir│  (network OFF)    │
└────────────────────┘             └──────────────────────┘              └─────────┬─────────┘
                                                                                     │ one PNG per surface
                                              ┌──────────────────────┐   reads      ▼
                                              │  AI Reviewer          │◀────────  <run>/*.png
                                              │ (vision + checklist)  │
                                              └──────────┬───────────┘
                                                         ▼  report.md / report.json (PASS/FAIL/UNSURE)
                              ┌───────────────────────────────────────────────────────┐
                              │ Orchestrator: scripts/ui-probe.ps1 / Claude workflow    │
                              │ seed → loop(probe plan): launch+capture → review → report│
                              └───────────────────────────────────────────────────────┘
```

### 5.2 Key architectural decisions

**Decision A — Fixtures are written through the app's own persistence services, never hand-authored SQL/JSON.**
The generator references the QuickMail project and calls `LocalStoreService.UpsertSummariesAsync(...)`, `UpsertDetailAsync(...)`, `UpsertCalendarEventAsync(...)`, and `AccountService.Save(...)`/`ContactService`/`FlagService`/`ViewService`/`RuleService`/`TemplateService`. The `mail.db` schema (with its 8 `MessageSummary` migrations and `_v2` tables) is created by `LocalStoreService`'s own initializer. **Rationale:** the only format guaranteed to match the shipping app is the one the shipping app writes. Hand-rolled SQL would silently rot on the next migration. **Alternative rejected:** checking a pre-built `.db` into the repo — it would drift and hides the format from review.

**Decision B — A "default fixture set" is code, deterministic, and reviewable.**
The dataset is defined in C# as a list of typed `MailMessageSummary`/`MailMessageDetail`/`AccountModel`/etc. builders with **fixed GUIDs and fixed timestamps** supplied by an injected clock, not `Guid.NewGuid()`/`DateTime.Now`. **Rationale:** determinism (Principle 2) and easy diffing. The set is curated to exercise the visually risky cases: unread vs read rows, a flagged row (flag color bar), an attachment indicator, an HTML message and a plain message (reading-pane paths), a pathologically long subject (wrapping/clipping), a mailing-list message, and a calendar invite (the event card in the reading pane).

**Decision C — One debug-gated launch flag drives everything: `--ui-probe`.**
`--ui-probe <surface>[;<surface>…]` implies three things at once: (1) `/debug`-level gating so the hook cannot exist in a normal session, (2) **automation mode** (below), and (3) a post-startup instruction to the surface router. It composes with `--theme`, `--text-scale`, `--profileDir`, `--capture-dir`. **Rationale:** a single, obvious, non-user-reachable entry point; the flag parser already exists (`App.xaml.cs` `--feature` machinery) and this slots beside it.

**Decision D — Automation mode hard-disables the network, at the DI root.**
When `--ui-probe` is present, `App.OnStartup` wires **no-op / offline** implementations for `ImapService`, `SmtpService`, `OAuthService`, Graph, and skips `StartBackgroundSyncAsync` and the update check entirely — the same seam `--online` already toggles, inverted and hardened. The app runs pure cache-first against the seeded `mail.db`. **Rationale:** safety (can never touch a real account or send), determinism (no live data, no update banner), and speed. **Anchor:** `onlineMode` branch at `App.xaml.cs:67, 105`; background sync is already separated from initial load.

**Decision E — The Surface Router lives in `MainWindow` and reuses existing open-paths.**
A small `UiProbeDriver` (constructed only in automation mode) receives the parsed surface list and, once the main window has loaded and the initial cache load has completed, invokes the **same** code the user's menu/command would (`OpenThemeManager()`, the Settings command, `SelectMessageCommand` for the reading pane, `OpenAddressBook`, etc.). It never re-implements a surface. After opening a surface it awaits a **"settled"** signal, calls the #175 capture service for that window, then advances (or exits). **Rationale:** reuse = the probe exercises the real UI path, not a parallel one. **Alternative rejected:** a separate probe-only window factory — it would test code users never run.

**Decision F — "Settled" is an explicit, per-surface signal, not a sleep.**
For plain WPF windows: `Loaded` + one `DispatcherPriority.ApplicationIdle` turn (layout + first paint done). For the reading pane and compose preview: the WebView2 `NavigationCompleted` event (the HTML actually rendered). The router awaits the strongest signal available for the target surface before capturing. **Rationale:** eliminates flaky "screenshot came out blank because WebView2 hadn't painted" — the exact failure mode #175 calls out. A bounded timeout (e.g. 10 s) fails the probe loudly rather than capturing garbage.

**Decision G — Capture is #175's service, invoked programmatically.**
Automation mode constructs the real `ScreenshotCaptureService` (#175) with `Enabled = true` and, instead of relying on the `Window.Loaded` class handler alone, the router calls `Capture(window, label)` at the settled moment with a deterministic label (`<NN>-<surface>-<theme>-<scale>.png`). **Rationale:** the capture/WebView2-fidelity problem is already solved in #175; do not re-solve it. **This is why #175 is a hard dependency of Phase 2.**

**Decision H — The orchestrator is a thin script; the AI review is a structured prompt.**
`scripts/ui-probe.ps1` (POSIX-friendly variant optional) seeds one profile, then for each `(surface, theme, scale)` in a **probe plan** (a small JSON/PS array) launches the exe once and collects the PNG. It then calls the reviewer: for each image, an AI vision call with a fixed checklist returns `{surface, verdict: PASS|FAIL|UNSURE, reason, image}`. Output is `report.md` + `report.json`. A Claude Code **workflow** variant can fan the review out one-agent-per-image and synthesize. **Rationale:** keep orchestration deterministic and out of the model; keep judgment in the model.

### 5.3 Runtime mode compatibility

| Mode | Effect |
|---|---|
| Normal (no `--ui-probe`) | Router/automation not constructed; hooks absent; zero behavior change |
| `--ui-probe …` | Implies `/debug` gating + network-off + drive-to-surface + capture + exit |
| `--online` | **Mutually exclusive** with `--ui-probe`; if both given, `--ui-probe` wins and forces offline, with a `LogService.Debug` note |
| `--profileDir <path>` | Required in practice; the harness always points at a throwaway fixture dir |
| `/debug` alone | Enables #175's manual capture (unchanged); does not auto-drive |

### 5.4 Shared component audit (mandatory)

| Component | File | Change | Risk / mitigation |
|---|---|---|---|
| `App` DI root | `App.xaml.cs` | Parse `--ui-probe`/`--theme`/`--text-scale`/`--capture-dir`; in automation mode wire offline services, skip sync/update-check, construct `ScreenshotCaptureService` + `UiProbeDriver` | Guarded entirely by the flag. Normal path untouched. Verify a no-flag launch is unchanged. |
| `MainWindow` | `Views/MainWindow.xaml.cs` | Host `UiProbeDriver`; expose the existing open-paths it calls (already methods) | Driver only constructed in automation mode; reuses existing handlers, adds no user surface |
| `ScreenshotCaptureService` | (#175) | Consumed as-is; `Capture()` called by the driver | #175 is the dependency; no change beyond what #175 defines |
| `IThemeService` | `Services/ThemeService.cs` | `--theme`/`--text-scale` applied via existing `ApplyTheme`/`ApplyAppearance` before a surface shows | Public API already exists; no change |
| `LocalStoreService`/`AccountService`/etc. | `Services/*` | **Consumed** by the fixture tool; **no change** | Read-through of the real write API; if a method isn't public enough for the tool, widen minimally |
| Network services | `Imap/Smtp/OAuth/Graph` | Offline no-op variants selected in automation mode | Same seam as `--online`; interfaces already exist |

**Summary:** new code = one console tool project + one `UiProbeDriver` + flag parsing + orchestrator script + review prompt. Changed shipping code is confined to `App.xaml.cs` (flag-gated) and a thin driver hosted by `MainWindow`. No `ConfigModel`/`config.ini` change, no user-facing surface, no change to the persistence or theme APIs (only consumption).

---

## Section 6: The default fixture set (v1)

Written by `QuickMail.Fixtures` with a fixed clock `T0` (e.g. `2026-01-15T09:00:00Z`) so all "N hours/days ago" render identically:

- **Account:** one `AccountModel` — `DisplayName = "Test User"`, `Username = "test@example.com"`, `AuthType = Password`, `IsDefault = true`, dummy `ImapHost`/`SmtpHost` (never contacted in automation mode). No credential is stored; automation mode never needs one.
- **Folders:** Inbox, Sent, and one custom folder ("Project X") to exercise the folder tree and Move/Copy pickers.
- **Inbox messages (deterministic IDs `msg-0001…`):**
  - Unread, plain-text, short subject.
  - Read, HTML body with headings/list/link/blockquote (reading-pane HTML path + link rendering).
  - Unread, **flagged** (flag color bar + unread accent bar coexisting — a #179 visual detail).
  - Read, **with attachment** (attachment indicator + the attachments list).
  - Unread, **pathologically long subject** and long sender display name (wrapping/clipping/ellipsis).
  - A **mailing-list** message (`is_mailing_list`).
  - A message carrying a **calendar invite** (`calendar_ics`) so the reading pane shows the event card with Accept/Tentative/Decline.
- **Sent:** two messages (so "By Recipient" grouping and the Sent view have content).
- **Supporting state:** 3–4 contacts + one group (Address Book), one custom flag definition (Flags UI), one saved view (Saved Views menu + Manage Views), one rule (Rules Manager), one template (Insert Template picker), a couple of calendar events (Calendar list).

This single set lights up every surface in the probe plan. Additional sets (`--set empty` for empty-state screens, `--set huge` for virtualization/scroll) are trivial follow-ons.

---

## Section 7: Operator / harness walkthrough (mandatory)

*(This tool is CLI/AI-driven; the "walkthrough" is the operator + automation flow rather than a screen-reader path. The app's own accessibility is unchanged — see §8.)*

**Path: one-shot local run**
1. Operator runs `pwsh scripts/ui-probe.ps1` (no args → default probe plan).
2. Script creates a temp dir `T`, runs the fixture generator into `T`, and confirms `T\mail.db` and `T\accounts.json` exist.
3. For each `(surface, theme, scale)` in the plan, the script runs `QuickMail.exe --ui-probe <surface> --theme <theme> --text-scale <scale> --profileDir T --capture-dir T\shots` and waits for the process to exit (bounded, e.g. 30 s; a hang fails that entry, not the run).
4. The app boots offline against the fixture, applies theme+scale, opens the surface, waits for "settled," captures `NN-surface-theme-scale.png`, exits 0.
5. After the loop, the script invokes the reviewer over `T\shots\*.png` and writes `T\shots\report.md` + `report.json`.
6. Script prints a summary: `12 surfaces × 3 configs = 36 shots; 34 PASS, 1 FAIL (theme-manager @ dark: description box overlaps buttons), 1 UNSURE`. Non-zero exit if any FAIL.

**Path: CI**
1. On a PR touching `Views/`, `Styles/`, or `Controls/`, a Windows job runs the same script on a desktop-enabled runner (WPF/WebView2 need a session; use a runner with an interactive desktop or a virtual display).
2. PNGs + `report.md` are uploaded as artifacts; the job fails on any FAIL; the AI report is posted as a PR comment.

**Path: developer inspecting a single surface**
1. `QuickMail.exe --ui-probe reading-pane --theme heather --profileDir T --capture-dir out` → one PNG of a rendered HTML message in Heather. The developer (or an AI) looks at exactly that.

---

## Section 8: Accessibility Checklist (mandatory)

- **No new user-facing UI.** `--ui-probe` and friends are debug-gated launch flags with **no** menu item, command, palette entry, or settings row. Nothing to label, announce, or place in an F6 ring.
- **Shipping app accessibility is unchanged.** The surface router calls the *same* handlers the user's menu/command already invoke; it adds no controls and no `AutomationProperties` changes.
- **This harness does not assess accessibility.** It is explicitly the *visual* net. The screen-reader acceptance walkthrough (spec §9/§10 of the theming spec, and every feature's own walkthrough) remains the human-owned gate; this spec must not be cited as covering AT usability.
- **Safety announcement inheritance.** If a surface is opened in automation mode, any announcements the real handler makes still fire (harmless, unheard) — the router does not suppress or fake them, so the real code path is exercised faithfully.

---

## Section 9: AI review — the checklist

Per image, the reviewer is asked a fixed, closed checklist (vision model), returning a verdict + the specific failed check:

1. **Blank/empty render** — is the main content area (esp. the reading pane) empty/white/black when it should show content?
2. **Missing styling** — do controls look like default/unstyled WPF (wrong for a themed app), or is a whole region unpainted?
3. **Theme applied** — do background/text/accent colors match the *named* theme for this shot (Parchment = warm off-white; Parchment Dark = dark; Ember/Fjord/Heather accent tints)? A dark-theme shot that looks light is a FAIL.
4. **Text legibility** — clipped, truncated-without-ellipsis, overlapping, or unreadably low-contrast text?
5. **Layout integrity** — controls overlapping, off-window, zero-sized, or grossly misaligned; scrollbars where content should fit; a dialog rendered without its buttons?
6. **Error state** — an exception dialog, "unable to load," a red error banner, or a `XamlParseException` fallback?
7. **Text artifacts** — stray access-key underscores (`_Reply` shown literally, #168), mojibake, `{Binding …}` shown literally, `#RRGGBB` leaking into UI text.
8. **Content presence** — for a seeded surface, is the expected fixture content visible (e.g. the long-subject message row, the flagged row's color bar, the attachment indicator)?

Output schema (per image): `{ surface, theme, scale, verdict: PASS|FAIL|UNSURE, failedChecks: [...], note, image }`. `UNSURE` is a first-class outcome (the model must not guess PASS). The run's exit code is non-zero if any `FAIL`. A Claude Code **workflow** variant fans this out one agent per image and synthesizes, which also gives cheap adversarial double-checking of any FAIL.

---

## Section 10: Success Metrics

- **Coverage:** one legible, correctly-labeled PNG per (surface × theme × scale) in the plan; the default plan covers ≥ 10 surfaces across ≥ 2 themes + a 150% pass.
- **Fidelity:** reading-pane and compose-preview shots contain rendered WebView2 HTML, never blank.
- **Determinism:** re-running the harness on the same commit produces visually identical images (enables future diffing).
- **Detection:** seeding a deliberate defect (e.g. hardcode a wrong color, remove a control template) makes the AI review FAIL that surface and name it. *(This is the harness's own acceptance test.)*
- **Safety:** zero network connections in automation mode; nothing written outside the run/profile dirs; no hook reachable without the flag.
- **Independence (the real metric):** Kelly can answer "did I visually break anything?" for a UI change **without a sighted person**.

---

## Section 11: Implementation Phases

### Phase 0 (dependency): Debug Screenshot Capture (#175)
The capture engine (`ScreenshotCaptureService`, WebView2-correct via `PrintWindow`/`CopyFromScreen`). If not yet built, build #175 Phase 1 first — this harness's capture step depends on it.

### Phase 1: Fixture generator (no UI change to the app)
**Goal:** `tools/QuickMail.Fixtures` writes a complete, deterministic profile via the real services; launching the shipping app with `--profileDir <it>` shows a populated inbox offline.
**Deliverables:** the console project; the default fixture set as code; a fixed-clock seam; `QuickMailFixturesTests` (round-trip: generate → `LocalStoreService` reads back the expected rows).
**Exit gate:** a human (or the next phase) launches `--profileDir <fixture>` and sees seeded mail with no crash and no network.
**Risk:** a persistence method the tool needs is `internal`. → widen minimally or add a thin fixture-seam. **Duration:** 4–6 h.

### Phase 2: Automation launch mode + surface router + auto-capture
**Goal:** `--ui-probe <surface> --theme --text-scale --capture-dir` boots offline, drives to the surface, waits for "settled," captures via #175, exits.
**Deliverables:** flag parsing in `App.xaml.cs`; offline-service wiring in automation mode; `UiProbeDriver` (surface map → existing handlers; settled-signal awaiting incl. WebView2 `NavigationCompleted`); deterministic capture labels; bounded timeouts → non-zero exit on hang.
**Tests:** headless-ish unit tests for the surface-name→action map and the settled-signal state machine (the actual pixel path is exercised by the orchestrator, not xUnit).
**Exit gate:** the reading-pane probe PNG shows rendered HTML; a bogus surface name exits non-zero with a clear message. **Duration:** 6–10 h.

### Phase 3: Orchestrator + probe plan + run folder
**Goal:** `scripts/ui-probe.ps1` seeds once, loops the plan launching the exe per entry, collects PNGs, prints a summary, exits non-zero on any missing/failed shot.
**Deliverables:** the script; a default `ui-probe-plan.json`; run-folder layout; a `--plan <file>` override.
**Exit gate:** one command yields a full run folder of labeled PNGs on a dev machine. **Duration:** 3–5 h.

### Phase 4: AI review integration
**Goal:** the reviewer turns a run folder into `report.md`/`report.json` with PASS/FAIL/UNSURE per surface; wired into the script and (optionally) a Claude Code workflow.
**Deliverables:** the §9 checklist prompt; the JSON schema; a synthesis step; optional CI wiring + PR-comment posting.
**Exit gate:** the §10 "deliberate defect" test — the review FAILs the broken surface and names it. **Duration:** 3–5 h.

### Phase 5 (v2, deferred): Baseline diff
Commit a baseline image set per theme; the reviewer additionally diffs and reports *changed* regions, escalating only meaningful visual deltas to the model. Deferred until v1 proves out.

---

## Section 12: Files to Create / Modify

### Create
| File | Purpose |
|---|---|
| `tools/QuickMail.Fixtures/QuickMail.Fixtures.csproj` + `Program.cs` | Fixture generator CLI (references QuickMail) |
| `tools/QuickMail.Fixtures/DefaultFixtureSet.cs` | The deterministic dataset as code |
| `QuickMail/Services/UiProbeDriver.cs` (or `Views/` helper) | Surface router + settled-signal + capture invocation (automation-only) |
| `scripts/ui-probe.ps1` | Orchestrator |
| `scripts/ui-probe-plan.json` | Default probe plan (surface × theme × scale) |
| `scripts/ui-review-prompt.md` | The §9 checklist prompt for the reviewer |
| `.claude/workflows/ui-probe.js` (optional) | Claude Code workflow variant of the review fan-out |
| `QuickMail.Tests/QuickMailFixturesTests.cs`, `UiProbeDriverTests.cs` | Round-trip + router/state-machine tests |

### Modify
| File | Change |
|---|---|
| `App.xaml.cs` | Parse `--ui-probe`/`--theme`/`--text-scale`/`--capture-dir`; automation-mode wiring (offline services, skip sync/update, construct capture + driver); all flag-gated |
| `Views/MainWindow.xaml.cs` | Host `UiProbeDriver` in automation mode; ensure surface open-paths are callable (they already are) |
| Possibly `Services/LocalStoreService.cs` et al. | Widen a method's visibility *only if* the fixture tool needs it |
| `docs/USER-GUIDE.md` | **No change** — this is developer tooling, not a user feature |

---

## Section 13: Known Risks & Open Questions

### 13.1 Risks
| Risk | Prob. | Impact | Mitigation |
|---|---|---|---|
| WebView2 not painted at capture → blank reading pane | Med | Major | Settle on `NavigationCompleted` (Decision F) + #175's `CopyFromScreen` fallback; the reading-pane shot is a Phase 2 exit gate |
| CI runner has no interactive desktop → WPF/WebView2 won't render | High (on default runners) | Major | Require a desktop-enabled Windows runner or a virtual display; document it; the harness is *headful* by design (Principle 4) |
| A persistence method needed by the fixture tool is `internal` | Med | Minor | Widen minimally or add a small, reviewed fixture seam (Phase 1 risk note) |
| Non-determinism leaks (relative dates, update banner, animation) → unstable images | Med | Moderate | Fixed clock; automation mode suppresses update-check/sync; disable animations in automation mode |
| The AI review over- or under-flags | Med | Moderate | Closed checklist (§9); `UNSURE` is allowed; workflow variant double-checks FAILs; the deliberate-defect test tunes sensitivity |
| Screenshots contain fixture "PII" | Low | Low | Content is synthetic (`test@example.com`), run dirs are throwaway; still local-only, never transmitted by the app |
| Hooks drift into a user-reachable state | Low | Major | Single flag, debug-gated, no menu/command/endpoint; a test asserts no `--ui-probe` handling without the flag |

### 13.2 Open questions (decide before build)
- **Canonical resolution/DPI for v1?** *(Proposed: 1920×1080 @ 100% Windows scaling; the app's text-scale knob is the scaling axis.)*
- **One surface per process vs one driven session?** *(Proposed: one-per-process for v1 determinism/isolation; driven session is a v2 speed optimization.)*
- **Probe-plan breadth for v1?** *(Proposed: all §4.1 surfaces × {Parchment, Parchment Dark} at 100%, plus the full set at 150% — enough to catch theme + scale breakage without a combinatorial explosion.)*
- **Reviewer: inline script call vs Claude Code workflow?** *(Proposed: ship the script path first; add the workflow variant for adversarial double-checking of FAILs.)*
- **CI now or local-only first?** *(Proposed: land Phases 1–4 local; add the CI job once the runner-desktop story is settled.)*

---

## Section 14: Implementation Guidance for AI

- **Reuse is the whole strategy.** Do **not** hand-emit SQL/JSON for fixtures (call the real services) and do **not** re-solve capture (use #175). If either temptation appears, stop — it means the seam is wrong.
- **Automation mode is a safety boundary.** Network-off and debug-gating are non-negotiable: the harness must be incapable of touching a real account, sending mail, or existing in a normal session. Wire the offline services at the DI root, not with scattered `if` checks.
- **Settle, don't sleep.** A fixed `Thread.Sleep` before capture is a defect; await the real "surface ready" signal (idle for WPF, `NavigationCompleted` for WebView2) with a bounded timeout that fails loudly.
- **Determinism first.** Fixed clock, fixed IDs, suppressed live/relative UI. If a shot isn't reproducible, the future baseline-diff phase is impossible.
- **Prove it with a planted bug.** Before calling the harness done, hardcode a wrong color or delete a control template and confirm the AI review FAILs exactly that surface (the §10 metric). A harness that never catches a real break is worthless.
- **Keep it developer-only.** No user guide entry, no menu, no command. This exists to serve the developer's independence, not the end user.
