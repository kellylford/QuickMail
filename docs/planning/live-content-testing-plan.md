# Live-Content Testing Infrastructure Plan

**Issue:** [#304](https://github.com/kellylford/QuickMail/issues/304) — Testing infrastructure: local IMAP/SMTP + CalDAV/CardDAV harness and testable seams for calendar/mail
**Cross-referenced issues:** #126, #268, #278, #293, #294, #296, #297, #311, #312, #313, #314
**Status:** Proposed — not yet implemented
**Date:** 2026-07-24

## 1. Problem

Every test in `QuickMail.Tests` runs against `StubServices.cs`. Nothing in the
repository ever opens a socket, parses a real server response, or exercises the
seam between a protocol client and the local cache. That gap has shipped real
bugs:

- **#297** — the IMAP fetch path persisted the parsed invite but not the raw
  `calendar_ics`, so the Accept/Decline card vanished once a message was cached.
  The failing code needs a live IMAP connection, so it had zero coverage.
- **#293** — the Google calendar write path hardcoded `calendars/primary`;
  unit tests verified *that* a create happened, not *which calendar* received it.
- **#126, #268, #278, #311, #313, #314** — a family of connection-handling bugs
  (dropped-connection recovery, IDLE watcher churn on account add, delete
  failing instead of reconnecting, UI "connected" state not tracking reality).
  All live below the stub seam.

Per the issue discussion, the goal is broader than calendar: a real-protocol
test tier for the whole program — mail, calendar, contacts — plus an
occasional live-account smoke test, all runnable within GitHub-hosted
infrastructure.

## 2. Constraints (what GitHub Actions actually supports)

These constraints shape everything below; they are worth stating explicitly
because the obvious answer ("add a Docker `services:` block") does not work
for this repository today.

1. **The whole app is one `net8.0-windows10.0.17763.0` / `UseWPF=true`
   assembly.** `ImapMailService`, `SmtpService`, and the CalDAV/Google/Graph
   clients are protocol code with no WPF dependency, but they live in a
   Windows-TFM project that **cannot compile on a Linux runner**. Until
   protocol code is extracted into a cross-platform library, every test job
   must run on `windows-latest`.
2. **Docker service containers attach only to Linux runners.** `windows-latest`
   runs Windows containers only, and GreenMail/Radicale images are Linux
   images. So per-PR local servers must run as **native processes** on the
   Windows runner.
3. **Windows runner images preinstall what we need natively:** Java (Temurin,
   `JAVA_HOME` set) runs the GreenMail standalone jar; Python + pip installs
   Radicale. No Docker required.
4. **Interactive OAuth cannot run in CI.** Nothing headless can complete a
   Google/Microsoft browser consent flow. Live-account tests must use either
   IMAP basic-auth accounts on Kelly-owned domains, or pre-provisioned refresh
   tokens stored as secrets (with a manual re-consent burden when they expire).
5. **GitHub features we can lean on:** repository secrets and environments,
   `schedule:` cron triggers, `workflow_dispatch`, `actions/cache` (for the
   GreenMail jar), artifact upload (server logs on failure), concurrency
   groups (serialize live-mailbox runs), and job-level `if:` guards (skip live
   jobs on forks where secrets are absent).

## 3. The tiered model

| Tier | What | Where it runs | When | Gates PRs? |
|------|------|---------------|------|------------|
| 0 | Existing unit tests against stubs (~current suite) | `windows-latest` | Every push/PR | Yes (today) |
| 1 | **Protocol integration tests** against local GreenMail (IMAP/SMTP) and Radicale (CalDAV/CardDAV) | `windows-latest`, servers as native processes | Every push/PR | Yes (once stable) |
| 2 | **Recorded/faked HTTP provider tests** — WireMock.Net in-process fakes for Google Calendar REST, Microsoft Graph, and captured iCloud CalDAV quirk responses | `windows-latest`, in-process (no server setup at all) | Every push/PR | Yes |
| 3 | **Live smoke tests** against real accounts on Kelly-owned domains (+ optionally Gmail/Outlook via stored refresh tokens) | `windows-latest`, secrets-gated | Weekly cron + manual dispatch | **No** — informational |
| 4 | (Future/optional) UI automation and Linux service-container jobs | see §8 | — | No |

What each tier can and cannot see:

- **Tier 1** proves *protocol correctness*: our IMAP/SMTP/DAV client code
  against a real conforming server — fetch, append, flags, IDLE, reconnect,
  ICS parts, DAV discovery and write targeting.
- **Tier 2** proves *REST client correctness* for providers whose real
  services we cannot run locally (Google Calendar, Graph), including replaying
  captured quirk responses (iCloud partition redirects, Graph paging, error
  shapes).
- **Tier 3** is the only tier that sees *provider behavior*: Gmail routing
  invites to Junk, Google's birthday-calendar delete restriction (#294),
  iCloud redirects, real TLS/auth negotiation. It exists to catch drift, not
  to gate merges — real servers bring rate limits and flakiness.

## 4. Tier 1 — local protocol servers on the Windows runner

### 4.1 New project: `QuickMail.IntegrationTests`

A separate xUnit project, **not** added to the existing test step. Rationale:

- The current CI test step and local `dotnet test` invocations target
  `QuickMail.Tests.csproj` explicitly, so a new project changes nothing for
  existing workflows — integration tests only run where the servers exist.
- No `[StaFact]`/WPF baggage: plain `[Fact]`s exercising services directly,
  which sidesteps the issue #211 teardown-crash workaround entirely.
- References `QuickMail.csproj` (same solution); can reuse
  `QuickMail.Tests/StubServices.cs` via link or a small shared file.

Skippability: each fixture probes its server port and, when the server is
absent (e.g. a contributor runs `dotnet test` on the solution without the
harness), tests **fail with a clear "server not running — see
docs/TESTING-INTEGRATION.md" message in CI but skip locally**. Concretely: an
environment variable `QUICKMAIL_IT_SERVERS=1` set by CI makes server absence a
failure; without it, fixtures use `Assert.Skip` (xUnit v3 supports dynamic
skip) so a plain local solution test run stays green.

### 4.2 GreenMail (IMAP + SMTP)

- **Run as:** `java -jar greenmail-standalone-<ver>.jar` with test-profile
  ports (SMTP 3025, IMAP 3143, no TLS) and users declared via
  `-Dgreenmail.users=user1:pw@localhost,user2:pw@localhost`. The jar is
  fetched from Maven Central and cached with `actions/cache`.
- **Why it works with zero app changes:** accounts are plain
  `AccountModel { ImapHost="localhost", ImapPort=3143, ImapUseSsl=false, ... }`;
  `ImapMailService.ConnectAsync` already accepts the password as a parameter,
  so the Windows Credential Manager is never touched. Non-SSL falls back to
  `StartTlsWhenAvailable`, which degrades cleanly to plaintext against
  GreenMail.
- **Seeding messages:** tests inject arbitrary MIME (including
  `text/calendar` invite parts built with MimeKit) either by SMTP delivery to
  the GreenMail user or by IMAP `APPEND` from the test itself. Both paths are
  plain MailKit calls — no GreenMail-specific API dependency.
- **A local dev story, not just CI:** a `scripts/start-test-servers.ps1`
  downloads (once) and launches GreenMail + Radicale so the same suite runs on
  a dev machine with one command.

**Verification task for the first spike:** confirm the GreenMail version we
pin supports IMAP `IDLE` well enough for the watcher tests (support exists in
the 2.x line, but the IDLE tests in §4.4 are the acceptance check). If IDLE
proves unreliable, the fallback for *watcher* tests specifically is
hMailServer (Windows-native, silent-installable) while GreenMail still serves
everything else — a decision to make from evidence in the spike, not now.

### 4.3 Radicale (CalDAV + CardDAV)

- **Run as:** `pip install radicale`, then
  `python -m radicale --storage-filesystem-folder=<temp> ...` with
  auth configured off (or a static htpasswd — Radicale 3 config detail for the
  spike), port 5232.
- **No app seams needed:** `CalDavCalendarClient` and `CardDavContactClient`
  already take `serverUrl` as a method parameter and accept an injected
  `HttpClient`. Tests point discovery at `http://localhost:5232/` directly.
- **Seeding:** create users/collections by writing to Radicale's filesystem
  storage or via `PUT` of `.ics`/`.vcf` bodies — both trivial.
- **What this covers:** multi-calendar enumeration, sync, and **write
  targeting** (create/edit/delete lands in the correct collection — the class
  of bug in #293, for the DAV path) plus CardDAV contact sync, without
  touching iCloud.

### 4.4 The connection-fault proxy (for the #311/#268/#278/#313/#314 family)

GreenMail can be stopped/started between operations, but the interesting bugs
involve a connection dying *mid-session* while the server stays up. Standard
tooling (Toxiproxy) is another moving part; a **tiny in-test TCP relay
fixture** (~100 lines of C#: listen on a local port, relay bytes to
GreenMail, expose `KillAllConnections()` / `PauseRelay()`) gives tests direct,
deterministic control:

- point the account at the proxy port instead of GreenMail's,
- perform ops, sever the socket at a chosen moment, assert recovery.

This unlocks the scenarios from the issue's second comment:

1. **Add-account watcher stability (#126):** two accounts idling via the
   proxy; connect a third; assert the first two's IDLE connections were not
   dropped/restarted (observable as no reconnect on their proxy ports).
2. **Delete reconnect-retry (#311):** kill the pooled connection, then
   delete; assert the operation transparently reconnects and succeeds.
3. **Dropped-connection recovery (#268/#278/#313):** sever, then fetch /
   folder-count / flag ops; assert recovery rather than surfaced errors or
   stale counts.
4. **Connection-status truth (#314):** assert the service-level connected
   state tracks real socket outcomes through kill/restore cycles.

### 4.5 Initial Tier-1 test slate (beyond connections)

Per the issue's "full program at this level" comment, a first coverage matrix
— each row is a test class in the new project:

| Area | Scenarios |
|------|-----------|
| Fetch → cache seam | The exact #297 path: deliver an invite with a `text/calendar` part, fetch via `ImapMailService`, assert the cached row retains `calendar_ics` and the card survives a second (cache-hit) open. **This is the suggested first end-to-end test.** |
| Message list / detail | Envelope fetch, body fetch (plain/HTML/multipart), attachments metadata, large-message handling |
| Flags & state | `\Seen` reconciliation, flag/unflag round-trip, mark-read propagation |
| Folder ops | List folders, move, delete-to-Trash semantics, folder unread counts |
| Send | SMTP send via `SmtpService`, **ICS reply routing** — the Accept/Decline reply leaves via the SMTP settings of the account that received the invite (two GreenMail users, exercises #296) |
| Sync | Periodic sync pulls new mail; IDLE-driven new-mail notification path |
| CalDAV | Discovery, multi-calendar enumeration, event create/edit/delete lands in the targeted collection, sync round-trip |
| CardDAV | Addressbook discovery, contact sync round-trip |

### 4.6 CI wiring

A new job in `.github/workflows/quickmail.yml` (or a sibling workflow) —
sketch, not final YAML:

```yaml
integration:
  runs-on: windows-latest
  steps:
    - checkout; setup-dotnet 8.0.x
    - actions/cache: greenmail jar (keyed on pinned version)
    - download jar if cache miss; start GreenMail (background, log to file)
    - pip install radicale==<pinned>; start Radicale (background, log to file)
    - wait-for-port loop on 3025/3143/5232 (fail fast with server log dump)
    - dotnet test QuickMail.IntegrationTests -c Release   # QUICKMAIL_IT_SERVERS=1
    - always: upload greenmail/radicale logs + trx as artifacts
```

Notes:

- Runs in parallel with the existing `build` job; existing job is untouched.
- The credentials-file steps from the build job are **not** needed — the
  integration project should not require the OAuth credential partial classes
  (they are app-project sources; the test project links against the built
  assembly which CI builds with placeholder files if needed — a spike detail).
- Budget: target < 5 minutes wall clock. Server startup is seconds; the tests
  are localhost round-trips.
- Make it **required for merge only after** a burn-in period (see §9 phases) —
  a flaky required job is worse than no job.

## 5. Tier 2 — WireMock.Net fakes for REST providers (Google, Graph, iCloud quirks)

For providers whose real backend cannot run locally, fake the HTTP layer
in-process with **WireMock.Net** (a NuGet package; starts an HTTP listener
inside the test process — no runner setup, works anywhere, could even join
`QuickMail.Tests` though keeping it in the integration project keeps intent
clear).

**Required seam (small, safe):** `GoogleCalendarClient`, `GooglePeopleClient`,
and `GraphClient` hardcode `private const string BaseUrl`. Change each to a
constructor-defaulted property (`string baseUrl = "https://..."`), plus the
`ICloudCalDavUrl`/`ICloudCardDavUrl` constants used at call sites
(`GraphCalendarSyncService`, `ICloudContactSource`) routed the same way.
Production behavior is unchanged; tests pass the WireMock URL. Auth seam:
these clients take a bearer token — tests supply a dummy string, no OAuth
involved.

What this tier buys:

- **Write-path calendar targeting for Google and Graph** (#304 item 4.3):
  assert the request line — e.g. that an edit to a secondary-calendar event
  hits `calendars/{thatCalendarId}/events/{id}`, the literal #293 bug shape,
  and that Graph resolves by global id via `/me/events/{id}`.
- **Recorded provider quirks as regression fixtures:** capture (once,
  manually, from real traffic) the iCloud partition-redirect PROPFIND
  response, Google error bodies (birthday-calendar delete rejection, #294),
  Graph paging envelopes — and replay them forever. This is the "recorded
  HTTP fixtures" caveat in the issue made concrete.
- Error-path behavior: 401 → token refresh flow, 429/5xx handling, malformed
  payloads — unreachable with stubs, unsafe to force against live services.

## 6. Tier 3 — scheduled live smoke against real accounts

A separate workflow (`live-smoke.yml`):

- **Triggers:** `schedule:` (weekly), `workflow_dispatch:` (on demand, e.g.
  before a release). Never on PRs; never a required check.
- **Accounts:** dedicated test mailboxes on Kelly-owned domains with IMAP
  basic auth or app passwords — host/user/password in repository secrets
  (`LIVE_IMAP_HOST`, `LIVE_IMAP_USER`, …). Optionally later: a Gmail and an
  Outlook test account via pre-provisioned refresh tokens in secrets, accepting
  the manual re-consent chore when they expire (start without these; add only
  if the owned-domain tier proves its worth).
- **Suite:** a thin `Trait("Category","LiveSmoke")` slice of the integration
  tests — connect, list, send-to-self round-trip (send via SMTP, poll IMAP
  until it arrives), invite round-trip, flags, and cleanup. Small on purpose:
  minutes, not comprehensive.
- **Hygiene:** a `concurrency:` group so two runs never share a mailbox;
  every test namespaces its subjects with a run id; a final cleanup step
  empties the test folders; the job `if:`-skips cleanly when secrets are
  absent (forks).
- **Failure handling:** upload logs, and open/update a pinned GitHub issue on
  failure (a `gh` CLI step) so drift is visible without anyone watching the
  Actions tab.

This is the only tier that can observe provider-specific behavior (Gmail
junk-routing of invites, #294's birthday-calendar rules, iCloud redirects) —
and even here, only the subset our test accounts can reproduce. Some provider
quirks will always be discovered in the field first; when they are, the
response is: capture the traffic, turn it into a Tier-2 recorded fixture.

## 7. Code changes required in the app (the "seams")

Deliberately small; no architecture change:

1. **Extract the calendar block of `GetMessageDetailCoreAsync`**
   (`ImapMailService.cs` ~366–412) into a pure internal helper
   (`PopulateCalendar(detail, rawIcs)`), so the invariant *"CalendarInvite set
   ⇒ CalendarIcs set"* is unit-testable without any server (issue item 3).
   The Tier-1 end-to-end test then guards the seam itself.
2. **`StubSmtpService.SendIcsReplyAsync` records `(account, organizerEmail)`**
   instead of discarding args, so invite-reply routing is assertable at the
   VM level in the existing unit suite (issue item 3).
3. **Base-URL constructor parameters** for `GoogleCalendarClient`,
   `GooglePeopleClient`, `GraphClient`, and the iCloud URL constants (§5).
4. **Nothing needed for IMAP/SMTP/DAV clients** — host/port/SSL come from
   `AccountModel`, passwords can be passed directly, DAV URLs are already
   parameters. This is why Tier 1 can start immediately.

Anything larger (e.g. extracting a `QuickMail.Core` library) is explicitly
deferred — see §8.

## 8. Deferred / optional future work

- **`QuickMail.Core` extraction (enables Linux + Docker service containers).**
  Moving Models + protocol services into a plain `net8.0` library would let
  integration tests run on `ubuntu-latest` with GreenMail/Radicale as real
  service containers, and is good hygiene besides. But it touches every file
  in `Services/` (namespace/project moves) for a payoff Tier 1 already
  delivers on Windows. Do it later, if ever, as a standalone refactor — not as
  a precondition for testing.
- **UI-level automation (FlaUI driving the real app via UIA).** Valuable in
  principle — it is the only automated way to see what the UIA tree exposes,
  extending the `SelectorItemAccessibilityTests` idea to whole windows — but
  WPF UI automation on hosted runners is a flakiness tar pit, and it cannot
  replace listening with a screen reader (per project policy, the user's
  report is authoritative). Revisit only after Tiers 1–3 are stable.
- **Coverage-driven expansion.** Once the harness exists, wire coverlet output
  from the integration job to spot which protocol paths remain untested.

## 9. Phasing

Each phase is independently shippable and useful; stop-points are deliberate.

**Phase 1 — Harness spike + the #297 test (the issue's "suggested first step",
adapted from docker-compose to native processes).**
`QuickMail.IntegrationTests` project; GreenMail fixture + start script;
CI job; one end-to-end test: seeded invite → fetch → `calendar_ics` persisted →
card survives cache-hit reopen. Also lands seams 7.1 and 7.2 with their unit
tests. *Exit criteria:* green on CI 10 consecutive runs; runtime < 5 min.

**Phase 2 — Connection-handling suite.**
TCP relay fixture; the four scenarios in §4.4 (#126, #311, #268/#278/#313,
#314). This is the highest-value regression content per the #312 review.

**Phase 3 — Mail breadth + Radicale.**
The rest of the §4.5 matrix (fetch/flags/folders/send/ICS-reply-routing/sync)
and the Radicale CalDAV/CardDAV fixture with discovery/sync/write-targeting
tests. After this phase, consider making the integration job a required check.

**Phase 4 — Tier 2 REST fakes.**
Base-URL seams (7.3); WireMock.Net tests for Google/Graph write targeting;
first recorded-quirk fixtures (iCloud redirect, #294 error body) as real
traffic gets captured.

**Phase 5 — Tier 3 live smoke.**
Owned-domain accounts + secrets; `live-smoke.yml` with weekly cron, manual
dispatch, cleanup, and issue-on-failure. Gmail/Outlook refresh-token accounts
only if justified afterward.

## 10. Risks and open questions

- **GreenMail IDLE fidelity** — the one dependency assumption that needs the
  Phase-1 spike to verify before Phase 2 commits to it (fallback named in
  §4.2).
- **GreenMail multipart section fetch (found in the Phase-1 spike):** GreenMail's
  `BODY[n.MIME]` response omits the terminating blank line after the MIME
  headers, so MailKit decodes nested body parts to empty entities (verified on
  1.6.15 and 2.1.11 via protocol log). Single-part messages work correctly.
  Mitigation: body-reading tests seed single-part messages; multipart-dependent
  tests are checked in `Skip`-annotated so the gap stays visible. Details in
  `docs/TESTING-INTEGRATION.md`.
- **Windows-runner process management** — background Java/Python processes
  must be health-checked (wait-for-port) and their logs uploaded on failure,
  or a hung server becomes an undiagnosable red build. Budgeted into §4.6.
- **Flakiness discipline** — the integration job stays non-required until it
  proves itself (Phase 1/3 exit criteria). Any test that flakes twice gets
  quarantined (trait-filtered) with an issue, not retried into submission.
- **Radicale 3 auth configuration** — minor spike detail (none vs. htpasswd).
- **Secret lifetime for OAuth live accounts** — refresh tokens expire and
  need manual re-consent; this is why owned-domain basic-auth accounts are the
  Tier-3 backbone and OAuth accounts are optional extras.
- **What stays untestable in CI:** interactive OAuth consent, WebView2
  rendering fidelity, screen reader experience, and provider quirks our test
  accounts can't reproduce. These remain manual, by design — the plan narrows
  the manual surface; it does not pretend to eliminate it.
