# Calendar View — Minimal Calendar Support — PM & Dev Specification

**Status:** Approved
**Date:** June 22, 2026
**Target:** Phase 2 (Table Stakes)
**Crew:** Bravo (PM → Dev Lead → Test Enforcer)
**Depends on:** `ics-calendar-pm-spec.md` (shipped — ICS parsing, invite accept/decline/tentative, event card in reading pane)

---

## 1. Executive Summary

QuickMail can already parse calendar invites and send ACCEPTED/TENTATIVE/DECLINED replies, but once a meeting is accepted it disappears — there is no way to see upcoming meetings inside the app. This spec adds a minimal **Calendar** surface: a new top-level pane, opened with `Ctrl+Shift+C`, that lists events the user has responded to (accepted or tentative), sorted by start time, with full keyboard navigation and screen reader announcements. Events are harvested from the local SQLite cache of messages that contained `text/calendar` parts, so no new network protocol or external calendar sync is required for v1. A pluggable `ICalendarProvider` interface leaves the door open for CalDAV / Graph calendar sync in a later phase without reworking the UI. Opening the source invite from an event honors the user's `MessageOpenMode` setting (ReadingPane / Tab / Window), exactly like opening a message from the message list.

---

## 2. User Problem & Opportunity

### 2.1 Current state (verified)

| Surface | Today | Pain | Who feels it |
|---|---|---|---|
| Accepted meeting | After pressing Accept on an invite, the only trace is the reply in Sent. The meeting itself is invisible inside QuickMail. | User must keep a separate calendar app open just to see today's meetings. | Every user, especially screen reader users who want one app for mail + schedule. |
| "What's next?" | No way to ask QuickMail "what meetings do I have today?" without scanning the message list for invite emails. | Cognitive load; missed meetings. | Keyboard and screen reader users most affected — no glanceable agenda. |
| External calendar sync | None. | Out of scope for v1, but users will ask. | Power users with existing Google/Outlook calendars. |

Verified against code: `MainViewModel.HasCalendarInvite`, `AcceptInviteCommand`, `BuildEventCardHtml`, and `IcsModel.GenerateReply` exist and send replies, but no `CalendarEvent` model, no calendar store, and no calendar UI exist anywhere in the workspace (grep for `Calendar` returns only the invite-handling code and the `CalendarInvite` property on `MailMessageDetail`).

### 2.2 Target personas

| Persona | Who | Core need | How they'd use this |
|---|---|---|---|
| **Kelly (screen reader user, daily)** | Blind professional, lives in QuickMail | See today's meetings without leaving the app | `Ctrl+Shift+C`, arrow through the list, hear "Team standup, 10:00 to 10:30, accepted." |
| **Keyboard power user** | Sightled but mouse-free | Jump to the next meeting and open the original invite | Select an event, press Enter to open the source message. |
| **Casual user** | Occasional invite recipient | Confirm a meeting was accepted and not lost | Open Calendar, see the event listed, done. |
| **Future: sync user** | Has a Google/Outlook calendar | Mirror external events | v2 — `ICalendarProvider` plug-in (out of scope here, but the interface is designed for it). |

### 2.3 Why now

- The ICS invite feature already parses every `text/calendar` part into an `IcsModel` with `Uid`, `StartTime`, `Summary`, `Location`, `Method`, and `Sequence`. The data is being thrown away after the reply is sent.
- `LocalStoreService` already has a migration framework (`PRAGMA user_version`) and a WAL-mode SQLite database — adding a `CalendarEvent` table is a small, well-trodden change.
- The F6 pane ring and `CommandRegistry` are mature; adding one new pane and one new command follows existing patterns exactly.

---

## 3. Design Principles

1. **Minimal, local-first.** v1 shows events harvested from messages already in the cache. No CalDAV, no Graph calendar API, no EWS. The `ICalendarProvider` interface exists so v2 can add providers without touching the VM or View.
2. **Keyboard parity with the message list.** The calendar list behaves like the flat message list: Up/Down to move, Enter to open the source message, Escape to return, F6 to cycle panes.
3. **Screen reader first, not last.** Every row announces a complete sentence ("Team standup, tomorrow 10:00 to 10:30, accepted, Location: Zoom"). No color-only state — response status is spoken.
4. **No business logic in code-behind.** `CalendarViewModel` owns state and commands; `CalendarPane` (a UserControl) handles only focus, keyboard routing, and subscribing to VM events.
5. **Zero change for users who never open it.** The calendar pane is hidden by default and opened on demand. No startup cost unless the user invokes it.

---

## 4. Feature Scope & Acceptance Criteria

### 4.1 In scope (v1)

