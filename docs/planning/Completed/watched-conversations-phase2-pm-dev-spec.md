# Watched Conversations, Phase 2 — PM & Dev Specification

**Status:** Approved for implementation
**Author:** Kelly with Claude
**Builds on:** `docs/planning/watched-conversations-pm-dev-spec.md` (v1, shipped in 0.8.38 — the folder, the `Ctrl+Shift+W` toggle, `WatchService`/`watches.json`)
**Applies to:** All accounts, all backends. Watches remain local to the profile; nothing is written to any server.

---

## Table of Contents

1. Executive Summary
2. Scope — what ships
3. Design Principles carried forward
4. Architecture & Technical Decisions
5. Shared Component Audit
6. **Keyboard Walkthrough** (required)
7. **Infrastructure Changes** (required)
8. **Accessibility Checklist** (required)
9. Acceptance Walkthrough
10. Implementation Phases
11. Files to Create / Modify
12. Tests to Add
13. Risks
14. **Out of Scope** (required)

---

## 1. Executive Summary

v1 made a watch a standing subscription and gave it a folder. It left three things undone and one thing unbuilt. Phase 2 closes all four:

- **A Watched Conversations manager** — a modeless window listing every watch, what it has collected, and when you started it, with rename, stop-watching, and go-to-conversation.
- **`Ctrl+Shift+W` in a message window** — the one place the shortcut was inert, because `MessageWindow` owns a separate command registry.
- **Notifications for watched conversations** — a toast when a message lands in a watched thread, in any folder, so a watch can tell you rather than waiting to be looked at.
- **A `Watched` filter** — so any folder can be narrowed to watched conversations, not only the aggregate folder.

Together these turn a watch from something you go and look at into something that reaches you, and give you a place to see and prune what you have accumulated.

---

## 2. Scope — what ships

| Feature | Entry point | Default | Notes |
|---|---|---|---|
| Watched Conversations manager | **Message → Watched Conversations…**, command `mail.watchManager` | no hotkey | Modeless, singleton. Palette-reachable; assignable in Settings → Keyboard. |
| Stop watching, from the manager | **Stop Watching** button, or `Delete` | — | Acts on the selected watch. |
| Rename a watch | **Rename** button | — | Cosmetic: edits `Label` only. Matching stays on `NormalizedSubject`. |
| Go to a watched conversation | **Enter** on a row | — | Opens the Watched Conversations folder with that conversation's newest message selected. |
| `Ctrl+Shift+W` in a message window | `window.toggleWatch` in `MessageWindow`'s local registry + its palette | `Ctrl+Shift+W` | Routes back to `MainViewModel` so main-window rows re-stamp. |
| Notifications for watched conversations | `NotifyOnWatchedConversation` in `config.ini`; Settings → Notifications | **off** | Independent of `NotifyOnNewMail`; fires in **any** folder, not just the inbox. |
| Watched filter | **View → Filter → Watched**, command `view.filterWatched` | no hotkey | New `MessageFilter.Watched`; saved-view key `"watched"`. |

---

## 3. Design Principles carried forward

1. **A watch is a subscription.** Everything new here serves being *told* and being able to *review*, not more ways to mark.
2. **Zero server footprint.** Unchanged. Rename, delete, notify, filter — all local.
3. **The manager is a review surface, not a second way to create watches.** Watches are created from a message, where the subject is real. (See §14 for why typing a subject is out.)
4. **No letter key is stolen from type-ahead.** The manager's list supports first-letter navigation, so **no button carries an access-key underscore**. Alt+letter is wired explicitly instead. This is issue #418's lesson: a bare mnemonic fires without Alt when focus is not in a text field, and `c` closed the folder picker.
5. **Never announce what the platform already reports.** Unchanged from v1.

---

## 4. Architecture & Technical Decisions

### 4.1 The manager is modeless

**Decision:** `.Show()`, singleton, owner = MainWindow, focus restored explicitly on close.

**Rationale:** CLAUDE.md's Modal Dialog Rules are explicit — a dialog with an editable `TextBox` (the rename box) opened over a window with a live WebView2 (the reading pane) can hard-deadlock the UI thread under a screen reader. `RowFieldsWindow` is the proven precedent and this mirrors it exactly, including the costs: Escape and Cancel must be wired by hand, and a second invocation must resurface the existing window rather than opening a rival that would fight it over `watches.json`.

### 4.2 Rename edits the label only

