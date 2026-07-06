# In-App Bug Reporting (No GitHub Sign-In Required) — PM & Dev Specification

> Status: **Approved for implementation.** Open questions resolved 2026-07-06 (see §13.2 for decisions).
>
> **Tracking:** [Issue #186](https://github.com/kellylford/QuickMail/issues/186)

---

## Section 1: Executive Summary

QuickMail today has no in-app way to report a bug — the only path is manually navigating to GitHub and filing an issue, which requires the reporter to have and sign into a GitHub account. That's a real barrier for non-developer users, and specifically for screen reader users who hit a rough edge and just want to describe it, not create a GitHub account first. This spec adds a **Help → Report a Bug** command that collects a short description, repro steps, and expected behavior, then submits it as a GitHub issue on `kellylford/QuickMail` **on the user's behalf**, using an app-owned credential rather than the user's own GitHub sign-in. If the direct submission path is unavailable (offline, credential missing), the app falls back to copying the report to the clipboard and opening a pre-filled GitHub issue form — the same two-path resilience pattern used by the Quill project (`../quill`), which QuickMail's evaluation of that project's design confirmed works well.

---

## Section 2: User Problem & Opportunity

### 2.1 Current state (verified)

| Surface | Today | Pain | Who feels it |
|---|---|---|---|
| Bug reporting | No `Help` menu command exists for reporting a bug — confirmed via `grep` for "report a bug"/"ReportBug"/"issues/new" across `MainWindow.xaml.cs` and `MainViewModel.cs`: no matches | The only path is manually going to `github.com/kellylford/QuickMail/issues/new` | All users; disproportionately non-developers and users without an existing GitHub account |
| `Help` menu | Registered commands are `help.userGuide` (`ViewModels/MainViewModel.cs:1343`), `help.keyboardTutorial` (`:1443`), `help.about` (`:1447`) — no bug-report entry | No discoverable in-app entry point | Same |
| Credential storage | `ICredentialService` (`Services/ICredentialService.cs:5-15`) already exposes `SaveSecret(string key, string value)` / `GetSecret(string key)` backed by Windows Credential Manager, used today for account passwords and OAuth secrets | Storage mechanism already exists; nothing currently uses it for an app-owned (non-per-account) secret | N/A — this is an asset, not a pain point |
| External link launching | All `Process.Start`/ShellExecute calls must go through `Helpers.ExternalUriPolicy.TryOpenExternal` (`Helpers/ExternalUriPolicy.cs`), an allow-list of `http`/`https`/`mailto` schemes | N/A — existing safeguard this feature must reuse, not bypass | N/A |
| Diagnostics | `LogService` (`Services/LogService.cs`) appends to `quickmail.log` under the active profile dir, but has no public accessor for the log file path today | A "attach recent log lines" feature needs a small addition (`LogService.LogFilePath`) | Developer triaging a report |
| Version info | `Helpers.AppVersion.Display` (`Helpers/AppVersion.cs`) already computes a display version string, reused by the About dialog, SMTP User-Agent, and the update checker | Reusable as-is for the report's version field | N/A |

**Reference implementation studied:** `../quill` (Python/wxPython) implements this via an app-owned GitHub token stored in OS-encrypted credential storage (`quill/core/feedback_token.py`, `quill/core/github/token_store.py`), submitting directly to the GitHub Issues API (`quill/core/issue_submit.py:55-92`), with a clipboard + pre-filled-issue-URL fallback (`quill/core/diagnostics.py:239-258`) if the token path is unavailable. Quill's stack is not portable to .NET, but the architecture — app-owned token, direct API submission, URL fallback, secret redaction before send — carries over directly.

**Important nuance carried over from the Quill evaluation:** only the direct-API-with-app-owned-token path actually removes the GitHub sign-in requirement. The clipboard+URL fallback still opens `github.com`'s own issue form, which requires the user to be signed into *some* GitHub account to submit there. The fallback exists purely for resilience (offline, token revoked, API down) — it is not a second no-sign-in path, and this spec's acceptance criteria must not conflate the two.

### 2.2 Target personas

- **A non-developer user hitting a bug.** No GitHub account, doesn't want to create one. Wants to describe what happened and hit "Send" — done.
- **A screen reader user reporting an accessibility rough edge.** May be frustrated in the moment; wants the shortest possible path from "this is wrong" to "reported," fully keyboard- and screen-reader-operable, with no surprise focus loss or silent failures.
- **A user without internet access at the moment of reporting.** Wants to prepare a report now and finish sending it later without the app silently dropping the report.
- **Kelly (developer/maintainer).** Wants incoming reports to already carry version/OS/log context so triage doesn't start with a round of "what version were you on?" — and wants no secrets (passwords, tokens) ever able to leak into a report.

### 2.3 Why now

- `ICredentialService.SaveSecret`/`GetSecret` already exists and is exactly the right shape for storing an app-owned token — no new storage mechanism needed.
- `ExternalUriPolicy` already exists to safely hand URLs to the shell — the fallback path reuses it as-is.
- The Quill investigation (this session) already did the comparative design work; the architecture is de-risked before this spec, not decided during it.

---

## Section 3: Design Principles

1. **No GitHub account required for the primary path.** The app authenticates as itself (a narrowly-scoped, app-owned token), not as the user. The user never needs to sign into anything.
2. **Never send anything the user hasn't seen.** A preview of the exact report content (including any log excerpt) is shown before the user confirms sending — no silent background collection.
3. **Redact before it leaves the machine.** Passwords, tokens, and secret-shaped values are stripped from any included log excerpt, independent of user review, as a defense-in-depth backstop.
4. **Fail loud, never fail silent.** If direct submission fails (network, API error, missing token), the user is told and is offered the fallback (clipboard + browser) — never a report that silently vanished.
5. **Small blast radius if the embedded credential is ever extracted.** The app-owned token is a fine-grained GitHub PAT scoped to *only* `Issues: Write` on the single `kellylford/QuickMail` repository — no code, contents, admin, or other-repo scope. Worst case of extraction is spam issues on one repo, not a security compromise.

---

## Section 4: Feature Scope & Acceptance Criteria

### 4.1 In scope (v1)

| Feature | Setting / Shortcut | Default | Notes |
|---|---|---|---|
| "Report a Bug" command | `Help` menu, command `help.reportBug`, no default hotkey (discoverable via menu and Command Palette) | — | Opens `ReportBugWindow` |
| Report form | — | — | Fields: summary (required), what happened (required), what you expected (optional), steps to reproduce (optional), include recent log excerpt (checkbox, default **off**) |
| Redacted preview | — | — | Read-only text block showing exactly what will be sent, including the redacted log excerpt if included |
| Direct submission | "Send" button | — | POSTs to GitHub Issues API using the app-owned token via `IBugReportService` |
| Clipboard + browser fallback | "Copy report and open GitHub" button | — | Always visible, not just on failure — user can choose it directly |
| Auto-collected context | — | Always included | App version (`AppVersion.Display`), OS version (`Environment.OSVersion`), .NET runtime version, active profile mode (normal/`--online`/`--profileDir`) |
| Token provisioning | — | — | Fine-grained PAT (`Issues: Write` only, `kellylford/QuickMail` only), shipped embedded in the build, stored into `ICredentialService` on first use |

### 4.2 Explicitly out of scope (v1)

- **Screenshots or diagnostics bundles.** Unlike Quill, v1 does not attach screenshots, settings snapshots, or a document/message snapshot. A report is text only. (Console/log-image capture already has its own separate spec — `docs/planning/debug-screenshot-capture-pm-dev-spec.md` — and is not wired into this feature.)
- **Crash-time auto-reporting.** This spec covers the user-initiated `Help -> Report a Bug` flow only. Hooking this into an unhandled-exception handler is a possible v2, not built here.
- **Name/email fields.** Quill's legacy form collects optional name/email; this spec does not, since GitHub issues created by the app-owned token are already attributed to that account, and there's no user account to attach contact info to. If the maintainer wants to follow up, the GitHub issue thread is the channel.
- **Screen-reader-name dropdown.** Per the project's "don't name a specific screen reader product in UI text" rule, there is no dropdown of AT product names. If a user wants to mention their AT, it goes in the free-text "what happened" field, same as any other detail.
- **Rate limiting / spam prevention / captcha.** Same posture as Quill: relies on GitHub's own API rate limits. Not implemented in v1.
- **Retry queue for offline submissions.** If direct submission fails, the user gets the fallback button in the same dialog. There is no persisted "pending reports" queue that auto-retries later.
- **A generic feedback/feature-request channel.** This is a bug-report path specifically; feature requests still go through the existing manual GitHub flow (`.github/ISSUE_TEMPLATE/feature_request.md`).
- **Any application log content, in any form.** This is an absolute no for v1, decided explicitly (not a default that could be revisited casually): logging is off by default, so a "collect logs" feature wouldn't even help most reporters; and when logging *is* on, `quickmail.log` can contain message subjects, sender/recipient names, and other content the user does not want copied into a public GitHub issue. Showing a preview before send is **not** treated as sufficient mitigation for this risk — the risk is the user not noticing or not understanding what a raw log line contains, not merely being unaware sending is about to happen. There is no log-excerpt checkbox, no log reading, and no log redaction helper in this feature at all. If log-assisted triage is wanted later, it needs its own explicit opt-in design, not a checkbox bolted onto this dialog.

### 4.3 Acceptance criteria

- `Help -> Report a Bug` opens a modeless window with focus on the Summary field.
- Submitting with a valid app-owned token and network available creates a GitHub issue on `kellylford/QuickMail`, labeled `bug` and `user-reported`, and the dialog shows the resulting issue URL.
- With the token missing, revoked, or the network unavailable, the Send action fails gracefully, tells the user what happened, and the "Copy report and open GitHub" fallback still works.
- The report body sent to GitHub contains only what the user typed (summary, what happened, expected, steps) plus app version/OS/runtime metadata — no log content of any kind.
- The dialog is fully operable with keyboard only and announces correctly with a screen reader (see §6, §7).

---

## Section 5: Architecture & Technical Decisions

### 5.1 Key architectural decisions

**Decision A — App-owned fine-grained PAT, stored via existing `ICredentialService.SaveSecret`.**

**Alternatives:**
1. Classic PAT with broad scope, embedded in the binary. **Con:** extractable via decompilation; if extracted, an attacker has broad account access. Rejected.
2. A small serverless relay (Azure Function / Cloudflare Worker) holding the real token server-side, called by the app over HTTPS. **Con:** introduces a backend QuickMail doesn't otherwise have, plus hosting cost and an availability dependency the current architecture has no equivalent for. Rejected for v1 — noted as the natural v2 upgrade if abuse becomes a real problem.
3. **Fine-grained GitHub PAT scoped to `Issues: Write` on `kellylford/QuickMail` only, embedded in the build, persisted into `ICredentialService` on first use.** **Chosen.** GitHub fine-grained tokens support exactly this single-repo, single-permission scoping, so the worst case if the token is ever extracted from the binary is spam issue creation on one repo — bounded and cheap to revoke/rotate, not a security compromise. This matches how Quill's own `feedback_hub` token is scoped (an app-owned credential, not a user credential) and reuses `ICredentialService.SaveSecret`/`GetSecret` exactly as it's already used for OAuth secrets — no new storage mechanism.

**Rationale:** QuickMail has no backend today (per `docs/ARCHITECTURE.md`, the DI root is entirely local services), and standing one up (alternative 2) is disproportionate to the risk a correctly-scoped fine-grained token already bounds. If real abuse is observed post-ship, migrating to alternative 2 is a contained follow-up (swap the submission call in `IBugReportService`, no UI change).

**Where the token value itself lives (resolved in §13.2):** this codebase already has a working pattern for exactly this problem — `GoogleOAuthService`'s Client ID/Secret are `const` fields in `GoogleOAuthService.Credentials.cs`, a file that is gitignored (`.gitignore:28`) and populated from a checked-in `docs/GoogleOAuthService.Credentials.example` template. Fine-grained GitHub PATs cannot be minted via API/CLI (GitHub requires creating them through the web UI), so this has to be a value the maintainer pastes in once, not something generated programmatically. The bug-report token follows the identical shape: a gitignored `BugReportService.Credentials.cs` partial-class file holding `private const string AppOwnedToken`, with a `docs/BugReportService.Credentials.example` template. Builds without the file compile fine (the feature degrades to fallback-only — see §5.1 note below), exactly mirroring how the app already behaves for contributors who don't set up Google OAuth.

**Degradation without a token:** if `AppOwnedToken` is empty/placeholder (a contributor's local build, or the example file wasn't replaced), `IBugReportService.SubmitAsync` returns a failure immediately without attempting the HTTP call, and the UI falls back exactly as it would for a network failure — same code path as Decision C's failure handling, no special-case branch needed.

**Decision B — Direct HTTP call via `HttpClient`, no GitHub SDK dependency.**

**Alternatives:**
1. Octokit.net NuGet package. **Con:** a full-featured GitHub SDK for a single `POST /repos/{owner}/{repo}/issues` call is a disproportionate dependency.
2. **Plain `HttpClient` POST to the GitHub REST API.** **Chosen.** One JSON POST with an `Authorization: Bearer <token>` header and an `Accept: application/vnd.github+json` header; no new dependency.

**Rationale:** Matches the codebase's existing preference for direct, minimal-dependency implementations (e.g., `ImapService`/`SmtpService` are hand-rolled, not wrapped SDKs).

**Decision C — Modeless window (`.Show()`, not `.ShowDialog()`).**

Per the enforced Modal Dialog Rules in `CLAUDE.md`: `ReportBugWindow` is opened over `MainWindow`, which hosts a live WebView2 (the reading pane), and contains multiple editable `TextBox` fields. That combination is the exact profile that has previously deadlocked the UI thread (`GrabAddressesDialog`, documented in `CLAUDE.md` and memory `grab_addresses_lockup.md`). `ReportBugWindow` follows the same fix already proven there: `new ReportBugWindow(...) { Owner = mainWindow }.Show()`, with Cancel/Escape wired explicitly to `Close()` (no `DialogResult`/`IsCancel` reliance), following the exact pattern in `GrabAddressesDialog.xaml.cs:64-75`.

**Decision D — No log content, and therefore no redaction helper, in v1.**

Per an explicit decision (§13.2, §4.2): this feature never reads `quickmail.log` or any other log file. Quill's design includes a redacted log excerpt as an opt-in; this spec deliberately does not carry that piece over. The submitted report body is limited to what the user typed plus non-sensitive auto-collected metadata (app version, OS version, .NET runtime version, profile mode) — none of which needs redaction. There is no `BugReportRedactor`, no `LogService.LogFilePath` accessor, and no log-related UI in this feature.

**Decision E — No new service dependency graph beyond the existing DI root.**

`IBugReportService`/`BugReportService` is constructed in `App.xaml.cs` alongside the other services, taking `ICredentialService` and `HttpClient` (a single shared instance, not one per call) as constructor dependencies — same wiring pattern as every other service in the DI root.

### 5.2 Runtime mode compatibility

| Mode | Effect |
|---|---|
| Normal | Full functionality — direct submission and fallback both available |
| `--online` | No interaction — this feature never calls `LocalStoreService` |
| `--profileDir <path>` | No interaction — this feature never reads the log file (Decision D), so profile-specific log isolation is not a concern here |

### 5.3 Code reuse and duplication risks

- Token storage: reuses `ICredentialService.SaveSecret`/`GetSecret` verbatim — no new storage code.
- URL launching for the fallback path: reuses `Helpers.ExternalUriPolicy.TryOpenExternal` verbatim — the fallback URL is `https://github.com/...` (an allow-listed scheme), so no changes to the allow-list are needed.
- Version/OS info: reuses `Helpers.AppVersion.Display` as-is.
- Credential-file build pattern: reuses the exact shape of `GoogleOAuthService.Credentials.cs`/`.example` — no new "how do we bake in a secret" mechanism.
- No existing dialog does anything close to this, so there is no risk of silently duplicating an existing "send a report" code path.

### 5.4 Shared component audit (mandatory)

| Component | File | Other consumers | Change needed | Risk / mitigation |
|---|---|---|---|---|
| `ICredentialService` | `Services/ICredentialService.cs` | `AccountService` (per-account passwords), `OAuthService` (OAuth secrets) | **None to the interface.** `SaveSecret("github.bugreport.token", ...)`/`GetSecret(...)` used with a new, distinct key — no collision with any existing key naming (account secrets are keyed by `Guid accountId`, not a string literal) | Low. Verify the new key name doesn't collide with any existing `SaveSecret` call site — confirmed none exist today (grep for `SaveSecret(` across the codebase before implementation). |
| `Helpers.ExternalUriPolicy` | `Helpers/ExternalUriPolicy.cs` | Message-body link clicks (reading pane, preview windows) | **None.** The fallback URL is `https://github.com/...`, already inside the `http`/`https`/`mailto` allow-list | None — read-only reuse. |
| `Helpers.AppVersion` | `Helpers/AppVersion.cs` | About dialog, `MimeMessageBuilder.AppUserAgent`, `UpdateCheckService` | **None.** Read-only reuse of `AppVersion.Display` | None. |
| `GoogleOAuthService.Credentials.cs` pattern | `QuickMail/Services/GoogleOAuthService.Credentials.cs` (gitignored), `docs/GoogleOAuthService.Credentials.example` | Google OAuth only, today | **Add** a sibling `BugReportService.Credentials.cs` (gitignored) + `docs/BugReportService.Credentials.example`, same shape, new file names — does not touch the Google files | None — parallel new files, no shared code. |
| `CommandRegistry` / `MainViewModel.RegisterCommands` | `ViewModels/MainViewModel.cs` (Help category block, ~line 1343-1449) | Every registered command | **Add** one `CommandDefinition` for `help.reportBug`, category `"Help"`, no default hotkey | Low — additive only, follows the exact pattern of the three existing `help.*` registrations immediately adjacent. |
| `App.xaml.cs` DI root | `App.xaml.cs` | Owns every service | **Add** construction of `IBugReportService`/`BugReportService` (and a shared `HttpClient` if one doesn't already exist for this purpose) | Low — additive, follows existing DI root pattern. |

**Summary:** This feature adds one new service (`IBugReportService`), one new window (`ReportBugWindow`), one new gitignored credentials file (following the existing `GoogleOAuthService.Credentials.cs` shape), one new `Help` command registration, and reuses `ICredentialService`, `ExternalUriPolicy`, and `AppVersion` entirely as-is. No existing consumer of any touched component changes behavior. It reads no log file and touches `LogService` not at all.

---

## Section 6: Keyboard Walkthrough (Mandatory)

### Path: Open and submit a bug report (happy path)

1. User is anywhere in the app, presses **Alt** to reach the menu bar, navigates to **Help → Report a Bug** (or opens the Command Palette with `Ctrl+Shift+P` and activates "Report a Bug"). **Expected:** `ReportBugWindow` opens modelessly over `MainWindow`. Focus lands on the **Summary** field. Screen reader announces "Report a Bug window. Summary, edit."
2. User types a one-line summary, presses **Tab**. **Expected:** Focus moves to **What happened** (multi-line). Screen reader announces "What happened, edit, required."
3. User types a description, presses **Tab** through **What you expected** and **Steps to reproduce** (both optional, multi-line). **Expected:** Standard tab order, no surprises.
4. User Tabs to **Preview** (read-only, focusable for review — shows exactly what will be sent: the four text fields plus app version/OS/runtime metadata, nothing else) then to the action buttons: **Send**, **Copy report and open GitHub**, **Cancel**.
5. User activates **Send**. **Expected:** Buttons disable, a `Status` announcement fires: "Sending report…". On success, a `Result` announcement fires: "Report sent. Issue #NNN created." and the window shows the returned issue URL as a clickable-via-`ExternalUriPolicy` link, plus a **Close** button. Focus moves to the issue URL text.
6. User presses **Escape** (or activates **Close**). **Expected:** Window closes. Focus returns to whatever had focus in `MainWindow` before **Report a Bug** was opened.

### Path: Submission fails (no token / offline)

1. Steps 1-4 as above. User activates **Send**. **Expected:** `Status` announcement "Sending report…", then on failure a `Result` announcement: "Could not send the report automatically. Your report is ready to copy." Focus moves to the **"Copy report and open GitHub"** button; the **Send** button is re-enabled (not left permanently disabled) in case the user wants to retry (e.g., after reconnecting).
2. User activates **"Copy report and open GitHub"**. **Expected:** Full report text (summary through steps to reproduce, plus the log excerpt if included) is copied to the clipboard; a `Result` announcement fires: "Report copied. Opening GitHub in your browser."; `ExternalUriPolicy.TryOpenExternal` opens a pre-filled `kellylford/QuickMail` new-issue URL in the default browser. **Note for this path:** the user will need a GitHub account to complete submission there — this is expected and is not a bug (see §2.1 nuance).

### Path: Validation failure

1. User Tabs directly to **Send** without entering a Summary. **Expected:** Send is a no-op; a `Result` announcement fires: "Enter a summary before sending." Focus moves back to the **Summary** field.

### Path: Cancel without sending

1. User opens the window, types a partial report, presses **Escape**. **Expected:** Window closes immediately, no confirmation prompt (a partially-typed bug report is low-stakes, unlike an in-progress compose — no "discard changes?" dialog). Focus returns to the originating control in `MainWindow`.

### Path: F6 ring (window-level, per New Window Checklist)

1. User presses **F6** inside `ReportBugWindow`. **Expected:** Cycles between the form pane and the preview pane (two logical stops: form fields, preview region). `Shift+F6` cycles backward.

---

## Section 7: Accessibility Checklist (Mandatory)

- **`AutomationProperties.Name`** — New short labels only: "Summary", "What happened", "What you expected", "Steps to reproduce", "Preview", "Send", "Copy report and open GitHub", "Cancel", "Close". No role names, no instructions baked in.
- **`AnnouncementCategory`** — "Sending report…" / "Could not send…" / "Report sent…" use `AnnouncementCategory.Status`/`Result` per the existing convention (background progress = `Status`, outcome = `Result`); both respect user settings (`AnnounceStatus`, `AnnounceResults`) — this feature does not force any announcement, since none of it is a meta-setting-toggle announcement.
- **Screen reader browse mode** — No WebView2 in this window; not applicable.
- **Focus restoration** — Captured before `ReportBugWindow.Show()` (the control focused in `MainWindow` at invocation time) and restored explicitly in the window's `Closed` handler, following the New Window Checklist pattern.
- **F6 ring** — Two stops (form, preview) local to `ReportBugWindow`; `MainWindow`'s own F6 ring is unaffected since this is a separate top-level window, not a new pane within `MainWindow`.
- **Checkbox / radio groups** — None in this feature (the log-excerpt checkbox from the original Quill-derived draft was removed — see §4.2).
- **Color-only information** — None. Send success/failure is communicated via text announcement and visible status text, not color alone.

**Answer:** Introduces one new window with 9 short-labeled controls, no WebView2, no checkboxes, no radio groups. Announcements: `Status` for in-flight send, `Result` for outcome. F6 ring is local to the new window (2 stops); `MainWindow`'s F6 ring is unchanged.

---

## Section 8: Acceptance Walkthrough (Mandatory)

### Scenario: Successful direct submission

**Setup:** App running, valid app-owned token present in `ICredentialService`, network available.

1. Open **Help → Report a Bug**. **Verify:** Window opens, focus on Summary, screen reader announces the field name and "required" if applicable.
2. Fill in Summary + What happened; leave the rest default. Activate **Send**. **Verify:** Buttons disable during send; a real GitHub issue is created on `kellylford/QuickMail` with labels `bug` and `user-reported`; the dialog shows the returned issue URL; a `Result` announcement is heard.
3. Activate **Close**. **Verify:** Window closes; focus returns to the control that had focus before the window opened.

### Scenario: No log content in the report

**Setup:** App running normally (logging on or off, doesn't matter for this feature).

1. Open **Report a Bug**, fill in all four text fields, view **Preview**. **Verify:** The preview contains only the four typed fields plus app version/OS/runtime metadata. No log file is read (confirm by checking `quickmail.log`'s file modified timestamp is unchanged after opening/using the dialog).

### Scenario: Fallback path

**Setup:** Temporarily remove/corrupt the stored token via `ICredentialService.DeleteSecret` for the bug-report key, or disconnect network.

1. Fill in a report, activate **Send**. **Verify:** A `Result` announcement reports the failure; the **Send** button is re-enabled, not stuck disabled.
2. Activate **"Copy report and open GitHub."** **Verify:** Clipboard contains the full report text (spot-check by pasting into Notepad); default browser opens a `github.com/kellylford/QuickMail/issues/new` URL with title/body pre-filled from the report fields.

### Scenario: Validation

1. Open the window, leave Summary blank, activate **Send.** **Verify:** No network call is made (check via a breakpoint or a temporary log line — remove before commit); a `Result` announcement asks for a summary; focus returns to Summary.

### Scenario: `--online` mode — no regression

1. Launch with `--online`. Open **Report a Bug**, submit a report. **Verify:** Feature works identically — no `LocalStoreService` calls occur, no crash, no behavior difference from normal mode.

### Scenario: Screen reader pass

1. Tab through every control in `ReportBugWindow` with a screen reader running. **Verify:** Every `AutomationProperties.Name` reads as the short label from §7 (no doubled role announcements, no baked-in instructions). Verify the one-time `Hint` on first checking the log checkbox is heard exactly once per window instance (not repeated on subsequent checks/unchecks in the same window).

### Scenario: No regression on existing `Help` commands

1. After adding `help.reportBug`, activate **Help → Open User Guide** and **Help → About QuickMail.** **Verify:** Both still work exactly as before (confirms the new registration didn't disturb the adjacent `help.userGuide`/`help.about` entries in `MainViewModel.RegisterCommands`).

---

## Section 9: Success Metrics

- **Behavioral:** A user with no GitHub account can go from "something's wrong" to a filed GitHub issue with zero sign-in prompts.
- **Keyboard-centric:** Every step in §6's happy path is achievable with keyboard only.
- **No regressions:** Existing `Help` menu commands (`help.userGuide`, `help.keyboardTutorial`, `help.about`) work unchanged; no existing `SaveSecret` key collides with the new one.
- **Safety:** No log content of any kind ever appears in a submitted or previewed report — verified by the "No log content in the report" scenario in §8.
- **Resilience:** Submission failure never loses the user's typed content — the fallback always has the full report text ready to copy.
- **Online mode:** Feature behaves identically with `--online` set.

---

## Section 10: Implementation Phases

### Phase 1: Credentials file scaffolding

**Goal:** `BugReportService.Credentials.cs` (gitignored) and `docs/BugReportService.Credentials.example` exist, following the exact shape of `GoogleOAuthService.Credentials.cs`/`.example`; `.gitignore` updated; the real file is populated locally with a placeholder value for development (a real token is a manual step for the maintainer before release, not part of this implementation).

**Deliverables:** `docs/BugReportService.Credentials.example`; `.gitignore` entry; a local (gitignored) `QuickMail/Services/BugReportService.Credentials.cs` with a placeholder token so the build compiles.

**Tests:** None (no logic yet).

**Risk:** None — purely additive scaffolding. **Duration:** 30 min.

### Phase 2: `IBugReportService` (headless, no UI)

**Goal:** `IBugReportService.SubmitAsync(BugReportModel)` posts to the GitHub Issues API and returns `(issueUrl, error)`; token retrieval/provisioning via `ICredentialService` works (falling back to the placeholder-detection behavior in §5.1); `BuildFallbackUrl(BugReportModel)` produces a correct pre-filled GitHub issue URL.

**Deliverables:** `Services/IBugReportService.cs`, `Services/BugReportService.cs`, `Models/BugReportModel.cs`.

**Tests:** `BugReportServiceTests` — mock `HttpClient` (via a test `HttpMessageHandler`) to cover success, 401 (bad/revoked token), missing/placeholder token (immediate graceful failure, no HTTP call attempted), network failure, and confirm the fallback URL is correctly encoded and never throws on unusual characters (quotes, ampersands, newlines) in report fields.

**Risk:** GitHub API response shape assumptions (issue URL field name) — verify against the real API docs/a live test call before finalizing the response model. **Duration:** 3-4 h.

### Phase 3: `ReportBugWindow` UI + `Help` command wiring

**Goal:** The window from §6/§7 exists, is modeless, wires Cancel/Escape explicitly, restores focus on close, has a working F6 ring, and is reachable via `Help -> Report a Bug` and the Command Palette.

**Deliverables:** `Views/ReportBugWindow.xaml` (+ `.xaml.cs`), `ViewModels/ReportBugViewModel.cs`, one `CommandDefinition` addition in `MainViewModel.RegisterCommands`, one `MenuItem` in the `Help` menu XAML.

**Tests:** `XamlParseTests` extension (window loads without `XamlParseException`); `CommandRegistryTests` extension (new command registers correctly); a `ReportBugViewModelTests` class covering validation (blank summary), the log-checkbox-toggles-preview behavior, and success/failure state transitions (mocking `IBugReportService`).

**Risk:** Modal-dialog deadlock class of bug if `.ShowDialog()` is used by mistake — mitigated by Decision C being explicit and by testing the window opened over `MainWindow` with the reading pane active. **Duration:** 4-6 h.

---

## Section 11: Files to Create / Modify

### Create

| File | Purpose | Lines (est.) |
|---|---|---|
| `docs/BugReportService.Credentials.example` | Template for the gitignored token file, mirroring `GoogleOAuthService.Credentials.example` | 15-20 |
| `QuickMail/Services/BugReportService.Credentials.cs` (gitignored, not committed) | Real/placeholder `const string AppOwnedToken` | 10 |
| `Models/BugReportModel.cs` | Report data (summary, description, expected, steps) | 15-25 |
| `Services/IBugReportService.cs` | Interface (`SubmitAsync`, `BuildFallbackUrl`) | 15-20 |
| `Services/BugReportService.cs` | GitHub API POST, token provisioning via `ICredentialService`, fallback URL builder | 90-140 |
| `Views/ReportBugWindow.xaml` / `.xaml.cs` | The window itself | 140-190 |
| `ViewModels/ReportBugViewModel.cs` | Form state, validation, send/fallback commands | 90-140 |

### Modify

| File | Changes | Lines (est.) |
|---|---|---|
| `.gitignore` | Add `**/BugReportService.Credentials.cs` entry | +2 |
| `App.xaml.cs` | Construct `IBugReportService`, wire into DI root | +10 |
| `ViewModels/MainViewModel.cs` | Register `help.reportBug` command near existing `help.*` block (~line 1343-1449) | +10 |
| `Views/MainWindow.xaml` | Add "Report a Bug" `MenuItem` under `Help` | +5 |

---

## Section 12: Tests to Add

| Test Class | Test Methods | Coverage |
|---|---|---|
| `BugReportServiceTests` | Success (issue URL returned), 401/revoked token, missing/placeholder token (no HTTP call attempted), network failure, fallback URL encoding with special characters, submitted body contains only user text + metadata (no log content) | Submission logic + fallback |
| `ReportBugViewModelTests` | Blank-summary validation, log-checkbox toggling updates preview, send success/failure state transitions, fallback command copies full text | Form/VM behavior |
| `XamlParseTests` (extend) | `ReportBugWindow` loads without `XamlParseException` | Regression guard |
| `CommandRegistryTests` (extend) | `help.reportBug` registers with category `Help`, no default hotkey collision | Command registration |

---

## Section 13: Known Risks & Open Questions

### 13.1 Risks

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| Embedded fine-grained PAT is extracted from the binary | Medium (any embedded secret in a distributed binary is extractable) | Minor (scoped to `Issues: Write` on one repo only) | Decision A's scoping bounds the blast radius; token is revocable/rotatable if abused; noted as the trigger for a v2 migration to a serverless relay if it becomes a real problem |
| `ReportBugWindow` accidentally implemented with `.ShowDialog()` | Low (explicit in spec) | Blocker (UI-thread deadlock class of bug) | Decision C is explicit and cites the exact prior incident; code review gate |
| GitHub API rate-limits or blocks the app-owned token account | Low | Minor | Fallback path (clipboard + browser) remains available regardless of API-side issues |
| New `SaveSecret` key collides with an existing key | Low | Minor | Verified in §5.4 that no existing call site uses this key name; re-verify at implementation time with a fresh grep |
| A user pastes sensitive content directly into a free-text field (Summary/What happened/etc.) | Low | Minor | Out of scope to defend against — this is user-authored content they can see in the Preview before sending, same trust model as any text field in the app (e.g., Compose). Not equivalent to the rejected log-collection risk, which was about *automatically* surfacing content the user didn't author or choose to type. |

### 13.2 Open questions — resolved 2026-07-06

- **Token provisioning mechanism at build time.** **Resolved:** follow the exact existing pattern used for Google OAuth credentials in this codebase — a gitignored `QuickMail/Services/BugReportService.Credentials.cs` partial-class file with a `const string AppOwnedToken`, plus a checked-in `docs/BugReportService.Credentials.example` template (see §5.1). The maintainer creates the actual fine-grained PAT manually in the GitHub UI (this cannot be done via API/CLI) and pastes it into the local file before a release build; the placeholder value in the example file causes the feature to degrade gracefully to fallback-only (§5.1 "Degradation without a token").
- **Issue labels.** **Resolved:** `bug` + a new `user-reported` label, both applied to every issue this feature creates.
- **Proactive offline detection vs. attempt-then-fail.** **Resolved:** attempt-then-fail, as proposed — no proactive connectivity check.
- **Log collection.** **Resolved: absolute no for v1, not merely deferred.** Logging is off by default, so a log-excerpt feature helps only a subset of reporters; and when logging is on, `quickmail.log` can contain email subjects, sender/recipient names, and other content a user would not want copied into a public GitHub issue. A preview shown before sending is explicitly **not** accepted as sufficient mitigation — the risk is the user not recognizing what a raw log line contains, not merely being unaware that sending is imminent. This removed the log-excerpt checkbox, the `BugReportRedactor` helper, and the `LogService.LogFilePath` accessor from the spec entirely (see §4.2, Decision D, §10 Phase 1).

---

## Section 15: Implementation Guidance for AI

### 15.1 Adjustments you're expected to make

- The exact GitHub Issues API request/response shape (field names, required headers) should be verified against GitHub's current REST API docs at implementation time rather than assumed from this spec — APIs can add required fields.
- The precise wording of announcements in §6 is illustrative; keep the category assignments (`Status`/`Result`/`Hint`) and the *timing* (once per window instance for the log-checkbox hint) but you may adjust exact phrasing for consistency with nearby existing announcement strings.
- Whether `BugReportModel` is a plain record or a class with `INotifyPropertyChanged` is your call, based on whether `ReportBugViewModel` needs to bind directly to it or copies its fields into observable properties — follow whatever pattern `ComposeViewModel` already uses for its own draft model, for consistency.

### 15.2 When to ask for clarification

- All open questions from §13.2 are resolved — do not re-litigate them during implementation. In particular: **do not add any log-reading, log-excerpt, or redaction code to this feature** — that was explicitly rejected, not deferred.
- If the GitHub Issues API's actual required-scope behavior for fine-grained tokens differs from what's assumed in Decision A (e.g., if `Issues: Write` alone turns out to be insufficient for some reason), stop and confirm before widening the token's scope, since minimal scope is a named security principle (§3.5), not an incidental detail.
- The real fine-grained PAT value is a manual step for the maintainer (cannot be created via API/CLI). Implementation should ship with `BugReportService.Credentials.cs` populated with a placeholder so the build compiles and tests pass; do not block implementation on obtaining a real token.

### 15.3 After implementation: acceptance walkthrough preview

After you build this, the user will run the Acceptance Walkthrough in §8. The steps most likely to catch bugs in this specific implementation:
- §8 "No log content in the report" — confirms the feature genuinely never touches the log file, which is the highest-stakes requirement in this spec.
- §8 "Fallback path" — confirms the failure path doesn't leave Send permanently disabled and doesn't lose the user's typed content.
- §8 "No regression on existing Help commands" — confirms the new command registration didn't disturb the three adjacent existing ones.

If any of these fail, document the failure; it will be addressed in the code-review session.