| Feature | Setting / Shortcut | Default | Notes |
|---|---|---|---|
| Calendar pane | `Ctrl+Shift+C` → `view.calendar` | Unassigned until user opens it once | Toggles the pane; pane remembers last open/closed state across restarts via `config.ini`. `Ctrl+Shift+L` is already taken by `mail.rules` (Manage Rules). |
| Event list | — | — | Flat list sorted by `StartTime` ascending; future events first, past events greyed at the bottom. |
| Event row content | — | — | Summary, start–end (local time), location, response status (Accepted/Tentative/Declined), organizer. |
| Open source message | `Enter` on an event | — | Opens the original invite email honoring the user's `MessageOpenMode` setting (ReadingPane / Tab / Window) — reuses the existing `SelectMessageAsync` path, not a custom open path. |
| Harvest from cache | — | — | `CalendarService` scans `MessageDetail` rows whose `CalendarInvite` JSON is non-null and upserts into `CalendarEvent` table. Runs on sync completion and on demand. |
| Response status tracking | — | — | When the user accepts/declines/tentatives an invite, the event's `ResponseStatus` is updated. Declined events are hidden by default with a "Show declined" toggle. |
| "Today" filter | `T` key in the list | Off | Filters to events starting today. Toggle; press again to show all. |
| Refresh | `F5` (when calendar pane is focused) | — | Re-harvests from cache. |
| Settings | `ShowDeclinedEvents` (bool) in `config.ini` | `false` | Whether declined events appear in the list. |

### 4.2 Explicitly out of scope (v1)

- **External calendar sync** (CalDAV, Graph calendar API, Google Calendar, ICS subscription URLs). The `ICalendarProvider` interface is defined but only the local-cache provider is implemented.
- **Creating or editing events.** QuickMail is a mail client; event authoring belongs to a calendar app.
- **Reminders / notifications** (toast, sound, popup before a meeting starts).
- **Recurring event expansion.** `IcsModel` does not parse `RRULE`. Recurring invites show as a single row for the first occurrence. v2 will add `RRULE` expansion.
- **All-day event rendering** as a separate band. All-day events appear as a normal row with "All day" in the time column.
- **Month/week grid view.** v1 is a list only. A grid is a v2 consideration once we have sync.
- **Drag-to-reschedule, free/busy lookup, attendee status of others.**
- **Calendar in `--online` mode.** The local cache is the source. In `--online` mode the calendar pane shows an empty state with a Hint explaining that calendar requires the local cache. (Rationale: harvesting from IMAP on every open would be far too slow and there is no IMAP SEARCH criterion for "messages with text/calendar parts" that is reliably supported.)

---

## 5. Architecture & Technical Decisions

### 5.1 Key architectural decisions

**Decision 1:** Events are stored in a new `CalendarEvent` SQLite table, harvested from cached `MessageDetail` rows.

**Alternatives:**
1. Re-parse ICS from `MessageDetail` on every calendar open. Pro: no new table. Con: slow on large caches; no place to store `ResponseStatus` after the user replies.
2. Keep events in a JSON file (`calendar.json`). Pro: simple. Con: no indexing by date; diverges from the SQLite-first pattern used for messages.

**Rationale:** SQLite is already the cache of record, has a migration framework, and supports `WHERE start_ticks >= ? ORDER BY start_ticks` efficiently. `ResponseStatus` must persist beyond the reply email, so a dedicated table is the cleanest home.

**Decision 2:** A new `ICalendarProvider` interface with one implementation (`LocalCacheCalendarProvider`). `CalendarService` delegates to it.

**Alternatives:**
1. Put all logic directly in `CalendarService`. Con: adding CalDAV later means rewriting the service.
2. Skip the interface, add it in v2. Con: retrofitting an interface across a VM and tests is more churn than defining it up front.