`WatchedConversation.NormalizedSubject` is the matching key; `Label` is display text. Rename edits `Label`. **It cannot edit the key** — doing so would silently change which messages the watch collects, so a rename would look cosmetic and behave like a re-subscription. The manager says so in a hint the first time it is focused.

Consequence: `Label` is now user data, so `WatchService.Rename` must persist it and the manager must not overwrite it on reload.

### 4.3 Message counts are computed, not stored

The manager shows how many cached messages each watch currently matches. That is one `LoadAllSummariesAsync()` plus an in-memory group-by, run once per manager open on a background thread with a `CancellationTokenSource` cancelled in `OnClosing`. It is **not** persisted — a stored count would drift from the cache on every sync, and a wrong count is worse than a late one.

In `--online` mode there is no local store; counts show as **"—"** rather than 0, because 0 would be a lie. This is stated in the window rather than silently degraded.

### 4.4 Watched notifications are a separate gate and a separate path

**Decision:** a new `NotifyOnWatchedConversation` setting and a new `MaybeNotifyWatchedMail` path, evaluated **before** the existing inbox path.

**Rationale:** the existing `MaybeNotifyNewMail` is inbox-only by design ("filtered mail in custom folders shouldn't pop notifications"). A watched conversation's next message may land anywhere — that is the point of the folder. Reusing the inbox path would either miss those messages or break the existing inbox-only guarantee.

**The ordering matters and is load-bearing.** `NewMailFilter.SelectNew` *consumes* what it inspects: it adds every message it returns to the shared `_notifiedMessageKeys` set. So:

- The watched path runs **first**, and is passed **only the watched subset** — so it claims watched messages and leaves everything else unclaimed.
- The inbox path then runs unchanged and claims the rest.

Passing the full `incoming` list to the watched path would consume non-watched inbox messages and silently suppress the ordinary new-mail toast. Both paths share one dedup set precisely so a watched inbox message produces **one** toast, not two — and it gets the watched one, which says more.

### 4.5 `Watched` is a real `MessageFilter` value

Adding a value touches eleven sites (§7). Two carry a known trap: `ViewManagerViewModel` calls `FilterLabel(CurrentFilter.ToString().ToLowerInvariant())` in two places, which produces `"withattachments"`/`"tome"` — spellings `FilterLabel` does not have, so those filters already render as "All" there. `Watched` lower-cases to `"watched"`, which *is* the key, so it works by luck. The spec notes it; fixing the two mis-routed call sites is out of scope (§14).

### 4.6 One explicit-subject entry point

v1's `ToggleWatchConversation()` resolves its target through the View. Phase 2 has two more callers that already know the subject (the message window, and the manager). Refactor:

- `ToggleWatchConversationFor(string? subject)` — the core, used by everything.
- `ToggleWatchConversation()` — resolves via the View, then calls the core. Unchanged behaviour.

## 5. Shared Component Audit

| Component | File | Other consumers | Change | Risk |
|---|---|---|---|---|
| `IWatchService` | `Services/IWatchService.cs` | `MainViewModel`, `StubWatchService` | Add `Rename`, `Unwatch(Guid)` | Low — additive; one stub to update |
| `MainViewModel` toggle | `ViewModels/MainViewModel.cs` | v1 toggle, menu, resolver | Split into `…For(subject)` + resolver wrapper | Low — v1 tests pin the behaviour |
| `MessageFilter` | `Models/MessageFilter.cs` | 11 sites (§7) | New `Watched` value | **Medium** — no reflective test enumerates the enum, so a missed site fails silently. §12 adds one. |
| `INotificationService` | `Services/INotificationService.cs` | `WindowsToastNotificationService`, `App`, `MainViewModel` | Add `ShowWatchedMail` | Low — additive |
| `MaybeNotifyNewMail` call sites | `MainViewModel.cs:2411`, `:2661` | IDLE path and periodic sweep | Add watched path **before** each | **Medium** — ordering is load-bearing (§4.4); covered by a test |
| `MessageWindow` | `Views/MessageWindow.xaml.cs` | Window open mode | New optional ctor param + registry entry + gesture-ladder branch | Low — optional param, existing callers unaffected |
| `ConfigModel`/`ConfigService` | | Settings dialog | One setting, parse + write + Settings UI | Low — but **must** round-trip (`ConfigServiceSaveTests`) |
| `MainWindow` Message menu | `Views/MainWindow.xaml` | | One item | Low |

---

## 6. Keyboard Walkthrough (Mandatory)

### Path A — open the manager and review

