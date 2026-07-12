# Windows Mail Client Registration — PM + Dev Spec

> Tracking issue: [#241](https://github.com/kellylford/QuickMail/issues/241)
> Status: **Draft — pending approval (Session 1)**
> Template: `docs/planning/SPEC-TEMPLATE.md`

---

## Section 1: Executive Summary

QuickMail is a full email client, but Windows has no record that it exists: it does not appear in **Settings → Apps → Default apps**, and a `mailto:` link clicked in a browser, Word, or any other app can never open QuickMail. This spec registers QuickMail as a Windows mail client following Microsoft's client-registration rules, so a user can *choose* QuickMail as their default mail app, and so `mailto:` links open a pre-populated QuickMail compose window. Because QuickMail ships via Velopack (a per-user, no-admin installer), all registration is written to `HKEY_CURRENT_USER` and hooked into the Velopack install/uninstall lifecycle. Windows 8+ forbids an app from *forcing* itself to be the default (the association is hash-protected), so v1 makes QuickMail **choosable** and offers a one-key jump to the Default Apps page — it never hijacks the default.

---

## Section 2: User Problem & Opportunity

### 2.1 Current state (verified)

| Surface | Today | Pain | Who feels it |
|---|---|---|---|
| `mailto:` link in a browser / Office / PDF | Opens Outlook, Store "Mail", or a browser picker — **never QuickMail** | User who lives in QuickMail is bounced to a different, often less accessible client | All QuickMail users, esp. screen-reader users |
| Settings → Apps → Default apps | QuickMail is **absent from the list** | Cannot be selected as the mail default even if the user wants to | All users |
| "Compose from a link" | No entry point — QuickMail only composes from inside the running app | Friction: copy address, alt-tab to QuickMail, new message, paste | Everyone following addresses on the web |
| App launch arguments | `Main` handles `/debug`, `--profileDir`, `--updateFeed` only (`App.xaml.cs:29`, verified) | No `mailto:` argument parsing exists | — |

**Verification notes:**
- `App.xaml.cs:29-39` — `Main` builds `VelopackApp` then `new App()`; no `mailto:` / protocol-argument handling.
- `App.xaml.cs:33` — only one Velopack hook today: `OnBeforeUninstallFastCallback`. There is **no** first-run / after-install hook, so there is nowhere registration currently happens.
- Velopack installs to `%LocalAppData%` per-user (see `docs/INSTALLER.md`) — confirms **no admin**, so HKLM is unavailable; HKCU is the correct hive.

### 2.2 Target personas

1. **Keyboard/screen-reader primary user** — uses QuickMail *because* it is accessible. Wants `mailto:` links to land in QuickMail, not get dumped into a client they find harder to use. Sets QuickMail as default once, benefits everywhere.
2. **Web-heavy researcher** — follows author/contact `mailto:` links dozens of times a day. Wants a link to open a ready-to-send compose window with the address filled in.
3. **"Make it my everything" adopter** — has moved off Outlook and wants QuickMail to be the system mail app, visible in Default Apps like any first-class client.
4. **Cautious user** — does *not* want their default silently changed. Wants QuickMail to be *available* to choose, but only if they opt in.

### 2.3 Why now

- Compose infrastructure is mature (`ComposeWindow` / `ComposeViewModel`, reply/template variants already exist) — a `mailto:`-seeded compose is a thin new entry point over proven code.
- Velopack lifecycle hooks already exist in `Main` (`App.xaml.cs:32`), so adding install/uninstall registration is a localized change.
- Accessibility payoff is direct and concrete (persona 1).

---

## Section 3: Design Principles

1. **Register, never hijack.** v1 makes QuickMail *choosable*. It must not attempt to write `MAILTO\UserChoice` or otherwise force the default — that is hash-protected on Win8+ and hostile to the user. The most we do is *offer* to open the Default Apps page.
2. **Per-user, no admin.** Everything lands in `HKEY_CURRENT_USER`. No operation may require elevation; a standard-user install must fully succeed.
3. **Clean install ↔ clean uninstall.** Every key we create at install is removed at uninstall. No orphaned registry entries, no dangling `RegisteredApplications` pointer to a deleted exe.
4. **The `mailto:` entry point reuses existing compose.** No parallel "lite compose" — a `mailto:` launch opens the real `ComposeWindow` seeded from the parsed URL, so accessibility and keyboard behavior are identical to normal compose.
5. **Graceful when not-yet-configured.** Registration failing (locked-down machine, roaming profile quirk) must never block app launch or crash; it degrades to "not registered" with a log line.

---

## Section 4: Feature Scope & Acceptance Criteria

### 4.1 In scope (v1)

| Feature | Setting / Shortcut | Default | Notes |
|---|---|---|---|
| HKCU mail-client registration | Written at install / first run | On (part of install) | `Clients\Mail\QuickMail` + `Capabilities` + `RegisteredApplications` |
| `mailto:` ProgId + command | — | — | `QuickMail.Url.mailto` → `"QuickMail.exe" "%1"` |
| `mailto:` argument parsing in `Main` | — | — | Detect a `mailto:` arg, route to compose |
| Compose window seeded from `mailto:` | — | — | Parses `to`, `cc`, `bcc`, `subject`, `body` per RFC 6068 |
| Deregistration at uninstall | Velopack `OnBeforeUninstall` hook | On | Removes all keys created above |
| "Set QuickMail as default mail app" helper | Menu item (Settings/Help area) + Command Palette | — | Opens `ms-settings:defaultapps` deep-link; shows current-default status |
| First-run offer (optional, see §13.2 Q1) | One-time prompt or status announcement | TBD | Point user at the helper, don't auto-change |

### 4.2 Explicitly out of scope (v1)

- **Simple MAPI provider** — the COM/DLL surface behind "Send to → Mail recipient" and Office's built-in "Email" button. Large, separate effort; a `mailto:` handler does *not* satisfy MAPI-based callers. Deferred to a future spec.
- **`.eml` / `.msg` file-type association** — opening a saved message file. Separate ProgId + `FileAssociations`; deferred.
- **Forcing / silently setting the default** — impossible cleanly on Win8+ and against Principle 1. We only deep-link to Settings.
- **Per-account "reply from" selection on a `mailto:` launch** — v1 seeds compose from the app's current/default account; account-picking for mailto is deferred.
- **HKLM (all-users) registration** — Velopack is per-user; no admin path in v1.

### 4.3 Acceptance criteria

- After install, QuickMail appears in **Settings → Apps → Default apps** and can be assigned to `mailto:` / "Email".
- After the user sets it as default, clicking a `mailto:you@example.com?subject=Hi` link opens a QuickMail compose window with To=`you@example.com`, Subject=`Hi`, focus in the body (or first empty field).
- Uninstall removes every key; re-scan of `HKCU\Software\RegisteredApplications` shows no `QuickMail` value and `Clients\Mail\QuickMail` is gone.
- Nothing requires elevation; a standard user completes all of the above.

---

## Section 5: Architecture & Technical Decisions

### 5.1 Registry layout (HKCU)

All paths under `HKEY_CURRENT_USER`. `<EXE>` = full path to the installed `QuickMail.exe` (resolved at runtime from `Environment.ProcessPath`, since Velopack's versioned install path changes on every update — **do not hard-code**).

```
HKCU\SOFTWARE\Clients\Mail\QuickMail
   (Default)                    = "QuickMail"                     ; display name
   HKCU\...\QuickMail\DefaultIcon
      (Default)                 = "<EXE>,0"
   HKCU\...\QuickMail\shell\open\command
      (Default)                 = "\"<EXE>\""
   HKCU\...\QuickMail\Capabilities
      ApplicationName           = "QuickMail"
      ApplicationDescription    = "Keyboard-first, accessible email client."
      ApplicationIcon           = "<EXE>,0"
      HKCU\...\Capabilities\UrlAssociations
         mailto                 = "QuickMail.Url.mailto"

HKCU\SOFTWARE\Classes\QuickMail.Url.mailto
   (Default)                    = "URL:MailTo Protocol"
   "URL Protocol"               = ""                              ; empty value, presence matters
   DefaultIcon\(Default)        = "<EXE>,0"
   shell\open\command\(Default) = "\"<EXE>\" \"%1\""

HKCU\SOFTWARE\RegisteredApplications
   QuickMail                    = "SOFTWARE\\Clients\\Mail\\QuickMail\\Capabilities"
```

After writing, broadcast `WM_SETTINGCHANGE` with `lParam = "Software\Clients\Mail"` (and Windows re-reads associations). This is best-effort.

**Why this shape:** `RegisteredApplications` + `Capabilities\UrlAssociations` is the modern Default Programs contract (what makes the app appear in the Settings list). The `Clients\Mail\QuickMail` key with `shell\open\command` is the legacy client registration Windows still reads for the "Email" client slot. The separate `QuickMail.Url.mailto` ProgId is what actually receives the `%1` URL when a `mailto:` link is invoked. All three are needed.

### 5.2 Key architectural decisions

**Decision A: Registration runs at install/first-run via a Velopack hook, and is written to HKCU only.**
- Alternatives: (1) write at *every* app launch — self-healing but noisy and racy; (2) MSIX declarative manifest — not how QuickMail ships (Velopack).
- Rationale: Velopack is per-user (HKLM unavailable without admin). Add `.OnFirstRun(...)` / after-install registration and pair it with the existing `OnBeforeUninstallFastCallback` for cleanup. To be self-healing after updates (exe path changes each version), **also re-assert registration once during normal `OnStartup`** if the stored `shell\open\command` path doesn't match the current `Environment.ProcessPath`. Cheap idempotent write; fixes the post-update stale-path problem.

**Decision B: A new `MailClientRegistrationService` (with `IMailClientRegistrationService`) owns all registry I/O.**
- Follows the "every service has an interface in `Services/I*.cs`" convention. Keeps `Registry` calls out of `App`/VMs. Methods: `Register()`, `Unregister()`, `IsRegistered()`, `IsCurrentDefault()`, `EnsureUpToDate()`.
- The uninstall hook runs in a **hookless process context** (no DI, no `OnStartup`) — same constraint as `LaunchUninstallDataPrompt` (`App.xaml.cs:51`). So `Unregister()` must be callable as a plain static path that does not depend on the service graph. Provide a `static void UnregisterStatic()` the hook calls directly, mirroring the existing detached-uninstall pattern.

**Decision C: `mailto:` launch reuses `ComposeWindow` / `ComposeViewModel`, seeded by a parsed `MailtoRequest`.**
- Parse in a pure helper `MailtoParser` (testable, no UI). `Main` detects an arg starting with `mailto:` (case-insensitive), constructs the app, and after startup opens a compose window from the parsed request instead of (or in addition to) the main window — see §5.5 for the single-instance question.
- Rationale: Principle 4 — identical accessibility/keyboard behavior to normal compose.

**Decision D: We do not set the default; we deep-link to `ms-settings:defaultapps`.**
- A `SetDefaultRequested` event on the VM → View opens the URI (existing "open in default browser / shell" pattern). Also surface `IsCurrentDefault()` so the menu item can read "Set as default mail app" vs. "QuickMail is your default mail app."

### 5.3 Runtime mode compatibility

Registration touches only the registry and the compose window — **no `LocalStoreService` calls** — so `--online` and `--profileDir` are unaffected. The `mailto:` compose path uses whatever account context the app already establishes at startup; in `--online` mode it composes from IMAP/SMTP exactly as normal compose does. `--profileDir` does not change registration (registration is machine-user-global, not per-profile) — **noted as an open question**, see §13.2 Q2.

| Mode | LocalStore used by this feature? | Registration behavior |
|---|---|---|
| Normal | No | Full register at install + self-heal at startup |
| `--online` | No | Same |
| `--profileDir <path>` | No | Same registration; compose uses that profile's accounts (see Q2) |

### 5.4 Shared component audit (mandatory)

| Component | File | Other consumers | Change needed | Risk |
|---|---|---|---|---|
| `App.Main` | `App.xaml.cs:29` | Sole process entry point | Detect `mailto:` arg; add first-run register hook | Must not break `/debug`, `--profileDir`, `--updateFeed` parsing or normal launch |
| Velopack hook chain | `App.xaml.cs:32` | Uninstall data prompt | Add register-on-install; add unregister to uninstall hook | Hook process has no DI — use static path (Decision B) |
| `ComposeWindow` / `ComposeViewModel` | `Views/ComposeWindow.xaml(.cs)`, `ViewModels/ComposeViewModel.cs` | New message, Reply, Reply-All, Forward, Templates | Add a construction path seeded from `MailtoRequest` (To/Cc/Bcc/Subject/Body) | Must not regress existing compose entry points; seed via existing settable properties, not a new parallel constructor if avoidable |
| `MainWindow` open sequence | `MainWindow.xaml.cs` / `MainViewModel` | App startup | If launched with `mailto:`, decide whether main window also shows (§5.5) | Focus/first-window behavior for screen readers |
| Command Registry | `MainViewModel.RegisterCommands` | All shortcuts | Register "Set as default mail app" command (category `Settings`, no default key) | Must appear in palette + keyboard-customization dialog (house rule) |
| Menu (Settings or Help) | `MainWindow.xaml` | — | Add menu item bound to the new command | `AutomationProperties.Name` = short label only |

If a `mailto:` compose needs data the existing compose properties don't expose, that gap is a design decision to resolve **before coding**, not during.

### 5.5 Single-instance & launch behavior (must decide before coding)

When a `mailto:` link fires while QuickMail is **already running**, Windows launches a *second* `QuickMail.exe "mailto:…"`. Two options:

- **Option 1 (recommended): single-instance relay.** New process detects a running instance (named `Mutex` + a lightweight IPC — named pipe or `WM_COPYDATA`), forwards the `mailto:` string to it, and exits; the running instance opens the compose window. Prevents a second full app/second sync from spinning up. More work.
- **Option 2: allow a second lightweight instance** that opens only a compose window against shared config/credentials. Simpler, but two instances hitting the same SQLite store / credential manager / sync is risky given the app's existing single-instance assumptions.

**Recommendation:** Option 1. This is called out as the single largest implementation risk (§13.1).

**Verified (2026-07-11):** QuickMail **does not** enforce app-level single-instance today. A grep for `Mutex` / single-instance guards across `QuickMail/` finds only a *window*-level guard for the Theme Manager (`MainWindow.xaml.cs:4475`), nothing at the process level in `Main`. Confirmed empirically — launching the app while an instance is already open starts a second full instance (second sync, second SQLite/credential access). So Option 1 is **net-new work**: this feature must *introduce* the mutex + IPC relay; there is no existing mechanism to reuse. This also means the two instances that run today already share the store without an explicit guard — Phase 4 should be treated as adding the guard the app has never had, and its blast radius is wider than just `mailto:` (see §13.1).

---

## Section 6: Keyboard Walkthrough (Mandatory)

### Path A: Set QuickMail as the default mail app

1. User opens the menu and selects **Set as default mail app** (or invokes it from the Command Palette). **Expected:** Windows **Settings → Default apps** opens focused (via `ms-settings:defaultapps`); screen reader announces the Settings page. QuickMail does not change anything itself. If custom announcements are on, a `Hint` fires before the jump: "Opening Windows Default Apps. Choose QuickMail for Email."
2. User assigns QuickMail as the Email default in Settings, returns to QuickMail (Alt+Tab). **Expected:** No crash, no state change in QuickMail. Next time the menu opens, the item reads "QuickMail is your default mail app" (from `IsCurrentDefault()`), announced as a normal menu label.

### Path B: Follow a `mailto:` link (QuickMail already running)

1. In a browser, user activates a `mailto:alex@example.com?subject=Lunch&body=Hi%20Alex` link. **Expected:** QuickMail comes to the foreground; a compose window opens. Screen reader announces the compose window; focus lands in the **body** (or the first empty required field — match existing compose focus behavior). To = `alex@example.com`, Subject = `Lunch`, Body = `Hi Alex`.
2. User Tabs / Shift+Tabs. **Expected:** Standard compose field order (To, Cc, Bcc, Subject, Body, toolbar) — identical to a normal new message. No new/stranded controls.
3. User presses Escape. **Expected:** Compose window's existing close behavior (discard-confirm if dirty). Focus returns per existing compose rules.

### Path C: Follow a `mailto:` link (QuickMail not running)

1. User activates a `mailto:` link with the app closed. **Expected:** QuickMail launches. Once startup reaches a ready state, the compose window opens seeded as in Path B. **Design decision (§13.2 Q4):** does the main window also appear, or only compose? Recommended: main window loads normally in the background, compose is the focused/foreground window — so a screen-reader user isn't dropped into a bare compose with no app context.

### Path D: `mailto:` with no address / malformed URL

1. User activates a bare `mailto:` (no address) or a malformed value. **Expected:** Compose opens with empty To (or whatever parsed cleanly); no crash. Malformed params are skipped, not fatal. If nothing parsed, it's just a blank compose — acceptable.

### Path E: Install / uninstall (no user keyboard interaction, listed for completeness)

1. Install completes. **Expected:** Registry keys written to HKCU (§5.1); QuickMail now listed in Default Apps. No prompt unless the first-run offer (Q1) is approved.
2. Uninstall runs. **Expected:** All keys removed; QuickMail disappears from Default Apps.

---

## Section 7: Accessibility Checklist (Mandatory)

- **AutomationProperties.Name (new):** the menu item — short label only: `"Set as default mail app"` (and the dynamic `"QuickMail is your default mail app"` state). No role words, no shortcut text.
- **AnnouncementCategory:**
  - Path A step 1 — "Opening Windows Default Apps. Choose QuickMail for Email." → **Hint** (instructional; gated by `AnnounceHints`).
  - `mailto:` compose opened — rely on the **existing** compose-window announcement; **no new announcement** unless testing shows the compose-open announcement doesn't fire on this launch path, in which case add a **Status** announce "Compose window opened."
  - First-run offer (if approved, Q1) — **Hint**, `force:false`.
- **WebView2 browse mode:** the `mailto:` compose reuses the existing compose editor — no new WebView2 browsing behavior.
- **Focus restoration:** compose window follows existing compose focus-return rules. For a `mailto:` launch into an already-running app, verify focus returns sensibly to the previously focused window on close (existing behavior — verify, don't assume).
- **F6 ring:** no new panes in `MainWindow`. Compose window's own F6 ring is unchanged.
- **Radio/checkbox groups:** none introduced.
- **Color-only information:** the "is default" state is conveyed by menu **text**, never color.

---

## Section 8: Acceptance Walkthrough (Mandatory — run in Session 3)

### Scenario 1: Registration appears in Default Apps
**Setup:** Fresh install (or run register path once).
1. Open **Settings → Apps → Default apps**, search "QuickMail". **Verify:** QuickMail is listed; an "Email" / `mailto` association slot is offered.
2. Assign QuickMail to Email. **Verify:** Settings accepts it (no error). 

### Scenario 2: `mailto:` link, app running
**Setup:** QuickMail running; QuickMail set as default mail app; a browser open.
1. Activate `mailto:test@example.com?subject=Hello&cc=b@example.com&body=Line1`. **Verify:** QuickMail foregrounds, compose opens, To=`test@example.com`, Cc=`b@example.com`, Subject=`Hello`, Body=`Line1`, focus in body. Screen reader announces the compose window.
2. Tab through fields. **Verify:** Standard compose order; no stray controls.
3. Escape. **Verify:** Existing discard/close behavior; focus returns correctly.

### Scenario 3: `mailto:` link, app closed
**Setup:** QuickMail not running.
1. Activate `mailto:test@example.com`. **Verify:** App launches, compose opens seeded with To. Main-window-visibility matches the Q4 decision. **No second sync storm / no duplicate instance issues** (Scenario 6).

### Scenario 4: Malformed / bare `mailto:`
1. Activate bare `mailto:`. **Verify:** Blank compose opens, no crash.
2. Activate `mailto:%%%bad`. **Verify:** No crash; bad params skipped.

### Scenario 5: Existing compose entry points — no regression
1. New message (existing shortcut). **Verify:** Works unchanged.
2. Reply / Reply-All / Forward / from Template. **Verify:** Each still seeds correctly; no field regressions from the new seed path.

### Scenario 6: Single-instance behavior (§5.5)
1. With app running, fire two `mailto:` links quickly. **Verify:** No second full instance; each opens a compose in the running app (or per the chosen option). No SQLite/credential contention error in `quickmail.log`.

### Scenario 7: Uninstall cleanup
1. Uninstall QuickMail. **Verify (via `reg query`):** `HKCU\Software\RegisteredApplications` has no `QuickMail` value; `HKCU\Software\Clients\Mail\QuickMail` and `HKCU\Software\Classes\QuickMail.Url.mailto` are gone.

### Scenario 8: Post-update self-heal
1. Simulate an update (exe path changes). Launch normally. **Verify:** `shell\open\command` now points at the new exe path; a `mailto:` link still opens the current version.

### Scenario 9: `--online` mode
1. Launch with `--online`, fire a `mailto:` link. **Verify:** Compose opens and can send via SMTP; no LocalStore crash.

---

## Section 9: Success Metrics

- **Behavioral:** QuickMail is selectable as the Email default; a `mailto:` link opens a correctly-seeded compose.
- **Keyboard-centric:** the whole flow (set default → follow link → compose → send/escape) works keyboard-only.
- **No regressions:** all existing compose entry points and their tests pass unchanged; startup-arg parsing (`/debug`, `--profileDir`, `--updateFeed`) unaffected.
- **Clean lifecycle:** install writes keys; uninstall removes them; update self-heals the exe path.
- **Accessibility:** screen-reader user follows a `mailto:` link and lands in a correctly-announced compose window.

---

## Section 10: Implementation Phases

### Phase 1: `MailtoParser` + `MailClientRegistrationService` (no wiring)
**Goal:** Pure, testable parsing and registry read/write exist, unused.
**Deliverables:** `Services/IMailClientRegistrationService.cs`, `Services/MailClientRegistrationService.cs` (incl. `static UnregisterStatic()`), `Helpers/MailtoParser.cs`, `Models/MailtoRequest.cs`.
**Tests:** `MailtoParserTests` (to/cc/bcc/subject/body, URL-decoding per RFC 6068, malformed, bare); `MailClientRegistrationServiceTests` (register→IsRegistered true→unregister→false, using a **redirected/test HKCU subtree or a fake registry abstraction** so tests don't pollute the real hive).
**Risk:** tests writing to the real registry. Mitigation: inject the base key path (default `HKCU`), point tests at a temp subkey and delete it in teardown.
**Duration:** 3–4 h.

### Phase 2: Install / uninstall / self-heal lifecycle
**Goal:** Registration happens at install and self-heals at startup; uninstall removes everything.
**Deliverables:** `App.xaml.cs` — add first-run/after-install register hook; call `UnregisterStatic()` from the uninstall hook alongside the existing data prompt; call `EnsureUpToDate()` in `OnStartup`. Wire the service into the DI root.
**Tests:** service-level (Phase 1 covers logic); manual install/uninstall in Session 3 (Scenarios 1, 7, 8).
**Risk:** uninstall hook has no DI (`App.xaml.cs:51` pattern) — must use the static path. Broadcast `WM_SETTINGCHANGE` best-effort.
**Duration:** 3–4 h.

### Phase 3: `mailto:` launch → compose
**Goal:** A `mailto:` argument opens a seeded compose window.
**Deliverables:** `App.Main` arg detection; compose-seeding path in `ComposeViewModel`/`ComposeWindow`; main-window-visibility decision (Q4).
**Tests:** `ComposeViewModelMailtoTests` (seed from `MailtoRequest` populates fields); manual Scenarios 2–5, 9.
**Risk:** compose seed path regressing existing entry points. Mitigation: reuse existing settable properties; Scenario 5.
**Duration:** 4–5 h.

### Phase 4: Single-instance relay (§5.5)
**Goal:** A `mailto:` while running forwards to the existing instance; no second app.
**Deliverables:** single-instance guard (mutex) + IPC relay (named pipe / `WM_COPYDATA`); handler that opens compose in the running instance.
**Tests:** manual Scenario 6; unit-test the relay message encode/decode if feasible.
**Risk:** highest-risk phase; depends on whether single-instance already exists. Verify first (§13.1).
**Duration:** 4–6 h.

### Phase 5: "Set as default" helper + menu
**Goal:** Menu item + palette command open Default Apps and reflect current-default state.
**Deliverables:** `CommandRegistry` registration (category `Settings`, no default key); menu item; `SetDefaultRequested` event → View opens `ms-settings:defaultapps`; `IsCurrentDefault()` label.
**Tests:** `CommandRegistryTests` (command registered); manual Path A / Scenario 1.
**Duration:** 2–3 h.

---

## Section 11: Files to Create / Modify

### Create
| File | Purpose | Lines (est.) |
|---|---|---|
| `Services/IMailClientRegistrationService.cs` | Service contract | 15–25 |
| `Services/MailClientRegistrationService.cs` | HKCU register/unregister/self-heal + static unregister | 150–220 |
| `Helpers/MailtoParser.cs` | RFC 6068 `mailto:` → `MailtoRequest` | 60–100 |
| `Models/MailtoRequest.cs` | Parsed fields (To/Cc/Bcc/Subject/Body) | 20–30 |
| `QuickMail.Tests/MailtoParserTests.cs` | Parser coverage | 80–120 |
| `QuickMail.Tests/MailClientRegistrationServiceTests.cs` | Register/unregister round-trip (redirected key) | 60–100 |
| `QuickMail.Tests/ComposeViewModelMailtoTests.cs` | Seed compose from request | 50–80 |

### Modify
| File | Changes | Lines (est.) |
|---|---|---|
| `App.xaml.cs` | `mailto:` arg detect; install register hook; uninstall unregister; startup self-heal | +40 |
| `ViewModels/ComposeViewModel.cs` | Seed-from-`MailtoRequest` path | +30 |
| `ViewModels/MainViewModel.cs` | Register "Set as default mail app" command; `IsCurrentDefault` label | +25 |
| `Views/MainWindow.xaml` | Menu item | +5 |
| `Views/MainWindow.xaml.cs` | `SetDefaultRequested` → open `ms-settings:defaultapps` | +10 |
| DI root (`App.xaml.cs` `OnStartup`) | Wire `MailClientRegistrationService` | +5 |

---

## Section 12: Tests to Add

| Test Class | Test Methods | Coverage |
|---|---|---|
| `MailtoParserTests` | to only; to+subject+body; cc/bcc; multiple recipients; percent-decoding; bare `mailto:`; malformed params; empty | Happy + edge |
| `MailClientRegistrationServiceTests` | Register→IsRegistered; Unregister→!IsRegistered; EnsureUpToDate rewrites stale path; UnregisterStatic removes keys | Round-trip on redirected HKCU subtree |
| `ComposeViewModelMailtoTests` | Seed populates To/Cc/Bcc/Subject/Body; empty request → blank compose | Happy + empty |
| `CommandRegistryTests` (existing) | New "Set as default mail app" command registered under `Settings` | Registration presence |

---

## Section 13: Known Risks & Open Questions

### 13.1 Risks

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| Second `QuickMail.exe` per `mailto:` collides on SQLite/credentials/sync | High | Blocker | Phase 4 single-instance relay. **No guard exists today (verified §5.5)** — this is net-new. Prefer `mailto:`-only relay (Q3 option b) so the existing multi-instance dev/test workflow is preserved |
| Adding an app-wide single-instance guard breaks the "run a second copy" workflow AI tests rely on | Medium | Major | Scope the guard to `mailto:` launches only (Q3 option b); a plain launch still starts a fresh instance |
| Post-update exe path goes stale, `mailto:` opens old/invalid path | High | Major | Self-heal `EnsureUpToDate()` at every `OnStartup` (Decision A) |
| Tests pollute the real registry | Medium | Major | Inject base key path; tests use temp subkey + teardown |
| Uninstall hook can't reach DI graph | Medium | Major | `UnregisterStatic()` (Decision B), mirrors `App.xaml.cs:51` pattern |
| Windows doesn't refresh associations until re-login | Low | Minor | Broadcast `WM_SETTINGCHANGE`; document as best-effort |
| User expects "set as default" to actually set it | Medium | Minor (UX) | Clear label + Hint that Windows requires the user's choice; can't be bypassed on Win8+ |

### 13.2 Open questions (resolve before approval)

1. **First-run offer?** Should a fresh install *offer* to set QuickMail as default (via the helper), or stay silent until the user finds the menu item? *Proposed:* silent by default; add a one-time, dismissible Hint the first time the user opens compose, respecting `AnnounceHints`. Needs Kelly's call.
2. **`--profileDir` interaction.** Registration is global-per-user, but a `--profileDir` user may run multiple isolated profiles. A `mailto:` launch has no `--profileDir` arg → uses the default profile. Acceptable for v1? *Proposed:* yes; document that `mailto:` always targets the default profile.
3. **Single-instance mechanism.** ~~Does QuickMail already enforce single-instance?~~ **Resolved (verified 2026-07-11): no app-level single-instance exists** (only a Theme-Manager window guard at `MainWindow.xaml.cs:4475`). Phase 4 must add a mutex + IPC relay from scratch. **New sub-question for Kelly:** introducing an app-level single-instance guard changes existing behavior — today you *can* run two instances (and AI test runs rely on launching a fresh instance next to your open app). Options: (a) full single-instance (second launch always relays to the first — cleanest for `mailto:`, but breaks the "run a second copy" workflow); (b) single-instance **only** for `mailto:` launches (a `mailto:` arg relays to a running instance; a plain launch still starts a new instance — preserves your test workflow). *Proposed: option (b).*
4. **Main window on `mailto:` cold launch.** Show main window + compose, or compose only? *Proposed:* load main window normally, foreground compose — better for screen-reader context.
5. **Icon resource.** `<EXE>,0` assumes the primary app icon is index 0. Verify the packed exe's icon index; adjust if needed.

---

## Section 14: Appendix — Keyboard Reference

| Key / Action | Effect | Notes |
|---|---|---|
| Menu → **Set as default mail app** | Opens Windows Default Apps | No default hotkey; discoverable via Command Palette |
| `mailto:` link (system-wide) | Opens seeded QuickMail compose | Requires QuickMail chosen as default in Settings |
| Compose Escape | Existing discard/close | Unchanged from normal compose |

No new global QuickMail hotkeys are introduced (the `mailto:` trigger is a Windows-level protocol invocation, not a QuickMail shortcut).

---

## Section 15: Implementation Guidance for AI

### 15.1 Adjustments you're expected to make
- The registry base path is injectable so tests can redirect it; you decide the exact abstraction (a `RegistryKey` parameter vs. a thin interface). Keep production default = `Registry.CurrentUser`.
- You decide `MailtoRequest`'s shape (record vs. class) and how multiple `to`/`cc` addresses are represented; match whatever `ComposeViewModel` already expects for recipients.
- Compose seeding: prefer setting existing public properties over adding a new constructor, **unless** the existing entry points make that awkward — if so, add an internal seed method and document why.

### 15.2 When to ask for clarification
- **Phase 4:** single-instance is confirmed absent (§5.5). Scope the relay to `mailto:` launches only unless Kelly chooses full single-instance (§13.2 Q3) — a plain launch must still start a fresh instance so the existing dev/test workflow keeps working. If you find yourself about to make *every* launch relay, stop and confirm.
- If seeding compose from `mailto:` requires exposing data the existing compose API doesn't (§5.4), stop and raise it — that's a design gap, not an implementation detail.
- The keyboard walkthrough (§6) is normative. If `mailto:` compose focus can't match existing compose focus behavior, stop and ask.

### 15.3 Acceptance walkthrough preview
Highest-risk Session-3 steps for this feature:
- **Scenario 6** (single-instance) — most likely to expose real bugs.
- **Scenario 8** (post-update self-heal) — the stale-path trap.
- **Scenario 7** (uninstall cleanup) — orphaned keys are easy to miss.

---

## Section 16: References

- [How to Register an Internet Browser or Email Client With the Windows Start Menu](https://learn.microsoft.com/en-us/windows/win32/shell/start-menu-reg) — `Clients\Mail`, `shell\open\command`, `WM_SETTINGCHANGE`.
- Default Programs / `RegisteredApplications` + `Capabilities\UrlAssociations` model (modern Settings → Default apps contract).
- RFC 6068 — the `mailto:` URI scheme (fields, percent-encoding).
- **Win8+ constraint:** `HKCU\...\URLAssociations\MAILTO\UserChoice` is hash-protected; apps cannot programmatically set the default — the user must choose it in Settings.
- QuickMail internals: `App.xaml.cs:29-80` (Velopack hooks, uninstall-prompt hookless pattern), `docs/INSTALLER.md` (per-user Velopack install).
