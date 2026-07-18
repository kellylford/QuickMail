# QuickMail v0.8.33 Release Notes

## Download

Two options are available for v0.8.33:

| Download | When to use |
|----------|-------------|
| **`QuickMail-win.msi`** — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

This is a big release. QuickMail now has a **full calendar**: create your own appointments, keep repeating events, get reminders, respond to meeting invitations, and connect your Microsoft or Google calendar. It also makes **new-mail sync more reliable**, quiets down **notifications after your computer wakes**, and fixes **opening a message in a tab**. See the [Calendar section of the User Guide](https://kellylford.github.io/QuickMail/calendar.html) for the full walkthrough. If you installed QuickMail from the MSI, this update is delivered automatically.

---

## New: The Calendar

QuickMail now has a real, keyboard-first calendar. Press **Ctrl+Shift+C** (or select the **Calendar** node in the folder tree) to open it. Everything is stored locally, so the calendar works offline, and every part of it is reachable with the keyboard and announced by screen readers.

The [Calendar page of the User Guide](https://kellylford.github.io/QuickMail/calendar.html) covers all of this in detail. In brief:

### Create and manage your own appointments

Press **N** to create an appointment. You get a title, all-day or timed start and end (type times naturally — "9", "9:00 AM", "14:30"), location, notes, and repeat options. Press **E** to edit and **Delete** to delete; QuickMail always confirms before deleting.

**Repeating appointments** support Daily, Weekly, Monthly, and Yearly patterns, an "every N" interval, an optional end date, and — for weekly events — a day-of-week picker. When you edit or delete one occurrence, QuickMail asks whether you mean **this event only** or **the whole series**.

### Four views and quick date navigation

Switch between **Agenda**, **Day**, **Week**, and **Month** views with **A**, **D**, **W**, and **M**. Move between periods with **Ctrl+Left** and **Ctrl+Right**, jump to today with **T**, and jump to any date with **Ctrl+G** (Go to Date). In Month view, arrow keys move day by day and **Enter** opens the selected day.

### Reminders

Turn on **Remind me before appointments** in **Settings → General** and choose how many minutes ahead you want them. QuickMail then shows a Windows notification and announces each reminder as it comes due. Reminders are off by default.

### Meeting invitations

Open an email that contains a meeting invitation and QuickMail adds an event card to the top with **Accept**, **Tentative**, and **Decline** buttons. Choosing one replies to the organizer and updates your calendar right away. Cancellations remove the matching entry automatically.

### Connect an online calendar

Add a **Microsoft** (Outlook.com, Microsoft 365) or **Google** account for email and QuickMail shows its calendar too — see your events, and create, edit, or delete **single** (non-repeating) events, with the change sent back to that account.

iCloud and other CalDAV calendars are not connected yet — adding an Apple account brings in its mail only. Per-account calendar connection for those providers is planned.

Connected calendars refresh in the background; **F5** refreshes on demand. Repeating appointments are always saved to your local calendar, and repeating events that come from an online calendar are read-only for now — the guide's [what the calendar does and does not do](https://kellylford.github.io/QuickMail/calendar.html) list lays out the current limits.

### Search and export

Press **Ctrl+Shift+S** to search your appointments by title, location, or notes. Export any appointment as a standard `.ics` file from the command palette.

---

## New: More reliable new-mail sync

QuickMail primarily learns about new mail through a live server connection (IMAP IDLE). This release adds a safety net behind that connection and fixes two related reliability problems:

- **A periodic new-mail check.** If a server never pushes, a held connection dies quietly, or you read or flag messages in another app, QuickMail now re-checks your inboxes on a schedule. Set it in **Settings → General**, under **Sync**, at **"Check for new mail every"** (Off, 1, 5, 15, 30, or 60 minutes; 5 by default). Changes apply without a restart.
- **No more "connection was forcibly closed" after idle.** Pooled connections that have been idle for a while are now checked and reconnected before reuse, so opening a message after your machine has been sitting a while no longer fails.
- **Read state stays in sync.** A message you read in another client (for example, Gmail on the web) now stops showing as unread in QuickMail.

---

## New: Quieter notifications after your computer wakes

When your computer wakes from sleep or a dropped connection reconnects, all the mail that arrived during the gap arrives at once. Previously that produced a single large "9 new messages" burst notification, and it could repeat on each wake. QuickMail now recognizes a catch-up backlog (more than a handful of messages in one batch) and skips the toast for it — the messages are still marked as seen so nothing re-fires. Real-time arrivals, a few at a time, notify normally.

---

## Fixed: Opening a message in a tab now shows just the message

When you set messages to open in a **tab** (Settings → Windowing → Reading mode → Tab), activating a message opened a tab that showed a *copy of the whole message list* with the message itself squeezed into a small, fixed strip at the bottom — not the message on its own. Opening in a **window** worked correctly.

Tabs now behave as expected: opening a message in a tab fills the pane with that message, and the message list is set aside until you return to it. The message list is still there whenever you need it —

- **Escape** from an open message returns you to the message list (the message's tab stays open).
- **Ctrl+W** closes the message's tab and returns you to the list.
- The **tab strip** (Ctrl+Shift+T, or F6 to reach it) lets you switch between the list and any open messages.

The list also stays visible while a message is still loading, and if a message fails to load the list remains on screen rather than leaving the pane blank.

---

## Accessibility

- The entire calendar is keyboard-only and screen-reader-first: every view, the appointment editor, Go to Date, search, and the invitation response buttons are reachable without a mouse and announced. Each event row is spoken as a concise summary by default; turn on **Show field labels in the calendar event list** in Settings for a labeled reading ("Subject …, when …, location …") instead. The details area is read from the top so you can review an appointment line by line.
- With a message open in a tab, the message list is removed from the view and from **F6** pane cycling, so F6 moves cleanly between the folder tree, the tab strip, and the open message without stopping on a list that isn't shown. Pressing **Escape** brings the list back and returns focus to it. While a message is loading — or if its load fails — the list stays in place and in the F6 cycle, so focus is never stranded on an empty pane.

---

## Thank You to Contributors

Thank you, as always, to everyone who contributes to QuickMail through code, bug reports, feature suggestions, and other feedback — including the reports that shaped the calendar's design, the tab-open behavior found during the theming review, and the notification and sync-reliability reports.

---

## Internal

### Full calendar (PR #273, spec `docs/planning/full-calendar-pm-dev-spec.md`)

- Local authoring with a modeless `EventEditorWindow` (per the modal-dialog rules — editable fields over the live WebView2), master/detail list + details pane, and four views (`Agenda`/`Day`/`Week`/`Month`) driven by `CalendarViewModel.ViewMode` + `ReferenceDate`. Month is a 42-cell grid with arrow navigation and Enter-to-drill.
- Recurrence is a practical RFC-5545 subset (`RecurrenceRule`, `RecurrenceExpander`): `FREQ` daily/weekly/monthly/yearly, `INTERVAL`, weekly `BYDAY`, and one `COUNT`/`UNTIL` end condition. Expansion is local wall-clock (DST-safe) with a ~10-year iteration cap. Per-occurrence edit/delete uses `EXDATE` + detach; whole-series edits preserve `EXDATE`s and the original start.
- All-day events are fully supported and persisted (`is_all_day` column; re-anchored to local midnight across all providers to avoid off-by-one-day display). The "timed events only / deferred" comment in `EventEditorViewModel` is stale and should be removed.
- Times stored as UTC ticks; TZID handling per provider (`Prefer: outlook.timezone` for Graph, RFC3339 offsets for Google).
- Sync engine (`GraphCalendarSyncService`, name predates multi-provider): read-down window −30…+365 days, replace-slice per account, background pass every 15 min plus one after startup mail sync, best-effort (never throws), `silentOnly` (no interactive sign-in). Write-back is Microsoft + Google **single events only** (recurring push rejected pre-network with `NotSupportedException`). Failed server create/edit falls back to a local save, announced. Providers gated by `IsCalendarPushAccount` / `IsGraphEligible` / `IsGoogleEligible`.
- Folder tree: `Calendar` sentinel node with `All Calendars` / `Local Calendar` / per-account children; `CalendarFilterFor` maps a node to `CalendarViewModel.AccountFilter`.
- Invitations: `LocalCacheCalendarProvider` harvests `text/calendar` parts; Accept/Tentative/Decline event card in the reading pane sends an ICS REPLY and upserts the response status immediately; `METHOD:CANCEL` marks events cancelled (filtered from all views).
- Reminders: opt-in 60-second timer (`CalendarReminders` / `CalendarReminderMinutes`, default off / 10 min), Windows notification + `AnnouncementCategory.Result`, fired at most once per `(uid, start)` per run.
- Export via `IcsModel.ExportEvent` (`calendar.exportEvent`, no default key).
- Settings: Calendar field labels; Calendar reminders + minutes. `ShowDeclinedEvents` remains config-only (read at construction; not live-updated).

### Removed the Settings-based Internet Calendar (CalDAV) source (issue #282)

- The global **Settings → General → Internet Calendar** (CalDAV/iCloud) configuration is removed, along with its `CalDavCalendarClient`, the `GraphCalendarSyncService` CalDAV branch, the `[caldav]` config keys, and the synthetic-id calendar tree node. Calendar sync is now Microsoft + Google only (tied to the account you added), plus the local calendar. Per Kelly: calendars should connect **per account** (a checkbox in the account editor, mirroring contact sync #256), and dedicated calendar accounts belong in the account manager — not a global setting. Tracked in #282; the CalDAV read-down code was removed rather than shipped half-wired.

### Go to Date (PR #279)

- `calendar.goToDate` (default **Ctrl+G**, rebindable, in the Command Palette) opens a modeless `GoToDateWindow` (DatePicker). Day/Week/Month keep their view and recenter; Agenda switches to Day. `RequestGoToDate` announces unavailability in online mode; the picker is single-instance.

### Mail-sync reliability (PRs #271, #272, split from #240)

- **#267 (PR #272):** `ConfigModel.MailSyncPollMinutes` (default 5; 0 disables; clamped 1–120) drives `StartFallbackSyncAsync` in `MainViewModel`, a periodic inbox re-sync behind IMAP IDLE. New Settings → Sync control; re-reads the interval each cycle so changes apply without a restart; notifies through the shared de-dupe path.
- **#268 (PR #271):** NOOP-probe pooled IMAP connections idle beyond 30s before reuse, fixing "An existing connection was forcibly closed by the remote host" when opening a message after a long idle.
- **#269 (PR #271):** reconcile `\Seen` for already-cached messages on folder sync, so a message read in another client stops showing unread.
- **#270 (PRs #271, #274):** the new-mail notify breakdown (incoming/unread/fresh counts and UIDs) logs at **Log** level when a toast actually fires (`fresh > 0`), so users who enable logging via Settings → Advanced (Log, not Debug) can capture it; the frequent `fresh == 0` evaluations stay Debug-only.

### Wake/reconnect toast suppression (PR #275)

- When one notify evaluation yields more than `MaxNotifyBatchSize` (5) genuinely-new messages, treat it as a catch-up backlog and skip the toast (messages still marked notified so they don't re-fire; count logged). The startup backlog was already excluded by the notify threshold; this is its mid-session equivalent for wake/reconnect bursts.

### Tab open-mode showed the message list instead of the message (issue #177, PR #264)

- In `MessageOpenMode.Tab`, the right-pane content region left the message-list container visible (it was the `DockPanel` fill child) while the reading pane showed as a `MinHeight=200` `Dock=Bottom` sliver, so a message tab rendered the list plus a body strip rather than the message alone. Window mode was unaffected (separate `MessageWindow`); Reading-Pane mode was unaffected.
- The content region is now a two-row `Grid` whose row sizes swap on a new `IsMessageListAreaVisible` VM flag via `BoolToGridLengthConverter`. The flag is `!(MessageOpenMode == Tab && ActiveTab is MessageTabViewModel && IsMessageOpen)` — the `IsMessageOpen` term keeps the list visible during the async body load and if the load fails/returns null, so a slow or failed open never blanks the pane (Feature Checklist rule 4). `CycleFocusAsync` gates the message-list F6 stop on the same flag; the window-level **Escape** handler routes to `ActivateMessageListTab()` (revealing the list, leaving the tab open) in Tab mode instead of `CloseReadingPane`.
- Reading-Pane and Window layouts are byte-for-byte equivalent (the flag is always true outside Tab mode). Independent review caught and fixed the transient/failed-load blank-pane case before merge. Adds `TabModeMessageListVisibilityTests` (8 cases).
- Brian Vogel's #177 review also surfaced two still-parked, pre-existing items — the Reading-Pane reading pane not being resizable, and account deletion triggering repeated re-auth prompts — neither of which is addressed here.

---

## Reporting Issues

Found a problem or have a suggestion? There are three ways to reach us — pick the one that fits:

1. **Report a Bug → Send** (Help menu, inside QuickMail). Files the report for you anonymously — it includes no email address or other identifying information, so there is no way to follow up with you. **Best when you don't want any follow-up.**
2. **Report a Bug → Copy report and open GitHub** (Help menu). Opens a pre-filled issue that you submit under your own GitHub account, so your GitHub contact information is attached. **Best when you have a GitHub account and want automatic filing plus direct contact.**
3. **Email** [quickmailissues@theideaplace.net](mailto:quickmailissues@theideaplace.net). **Best when you don't mind sending email and want a personal follow-up.**

Full details, including exactly what a report contains (and what it never contains), are in the [Reporting Issues section of the User Guide](https://kellylford.github.io/QuickMail/reporting-issues.html).