1. User presses `Alt+M`, arrows to **Watched Conversations…**, Enter. (Or runs it from the Command Palette.)
   **Expected:** A window titled **Watched Conversations** opens. Focus lands on the watch list, on the first row. Screen reader reads the row: *"Budget Review, 12 messages, watched 3 August."* A hint is announced once: *"Renaming changes the label only, not which messages are collected."* (`Hint`).
2. User arrows down.
   **Expected:** Normal list navigation. Rows read as above. Counts show **—** for every row when running in `--online` mode.
3. User types `t`.
   **Expected:** Selection jumps to the next watch whose label starts with "t". **No button activates** — no button carries an access-key underscore.
4. User presses `F6`.
   **Expected:** Focus cycles list → edit panel → button bar → list. `Shift+F6` reverses.
5. User presses `Escape`.
   **Expected:** Window closes. Focus returns to whatever had it before the window opened (the message list if nothing did).

### Path B — go to a watched conversation

1. Focus on a row in the manager. User presses `Enter`.
   **Expected:** The manager closes. The main window selects **Watched Conversations**, and the newest message of that conversation is selected in the message list. Focus lands on the message list. Status reads *"N watched messages."*
2. **Edge case:** the conversation has no cached messages (count 0).
   **Expected:** The manager stays open, nothing navigates, and the user hears *"That conversation has no cached messages."* (`Result`).

### Path C — stop watching from the manager

1. Focus on a row. User presses `Delete` (or activates **Stop Watching**).
   **Expected:** The row disappears. Selection moves to the row that follows it, or the previous row if it was last. Screen reader announces *"Stopped watching: Budget Review."* (`Result`). If it was the only watch, the list is empty and the window says *"No watched conversations."*
2. **No confirmation prompt.** Stopping a watch destroys nothing — no message is deleted, and re-watching is one keystroke from any message in the thread.

### Path D — rename a watch

1. Focus on a row. User activates **Rename** (or `Alt+R`).
   **Expected:** The edit panel switches to a text box holding the current label, focus moves into it with the text selected. Screen reader reads *"Label"* and the text.
2. User types a new label and presses `Enter` (or activates **Save**).
   **Expected:** The list row updates. Focus returns to that row in the list. Announced: *"Renamed to Q3 budget thread."* (`Result`).
3. User presses `Escape` while the text box has focus.
   **Expected:** The rename is abandoned, the panel returns to read-only, focus returns to the list row. **The window does not close** — Escape closes the window only when a rename is not in progress.
4. **Edge case:** the user clears the box and saves.
   **Expected:** Refused, with an inline error and *"A label cannot be empty."* (`Result`). The label is unchanged.

### Path E — Ctrl+Shift+W in a message window

1. **Message open mode** is **Window**. User presses Enter on a message; it opens in its own window. User presses `Ctrl+Shift+W`.
   **Expected:** *"Watching conversation: QuickMail 1.4 released."* (`Result`). Same key again unwatches.
2. User presses `Ctrl+Shift+P` in that window.
   **Expected:** The window's palette lists **Watch Conversation**.
3. User closes the window and returns to the main window.
   **Expected:** The message list reflects the new watch state — rows re-stamped, and if Watched Conversations is the open folder its membership is correct.

### Path F — a notification arrives

1. **Settings → Notifications → Show a notification when a watched conversation gets a reply** is on. A reply to a watched thread arrives, in any folder.
   **Expected:** A Windows toast: title *"Watched: QuickMail 1.4 released"*, body the sender and subject. No in-app announcement — the toast is the notification, and announcing as well would interrupt.
2. User activates the toast.
   **Expected:** QuickMail comes forward and opens that message, exactly as a new-mail toast does.
3. **Edge case:** the same message is both in the inbox and in a watched conversation, with both notification settings on.
   **Expected:** **One** toast, the watched one.

### Path G — the Watched filter

1. In any folder, user opens **View → Filter** and chooses **Watched**.
   **Expected:** The list narrows to messages in watched conversations. Window title reflects the filter. Choosing **Show All** restores.
2. User saves the current view.
   **Expected:** Stored with `Filter = "watched"`; the View Manager shows **Watched**; re-applying restores the filter.

---

## 7. Infrastructure Changes (Mandatory)

