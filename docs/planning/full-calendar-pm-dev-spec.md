# Full Calendar — Best-in-Class Accessible Calendar with Sync — PM & Dev Specification

**Status:** Approved for M1 — §15 open questions Q1–Q7 resolved by Kelly on July 16, 2026 (see §15). M1 (local authoring, recurrence, time zones) may proceed; M2–M6 follow the resolved decisions below.
**Date:** June 26, 2026 (decisions resolved July 16, 2026)
**Target:** Phase 3+ (multi-release; sequenced into v-series milestones below)
**Crew:** Bravo (PM → Dev Lead → Test Enforcer)
**Depends on (shipped):**
- `ics-calendar-pm-spec.md` — ICS parsing, invite accept/decline/tentative, event card in reading pane
- `calendar-view-pm-dev-spec.md` — minimal read-only calendar list harvested from the local cache
- `graph-backend-pm-spec.md` — Microsoft Graph mail backend, MSAL OAuth, per-backend scope selection

> **Author's note for the reviewer (Kelly):** This is greenfield planning for a *complete* calendar, not an incremental tweak. The feature is large, so it is sequenced into independently shippable milestones (M1–M6). Where a design decision is genuinely open and more than one answer is defensible, I have **not** forced a single choice — §5 and §15 lay out each viable option with trade-offs and a recommendation, per your instruction to "assume all choices are valid and write up for each." Treat the recommendations as defaults you can override during review; nothing below is locked until you approve it. Several decisions (which sync stack for Google, how aggressive the month-grid accessibility model should be, whether reminders need OS-level toast) deserve your explicit call before M2+ implementation begins — they are collected in §15.

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [User Problem & Opportunity](#2-user-problem--opportunity)
3. [Design Principles](#3-design-principles)
4. [Feature Scope & Acceptance Criteria](#4-feature-scope--acceptance-criteria)
5. [Architecture & Technical Decisions](#5-architecture--technical-decisions)
6. [Keyboard Walkthrough](#6-keyboard-walkthrough-mandatory)
7. [Accessibility Checklist](#7-accessibility-checklist-mandatory)
8. [Acceptance Walkthrough](#8-acceptance-walkthrough-mandatory)
9. [Success Metrics](#9-success-metrics)
10. [Implementation Phases (Milestones)](#10-implementation-phases-milestones)
11. [Files to Create / Modify](#11-files-to-create--modify)
12. [Tests to Add](#12-tests-to-add)
13. [Known Risks](#13-known-risks)
14. [Keyboard Reference](#14-keyboard-reference)
15. [Open Questions — Decisions Needed Before M2](#15-open-questions--decisions-needed-before-m2)
16. [Implementation Guidance for AI](#16-implementation-guidance-for-ai)

---

## 1. Executive Summary

QuickMail today can parse meeting invites, send RSVP replies, and show a **read-only** list of accepted meetings harvested from the local message cache (the `CalendarEvent` table, surfaced as a virtual "Calendar" folder). It cannot create events, edit them, expand recurring meetings, remind the user before a meeting, handle time zones correctly, or — most importantly — **sync with the calendars users already keep** in Microsoft 365, Google, iCloud, Fastmail, or any CalDAV server. Users must still keep a separate calendar app open.

This spec defines a **full, best-in-class accessible calendar**: two-way sync with the common calendar services via a pluggable provider model (Microsoft Graph, Google, generic CalDAV, and read-only ICS feed subscriptions), full event authoring (create / edit / delete / invite attendees), recurring-event support (RRULE parse + expand + per-instance edits), correct time-zone handling, reminders/notifications, multiple named calendars with visibility toggles, and four navigation surfaces — Agenda (list), Day, Week, and Month — every one of which is fully keyboard-operable and designed screen-reader-first. The existing read-only calendar list becomes the Agenda view; nothing the user relies on today is removed.

The work reuses what already exists: the `ICalendarProvider` interface (designed in the minimal-calendar spec precisely as this sync plug-in point), the MSAL/OAuth stack and per-backend scope selection from the Graph mail backend, the `CommandRegistry`/F6/announcement infrastructure, and the ICS reply pipeline. The headline risk is the **accessible month grid** and **two-way conflict resolution during sync** — both are addressed explicitly in §5 and §13.

---

## 2. User Problem & Opportunity

### 2.1 Current state (verified against code)

| Surface | Today | Pain | Who feels it |
|---|---|---|---|
| View meetings | Read-only Agenda list of events harvested from cached invites (`CalendarViewModel`, `CalendarList` in `MainWindow`, virtual folder `IsCalendarView`). | Only shows meetings that arrived as email invites *and* are still in the local cache. Nothing the user created elsewhere appears. | Every user. |
| Create an event | Impossible. There is no event-authoring UI anywhere (`grep` confirms `CalendarViewModel` exposes only `RefreshCommand`, `ToggleTodayFilterCommand`, `OpenSourceMessageCommand`). | Must switch to another app to add a meeting. | Every user. |
| Edit / delete an event | Impossible. `CalendarEvent` has a settable `ResponseStatus` but no edit/delete path. | Cannot fix a typo, move a meeting, or cancel one. | Every user. |
| Recurring meetings | `IcsModel` does **not** parse `RRULE` (verified — the parser switch handles only `DTSTART/DTEND/SUMMARY/UID/...`). A weekly standup shows as a single row for one occurrence. | Misleading agenda; missed recurrences. | Anyone with recurring meetings (most professionals). |
| Time zones | `IcsModel.ParseIcsDateTime` **ignores `TZID`** and treats non-`Z` times as local machine time (documented limitation in the code). | A 10:00 Eastern meeting shows at 10:00 local for a Pacific user — wrong by hours. | Anyone collaborating across time zones. |
| Reminders | None. No notification, toast, or sound before a meeting. | Missed meetings; the app cannot replace a dedicated calendar. | Every user. |
| Sync with Google / M365 / iCloud / CalDAV | None. The only provider is `LocalCacheCalendarProvider`. | The calendar is an island; it never reflects the user's real calendar. | **Every user** — this is the headline gap. |
| Day / Week / Month views | None. Agenda list only. | No glanceable "what does my week look like." | Sighted and screen-reader users alike. |
| Multiple calendars | One merged list across accounts; no per-calendar concept, color, or visibility toggle. | Cannot separate Work / Personal / a shared team calendar. | Power users. |

**What already exists and will be reused (verified):**
- `CalendarEvent` (`Models/CalendarEvent.cs`), `CalendarResponseStatus` enum (`Pending, Accepted, Tentative, Declined, Cancelled`).
- `ICalendarProvider` (`Services/ICalendarProvider.cs`) — `LoadEventsAsync / UpsertEventAsync / UpdateResponseStatusAsync / DeleteEventAsync`. **This is the intended sync plug-in point.**
- `ICalendarService` / `CalendarService` — in-memory sorted list, refresh, status updates.
- `LocalCacheCalendarProvider` — harvests `text/calendar` parts from the cache.
- `CalendarViewModel` — Agenda list, Today filter, refresh, open-source-message; announcements via `AnnouncementRequested`.
- ICS RSVP pipeline — `IcsModel.GenerateReply`, `SendIcsReplyAsync`, accept/decline/tentative on the reading-pane event card.
- MSAL OAuth (`OAuthService`) with per-backend scopes; Graph mail backend (`GraphMailService`, `GraphSendMailService`).
- `CommandRegistry`, F6 pane ring, `AccessibilityHelper.Announce`, `BatchObservableCollection<T>`.

### 2.2 Target personas

| Persona | Who | Core need | How they'd use this |
|---|---|---|---|
| **Kelly (screen-reader user, daily driver)** | Blind professional who lives in QuickMail | One accessible app for mail *and* a real, synced calendar | Sync M365 + Google; press the calendar shortcut, arrow through today's Agenda, create a meeting with the keyboard, hear a reminder 10 min before. |
| **Cross-time-zone collaborator** | Sighted or SR, works with remote teams | Meetings shown at the *correct* local time | Accept an invite scheduled in Eastern; QuickMail shows it correctly in Pacific because TZID is honored. |
| **Power user with multiple calendars** | Work (M365) + Personal (Google) + a shared family calendar (CalDAV) | See all calendars together or filter to one; create on the right calendar | Toggle calendar visibility; pick "Personal" when creating an event; color + label distinguish them. |
| **Keyboard power user** | Mouse-free, sighted | Navigate Week/Month fast; jump to a date; create with one shortcut | `Ctrl+Shift+K` to open, `W` for week, `T` for today, `N` for new event, type, Enter. |
| **Self-hosting / privacy user** | Runs Nextcloud or Fastmail | CalDAV that "just works" without a Google/Microsoft account | Add a CalDAV calendar by URL + app password; two-way sync. |

### 2.3 Why now

- The `ICalendarProvider` seam was deliberately shipped in the minimal calendar so that adding real providers is **additive, not invasive** — the VM and store already speak to the interface.
- The MSAL/OAuth stack and the Graph backend are in the tree; adding `Calendars.ReadWrite` scope and Graph calendar endpoints is a well-trodden extension, not new auth plumbing.
- The ICS RSVP pipeline already produces and parses `text/calendar`; closing the loop so an RSVP also updates the *synced* calendar is the natural next step.
- The accessibility infrastructure (F6 ring, announcement categories, command registry) is mature, so the large UI surface can follow established patterns rather than inventing them.

---

## 3. Design Principles

1. **Screen-reader-first, every view.** The Agenda list stays the canonical, lowest-friction surface. Day/Week/Month are *additions*, never the only way to do anything. Every grid cell, every event, and every navigation move produces a complete spoken sentence. No information is conveyed by color alone — calendar identity, response status, and busy/free are always spoken as text.
2. **Keyboard parity with the best calendars, in Windows idiom.** Arrow keys move within a view; `T` = today, `D/W/M/A` switch views, `N` = new, Enter = open/edit, Delete = delete (confirmed), Ctrl+Arrows page the period. No action requires a mouse.
3. **Local-first, sync-reconciled.** SQLite is always the source the UI reads from; sync reconciles SQLite with the remote in the background. The UI never blocks on the network. Offline edits queue and replay.
4. **Pluggable providers, uniform UI.** Microsoft Graph, Google, CalDAV, and ICS-feed all implement `ICalendarProvider` (extended). The VM/View never branch on provider type. Adding a provider later touches no UI.
5. **Two-way by default, read-only where the source is read-only.** A subscribed ICS feed is read-only; a Graph/Google/CalDAV calendar is read-write. The UI disables authoring affordances for read-only calendars and *says so* when the user tries.
6. **Reuse the RSVP loop.** Accepting an invite already sends an ICS reply; with sync on, the same action also writes the response to the user's real calendar so the two never disagree.
7. **Nothing existing breaks.** The current read-only Agenda is preserved as the Agenda view. Users who never configure sync get a strictly better local calendar (authoring, recurrence, reminders) with zero new network activity.
8. **Privacy and least scope.** Calendar OAuth scopes are requested only when the user adds a calendar of that type. Credentials follow existing rules: OAuth via MSAL/DPAPI cache; CalDAV app-passwords via `CredentialService` (Windows Credential Manager), never JSON.

---

## 4. Feature Scope & Acceptance Criteria

### 4.1 In scope (full feature, across milestones M1–M6)

| Area | Capability | Setting / Shortcut | Default | Milestone |
|---|---|---|---|---|
| **Authoring** | Create event | `N` in any calendar view / `Ctrl+Shift+N` global | — | M1 |
| | Edit event | `Enter` on a writable event → editor; `E` | — | M1 |
| | Delete event | `Delete` (confirmed) | — | M1 |
| | Invite attendees | Attendee field in editor; sends ICS via existing send path | — | M1 |
| **Recurrence** | Parse + expand `RRULE` (daily/weekly/monthly/yearly, COUNT/UNTIL/BYDAY/INTERVAL) | — | — | M1 |
| | Per-instance edit scope ("This event" / "This and following" / "All events") | Prompt on edit/delete of a series | — | M1 |
| | `EXDATE` / overridden instances respected | — | — | M1 |
| **Time zones** | Honor `TZID` on parse; store UTC; display in user-chosen zone | `CalendarDisplayTimeZone` (default = system) | system zone | M1 |
| **Views** | Agenda (list) — existing, becomes the default view | `A` | Agenda | shipped/M2 |
| | Day view | `D` | — | M2 |
| | Week view | `W` | — | M2 |
| | Month view (accessible grid) | `M` | — | M3 |
| | Go to today / pick a date | `T` / `G` (go-to-date dialog) | — | M2 |
| | Page period back/forward | `Ctrl+Left` / `Ctrl+Right` | — | M2 |
| **Sync — Microsoft 365** | Two-way via Graph Calendar | Add in Calendar Accounts dialog | off until added | M4 |
| **Sync — Google** | Two-way (CalDAV **or** Google Calendar API — see §15 Q1) | Add in Calendar Accounts dialog | off until added | M4/M5 |
| **Sync — CalDAV (generic)** | Two-way (iCloud, Fastmail, Nextcloud, …) | Add by URL + credentials | off until added | M5 |
| **Sync — ICS feed** | Read-only subscription by URL | Add by URL | off until added | M5 |
| **Multiple calendars** | Per-account named calendars; color + text label; visibility toggle | Calendar list panel; `Space` toggles | all visible | M4 |
| | Default calendar for new events | `DefaultCalendarId` | first writable | M4 |
| **Reminders** | Pre-event notification (Windows toast via existing `INotificationService` + in-app `Result` announcement) | `CalendarReminders` (bool), `DefaultReminderMinutes` | **off (opt-in)** / 10 min | M6 |
| | Per-event override of reminder lead time | In editor | inherits default | M6 |
| **Search** | Find events by text/date | `Ctrl+F` within calendar | — | M3 |
| **RSVP integration** | Accepting an invite also writes the response to the synced calendar | — (automatic when synced) | on | M4 |

### 4.2 Explicitly out of scope (this spec)

- **Free/busy lookup of other attendees / scheduling assistant.** A "find a time" availability grid is a major sub-feature; deferred to a future spec. (We *send* attendee invites; we don't query their availability.)
- **Tasks / to-dos / VTODO.** Calendar events only.
- **Calendar sharing / delegation management** (granting others access to your calendar). We *consume* shared calendars the provider exposes; we don't administer permissions.
- **Resource booking** (rooms, equipment).
- **Natural-language event entry** ("lunch with Sam tomorrow at noon"). Structured editor only in v1; NL entry is a possible later enhancement.
- **Print / export to PDF.** `.ics` export of a single event *is* in scope (M1, reuses ICS generation); printing a month grid is not.
- **Webhook push for calendar changes.** Like the Graph mail backend, sync is **poll-based** (configurable cadence). Webhooks need a public callback URL — impractical for a desktop client.
- **Outlook categories / Google event colors as first-class taxonomy.** We render a per-*calendar* color; per-event category colors are display-only passthrough at most, not an editing surface.
- **Time-zone *picker per event*** (a meeting authored in a non-local zone). v1 authors in the display zone; storing/showing a per-event source zone is M-later if requested.

### 4.3 Acceptance criteria (feature-level)

- A user can add a Microsoft 365, Google, and CalDAV calendar, and a read-only ICS feed, and see all their events merged in the Agenda — each event spoken with its calendar name.
- A user can create, edit, and delete an event entirely by keyboard, and the change propagates to the remote calendar within one sync cycle.
- A recurring weekly meeting shows on every correct date; editing "this event only" does not alter the series; the remote reflects the chosen scope.
- A meeting scheduled in a different time zone displays at the correct local time.
- A reminder fires before a meeting with an accessible announcement; the user can dismiss or snooze by keyboard.
- Month, Week, Day, and Agenda are all fully navigable by keyboard, and every move is announced.
- Accepting an emailed invite updates both the RSVP reply *and* the synced calendar entry.
- With no calendars configured, the local authoring/recurrence/reminder features still work against the local store; `--online` mode degrades gracefully (see §5.2).

---

## 5. Architecture & Technical Decisions

### 5.1 Key architectural decisions

#### Decision 1 — Extend `ICalendarProvider` into a capability-typed, multi-calendar, syncable interface

The shipped interface assumes one flat event list. A full provider must expose **multiple calendars**, **write-back**, **per-calendar read-only**, and **incremental sync tokens**.

**Choice:** Evolve `ICalendarProvider` to:

```csharp
public interface ICalendarProvider
{
    string ProviderId { get; }              // stable id for this configured provider instance
    CalendarProviderKind Kind { get; }      // LocalCache | MicrosoftGraph | Google | CalDav | IcsFeed
    bool SupportsWrite { get; }

    Task<IReadOnlyList<CalendarCollection>> GetCalendarsAsync(CancellationToken ct = default);
    Task<CalendarSyncResult> SyncAsync(string calendarId, string? syncToken, CancellationToken ct = default);

    // Write path (no-op / throws NotSupported for read-only providers)
    Task<string> CreateEventAsync(string calendarId, CalendarEvent evt, CancellationToken ct = default);
    Task UpdateEventAsync(string calendarId, CalendarEvent evt, EditScope scope, CancellationToken ct = default);
    Task DeleteEventAsync(string calendarId, string uid, EditScope scope, CancellationToken ct = default);
    Task SetResponseStatusAsync(string calendarId, string uid, CalendarResponseStatus status, CancellationToken ct = default);
}
```

The shipped methods (`LoadEventsAsync`, `UpsertEventAsync`, `UpdateResponseStatusAsync`, `DeleteEventAsync`) are kept on `LocalCacheCalendarProvider` as internal harvest plumbing or adapted; the *public* contract becomes the above. `CalendarSyncResult` carries changed/deleted events + a new `syncToken`.

**Alternatives:**
1. *Keep the flat interface, add sync as a side API.* Con: every provider needs multi-calendar; bolting it on later means a second refactor.
2. *One mega-provider with `if (kind == …)`.* Con: violates Principle 4; untestable; exactly what the seam was created to avoid.

**Rationale:** The interface was introduced for this moment. Capability flags (`SupportsWrite`) + `EditScope` keep the VM provider-agnostic while letting read-only feeds participate uniformly.

#### Decision 2 — Local SQLite is the single source the UI reads; a `CalendarSyncService` reconciles in the background

**Choice:** Add `Calendar` (calendar collections) and extend `CalendarEvent` tables (schema migration). The UI binds only to the store. `CalendarSyncService` runs a poll loop per writable provider (default 5 min, configurable), pulls remote changes via `SyncAsync`, pushes queued local edits, and resolves conflicts (Decision 5).

**Alternatives:**
1. *UI reads providers directly / live.* Con: blocks on network; breaks offline; defeats `--online` reasoning.
2. *Sync only on demand.* Con: stale calendar; reminders can't fire for events not yet pulled.

**Rationale:** Mirrors the proven mail model (SQLite-of-record, background sync, poll-not-webhook). Reminders, search, and all four views operate on local data and stay fast.

#### Decision 3 — Calendar UI is a **mode of the existing main window**, not a separate window; the Agenda virtual folder grows view-switching and an authoring editor

The shipped calendar is already a virtual folder (`IsCalendarView`) with `CalendarList` in `MainWindow`. We keep that home and add:
- A **view switcher** (Agenda/Day/Week/Month) hosted in the same content region.
- A **calendar list panel** (the named calendars with visibility checkboxes) — a new F6 stop *only while the calendar mode is active*.
- An **event editor** opened as a **modeless** window (`EventEditorWindow`), per the modal-dialog rules in CLAUDE.md (editable text over a window with a live WebView2 reading pane → must be modeless; Escape/Cancel wired explicitly).

**Alternatives:**
1. *Separate top-level `CalendarWindow`.* Con: New Window Checklist burden (own F6 ring, focus restoration across windows is fragile); duplicates the main shell.
2. *Editor as a `ShowDialog()` modal.* Con: directly violates the enforced modal-dialog rule — editable `TextBox` + WebView2 + screen reader + nested message loop = frozen dispatcher (the GrabAddresses lockup). **Editor must be modeless.**

**Rationale:** Lowest-risk integration; reuses the existing F6 ring, focus, and command infrastructure; respects the hard-won modal rules.

#### Decision 4 — Day/Week/Month grids are native WPF `Grid`/`ItemsControl` with **explicit `AutomationPeer` / data-table semantics**, never WebView2

**Choice:** Render time grids with native controls. The **month grid** is the accessibility crux. Options for its a11y model (see §15 Q2 — pick before M3):
- **Option A (recommended): table semantics.** Expose the month as a 7-column data grid; each day cell announces "Tuesday June 9, 3 events" on focus, Enter drills into that day's Agenda. Arrow keys move by day/week with wrap; screen readers get real row/column context.
- **Option B: list-of-days.** Flatten month to a vertical list of day rows (no 2-D grid). Simpler a11y, loses the spatial week structure sighted users expect.
- **Option C: hybrid.** Visual 2-D grid for sighted users; a parallel off-screen day list as the accessible tree.

**Rationale:** Native controls keep navigation in the UIA tree (most reliable for screen readers) and avoid WebView2 pool pressure and JS F6-relay complexity. Option A gives the richest experience but is the most work; the decision is flagged for your call.

#### Decision 5 — Sync conflict resolution: **last-writer-wins by `SEQUENCE`/`ETag`, with a conflict log**

**Choice:** Each event tracks the remote `ETag`/`SEQUENCE` and a local `dirty` flag. On sync, if both sides changed: the higher `SEQUENCE` (or server `ETag` mismatch with no local edit) wins; if both edited concurrently, **server wins and the local edit is preserved as a duplicate "(local copy)" event** plus a `Result` announcement, so no user edit is silently lost.

**Alternatives:**
1. *Always server-wins (drop local).* Con: silent data loss — unacceptable.
2. *Always prompt the user.* Con: a modal mid-sync violates the "no modal during background work" posture and is hostile for screen-reader users; only prompt for true concurrent edits is the compromise.

**Rationale:** Conservative; never loses a user edit; matches how mature clients behave. Concurrent edits are rare for a single-user desktop client.

#### Decision 6 — Reuse MSAL for Graph calendar; choose Google + CalDAV credential paths

- **Microsoft Graph calendar:** add `https://graph.microsoft.com/Calendars.ReadWrite` to a calendar-scoped scope set in `OAuthService`. Reuses the entire MSAL/DPAPI cache. (M365 mail and calendar can share one MSAL account.)
- **Google:** two viable stacks — Google Calendar **API** (Google OAuth, `https://www.googleapis.com/auth/calendar`) **or** Google's **CalDAV** endpoint (also Google OAuth). See §15 Q1.
- **CalDAV (generic):** URL + username + **app password**, stored via `CredentialService` (Credential Manager), never JSON. Auto-discovery via `.well-known/caldav` + `PROPFIND`.
- **ICS feed:** plain HTTPS GET of an `.ics` URL on a poll; read-only; no credentials (or optional basic auth via `CredentialService`).

**Rationale:** Maximizes reuse of the existing auth stack; keeps credential handling within the enforced rules.

#### Decision 7 — Recurrence is expanded **in a shared `RecurrenceExpander`**, stored as a master event + materialized instances within the visible window

**Choice:** Parse `RRULE/EXDATE/RDATE` into a master `CalendarEvent` (with `RecurrenceRule`), and expand occurrences **lazily for the currently visible date window** (plus a reminder look-ahead). Overrides (a single modified instance) are stored as exception events keyed by `(Uid, RecurrenceId)`.

**Alternatives:**
1. *Materialize all occurrences forever.* Con: unbounded rows for "daily, no end."
2. *Expand only at render with no storage.* Con: reminders need look-ahead beyond the visible window.

**Rationale:** Bounded storage, correct reminders, standard iCalendar override model.

### 5.2 Runtime mode compatibility

| Mode | LocalStore available? | Calendar behavior |
|---|---|---|
| Normal | ✓ | Full functionality. SQLite of record; background sync per configured provider. |
| `--online` | ✗ | **Decision needed (§15 Q5).** Two defensible options: (A) calendar disabled with a clear Hint (matches shipped minimal-calendar behavior), or (B) calendar runs **purely against remote providers** with no local cache (no reminders/search, live-fetch each view). Recommended: **A for M1–M3 (local features need the store), reconsider B once sync lands.** Either way: no crash, no `SqliteException`. |
| `--profileDir <path>` | ✓ (alt path) | Works against the alternate `mail.db`; calendar tables live there too. |

### 5.3 Code reuse and duplication risks

- **ICS generation** (`IcsModel.GenerateReply`, `EscapeIcsText`) — extend into a fuller `IcsWriter` for *outbound event creation/invites* and *single-event `.ics` export*, rather than duplicating escaping/formatting. Plan: extract a shared `IcsSerializer` used by both reply and create paths.
- **`IcsModel.Parse`** — currently lacks `RRULE`, `TZID`, `EXDATE`, attendees. Extend the *same* parser (don't fork) and add `RecurrenceRule`, time-zone resolution, and attendee parsing. `IcsModelTests` must keep passing and grow.
- **OAuth scopes** — `OAuthService` already selects scopes per backend. Add a calendar scope set; reuse `GetAccessTokenAsync(account, scopes, ct)`. No new auth flow.
- **Graph HTTP plumbing** — `GraphMailService` already has the HttpClient + token + throttling pattern. Extract the shared bits (auth header, 429 `Retry-After` backoff, `$batch`) into a `GraphHttp` helper used by both mail and calendar, rather than copying.
- **`BatchObservableCollection<T>`** — reuse for all event collections to avoid per-insert SR re-announcement.
- **Announcement / F6 / focus-restoration** — reuse existing patterns; no new infra except registering new commands and new (mode-scoped) F6 stops.

### 5.4 Shared component audit (mandatory)

| Component | File | Other consumers | Change needed | Risk |
|---|---|---|---|---|
| `ICalendarProvider` | `Services/ICalendarProvider.cs` | `LocalCacheCalendarProvider`, `CalendarService`, stubs | **Breaking** interface expansion (Decision 1). | Every implementer + stub must update. Update `LocalCacheCalendarProvider` and `StubServices.cs` in the same PR. |
| `ICalendarService` / `CalendarService` | `Services/I?CalendarService.cs` | `CalendarViewModel`, `MainViewModel`, stubs | Add multi-calendar, create/update/delete, sync orchestration (or delegate to new `CalendarSyncService`). | VM + stub updates; keep `Events`/`RefreshAsync` working so Agenda doesn't regress. |
| `CalendarEvent` | `Models/CalendarEvent.cs` | provider, store, VM, view, tests | Add `CalendarId`, `ProviderId`, `RecurrenceRule`, `RecurrenceId`, `ETag`, `IsDirty`, `Attendees`, `ReminderMinutes`, `SourceTimeZone`, `IsReadOnly`. | Additive properties; `DisplayLine` must still produce the spoken sentence (extend, don't break). |
| `IcsModel` | `Models/IcsModel.cs` | `ImapMailService`, `MainViewModel`, `LocalCacheCalendarProvider`, `IcsModelTests` | Add `RRULE/EXDATE/TZID/ATTENDEE` parsing; extract `IcsSerializer`. | Existing invite-card path must be unaffected; grow `IcsModelTests` first. |
| `CalendarViewModel` | `ViewModels/CalendarViewModel.cs` | `MainViewModel`, `MainWindow`, `CalendarViewModelTests` | Add view mode, navigation, authoring commands, calendar-visibility, reminders surface. | Keep Agenda behavior + `OpenSourceMessageRequested`/`AnnouncementRequested` events intact. |
| `MainViewModel` | `ViewModels/MainViewModel.cs` | `MainWindow`, every VM-construction test | New services in ctor (`CalendarSyncService`, providers); new commands; RSVP→sync wiring. | Constructor signature change breaks stub-based tests — update `StubServices.cs` + `ViewModelConstructionTests` same PR. |
| `MainWindow.xaml(.cs)` | `Views/MainWindow.xaml(.cs)` | XAML/F6/hotkey tests | Host view switcher + calendar-list panel + editor launch; new mode-scoped F6 stops; new key handling in calendar mode. | F6 ring changes — re-run `ViewManagerHotkeyIntegrationTests`; ensure stops are added only while calendar mode is active. |
| `LocalStoreService` / `ILocalStoreService` | `Services/*LocalStoreService*.cs` | `SyncService`, VM, store tests, stubs | New `Calendar` table + `CalendarEvent` columns + sync-token/queue tables; new CRUD methods. **Schema migration (next `user_version`).** | Migration must be additive; every stub must implement new methods or compilation breaks. |
| `OAuthService` | `Services/OAuthService.cs` | mail auth | Add `CalendarScopes` set(s). | Additive; no change to mail flows. |
| `GraphMailService` | `Services/GraphMailService.cs` | mail | Extract shared `GraphHttp` helper. | Refactor risk to mail — keep behavior; cover with existing mail tests. |
| `ConfigModel` / `ConfigService` | `Models/ConfigModel.cs`, `Services/ConfigService.cs` | everywhere settings are read | New keys: display time zone, reminders on/default minutes, sync cadence, default calendar, per-provider config index. | Additive INI keys; round-trip tests. |
| `CommandRegistry` | (registrations in `MainViewModel`/`MainWindow`) | palette, hotkey dialog, tests | Register all new calendar commands (category `View` or new `Calendar`? see §15 Q4). | `CommandRegistryTests` extended. |
| `App.xaml.cs` | `App.xaml.cs` | DI root | Construct providers, `CalendarSyncService`; dispose in `OnExit`. | DI wiring; new `IDisposable` for sync loop — follow the IDisposable rules (Cancel before Dispose). |

**Expected outcome:** "This feature expands `ICalendarProvider`/`ICalendarService` (breaking — all implementers + stubs updated in the same PR), grows `CalendarEvent`/`IcsModel` additively, adds a `CalendarSyncService` and per-provider implementations, and extends `MainViewModel`/`MainWindow` with a view switcher, calendar panel, and modeless editor. The riskiest blast radii are the provider-interface change (mitigated by updating stubs first) and the F6 ring change (mitigated by mode-scoping the new stops and re-running the hotkey integration tests)."

---

## 6. Keyboard Walkthrough (Mandatory)

> Covers the headline paths across milestones. Per-milestone implementation should re-verify the relevant subset. The default open shortcut is **`Ctrl+Shift+K`** for the calendar mode (K = calendar; `Ctrl+Shift+C` was the older minimal-calendar proposal but the shipped build reaches the calendar via the folder tree — see §15 Q3 to confirm the canonical gesture). Where a step's announcement category matters, it is noted (Hint/Status/Result).

### Path: Open calendar and switch views

1. User presses `Ctrl+Shift+K`. **Expected:** Calendar mode activates in the content region, Agenda view shown, focus on the event list. SR: "Calendar, Agenda view. N events. Press D for day, W for week, M for month. Press N for new event. Press T for today." (Hint)
2. User presses `W`. **Expected:** Week view replaces Agenda, focus on the current day/time cell. SR: "Week of June 22 to 28. Monday June 22." (Status)
3. User presses `M`. **Expected:** Month grid, focus on today's cell. SR: "June 2026. Friday June 26, 2 events." (Status)
4. User presses `A`. **Expected:** Back to Agenda, focus restored to the previously focused event. SR announces that event's full line. (Status)

### Path: Navigate Month grid (Option A semantics)

1. Month view, focus on "Friday June 26, 2 events". User presses Right. **Expected:** Focus to Saturday June 27. SR: "Saturday June 27, no events." (Status)
2. User presses Down. **Expected:** Focus to the same weekday next week (Saturday July 4). SR announces it; if it crosses into the next month, the grid pages and SR says "July 2026. Saturday July 4, …". (Status)
3. User presses Enter on a day with events. **Expected:** Drill into that day's Agenda (Day view scoped to that date), focus on the first event. SR: "Tuesday June 30, 3 events. Team standup, 9:00 to 9:30, Accepted, Work calendar." (Result)
4. User presses Escape. **Expected:** Return to Month grid at the same day cell. (Status)

### Path: Create an event (keyboard-only)

1. Any view. User presses `N`. **Expected:** Modeless `EventEditorWindow` opens, focus on the Title field. SR: "New event. Title, edit." (the field's short Name only). A Hint fires once: "Tab through title, date, time, calendar, attendees. Press Ctrl+Enter to save, Escape to cancel." (Hint)
2. User types a title, Tab to Start date, types/picks a date, Tab to Start time, etc. **Expected:** Each field announces its label and value; date/time use accessible pickers (editable text + spinner, not a mouse-only calendar popup).
3. User Tabs to **Calendar** combo. **Expected:** SR: "Calendar, combo box, Work." Default is `DefaultCalendarId`. Read-only calendars are not listed.
4. User Tabs to **Attendees**, types `sam@example.com`. **Expected:** Autocomplete from contacts (reusing the existing contact autocomplete pattern). Adding an attendee will cause an ICS invite to be sent on save.
5. User Tabs to **Reminder**, leaves default (10 min). User Tabs to **Recurrence**, optionally sets "Weekly on Tuesday".
6. User presses `Ctrl+Enter`. **Expected:** Editor closes (modeless → explicit `Close()`), event appears in the view, written to the local store, queued for sync, and if attendees were added, an ICS invite is sent. SR: "Event created. Team sync, Tuesday June 30, 9:00." (Result) Focus returns to the originating view at the new event.
7. **Error case** — save with empty title: SR: "Title is required." (Result) Focus stays in the Title field; editor does not close.

### Path: Edit a recurring event (scope prompt)

1. Focus a recurring instance, press `Enter` (or `E`). **Expected:** Because it is part of a series, a small **scope choice** appears *inside the editor header* (not a separate modal): a radio group "This event / This and following / All events", default "This event". SR: "This is a repeating event. Choose what to change. This event, radio, selected." (Hint) — radio group is one tab stop with directional navigation.
2. User changes the time, picks "All events", `Ctrl+Enter`. **Expected:** The series master updates; the change syncs with the chosen scope. SR: "All occurrences updated." (Result)

### Path: Delete an event

1. Focus an event, press `Delete`. **Expected:** Confirmation requested via VM event (View shows it). For a series: scope radio (This / Following / All). SR: "Delete Team sync? This event, radio, selected. Press Enter to confirm, Escape to cancel." (Hint)
2. User confirms. **Expected:** Event removed locally, deletion queued for sync, ICS cancellation sent if the user is the organizer with attendees. SR: "Event deleted." (Result) Focus moves to the adjacent event.

### Path: Add a calendar account & first sync

1. User opens Calendar Accounts (command `calendar.accounts`, e.g. via palette). **Expected:** Modeless dialog listing configured calendar sources with an "Add" button. SR: "Calendar accounts. N configured."
2. User Adds → chooses type (Microsoft 365 / Google / CalDAV / ICS feed). For M365: OAuth flow reuses the system browser; SR: "Opening Microsoft sign-in in your browser." (Status) On success: "Signed in. Discovering calendars." (Status) then "3 calendars found: Calendar, Birthdays, Team. All shown." (Result)
3. Background sync runs; Agenda populates. SR (if `AnnounceStatus`): "Calendar sync complete. N events." (Status)
4. **Read-only feed:** Adding an ICS URL → "Subscribed to Holidays. Read only." (Result) Authoring on its events is disabled; pressing `N` while that calendar is the only target says "This calendar is read only." (Result)

### Path: Toggle calendar visibility

1. User presses `F6` until "Calendars" panel (mode-scoped stop). SR: "Calendars list. 4 calendars." Arrow to "Personal (Google)". SR: "Personal, Google, shown, checkbox, checked."
2. User presses `Space`. **Expected:** Personal events hidden from all views. SR: "Personal hidden." (Result)

### Path: Reminder fires

1. A meeting is 10 minutes away. **Expected:** An in-app reminder banner appears (and OS toast + sound if enabled). SR: "Reminder. Team sync in 10 minutes, 9:00, Work calendar. Press Enter to open, S to snooze, Escape to dismiss." (Result — reminders are always Result-category content the user opted into via `CalendarReminders`).
2. User presses `S`. **Expected:** Snooze sub-choice (5 min default). SR: "Snoozed 5 minutes." (Result)

### Path: Time zone correctness

1. User accepts an invite whose `DTSTART;TZID=America/New_York:...T100000`. **Expected:** Stored as UTC; displayed in the user's display zone (e.g. Pacific → "7:00 AM"). SR Agenda line reflects 7:00, not 10:00. (Result on focus)

### Path: `--online` graceful degradation (recommended Option A)

1. App launched `--online`. User presses `Ctrl+Shift+K`. **Expected:** Calendar shows: "Calendar requires the local message cache and is unavailable in online mode." SR speaks it (Hint). No crash. Escape returns to mail.

---

## 7. Accessibility Checklist (Mandatory)

- **`AutomationProperties.Name` (short labels only):**
  - Event list / Day / Week: `"Calendar events"`; each event row Name = its spoken `DisplayLine` (summary, time, status, calendar) — no role word.
  - Month grid: cell Name = `"Tuesday June 9, 3 events"` (computed); grid Name = `"Month June 2026"`.
  - View switcher buttons: `"Agenda"`, `"Day"`, `"Week"`, `"Month"`.
  - Calendars panel: `"Calendars"`; each row `"Personal, Google, shown"` (checkbox state via UIA, not in the Name string for the checkbox itself).
  - Editor fields: `"Title"`, `"Start date"`, `"Start time"`, `"End time"`, `"Calendar"`, `"Attendees"`, `"Reminder"`, `"Repeat"`, `"Location"`, `"Notes"`. No hints baked in.
  - Reminder banner controls: `"Open event"`, `"Snooze"`, `"Dismiss"`.
- **`AnnouncementCategory`:**
  - View switches, date navigation, grid moves, sync-progress → `Status`.
  - Counts after filter/sync-complete, create/edit/delete confirmations, errors, RSVP results, reminders → `Result`.
  - First-focus intros ("Press D for day…"), repeating-event scope explanation, read-only feed explanation, `--online` unavailable → `Hint`.
  - No `force: true` for regular content. (A meta "Calendar reminders turned off/on" toggle confirmation may use force, per the master-switch convention.)
- **Screen-reader browse mode / WebView2:** No WebView2 in any calendar view or the editor. All native controls → standard UIA navigation; no JS F6 relay needed. (The reading pane keeps its WebView2 only for the *source invite* opened from an event.)
- **Focus restoration:**
  - View switches restore focus to the equivalent item in the new view (or today).
  - Editor (modeless): capture the originating focused element/index; on `Close()` (save, Escape, or Cancel) return focus there. Escape handled via `PreviewKeyDown` (guarded against stealing Escape from an open ComboBox dropdown), Cancel via `Click`, Save via `Ctrl+Enter` then `Close()`.
  - Drill from Month → Day → Escape returns to the same month cell.
- **F6 ring:** Calendar mode adds up to two **mode-scoped** stops — the calendar content (view) and the "Calendars" panel — present in the ring **only while calendar mode is active**, removed when the user returns to mail. Update `CycleFocusAsync` / `GetFocusedPaneIndex` accordingly and re-run the hotkey integration tests. Do not strand any stop.
- **Checkbox / radio groups:**
  - Calendars-visibility list = checkboxes (`Space` toggles), each its own logical item.
  - Edit/Delete **scope** = radio group: single tab stop, `TabNavigation="Once"`, `DirectionalNavigation="Cycle"`, shared `GroupName`.
- **Color-only information:** Never sole indicator. Calendar identity, response status, busy/free, read-only — all spoken as text. Per-calendar color is a supplementary dot/swatch with a text label beside it.
- **Date/time entry:** Editable text fields with spinner support and clear formats; no mouse-only popup calendar as the only entry path. A keyboard-operable date picker (arrow keys within a roving-tabindex day grid) may supplement it.

---

## 8. Acceptance Walkthrough (Mandatory)

> Run per milestone for the subset that has landed. Mark each step pass/fail.

### Scenario: Create, sync, and verify a new event (M1 + M4)

**Setup:** Normal mode, one writable calendar configured (or local-only for M1).

1. Open calendar, press `N`. **Verify:** Editor opens modeless, focus on Title, Hint heard once.
2. Enter title, start/end date+time, leave Calendar = default, `Ctrl+Enter`. **Verify:** Editor closes; event appears in Agenda; "Event created…" (Result); focus on the new event.
3. (M4) Wait one sync cycle / force sync. **Verify:** Event appears on the remote calendar (check provider's web UI). No duplicate locally.
4. **Edge:** Save with empty title. **Verify:** "Title is required." (Result); editor stays open; focus in Title.

### Scenario: Recurring series + per-instance edit (M1)

1. Create a weekly-on-Tuesday event. **Verify:** It appears on the next several Tuesdays in Week/Month.
2. Open one instance, choose "This event", change time, save. **Verify:** Only that instance moves; others unchanged.
3. Open another instance, choose "All events", change location, save. **Verify:** All future instances reflect the new location; the overridden one keeps its own time but gets the new location (or per RFC override rules — document the chosen behavior).

### Scenario: Time-zone correctness (M1)

1. Import/accept an invite with `TZID=America/New_York` 10:00. **Verify:** Displayed at the correct local time for the machine's display zone; Agenda line and editor both correct.

### Scenario: Month grid keyboard + screen reader (M3)

1. Enter Month, arrow around. **Verify:** Every move announces weekday, date, and event count; week-crossing and month-paging announce correctly.
2. Enter on a day with events → Day view; Escape → back to the same cell. **Verify:** Focus restoration exact.

### Scenario: Multi-provider merge + visibility (M4/M5)

1. Configure M365 + Google + CalDAV + one ICS feed. **Verify:** Agenda merges all; each event's calendar is spoken.
2. F6 to Calendars, `Space` to hide one. **Verify:** Its events vanish from all views; "X hidden" (Result).
3. **Read-only feed:** attempt `N` targeting it. **Verify:** "This calendar is read only." (Result); no editor for it.

### Scenario: Conflict resolution (M4)

1. Edit an event locally while offline; change the same event on the provider's web UI; reconnect/sync. **Verify:** Server version wins, local edit preserved as "(local copy)"; "Sync conflict on Team sync; your edit kept as a copy." (Result). No silent loss.

### Scenario: Reminder (M6)

1. Set an event 1 minute out with a 1-minute reminder. **Verify:** Banner + (if enabled) toast + sound; SR speaks it; `S` snoozes; Enter opens; Escape dismisses.

### Scenario: RSVP → sync (M4)

1. With sync on, accept an emailed invite from the reading-pane card. **Verify:** RSVP reply sent (existing) **and** the synced calendar shows the event as Accepted.

### Scenario: `--online` degradation

1. Launch `--online`, open calendar. **Verify:** Unavailable message (or remote-only mode per §15 Q5); no crash/`SqliteException`.

### Scenario: Regressions

1. Existing Agenda (read-only harvested) still works when no providers are configured. **Verify:** Today filter, open-source-message, F6, announcements unchanged.
2. `dotnet test` — all prior tests pass; provider-interface and store-schema changes covered. **Verify:** Green.

---

## 9. Success Metrics

- **Behavioral:** A user with M365 + Google + a CalDAV calendar sees a unified, correct Agenda; creating an event locally appears on the right remote calendar within one sync cycle.
- **Keyboard-centric:** Every action (open, switch view, navigate grid, create, edit, delete, RSVP, toggle calendar, snooze reminder) is keyboard-only with no stranded focus, verified in §6/§8.
- **Accessibility:** Screen-reader user can operate the Month grid, hear complete event sentences everywhere, and never depends on color. No WebView2 in calendar surfaces.
- **Correctness:** TZID-bearing events display at the right time; recurring series expand on correct dates; per-instance edits don't corrupt the series.
- **Resilience:** Offline edits queue and replay; conflicts never lose a user edit; throttling (429) is handled with backoff.
- **No regressions:** Existing mail, the read-only Agenda, F6 ring (when calendar inactive), and all current tests are unchanged.
- **Online mode:** No crash; documented degradation behavior.

---

## 10. Implementation Phases (Milestones)

Each milestone is independently shippable and code-reviewable. M1–M3 deliver a vastly better **local** calendar (authoring, recurrence, time zones, all four views) with **no new network**. M4–M6 add sync and reminders. This lets value ship early and isolates the network/sync risk.

### M1 — Authoring, recurrence, time zones (local store)

> **Progress — July 16, 2026 (branch `claude/local-calendar-authoring`).** Landed local M1 (partial) + part of M2:
> - **Local appointment authoring** (create / edit / delete) via `EventEditorViewModel` + modeless `EventEditorWindow`; local events use `CalendarEvent.AccountId == Guid.Empty` as a "local calendar" sentinel.
> - **Master/detail view** — columned `ListView` (Subject / When / Status) over a read-only Details pane (the ghmanage pattern Kelly asked for).
> - **All-day appointments** — one additive `is_all_day` column (idempotent ALTER); editor checkbox disables the time fields; all-day-aware display.
> - **Agenda / Day / Week views** (`CalendarViewMode`) — a windowed filter over the same accessible list plus a period label and navigation (A/D/W, Ctrl+Left/Right, T = today). New `Calendar` command category.
> - Tests: `EventEditorViewModelTests`, `CalendarViewModelTests` (authoring + view windowing), `CalendarStoreTests` (all-day round-trip); 56 calendar + 35 XAML/construction pass.
>
> **Still open:** recurrence (RRULE parse/expand + `RecurrenceExpander`), TZID handling, `.ics` export, the **Month grid** (M3 — deliberately deferred: needs a custom `AutomationPeer` validated live with Kelly's screen reader per Q2), an optional richer 2-D week time-grid, and all sync/reminders (M4–M6). Open gesture is the already-shipped `Ctrl+Shift+C` (not `Ctrl+Shift+K` — the Q3 premise that no toggle existed was inaccurate). **Open a11y question for Kelly:** whether the Agenda list should navigate row-as-sentence (current) or column-by-column.

**Goal:** Create/edit/delete events locally; recurring series expand correctly; TZID honored; single-event `.ics` export.

**Deliverables:**
- Extend `CalendarEvent` (recurrence, attendees, reminder, source TZ, calendarId placeholder, ETag/dirty for later).
- Extend `IcsModel.Parse` (RRULE/EXDATE/TZID/ATTENDEE); extract `IcsSerializer`; create `RecurrenceExpander` + `RecurrenceRule` model.
- `EventEditorWindow` (modeless) + `EventEditorViewModel`; create/edit/delete commands; scope radio for series.
- `LocalStoreService` schema migration: `Calendar` table (local default calendar) + new `CalendarEvent` columns + CRUD.
- Wire delete/RSVP/create into `CalendarService`.

**Tests:** `RecurrenceExpanderTests`, expanded `IcsModelTests`, `IcsSerializerTests`, `EventEditorViewModelTests`, `CalendarStoreTests` (migration + CRUD).

**Risk:** Recurrence edge cases (DST transitions, monthly-by-day). Mitigation: table-driven tests against known iCalendar fixtures.

**Duration:** 6–9 hours.

### M2 — Day & Week views + navigation

**Goal:** Day and Week native grids; view switcher; date navigation; go-to-date.

**Deliverables:** `CalendarViewModel` view-mode state + nav commands; `DayView`/`WeekView` controls; view switcher in `MainWindow`; command registrations; mode-scoped F6.

**Tests:** `XamlParseTests`, `CalendarViewModelTests` (view switch, paging, today/go-to), `CommandRegistryTests`, `ViewManagerHotkeyIntegrationTests`.

**Risk:** F6 ring + key handling conflicts. Mitigation: mode-scope the stops; run hotkey tests first.

**Duration:** 6–8 hours.

### M3 — Month grid (accessible) + search

**Goal:** Accessible Month grid (per §15 Q2 decision) with custom `AutomationPeer` semantics; in-calendar search.

**Deliverables:** `MonthView` + `AutomationPeer`; arrow/page navigation; drill-to-day; `Ctrl+F` search over local events.

**Tests:** `MonthViewAutomationTests` (peer exposes correct row/col + names), navigation tests, search tests.

**Risk:** Custom AutomationPeer correctness. Mitigation: this is the highest-a11y-risk milestone — manual screen-reader pass is mandatory before merge (Session 3).

**Duration:** 8–12 hours.

### M4 — Microsoft Graph calendar sync (two-way) + multi-calendar UI + conflict handling + RSVP→sync

**Goal:** Real two-way sync for M365; calendars panel with visibility; conflict resolution; RSVP writes through.

**Deliverables:** Expand `ICalendarProvider`; `GraphCalendarProvider` (+ extract `GraphHttp`); `CalendarSyncService` (poll loop, queue, conflict per Decision 5); add `Calendars.ReadWrite` scope; `CalendarAccountsWindow` (modeless); calendars panel; store: sync-token + edit-queue tables.

**Tests:** `GraphCalendarProviderTests` (HttpMessageHandler stub), `CalendarSyncServiceTests` (pull/push/conflict/offline-replay), `CalendarAccountsViewModelTests`.

**Risk:** Two-way conflict + token handling. Mitigation: Decision 5 conservative model; extensive sync unit tests with simulated concurrent edits.

**Duration:** 12–18 hours.

### M5 — Google + CalDAV + ICS-feed providers

**Goal:** Google (per §15 Q1), generic CalDAV (iCloud/Fastmail/Nextcloud), read-only ICS feed.

**Deliverables:** `GoogleCalendarProvider`, `CalDavCalendarProvider` (PROPFIND/REPORT, ETag sync, app-password via `CredentialService`), `IcsFeedProvider` (read-only); add types to the Calendar Accounts dialog.

**Tests:** `CalDavCalendarProviderTests` (stubbed DAV responses), `IcsFeedProviderTests`, Google provider tests.

**Risk:** CalDAV server quirks. Mitigation: test against fixtures from the three target servers; document tested servers.

**Duration:** 12–18 hours.

### M6 — Reminders & notifications

**Goal:** Pre-event reminders (Windows toast via the existing `INotificationService`, plus the in-app `Result` announcement), snooze, per-event override. Reminders default **off** (opt-in).

**Deliverables:** `ReminderService` (look-ahead timer over local store); `ShowReminder(...)` added to `INotificationService` / `WindowsToastNotificationService` (reuses the shipped toast dependency — no new package); reminder commands (Open / Snooze / Dismiss); settings (on/off default off, default minutes); editor reminder field already in M1.

**Tests:** `ReminderServiceTests` (fires at correct lead time, snooze, respects setting), settings round-trip.

**Risk:** Timer accuracy / sleep-wake. Mitigation: re-evaluate look-ahead on resume; `PeriodicTimer` with drift correction.

**Duration:** 5–7 hours.

---

## 11. Files to Create / Modify

### Create (high level)

| File | Purpose |
|---|---|
| `Models/RecurrenceRule.cs` | Parsed RRULE/EXDATE/RDATE |
| `Models/CalendarCollection.cs` | A named calendar (id, name, color, read-only, providerId, accountId) |
| `Models/EditScope.cs` | Enum: ThisEvent \| ThisAndFollowing \| AllEvents |
| `Models/CalendarProviderKind.cs` | Enum of provider kinds |
| `Helpers/RecurrenceExpander.cs` | Expand a master into instances within a window |
| `Helpers/IcsSerializer.cs` | Outbound ICS (create/invite/export); extracted from `IcsModel` |
| `Services/CalendarSyncService.cs` (+ interface) | Poll, push, conflict, queue |
| `Services/GraphCalendarProvider.cs` | M365 two-way |
| `Services/GoogleCalendarProvider.cs` | Google two-way |
| `Services/CalDavCalendarProvider.cs` | Generic CalDAV two-way |
| `Services/IcsFeedProvider.cs` | Read-only feed |
| `Services/GraphHttp.cs` | Shared Graph HTTP (auth, 429 backoff, $batch) |
| `Services/ReminderService.cs` (+ interface) | Pre-event reminders |
| `ViewModels/EventEditorViewModel.cs` | Authoring |
| `ViewModels/CalendarAccountsViewModel.cs` | Manage calendar sources |
| `Views/EventEditorWindow.xaml(.cs)` | Modeless editor |
| `Views/CalendarAccountsWindow.xaml(.cs)` | Modeless accounts manager |
| `Views/DayView.xaml(.cs)`, `WeekView.xaml(.cs)`, `MonthView.xaml(.cs)` (+ `MonthViewAutomationPeer.cs`) | View surfaces |
| `Views/ReminderBanner.xaml(.cs)` | Reminder UI |
| Test files mirroring each new service/VM/helper | — |

### Modify (high level)

| File | Changes |
|---|---|
| `Models/CalendarEvent.cs` | New properties (recurrence, attendees, reminder, TZ, calendarId, ETag, dirty, readOnly) |
| `Models/IcsModel.cs` | RRULE/EXDATE/TZID/ATTENDEE parsing; delegate writing to `IcsSerializer` |
| `Services/ICalendarProvider.cs` | Capability/multi-calendar/sync expansion (breaking) |
| `Services/I?CalendarService.cs` / `CalendarService.cs` | Multi-calendar, CRUD, sync orchestration |
| `Services/LocalCacheCalendarProvider.cs` | Conform to new interface |
| `Services/ILocalStoreService.cs` / `LocalStoreService.cs` | Calendar table, new columns, sync-token + queue tables, CRUD, migration |
| `Services/OAuthService.cs` | Calendar scope sets |
| `Services/GraphMailService.cs` | Extract `GraphHttp` |
| `ViewModels/CalendarViewModel.cs` | View modes, nav, authoring, visibility, reminders surface |
| `ViewModels/MainViewModel.cs` | New services/commands; RSVP→sync wiring |
| `Views/MainWindow.xaml(.cs)` | View switcher, calendars panel, editor launch, mode-scoped F6, key handling |
| `Models/ConfigModel.cs` / `Services/ConfigService.cs` | New keys (TZ, reminders, cadence, default calendar, providers) |
| `App.xaml.cs` | Construct providers + sync/reminder services; dispose properly |
| `QuickMail.Tests/StubServices.cs` | Update stubs for expanded interfaces; add stub providers |

### Estimated dependencies

- Google API path (if chosen over CalDAV) may add a NuGet dependency — see §15 Q1 (raw HttpClient avoids it, consistent with the Graph-mail decision).

---

## 12. Tests to Add

| Test Class | Coverage |
|---|---|
| `RecurrenceExpanderTests` | daily/weekly/monthly/yearly, INTERVAL, COUNT, UNTIL, BYDAY, EXDATE, DST boundary, overrides |
| `IcsModelTests` (extend) | parse RRULE/TZID/EXDATE/ATTENDEE; existing invite parsing unaffected |
| `IcsSerializerTests` | round-trip create→parse; invite + export; escaping |
| `EventEditorViewModelTests` | validation, scope selection, attendee→invite trigger, save/cancel |
| `CalendarStoreTests` (extend) | migration to new schema; calendar + event CRUD; sync token + queue persistence |
| `CalendarServiceTests` (extend) | multi-calendar merge, visibility filter, CRUD delegation |
| `CalendarViewModelTests` (extend) | view switch, paging, today/go-to, authoring commands, visibility toggle |
| `CalendarSyncServiceTests` | pull/push, incremental token, offline queue replay, conflict (server-wins + local copy), 429 backoff |
| `GraphCalendarProviderTests` | HttpMessageHandler-stubbed CRUD + delta |
| `CalDavCalendarProviderTests` | stubbed PROPFIND/REPORT, ETag sync, read-only detection |
| `IcsFeedProviderTests` | fetch + parse feed; read-only enforcement |
| `GoogleCalendarProviderTests` | stubbed CRUD/sync (per chosen stack) |
| `ReminderServiceTests` | fire at lead time, snooze, per-event override, respects setting, resume re-eval |
| `MonthViewAutomationTests` | peer row/col/name correctness; navigation |
| `CommandRegistryTests` / `ViewManagerHotkeyIntegrationTests` (extend) | new commands; mode-scoped F6 |
| `XamlParseTests` (extend) | all new XAML loads on STA thread |
| `SettingsViewModelTests` (extend) | new config keys round-trip |

**Rule:** every new public method gets a test; every branch (each view mode, each edit scope, each provider capability, conflict path, read-only path, online-mode path) gets a case.

---

## 13. Known Risks

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| `ICalendarProvider` breaking change ripples to all implementers/stubs | High | Major | Update `LocalCacheCalendarProvider` + `StubServices` in the same PR as the interface; compiler enforces completeness. |
| `MainViewModel` ctor change breaks stub-based tests | High | Blocker | Update stubs + `ViewModelConstructionTests` in the same PR; run them first. |
| F6 ring change strands focus / breaks hotkey tests | Medium | Major | Mode-scope new stops; grep tests for fixed pane counts; run `ViewManagerHotkeyIntegrationTests` before/after. |
| Month-grid screen-reader semantics insufficient | Medium | Major | Decide a11y model (§15 Q2) before M3; mandatory manual SR pass; custom `AutomationPeer` with tests. |
| Two-way sync loses a user edit | Low | Blocker | Decision 5: never drop a local edit; preserve as "(local copy)"; conflict tests with simulated concurrency. |
| TZID/DST handling subtly wrong | Medium | Major | Store UTC; resolve TZID via `TimeZoneInfo`; DST-boundary tests; never trust the machine zone for non-`Z` times once TZID is present. |
| Recurrence expansion unbounded / slow | Medium | Major | Lazy windowed expansion (Decision 7); cap look-ahead; perf test with "daily forever". |
| Graph/Google/CalDAV throttling or quirks | Medium | Major | Reuse `GraphHttp` 429 backoff; document tested servers; conservative poll cadence. |
| Modal editor reintroduces the WebView2 dispatcher deadlock | Low | Blocker | Editor is **modeless** by rule; Escape/Cancel/Save wired explicitly; no `ShowDialog` over the reading pane. |
| Credential handling drifts from rules | Low | Major | OAuth via MSAL/DPAPI; CalDAV app-passwords via `CredentialService`; never JSON. Code-review gate. |
| `--online` behavior undecided | Medium | Minor | §15 Q5; default to clear-unavailable (Option A) until sync lands. |
| Scope creep (free/busy, NL entry, tasks) | Medium | Major | §4.2 explicit out-of-scope; reviewer enforces. |

---

## 14. Keyboard Reference

| Key | Action | Notes |
|---|---|---|
| `Ctrl+Shift+K` | Open calendar mode | Canonical gesture pending §15 Q3 |
| `A` / `D` / `W` / `M` | Agenda / Day / Week / Month view | Within calendar mode |
| `T` | Go to today | |
| `G` | Go to date (dialog) | |
| `Ctrl+Left` / `Ctrl+Right` | Previous / next period | Day/Week/Month aware |
| `Up`/`Down`/`Left`/`Right` | Navigate within view | Month grid: day/week moves with wrap + paging |
| `Enter` | Open event (or drill into day from Month) | |
| `N` / `Ctrl+Shift+N` | New event | Global `Ctrl+Shift+N` opens editor anywhere |
| `E` | Edit focused event | |
| `Delete` | Delete focused event (confirmed; scope for series) | |
| `Ctrl+Enter` | Save (in editor) | Modeless editor |
| `Escape` | Cancel editor / close calendar / up one level | Context-sensitive |
| `Ctrl+F` | Search events | Within calendar |
| `Space` | Toggle focused calendar's visibility | In Calendars panel |
| `S` | Snooze | In reminder banner |
| `F6` / `Shift+F6` | Cycle panes (incl. mode-scoped calendar stops) | |

(All registered via `CommandRegistry`; `InputGestureText` on menus must match. Category per §15 Q4.)

---

## 15. Open Questions — Decisions Needed Before M2

> **RESOLVED — July 16, 2026 (Kelly).** All seven questions are decided. Summary:
>
> | Q | Decision |
> |---|---|
> | Q1 | **CalDAV** for Google (one CalDAV path also serves iCloud/Fastmail/Nextcloud). |
> | Q2 | **Real grid/table semantics** for the Month grid (Option A); custom `AutomationPeer`, validated live with a screen reader before M3 merge. |
> | Q3 | **Both** — register a `Ctrl+Shift+K` command *and* keep folder-tree entry (Option C). |
> | Q4 | **New `Calendar` category** (sixth category; update the category-validation list). |
> | Q5 | **Unavailable with a Hint** in `--online` mode through M3 (Option A); reconsider remote-only after M4 sync lands. |
> | Q6 | **Reminders default OFF (opt-in).** When enabled: 10-min lead. Sync poll 5 min. |
> | Q7 | **Windows toast, reusing the existing notification service — no new dependency.** The `Microsoft.Toolkit.Uwp.Notifications` dependency this question worried about already ships (added for new-mail toasts); add a `ShowReminder(...)` method to `INotificationService` / `WindowsToastNotificationService`. |
>
> The per-question write-ups below are retained for rationale.

**Q1 — Google sync stack: CalDAV vs Google Calendar API?** — **DECIDED: CalDAV (Option A).**
- *Option A — CalDAV (Google's CalDAV endpoint + Google OAuth).* Pro: one CalDAV code path also serves iCloud/Fastmail/Nextcloud; less Google-specific code. Con: Google's CalDAV is less feature-rich than its REST API; some edge cases (notifications, conferencing) unavailable.
- *Option B — Google Calendar REST API.* Pro: richest features, clean incremental `syncToken`. Con: Google-specific code + possibly a NuGet dep (or hand-rolled DTOs, consistent with the raw-HttpClient mail decision); separate OAuth client/consent screen verification overhead.
- **Recommendation:** **A** (CalDAV) for v1 to maximize reuse and minimize Google-specific surface; revisit B if users hit CalDAV limitations. *Your call.*

**Q2 — Month-grid accessibility model: table semantics (A), list-of-days (B), or hybrid (C)?** — **DECIDED: Option A, real grid/table semantics.** The custom `AutomationPeer` must be validated by Kelly with a real screen reader before M3 merge (per §16.2). See Decision 4. **Recommendation: A** (richest, real grid semantics) — but it is the most work and the highest a11y risk; **B** is a safe fallback if A proves unreliable with your screen reader. This is exactly the kind of call where your lived screen-reader expertise should decide; I will not assert what is "better" without your test.

**Q3 — Canonical open gesture.** — **DECIDED: Option C, both.** Register a `Ctrl+Shift+K` command (palette + customizable) and keep folder-tree entry; verify the key is free in `CommandRegistry` at implementation time. The minimal-calendar spec proposed `Ctrl+Shift+C`; the shipped build reaches the calendar via the folder tree (no dedicated toggle wired). Options: (A) adopt `Ctrl+Shift+K` as the calendar-mode shortcut; (B) keep folder-tree entry only; (C) both. **Recommendation: C** — register a command (so it's in the palette + customizable) *and* keep folder-tree entry. Confirm the key (`Ctrl+Shift+K` is currently free; verify against `CommandRegistry` at implementation time).

**Q4 — Command category.** — **DECIDED: add a new `Calendar` category.** New commands could go under the existing `View` category or a new `Calendar` category. The enforced list today is `View, Mail, Account, Contacts, Settings, Help`. **Recommendation:** add a **`Calendar`** category (cleaner palette grouping) — but this touches the category-validation list; confirm you want a sixth category.

**Q5 — `--online` behavior.** — **DECIDED: Option A (unavailable with a Hint) through M3; reconsider remote-only after M4 sync lands.** Option A: calendar unavailable with a Hint (matches shipped behavior, simplest). Option B: remote-only live mode (no reminders/search). **Recommendation: A** through M3; reconsider B after sync lands in M4.

**Q6 — Default sync cadence & reminder defaults.** — **DECIDED: reminders default OFF (opt-in); when enabled, 10-min lead; sync poll 5 min (configurable 1–60).** This matches the announcement-infra convention of off-by-default for potentially intrusive features. Original proposal (reminders on by default) is overridden.

**Q7 — OS toast dependency.** — **DECIDED: use native Windows toast, reusing the existing notification service. No new dependency.**

This question's original premise is **out of date**: it was written before new-mail notifications shipped. `Microsoft.Toolkit.Uwp.Notifications` (v7.1.3) is **already** a `PackageReference` in `QuickMail.csproj` and already ships inside the self-contained single-file exe — it was added for new-mail toasts. `WindowsToastNotificationService` already uses `ToastNotificationManagerCompat`, which auto-registers the AppUserModelID + COM activator for an unpackaged Win32 app (no MSIX/appxmanifest); Velopack's Start Menu shortcut supplies the toast's app name/icon. There is therefore **no new dependency and no additional packaging work** for reminders.

**Implementation:** extend `INotificationService` with a `ShowReminder(...)` method (event title, start time, calendar name; toast actions Open / Snooze / Dismiss) and implement it in `WindowsToastNotificationService` alongside `ShowNewMail` / `ShowInfo`, reusing the existing `Activated` argument-parsing/activation plumbing. Every platform call stays guarded (degrade to a logged no-op, never a crash), matching the existing service. A custom reminder window (Outlook-classic style) is explicitly **not** required for v1 and remains a possible later enhancement. The `AnnouncementCategory.Result` in-app announcement (§7) still fires regardless, so reminders are accessible even where the OS toast is unavailable.

---

## 16. Implementation Guidance for AI

### 16.1 Adjustments you're expected to make
- The spec names new `CalendarEvent` fields and store tables but not exact column types/indexing — decide based on query patterns (range scans by start-time per calendar; lookups by `(Uid, RecurrenceId)`; the sync token per `(providerId, calendarId)`).
- `RecurrenceExpander`'s exact override-merge rules (when "all events" edits collide with a per-instance override) follow RFC 5545 semantics; pick the standard behavior and document it in the class + tests.
- The view-switcher hosting mechanism (separate UserControls swapped by a `ContentControl` vs a `TabControl`) is yours to choose; favor whatever keeps the F6 ring and focus restoration simplest.

### 16.2 When to ask for clarification (stop and check before proceeding)
- **§15 Q1, Q2, Q4** are normative design forks that affect public surface (Google stack, month a11y model, command category). Do **not** guess these — confirm with Kelly before M2/M3/M4 respectively.
- If the `ICalendarProvider` expansion forces a change that would regress the read-only Agenda, stop and report rather than working around it.
- The month-grid `AutomationPeer` must be validated by Kelly with a real screen reader (Session 3 of M3). Do not claim it "works for screen readers" — per CLAUDE.md, defer to the user's actual experience.

### 16.3 Highest-risk acceptance steps (verify these first after each milestone)
- M1: recurring per-instance edit not corrupting the series; TZID display correctness.
- M3: Month-grid keyboard navigation + every-move announcement; focus restoration on drill/Escape.
- M4: conflict resolution never losing a local edit; RSVP writing through to the synced calendar; no duplicate events after sync.

### 16.4 Hard rules to honor (from CLAUDE.md)
- Editor and accounts windows are **modeless** (`Show()`), Escape/Cancel/Save wired explicitly; never `ShowDialog()` over the live reading-pane WebView2 with editable text.
- No business logic in code-behind; VMs raise events, Views show dialogs/announce.
- All announcements via `AccessibilityHelper.Announce(text, category)` with an explicit category; never bake hints into `AutomationProperties.Name`.
- Every shortcut registered in `CommandRegistry`; no hardcoded `PreviewKeyDown` branches for new actions.
- New windows satisfy the New Window Checklist (F6, WebView2 F6 relay if any WebView2 — there is none here, command palette, cancellation token, focus restoration).
- New `IDisposable` services (sync, reminders) Cancel-before-Dispose and are disposed in `App.OnExit`.
- Credentials: OAuth via MSAL; CalDAV app-passwords via `CredentialService`; never JSON.

---

*This is a draft for Kelly's review. Resolve §15 (at least Q1–Q5) before approving M2+ for implementation. M1 can proceed on approval as it has no open network/provider decisions.*