**Rationale:** The interface is one file and costs nothing at v1. It makes the v2 sync work additive rather than invasive, and makes `CalendarService` unit-testable with a stub provider (matching the project's stub-everything test convention).

**Decision 3:** The calendar pane is a `UserControl` (`CalendarPane.xaml`) hosted in `MainWindow` via visibility toggle, not a separate `Window`. Opening the source invite from an event honors the user's `MessageOpenMode` setting (ReadingPane / Tab / Window), exactly like opening a message from the message list — the calendar VM raises an event carrying the source message identity, and `MainWindow` routes it through the same `SelectMessageAsync` path the message list uses.

**Alternatives:**
1. Separate `CalendarWindow`. Pro: isolated F6 ring. Con: focus restoration across windows is fragile (per the New Window Checklist); a pane reuses the existing F6 ring and focus infrastructure.
2. A tab in the tab strip. Con: tabs are message-oriented; calendar is a different object type.
3. Always open the source invite in the reading pane regardless of `MessageOpenMode`. Con: violates the user's windowing preference; a Tab/Window user would be surprised to lose their tab or window.

**Rationale:** A pane is the lowest-risk integration. It joins the F6 ring at index 8 (after reading pane, before status bar) and reuses `CycleFocusAsync` / `GetFocusedPaneIndex`. Routing the source-message open through the existing `SelectMessageAsync` path means all three `MessageOpenMode` values work for free, with no duplicated open logic.

**Decision 4:** No WebView2 in the calendar pane. The list is a native WPF `ListView` (virtualized) and event details are a native `TextBlock` / `Expander`, not HTML.

**Rationale:** Avoids WebView2 pool pressure (bounded to 6 per account), avoids the F6-relay JS injection complexity, and keeps screen reader navigation in the native UI tree where it is most reliable. The reading pane already owns WebView2 for message bodies.

### 5.2 Runtime mode compatibility

| Mode | LocalStoreService available? | Calendar behavior |
|---|---|---|
| Normal | ✓ | Full functionality. `CalendarService` reads/writes the `CalendarEvent` table. |
| `--online` | ✗ | Calendar pane shows an empty state: "Calendar is unavailable in online mode. It requires the local message cache." Announced as `Hint`. No crash, no `SqliteException`. |
| `--profileDir <path>` | ✓ (alternate path) | Works normally against the alternate `mail.db`. |

### 5.3 Code reuse and duplication risks

- **Event card HTML** (`MainViewModel.BuildEventCardHtml`) is not reused — the calendar pane uses native controls, not HTML. No duplication.
- **Focus restoration** pattern exists in `MainWindow`, `ComposeWindow`, `AddressBookWindow`. Since the calendar is a pane (not a window), it does **not** need its own focus-restoration-on-close; it follows the same F6 ring rules as the message list. No new pattern.
- **`IcsModel.Parse`** is reused by `LocalCacheCalendarProvider` to re-parse stored ICS text when harvesting. No duplication.
- **`BatchObservableCollection<T>`** is reused for the event list to prevent per-insert screen reader re-announcement during harvest.

### 5.4 Shared component audit (mandatory)

| Component | File | Other consumers | Change needed | Risk |
|---|---|---|---|---|
| `MainViewModel` | `ViewModels/MainViewModel.cs` | `MainWindow`, every test that constructs the VM | Add `CalendarViewModel` property and `IsCalendarPaneOpen` bool; add `view.calendar` command registration. | Existing `ViewModelConstructionTests` must still construct the VM — new constructor parameter (`ICalendarService`) must be added to the stub or the constructor will throw. Verify all stub-based tests. |
| `MainWindow.xaml` | `Views/MainWindow.xaml` | `XamlParseTests` | Add `CalendarPane` UserControl host with visibility binding. | XAML parse test must still pass. |
| `MainWindow.xaml.cs` | `Views/MainWindow.xaml.cs` | `CommandRegistryTests`, `ViewManagerHotkeyIntegrationTests`, F6 tests | Add pane index 8 to `GetFocusedPaneIndex` and `CycleFocusAsync`; register `view.calendar` command. | F6 ring order changes — verify Shift+F6 still wraps correctly and no existing test asserts a fixed pane count. |
| `LocalStoreService` | `Services/LocalStoreService.cs` | `SyncService`, `MainViewModel`, all store tests | Add `CalendarEvent` table + migration (schema v4); add `UpsertCalendarEventAsync`, `LoadCalendarEventsAsync`, `UpdateCalendarResponseStatusAsync`, `DeleteCalendarEventAsync`. | Migration must be additive (new table only) so existing data is untouched. `LocalStoreServiceTests` must still pass. |
| `ILocalStoreService` | `Services/ILocalStoreService.cs` | `StubLocalStoreService` in tests | Add the four calendar methods to the interface. | **Every stub must implement the new methods** or compilation breaks. Update `StubServices.cs`. |
| `App.xaml.cs` | `App.xaml.cs` | — | Construct `CalendarService` and pass to `MainViewModel`. | Low risk; one new line in the DI chain. |
| `IcsModel` | `Models/IcsModel.cs` | `ImapMailService`, `MainViewModel`, `IcsModelTests` | No change required for v1. v2 may add `RRULE` parsing. | None. |

**Expected outcome:** "This feature modifies `MainViewModel`, `MainWindow.xaml(.cs)`, `LocalStoreService`, `ILocalStoreService`, `App.xaml.cs`, and `StubServices.cs`. Their other consumers are the test suite and `SyncService`. Changes to `LocalStoreService` are additive (new table + new methods). Changes to `ILocalStoreService` require stub updates — call out in the implementation checklist. F6 ring changes require re-running `ViewManagerHotkeyIntegrationTests`."

---

## 6. Keyboard Walkthrough (Mandatory)

### Path: Open the calendar pane for the first time

1. User is in the message list. User presses `Ctrl+Shift+C`. **Expected:** Calendar pane appears (replacing or alongside the reading pane area — see §5.1 Decision 3: it takes the reading-pane slot when no message is open, and appears in the F6 ring regardless). Focus moves to the calendar list. Screen reader announces: "Calendar. N upcoming events. Use Up and Down arrows to browse. Press Enter to open the invitation. Press T to filter to today. Press Escape to close." (Hint category — respects `AnnounceHints`.)
2. If there are zero events, screen reader announces: "Calendar. No events. Press Escape to close." (Status category.)

### Path: Browse today's meetings

1. Calendar pane is open, focus on the event list. User presses `T`. **Expected:** List filters to events starting today. Screen reader announces: "Today. N events." (Result category.)
2. User presses Down arrow. **Expected:** Focus moves to the first event. Screen reader announces: "Team standup, today 10:00 to 10:30, Accepted. Location: Zoom. Organizer: Alex." (Result category — full sentence, no role name.)
3. User presses Down arrow again. **Expected:** Next event announced in full.
4. User presses `T` again. **Expected:** Filter clears. Screen reader announces: "All events. N events." (Result category.)

### Path: Open the source invite from an event

1. Focus is on an event. User presses `Enter`. **Expected:** The original invite message opens honoring the user's `MessageOpenMode` setting:
   - **ReadingPane mode:** the message loads in the reading pane. Screen reader announces: "Opening message. [subject]." (Status category.) Focus moves to the message body (WebView2).
   - **Tab mode:** a new tab opens with the message. Screen reader announces: "Opening message. [subject]." Focus moves to the message body in the tab.
   - **Window mode:** a new `MessageWindow` opens. Screen reader announces: "Opening message. [subject]." Focus moves to the message body in the window.
   In all three cases the open routes through the existing `SelectMessageAsync` path — the calendar VM raises an event carrying the source message identity, and `MainWindow` constructs the `MailMessageSummary` and calls the same command the message list uses.
2. User presses `Escape`. **Expected:** The message surface closes (reading pane closes / tab closes / window closes). Focus returns to the calendar list at the same event row. Screen reader announces: "Calendar. [event summary]." (Result category.)

### Path: F6 cycle with calendar pane open

1. Focus is in the calendar list (pane index 8). User presses `F6`. **Expected:** Focus moves to the status bar (pane index 5, the next stop). Screen reader announces status bar content.
2. User presses `Shift+F6`. **Expected:** Focus moves back to the calendar list.
3. User presses `Shift+F6` again. **Expected:** Focus moves to the reading pane if a message is open, otherwise to the message list.

### Path: Close the calendar pane

1. Calendar pane is open, focus in the list. User presses `Escape`. **Expected:** Calendar pane closes. Focus returns to the message list at the row that was focused before the calendar was opened. Screen reader announces: "Calendar closed." (Result category.)
2. User presses `Ctrl+Shift+C` again. **Expected:** Calendar pane reopens, focus returns to the list, last filter state (today/all) is preserved.

### Path: Declined events toggle

1. User is in Settings → General. A checkbox "Show declined events" is present, unchecked. User presses Space to check it. **Expected:** Checkbox is checked. Screen reader announces: "Show declined events, checked." (Result category.)
2. User opens the calendar pane. **Expected:** Declined events appear in the list, sorted by start time, with "Declined" spoken in the row announcement.

### Path: Error — calendar unavailable in `--online` mode

1. App launched with `--online`. User presses `Ctrl+Shift+C`. **Expected:** Calendar pane opens showing a single message: "Calendar is unavailable in online mode. It requires the local message cache." Screen reader announces the same text (Hint category). Focus lands on the message. Pressing `Escape` closes the pane and returns focus to the message list.

### Path: Refresh

1. Calendar pane is focused. User presses `F5`. **Expected:** Re-harvest runs. Screen reader announces: "Refreshing calendar." (Status category.) On completion: "Calendar updated. N events." (Result category.)

---

## 7. Accessibility Checklist (Mandatory)

- **`AutomationProperties.Name`** — short labels only:
  - Calendar list: `"Calendar events"`
  - Each event row: the full sentence already announced (e.g. `"Team standup, today 10:00 to 10:30, Accepted. Location: Zoom."`) — no role name, no "row" prefix.
  - Today filter toggle: `"Today only"`
  - Refresh button (if rendered as a button): `"Refresh calendar"`
  - Settings checkbox: `"Show declined events"`
- **`AnnouncementCategory`**:
  - Pane open/close, filter toggle, refresh result → `Result`
  - "Refreshing calendar", "Opening message" → `Status`
  - "Use Up and Down arrows…" intro, online-mode unavailable message → `Hint`
  - No `force: true` calls.
- **Screen reader browse mode** — no WebView2 in the calendar pane. The list is a native `ListView`; screen readers handle it with their standard list navigation. No virtual cursor, no JS relay needed.
- **Focus restoration** — when the pane closes (Escape) or an event opens a message (Enter), focus returns to the originating position. For pane close: capture the message-list focused index before opening; restore on close. For event-open: the existing open-message focus path applies; Escape from the message returns to the calendar list (new — must be wired in `MainWindow.xaml.cs`).
- **F6 ring** — yes, the calendar list is a new pane at index 8, inserted after the reading pane (4) / tab strip (7) and before the status bar (5). Update `GetFocusedPaneIndex` and `CycleFocusAsync`.
- **Checkbox / radio groups** — one new checkbox in Settings (Show declined events). Standard WPF checkbox; no radio group.
- **Color-only information** — response status is **not** communicated by color alone. Each row speaks "Accepted" / "Tentative" / "Declined" as text. A subtle color dot may accompany the text for sighted users but is not the sole indicator.

---

## 8. Acceptance Walkthrough (Mandatory)

### Scenario: Primary happy path — see an accepted meeting

**Setup:** App running in normal mode. At least one invite has been accepted previously (the invite email is in the cache).

1. Press `Ctrl+Shift+C`. **Verify:** Calendar pane opens. Focus is in the event list. Screen reader announces the event count and the hint.
2. Press Down arrow. **Verify:** First event is announced in full: summary, time, status, location, organizer.
3. Press `T`. **Verify:** List filters to today only. Announcement says "Today. N events."
4. Press `T` again. **Verify:** Filter clears. Announcement says "All events."

### Scenario: Open the source invite

**Setup:** Calendar pane open, at least one event in the list. `MessageOpenMode` is ReadingPane (default).

1. Focus an event. Press `Enter`. **Verify:** The original invite message opens in the reading pane. Announcement: "Opening message. [subject]." Focus moves to the message body.
2. Press `Escape`. **Verify:** Reading pane closes. Focus returns to the calendar list at the same event row. Announcement: "Calendar. [event summary]."

### Scenario: Open the source invite — Tab mode

**Setup:** `MessageOpenMode` set to Tab (Settings → Windowing). Calendar pane open, at least one event.

1. Focus an event. Press `Enter`. **Verify:** A new tab opens with the invite message. Focus moves to the message body in the tab.
2. Press `Ctrl+W`. **Verify:** Tab closes. Focus returns to the calendar list at the same event row.

### Scenario: Open the source invite — Window mode

**Setup:** `MessageOpenMode` set to Window. Calendar pane open, at least one event.

1. Focus an event. Press `Enter`. **Verify:** A new `MessageWindow` opens with the invite message. Focus moves to the message body in the window.
2. Press `Escape` (or close the window). **Verify:** Window closes. Focus returns to the calendar list at the same event row.

### Scenario: F6 ring includes calendar

**Setup:** Calendar pane open, focus in the event list.

1. Press `F6`. **Verify:** Focus moves to the status bar.
2. Press `Shift+F6`. **Verify:** Focus returns to the calendar list.
3. Press `Shift+F6` again. **Verify:** Focus moves to the reading pane (if a message is open) or the message list.

### Scenario: Close and reopen preserves filter

**Setup:** Calendar pane open, Today filter active.

1. Press `Escape`. **Verify:** Pane closes. Focus returns to the message list. Announcement: "Calendar closed."
2. Press `Ctrl+Shift+C`. **Verify:** Pane reopens. The Today filter is still active. Focus is in the list.

### Scenario: Show declined events toggle

**Setup:** At least one declined event exists. Settings → General open.

1. Focus the "Show declined events" checkbox. Press Space. **Verify:** Checkbox checked. Announcement: "Show declined events, checked."
2. Open the calendar pane. **Verify:** Declined events appear in the list with "Declined" in the announcement.
3. Return to Settings, uncheck. Reopen calendar. **Verify:** Declined events are hidden.

### Scenario: `--online` mode graceful degradation

**Setup:** Launch app with `--online`.

1. Press `Ctrl+Shift+C`. **Verify:** Pane opens with the unavailable message. No crash, no `SqliteException`. Announcement speaks the message (Hint).
2. Press `Escape`. **Verify:** Pane closes, focus returns to message list.

### Scenario: Refresh

**Setup:** Calendar pane open.

1. Press `F5`. **Verify:** Announcement "Refreshing calendar." (Status). After completion: "Calendar updated. N events." (Result). No duplicate rows appear after refresh (upsert by `Uid` + `AccountId`).

### Scenario: Regression — existing F6 behavior unchanged when calendar is closed

**Setup:** Calendar pane has never been opened this session.

1. Press `F6` repeatedly from the toolbar. **Verify:** Cycle is Toolbar → Accounts → Folders → (Search) → Messages → (Tabs) → (Reading pane) → Status bar → Toolbar. Calendar pane is **not** in the ring when closed.

### Scenario: Regression — `ViewModelConstructionTests` and `XamlParseTests` pass

**Setup:** Run the test suite.

1. `dotnet test QuickMail.Tests/QuickMail.Tests.csproj -c Release`. **Verify:** All existing tests pass, plus new calendar tests.

---

## 9. Success Metrics

- **Behavioral:** User accepts an invite, presses `Ctrl+Shift+L`, and sees the meeting in the calendar list within 2 seconds.
- **Keyboard-centric:** Open, browse, filter to today, open source message, close — all keyboard-only, no stranded focus.
- **Accessibility:** Screen reader user hears a complete event sentence on every row focus; no color-only state; F6 ring includes the pane.
- **No regressions:** `ViewModelConstructionTests`, `XamlParseTests`, `CommandRegistryTests`, `ViewManagerHotkeyIntegrationTests`, `LocalStoreServiceTests` all pass unchanged in behavior (stub updates required for the new interface methods).
- **Online mode:** Calendar pane opens with a clear message, no crash.
- **Performance:** Harvesting 500 cached events completes in under 1 second; list is virtualized so 10,000 events scroll smoothly.

---

## 10. Implementation Phases

### Phase 1: Data model & store

**Goal:** `CalendarEvent` model, `ICalendarProvider` interface, `LocalCacheCalendarProvider`, and `CalendarService` exist; SQLite table created via migration; store methods tested.

**Deliverables:**
- Create `QuickMail/Models/CalendarEvent.cs` (data class: `Uid`, `AccountId`, `Summary`, `StartTime`, `EndTime`, `Location`, `Organizer`, `OrganizerName`, `Description`, `ResponseStatus`, `Sequence`, `SourceMessageId`, `SourceFolder`, `Method`)
- Create `QuickMail/Models/CalendarResponseStatus.cs` (enum: `Pending`, `Accepted`, `Tentative`, `Declined`)
- Create `QuickMail/Services/ICalendarProvider.cs` (interface: `LoadEventsAsync`, `UpsertEventAsync`, `UpdateResponseStatusAsync`, `DeleteEventAsync`)
- Create `QuickMail/Services/LocalCacheCalendarProvider.cs` (implements `ICalendarProvider` against `LocalStoreService`)
- Create `QuickMail/Services/ICalendarService.cs` and `QuickMail/Services/CalendarService.cs` (delegates to provider, exposes `RefreshAsync`, `Events` collection, filter logic)
- Modify `QuickMail/Services/LocalStoreService.cs` — add `CalendarEvent` table, schema v4 migration, four store methods
- Modify `QuickMail/Services/ILocalStoreService.cs` — add the four calendar store methods
- Modify `QuickMail.Tests/StubServices.cs` — implement the new methods on `StubLocalStoreService`

**Tests:**
- `CalendarStoreTests` — round-trip upsert/load/update-status/delete; migration from v3 → v4
- `LocalCacheCalendarProviderTests` — harvest from stubbed `MessageDetail` rows with `CalendarInvite` JSON
- `CalendarServiceTests` — filter to today, sort, hide declined

**Risk:** Migration must be additive. Mitigation: test against a v3 `mail.db` fixture and assert existing tables are untouched.

**Duration:** 3–4 hours

### Phase 2: ViewModel & commands

**Goal:** `CalendarViewModel` exists, `MainViewModel` owns it, `view.calendar` command registered, response status updates on accept/decline/tentative.

**Deliverables:**
- Create `QuickMail/ViewModels/CalendarViewModel.cs` (owns `Events: BatchObservableCollection<CalendarEvent>`, `IsTodayFilter`, `RefreshCommand`, `OpenSourceMessageCommand` — raises `OpenSourceMessageRequested` event carrying `(AccountId, SourceFolder, SourceMessageId)` so the View can route through `SelectMessageAsync`; announcements via `AnnouncementRequested` event)
- Modify `QuickMail/ViewModels/MainViewModel.cs` — add `CalendarViewModel CalendarVm`, `bool IsCalendarPaneOpen`, register `view.calendar` command in `RegisterCommands`, wire `AcceptInvite`/`DeclineInvite`/`TentativeInvite` to update `CalendarService` response status
- Modify `QuickMail/Models/ConfigModel.cs` — add `ShowDeclinedEvents` (bool, default false), `CalendarPaneOpen` (bool, default false)
- Modify `QuickMail/Services/ConfigService.cs` — read/write the two new keys

**Tests:**
- `CalendarViewModelTests` — filter toggle, refresh, open-source-message raises event
- `ViewModelConstructionTests` — `MainViewModel` constructs with the new `ICalendarService` parameter via stub
- `SettingsViewModelTests` — new settings round-trip

**Risk:** `MainViewModel` constructor signature change breaks stub-based tests. Mitigation: update `StubServices.cs` in the same phase; run `ViewModelConstructionTests` immediately.

**Duration:** 3–4 hours

### Phase 3: UI pane & F6 integration

**Goal:** `CalendarPane.xaml` UserControl renders the list; pane is in the F6 ring; keyboard walkthrough passes.

**Deliverables:**
- Create `QuickMail/Views/CalendarPane.xaml` + `.xaml.cs` (virtualized `ListView`, `TextBlock` for empty/online-mode state, keyboard: Up/Down/Enter/Escape/T/F5)
- Modify `QuickMail/Views/MainWindow.xaml` — host `CalendarPane` with visibility bound to `IsCalendarPaneOpen`
- Modify `QuickMail/Views/MainWindow.xaml.cs` — add pane index 8 to `GetFocusedPaneIndex` and `CycleFocusAsync`; handle Escape-to-close and Enter-to-open-source-message (subscribe to `CalendarVm.OpenSourceMessageRequested`, construct `MailMessageSummary` from the event args, and call the existing `SelectMessageCommand` so `MessageOpenMode` is honored); focus restoration
- Modify `QuickMail/Views/SettingsView.xaml` (or equivalent) — add "Show declined events" checkbox in General tab

**Tests:**
- `XamlParseTests` — `MainWindow` and `CalendarPane` XAML load without `XamlParseException`
- `ViewManagerHotkeyIntegrationTests` — F6 ring includes pane 8 when open, excludes when closed
- `CommandRegistryTests` — `view.calendar` registered with `Ctrl+Shift+C`, category `View`

**Risk:** F6 ring order change breaks an existing fixed-count assertion. Mitigation: grep tests for pane-count assertions before editing; run `ViewManagerHotkeyIntegrationTests` first.

**Duration:** 4–5 hours

### Phase 4: Harvest on sync & polish

**Goal:** `CalendarService.RefreshAsync` runs after `SyncService.FolderSynced`; announcements polished; `--online` empty state verified.

**Deliverables:**
- Modify `QuickMail/App.xaml.cs` — construct `CalendarService`, pass to `MainViewModel`
- Modify `QuickMail/ViewModels/MainViewModel.cs` — subscribe `SyncService.FolderSynced` → trigger `CalendarService.RefreshAsync` (debounced, on UI thread)
- Modify `QuickMail/ViewModels/MainViewModel.cs` — `SendIcsReply` updates `CalendarService` response status after a successful reply
- Verify `--online` empty state path (no `LocalStoreService` calls)

**Tests:**
- `CalendarServiceTests` — harvest triggered after sync event
- `MainViewModelInviteTests` (new or extended) — accept/decline updates the calendar event status

**Risk:** Harvest on every `FolderSynced` could be noisy. Mitigation: debounce 2 seconds; only re-harvest folders that actually contained `text/calendar` parts (track a hint flag on the sync event).

**Duration:** 2–3 hours

---

## 11. Files to Create / Modify

### Files to Create

| File | Purpose | Lines (est.) |
|---|---|---|
| `QuickMail/Models/CalendarEvent.cs` | Event data class | 40–60 |
| `QuickMail/Models/CalendarResponseStatus.cs` | Response enum | 10 |
| `QuickMail/Services/ICalendarProvider.cs` | Provider interface (v2 sync plug-in point) | 20 |
| `QuickMail/Services/LocalCacheCalendarProvider.cs` | Local-cache implementation | 80–120 |
| `QuickMail/Services/ICalendarService.cs` | Service interface | 20 |
| `QuickMail/Services/CalendarService.cs` | Service: refresh, filter, sort, status updates | 120–160 |
| `QuickMail/ViewModels/CalendarViewModel.cs` | Pane VM | 150–200 |
| `QuickMail/Views/CalendarPane.xaml` | Pane UI | 80–120 |
| `QuickMail/Views/CalendarPane.xaml.cs` | Pane code-behind (focus, keyboard only) | 80–120 |
| `QuickMail.Tests/CalendarStoreTests.cs` | SQLite round-trip + migration | 80–120 |
| `QuickMail.Tests/LocalCacheCalendarProviderTests.cs` | Harvest from cached details | 60–90 |
| `QuickMail.Tests/CalendarServiceTests.cs` | Filter/sort/status | 80–120 |
| `QuickMail.Tests/CalendarViewModelTests.cs` | VM behavior | 80–120 |

### Files to Modify

| File | Changes | Lines changed (est.) |
|---|---|---|
| `QuickMail/Services/LocalStoreService.cs` | `CalendarEvent` table, v4 migration, four methods | +120 |
| `QuickMail/Services/ILocalStoreService.cs` | Four new method signatures | +20 |
| `QuickMail/Models/ConfigModel.cs` | `ShowDeclinedEvents`, `CalendarPaneOpen` | +10 |
| `QuickMail/Services/ConfigService.cs` | Read/write two new keys | +20 |
| `QuickMail/ViewModels/MainViewModel.cs` | `CalendarVm`, `IsCalendarPaneOpen`, `view.calendar` command, sync subscription, response-status update in `SendIcsReply` | +120 |
| `QuickMail/Views/MainWindow.xaml` | Host `CalendarPane` | +20 |
| `QuickMail/Views/MainWindow.xaml.cs` | F6 pane 8, Escape/Enter wiring, focus restoration | +60 |
| `QuickMail/Views/SettingsView.xaml` | "Show declined events" checkbox | +10 |
| `QuickMail/App.xaml.cs` | Construct `CalendarService`, pass to VM | +5 |
| `QuickMail.Tests/StubServices.cs` | Implement new `ILocalStoreService` methods; add `StubCalendarService` | +40 |
| `QuickMail.Tests/ViewModelConstructionTests.cs` | Construct VM with `ICalendarService` stub | +5 |

---

## 12. Tests to Add

| Test Class | Test Methods | Coverage |
|---|---|---|
| `CalendarStoreTests` | Upsert/load round-trip; update response status; delete by Uid; migration v3→v4 preserves existing message tables | Happy path + migration |
| `LocalCacheCalendarProviderTests` | Harvest from `MessageDetail` rows with `CalendarInvite`; skip rows without invite; dedupe by Uid+AccountId | Happy path + edge cases |
| `CalendarServiceTests` | `RefreshAsync` populates events; Today filter; hide declined when `ShowDeclinedEvents=false`; sort by start time ascending | Filter/sort logic |
| `CalendarViewModelTests` | Toggle Today filter raises announcements; `OpenSourceMessageCommand` raises event with correct `SourceMessageId`; refresh command calls service | VM behavior, no UI |
| `CalendarPaneXamlTests` (or extend `XamlParseTests`) | `CalendarPane.xaml` loads without `XamlParseException` (STA thread) | XAML validity |
| `CommandRegistryTests` (extend) | `view.calendar` registered with `Ctrl+Shift+C`, category `View` | Command registration |
| `ViewManagerHotkeyIntegrationTests` (extend) | F6 ring includes pane 8 when `IsCalendarPaneOpen=true`; excludes when false | F6 integration |

**Key rule:** Every new public method on `CalendarService`, `LocalCacheCalendarProvider`, and `CalendarViewModel` gets at least one test. Every branch (today filter on/off, declined shown/hidden, online-mode empty state) gets a test case.

---

## 13. Known Risks & Open Questions

### 13.1 Risks

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| `MainViewModel` constructor signature change breaks stub-based tests | High | Blocker | Update `StubServices.cs` in Phase 2 before running any VM test. Document in §5.4. |
| F6 ring order change breaks `ViewManagerHotkeyIntegrationTests` | Medium | Major | Grep tests for pane-count/fixed-index assertions before editing; run the test first in Phase 3. |
| Harvest on every `FolderSynced` causes UI lag on large caches | Medium | Major | Debounce 2 s; only re-harvest folders that contained calendar parts. |
| Recurring events (`RRULE`) show as a single row, misleading users | High (for recurring invites) | Minor | Document in out-of-scope; v2 adds expansion. Row announcement says "Recurring event (first occurrence shown)." |
| `--online` mode: user expects calendar to work | Medium | Minor | Clear empty-state message (Hint). Document in user guide. |
| Declined events hidden by default — user forgets they declined something | Low | Minor | "Show declined events" toggle in Settings; default off is the less-noisy choice. |

### 13.2 Resolved decisions (review June 22, 2026)

1. **Calendar pane layout** — **Resolved:** The calendar pane behaves like the message list and honors the user's `MessageOpenMode` setting for opening the source invite. The pane itself takes the reading-pane slot when no message is open and is reachable via F6 regardless. No split layout in v1.
2. **Default gesture** — **Resolved:** `Ctrl+Shift+C` (Calendar). `Ctrl+Shift+L` is already assigned to `mail.rules` (Manage Rules) in `MainViewModel.RegisterCommands` — verified in code.
3. **Account merging** — **Resolved:** v1 merges all accounts into one calendar, matching the unified inbox philosophy. A per-account filter is a v2 candidate.
4. **Time zone display** — **Resolved:** Local time via `ToLocalTime()` (already used in `IcsModel.DisplaySummary`) is sufficient for v1. A time-zone selector is deferred to v2.

---

## 14. Out of Scope (explicit, for the code reviewer)

- CalDAV / Graph / Google Calendar sync (`ICalendarProvider` interface only).
- Event creation, editing, or deletion by the user.
- Reminders, notifications, toast popups.
- Recurring event expansion (`RRULE`).
- Month/week grid view.
- All-day event band rendering (shown as a normal row).
- Attendee status of other participants.
- Free/busy lookup.
- Calendar export to `.ics` file.
- Per-account calendar filter.
- Time-zone selector.

---

*This spec is approved. Hand off to Dev Lead for implementation.*