**Commands added** (all category as noted, none with a default key unless stated):
- `mail.watchManager` — Mail, "Watched Conversations…", no default key.
- `view.filterWatched` — View, "Show Watched Conversations Only", no default key.
- `window.toggleWatch` — Mail, "Watch Conversation", `Ctrl+Shift+W`, in `MessageWindow`'s **local** registry (plus a branch in that window's hand-written gesture ladder, which is what actually dispatches there).
- Manager-local registry: `watchmgr.goto`, `watchmgr.stop`, `watchmgr.rename`, `watchmgr.close` — no default keys.

**F6 ring:** the main window's ring is **unchanged**. The manager window defines its own three-stop ring: list → edit panel → button bar.

**`AutomationProperties.Name` introduced:** "Watched conversations" (the list), "Label" (the rename box), "Stop watching", "Rename", "Go to conversation", "Close". Short labels only.

**`AccessibilityHelper.Announce` calls added:**
| Text | Category | Why |
|---|---|---|
| "Renaming changes the label only, not which messages are collected." | `Hint` | Non-obvious consequence, delivered once on first focus |
| "Stopped watching: {label}" | `Result` | Outcome of an explicit action |
| "Renamed to {label}" | `Result` | Outcome |
| "A label cannot be empty." | `Result` | Refusal |
| "That conversation has no cached messages." | `Result` | Refusal |
| "Watching conversation: {subject}" / "Stopped watching: {subject}" (message window) | `Result` | Reuses v1's wording |

**No announcement** accompanies a watched-conversation toast: the toast *is* the notification.

**`MessageFilter.Watched` — the eleven sites:** enum value; `MatchesFilter`; `MainViewModel.FilterLabel`; `IsFilterWatched` property; `OnActiveFilterChanged` notification list; `ApplyViewAsync` string switch; `SetFilterAsync` string switch; `ViewManagerViewModel.FilterKey`; `ViewManagerViewModel.FilterLabel`; the View → Filter `MenuItem`; the `view.filterWatched` registration. Plus `SavedView.Filter`'s doc comment.

**VM state:** `MainViewModel.IsFilterWatched` added. `IsWatchTargetWatched` / `HasWatchTarget` unchanged from v1.

**Config:** `NotifyOnWatchedConversation` (bool, default **false**) — `ConfigModel`, `ConfigService` parse + write, `SettingsViewModel` load/save, Settings → Notifications checkbox.

---

## 8. Accessibility Checklist (Mandatory)

- **No button in the manager carries an access-key underscore.** The list has first-letter type-ahead and a bare mnemonic fires without Alt when focus is not in a text field (#418). `Alt+G` (go to), `Alt+R` (rename), `Alt+S` (stop watching) are wired explicitly in the window's single `OnPreviewKeyDown`, guarded on `Keyboard.Modifiers == ModifierKeys.Alt` exactly — AltGr reports as Ctrl+Alt and must reach type-ahead.
- **The list is a `ListBox`, not a `TreeView`.** WPF disables `TextSearch` on `TreeView`, which is how #418 shipped a type-ahead claim that did nothing. It declares `TextSearch.TextPath`, carries an `x:Name`, and is registered in `TypeAheadWiringTests.Sites`.
- **The row item type overrides `ToString()`** to its full spoken text, and is registered in `SelectorItemAccessibilityTests`. A screen reader reads a Selector item's name from `ToString()`, never from `DisplayMemberPath` — the theme ComboBox shipped that bug.
- **Escape is context-sensitive** — abandons a rename in progress, otherwise closes the window. Guarded so it never steals Escape from an open dropdown.
- **Focus restoration:** captured before `.Show()`, restored in `Closed`. WPF's return-to-owner is unreliable for virtualized list items.
- **No colour-only state.** Counts and dates are text. No swatches.
- **Modal dialog rules:** the window is modeless and opens no nested dialog except the command palette, which is opened *from* it and restores focus after.

---

## 9. Acceptance Walkthrough

1. **Manager, happy path** — watch three conversations, open the manager, verify counts match what the folder shows, rename one, stop another, Enter on the third and land on its newest message.
2. **Type-ahead** — in the manager list, type each of `g`, `r`, `s`, `c` in turn. **Verify no button fires** and selection moves by label.
3. **`--online`** — launch with `--online`, open the manager. **Verify** counts read "—" and nothing throws.
4. **Window mode** — set Message open mode to Window, open a message, `Ctrl+Shift+W`, verify announcement; check the window's `Ctrl+Shift+P` lists it; close and verify main-window rows agree.
5. **One toast, not two** — with both notification settings on, arrange a reply to a watched thread to land in the inbox. **Verify exactly one toast, and that it is the watched one.**
6. **Filter round-trip** — apply View → Filter → Watched, save as a view, restart, re-apply. **Verify** the filter is restored and the View Manager says "Watched".
7. **Settings round-trip** — toggle the new notification setting, restart, verify it stuck (`ConfigServiceSaveTests` covers this too).
8. **Regression** — v1 behaviours: `Ctrl+Shift+W` from the list and from a conversation group header; the folder; the row field.

---

## 10. Implementation Phases

1. **Service + VM core** — `IWatchService.Rename`/`Unwatch(Guid)`, `ToggleWatchConversationFor`, `MessageFilter.Watched` and its eleven sites.
2. **Message window** — ctor param, registry entry, gesture ladder, callback to `MainViewModel`.
3. **Notifications** — setting, `ShowWatchedMail`, `MaybeNotifyWatchedMail`, ordering at both call sites.
4. **Manager window** — VM, XAML, code-behind, MainWindow opener, menu item, command.
5. **Tests, docs, review.**

---

## 11. Files to Create / Modify

**Create:** `ViewModels/WatchedConversationsViewModel.cs`, `Views/WatchedConversationsWindow.xaml(.cs)`, `QuickMail.Tests/WatchedConversationsManagerTests.cs`, `QuickMail.Tests/WatchedNotificationTests.cs`.

**Modify:** `Services/IWatchService.cs`, `Services/WatchService.cs`, `Services/INotificationService.cs`, `Services/WindowsToastNotificationService.cs`, `Models/MessageFilter.cs`, `Models/SavedView.cs` (doc), `Models/ConfigModel.cs`, `Services/ConfigService.cs`, `ViewModels/MainViewModel.cs`, `ViewModels/ViewManagerViewModel.cs`, `ViewModels/SettingsViewModel.cs`, `Views/MainWindow.xaml(.cs)`, `Views/MessageWindow.xaml.cs`, `Views/SettingsDialog.xaml`, `QuickMail.Tests/StubServices.cs`, `QuickMail.Tests/TypeAheadWiringTests.cs`, `QuickMail.Tests/SelectorItemAccessibilityTests.cs`, `docs/KEYBOARD-SHORTCUTS.md`, `docs/USER-GUIDE.md`.

---

## 12. Tests to Add

| Test | Covers |
|---|---|
| `MessageFilter.Watched` round-trips through every string map | The eleven-site risk. **Includes a reflective test asserting every `MessageFilter` value has a `FilterKey`, a `FilterLabel`, and a `MatchesFilter` arm** — the gap that makes a missed site silent today. |
| `MatchesFilter` returns watched-only | Filter behaviour |
| `WatchService.Rename` persists and does not change matching | §4.2 |
| `Unwatch(Guid)` removes exactly one | Manager delete |
| Manager VM: rows carry label/count/date; empty label refused; go-to with 0 messages refuses | §6 C/D/B |
| Watched notification fires for a non-inbox folder | §4.4 |
| **A watched inbox message produces one toast, the watched one** | §4.4 ordering — the load-bearing case |
| Non-watched inbox mail still toasts normally after the watched path ran | The consumption trap |
| `NotifyOnWatchedConversation` round-trips through `config.ini` | Config wiring |
| Type-ahead + selector-accessibility registration | The two enforced suites |

---

## 13. Risks

| Risk | Prob. | Impact | Mitigation |
|---|---|---|---|
| A missed `MessageFilter` site fails silently | High | Major | New reflective test over every enum value |
| Watched path consumes non-watched messages, killing the inbox toast | Medium | Major | Pass only the watched subset; explicit test |
| A manager button steals a type-ahead letter | Medium | Major | No underscores anywhere; acceptance step 2 types every button's first letter |
| Rename read as changing what is matched | Medium | Minor | Hint on first focus; spec'd as label-only |
| Modal manager deadlocks under a screen reader | Low | Blocker | Modeless by design (§4.1) |

---

## 13A. Fixes from adversarial review (2026-08-03)

Seven confirmed defects, several found by live probe rather than by reading. Recorded because each is re-introducible.

### 13A.1 `Ctrl+Shift+W` was still inert in a message window — the gap this phase existed to close

`MessageWindow` has **its own** WebView2 keydown relay, separate from `MainWindow`'s. Phase 2 added the gesture to the WPF key ladder and to that window's registry, but focus lands *inside the document* the moment a message window opens, so neither is reachable. The relay's `e.key === 'w'` test also cannot match with Shift held — the browser reports `'W'`.

**Fix:** relay `ctrl-shift-w` (accepting both cases), ordered **before** the lower-case-only `ctrl-w` branch so closing the window and watching a thread stay distinct. `ctrl-shift-p` was relayed at the same time — the command palette had the identical, pre-existing gap.

**Rule this establishes: a gesture added to a window that hosts a WebView2 is not done until it is in that window's relay.** There are now two relays; a third window would need a third.

### 13A.2 Escape closed the manager out from under an in-progress rename

`PreviewKeyDown` **tunnels root-to-leaf**, so the `Window.OnPreviewKeyDown` override runs *before* the text box's own handler — the opposite of what the code's comment claimed. Escape in the rename box discarded the edit *and* closed the window.

**Fix:** the window handler checks `IsRenaming` first and cancels the rename instead of closing. The inner handler stays as a second line of defence.

### 13A.3 Rename applied to whatever was selected at save time

`BeginRename` captured the label but not the identity; `SaveRename` re-read `SelectedWatch`. Selection is reachable while renaming (F6 → arrows → F6), so a plain keyboard sequence renamed the **wrong watch**, silently.

**Fix:** `_renamingId` captures the identity at `BeginRename`; `SaveRename` resolves by id, restores selection to that row, and reports gracefully if the watch vanished meanwhile. `Alt+G/R/S/C` are also suppressed while renaming — `Alt+S` could otherwise stop watching a row the open edit was no longer about.

### 13A.4 Stopping a watch from the manager never reached `MainViewModel`

The manager wrote straight to `IWatchService`, bypassing `RefreshWatchState`. Because the window is **modeless**, the Watched Conversations folder is very likely visible behind it — so pruning left those messages on screen, still stamped as watched.

**Fix:** a `WatchesChanged` event, routed by `MainWindow` to the new `MainViewModel.RefreshWatchStateFor`. Same discipline as the message window's toggle: **one writer, one place that reacts.**

### 13A.5 The manager list was a constructor-time snapshot

`Ctrl+Shift+W` stays live behind the modeless window, so a row could refer to a watch that no longer exists. `Unwatch` returned false and the method returned — no announcement, no status change, nothing. A dead key.

**Fix:** both stale paths announce "That conversation is no longer watched." and reload.

### 13A.6 The Watched filter did not re-filter on unwatch

`RefreshWatchState` pruned only when the *folder* was open. With **View → Filter → Watched** active in an ordinary folder, unwatching left messages in a list that claimed to show only watched mail.

**Fix:** re-apply filters when `ActiveFilter == Watched`.

### 13A.7 The message-window announcement was raised on the main window

`MainViewModel.AnnouncementRequested` is handled by `MainWindow`, which raised the UIA notification on **its own** peer — while the user was in the message window.

**Fix:** `_announceTarget` redirects for the duration of a synchronous call made on another window's behalf.

### 13A.8 Smaller

- `CA1001` on the new window: `[SuppressMessage]` stating the real reason (WPF never calls `Dispose` on a `Window`; `OnClosing` cancels and disposes), per CLAUDE.md — rather than leaving the branch building with a warning `main` does not have.
- `LoadCountsAsync` now runs the read and the `GroupBy` inside `Task.Run`. Microsoft.Data.Sqlite's `*Async` methods complete synchronously, so both were running on the dispatcher — §4.3 said background and the code was not.
- A `XamlParseTests` entry for the new window; every other window has one.
- `_hintAnnounced` was dead (the window is constructed fresh per open). Removed, and the comment now says the hint fires per open — which is correct for a `Hint`-category announcement the user can turn off.

---

## 14. Out of Scope (Mandatory)

- **Creating a watch by typing a subject.** Considered and dropped: a hand-typed subject has to match the normalized form exactly to collect anything, so a near-miss produces a watch that silently never fires — the worst failure this feature can have. Watches are created from a real message, where the subject is known correct.
- **Editing a watch's matching key.** Rename is label-only (§4.2).
- **Per-watch notification opt-out.** The setting is global.
- **Per-watch account scoping**, **expiry/auto-cleanup**, **server-side sync of watches** — all still out, as in v1.
- **Header-based threading.** Still out; matching stays on normalized subject.
- **Fixing `ViewManagerViewModel`'s two mis-routed `FilterLabel` call sites** (`"withattachments"`/`"tome"` render as "All"). Pre-existing, unrelated to this feature (§4.5).
- **Bulk operations in the manager** — no multi-select stop-watching.
- **Sorting or filtering the manager list.** It is ordered newest-watch-first.
