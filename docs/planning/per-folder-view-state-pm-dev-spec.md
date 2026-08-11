# Per-Folder View State — PM/Dev Spec

**Issue:** [#520 — Save view setting for each folder](https://github.com/kellylford/QuickMail/issues/520)
**Status:** Draft, awaiting approval
**Date:** 2026-08-11

---

## Section 1: Executive Summary

QuickMail's message-list presentation — grouping, filter, sort, date range — is held as five
independent `MainViewModel` properties, each with its own persistence rule, mutated from four
call sites that each reset a *different subset*. The result is that activating a saved view
permanently rewrites the user's global grouping default, and "Clear View" undoes two of the six
things "Apply View" did.

This spec replaces those five loose properties with one `ListState` record and a three-layer
resolver (active view → per-folder memory → global default). Every navigation and every
deactivation becomes a single whole-record assignment, which makes a partial restore
inexpressible rather than merely unlikely. On top of that foundation, each folder remembers the
presentation it was last given, which is what #520 asks for.

---

## Section 2: User Problem & Opportunity

### 2.1 Current state (verified against the code)

| Surface | Today | Pain | Who feels it |
|---|---|---|---|
| Activating a saved view | `ApplyViewAsync` sets `ViewMode` ([MainViewModel.cs:1599](../../QuickMail/ViewModels/MainViewModel.cs)); `OnViewModeChanged` writes `cfg.ViewMode` and saves ([:3947-3949](../../QuickMail/ViewModels/MainViewModel.cs)) | One use of a Conversations view permanently changes the global default for every folder, across restarts | Anyone with a saved view whose grouping differs from their usual |
| Clear View | `ClearViewAsync` ([:4589](../../QuickMail/ViewModels/MainViewModel.cs)) resets `ActiveView` and `ActiveDayLimit` only | Grouping, sort, filter and flag sub-filter all survive the "clear" | #520's reporter, verbatim |
| Clear View on a multi-folder view | `SelectedFolder` stays on the `\0View:{id}` sentinel; `FetchVirtualAsync` re-fetches the same view's folders | The message set does not change; only the window title does | Anyone with a multi-folder view |
| Folder navigation | `SelectFolderAsync` ([:4156-4166](../../QuickMail/ViewModels/MainViewModel.cs)) resets six properties but never `ViewMode` or `ActiveSort` | Grouping is global and sticky; a receipts folder that shares a subject collapses to one 151-message conversation | #520's reporter |
| Sort order | `OnActiveSortChanged` writes `cfg.Sort`, but `ConfigService` has **no reader and no writer** for a `Sort` key (verified: `grep -i sort Services/ConfigService.cs` returns nothing) | Sort survives the process (cached `ConfigModel`) and is silently lost on every restart | Everyone who sets a sort order |
| Saving a view while "Flagged First" is active | `ViewManagerViewModel.SortKey` ([:724-732](../../QuickMail/ViewModels/ViewManagerViewModel.cs)) has no `FlaggedFirst` arm | The view silently stores `dateDesc`; `ParseSort` understands `flaggedFirst`, so only the write side is broken | Anyone using Flagged First |
| Clear View discoverability | No `view.clearView` id exists in `CommandRegistry` | Menu-only; absent from the Command Palette; cannot be bound to a key. Violates the project's own enforced shortcut rule | Keyboard and palette users |

**There is no per-folder persisted state of any kind today.** The only folder-keyed structures in
`MainViewModel` are `_folderCountCts` and `_lastFolderCountSweep`, both unread-count plumbing.

### 2.2 Target personas

- **The receipts filer (#520's reporter).** Keeps Inbox in Conversations because threads matter
  there, and needs one archive folder in Messages because 151 receipts share a subject. Today he
  must re-toggle grouping every time he moves between them.
- **The saved-view user.** Has a "Flagged this week" view bound to Ctrl+1. Wants pressing it and
  then leaving it to be symmetric — everything it changed, changed back.
- **The keyboard/palette user.** Expects "Clear View" to be reachable from Ctrl+Shift+P and
  bindable in Settings → Keyboard, like every other action in the app.
- **The screen-reader user.** Needs the folder's presentation to be *knowable*, not guessable —
  and needs it disclosed through the existing View mode toolbar button, not a new announcement
  on every folder change.

### 2.3 Why now

The reported bug and the requested feature are the same defect seen from two sides: there is no
single object representing "how this list is presented", so nothing can be saved, restored, or
scoped. Fixing #520 by bolting a per-folder dictionary onto five loose properties would make the
existing asymmetry worse. The record-plus-resolver refactor is the smaller change *and* the one
that fixes the bug.

---

## Section 3: Design Principles

1. **One record, one assignment.** All list presentation lives in a single value type. Restoring
   is a whole-record assignment, so a half-restore cannot be written by accident. Adding a sixth
   presentation setting later means adding one field, not remembering four reset sites.
2. **Applying a view never writes a preference.** A view is a temporary overlay. Nothing it does
   is persisted anywhere except `ActiveView`. This single rule is what fixes the reported bug.
3. **One new setting, one new command, no new dialogs.** The failure mode for this feature is a
   panel of knobs nobody understands. The escape hatch is a command, not a management UI.
4. **Never announce what a control already shows.** The toolbar's **View mode** button already
   displays the current grouping and is in the F6 ring. No new folder-change announcement.
5. **Predictable beats clever.** No heuristics about when to remember. Either a folder has
   remembered state or it inherits the default.

---

## Section 4: Feature Scope & Acceptance Criteria

### 4.1 In scope

| Feature | Setting / Command | Default | Notes |
|---|---|---|---|
| `ListState` record + resolver | — | — | Replaces five loose properties as the unit of assignment |
| Symmetric apply/clear | — | — | `ClearViewAsync` restores everything `ApplyViewAsync` set |
| Per-folder memory | `RememberViewPerFolder` (config.ini, General tab) | `on` | Off ⇒ resolver skips the folder layer; today's global-sticky behaviour |
| Reset this folder | `view.resetFolderView`, category `View` | no default key | Palette + View → Views menu |
| Clear View registered | `view.clearView`, category `View` | no default key | Fixes the standing registry-rule violation |
| `Sort` actually persisted | — | `dateDesc` | Adds the missing `ConfigService` reader/writer |
| `FlaggedFirst` saves correctly | — | — | `SortKey` routed through `ConfigModel.ToConfigString` |
| Filter string mapping centralised | — | — | `ConfigModel.ParseFilter` / `ToConfigString(MessageFilter)` replaces three copies |

### 4.2 Explicitly out of scope

- **No pruning of the per-folder store.** Entries for deleted folders are left in place. Pruning
  would have to distinguish real folders from virtual sentinels and view sentinels, and getting
  that wrong silently drops state. The file is a few hundred bytes per folder.
- **No "Forget all remembered folder views" button.** Turning `RememberViewPerFolder` off makes
  the whole layer inert, which is the same outcome with one fewer control.
- **No per-tab list state.** `MessageListTabViewModel` continues to share the VM's state. The
  record makes this a clean follow-up; it is not this change.
- **Search text, selected message, scroll position, row-field layout, density, and reading mode
  are not remembered per folder.** Search is transient by design; the rest are global by design.
- **The calendar is untouched.** `CalendarViewModel` has its own separate view mode and is not
  part of `ListState`.
- **The `--online` startup gap is not fixed here.** `StartBackgroundSyncAsync` returns before
  applying `IsDefault` when `OnlineMode` is set ([:2248-2259](../../QuickMail/ViewModels/MainViewModel.cs)),
  so the default view never applies in online mode. Real bug, unrelated cause — filed separately.

---

## Section 5: Architecture & Technical Decisions

### 5.1 Key architectural decisions

---

**Decision 1: One `readonly record struct ListState` holds grouping, filter, flag sub-filter, sort, and day limit.**

```csharp
public readonly record struct ListState(
    ViewMode Mode, MessageFilter Filter, string? FlagFilterId,
    MessageSort Sort, int? DayLimit);
```

**Alternatives:**
1. *Keep the five properties, add a per-folder dictionary of five-tuples.* Pro: smaller diff. Con:
   the apply/clear asymmetry — the actual reported bug — survives untouched, and a sixth setting
   later has to be threaded through four reset sites again.
2. *A mutable `ListState` class.* Pro: familiar. Con: aliasing. A remembered state handed out by
   reference and then mutated by the VM would corrupt the store silently.

**Rationale:** A value type makes "restore" a single `=`. The five reset sites collapse to one
method, and the test suite gets one place to assert against.

---

**Decision 2: Three layers, resolved on every navigation.**

```
Resolve(folder) => ActiveView?.ToListState()                          // 1. explicit overlay
                ?? (RememberViewPerFolder ? _store.Get(folder) : null) // 2. folder memory
                ?? _defaultListState;                                  // 3. global default
```

**Alternatives:**
1. *Two layers (view → folder), no global default.* Con: a never-visited folder has no state to
   resolve to.
2. *Assign a named saved view to a folder.* Pro: explicit, no hidden state. Con: the issue asks
   for automatic remembering, and it forces view creation for a one-off grouping change.

**Rationale:** Each layer answers a distinct question: what am I *doing right now* (view), what
does *this folder* want (memory), what does *this app* do by default (config).

---

**Decision 3: A manual change writes the whole record to the folder's memory, but writes only the
single field that changed to the global default.**

The global default holds two fields (`ViewMode`, `Sort` — filter, flag sub-filter and day limit
always default to All/null/null). Each is written by, and only by, its own change handler:
`OnViewModeChanged` writes `cfg.ViewMode`; `OnActiveSortChanged` writes `cfg.Sort`. Neither ever
writes the other's field.

**Alternatives:**
1. *Update folder memory only, never the global default.* Con: `Sort` has no Settings UI at all —
   the View menu and toolbar are the only way to change it — so its default for newly-visited
   folders would freeze at whatever it was when the user upgraded, permanently unreachable.
   (`ViewMode` does have one, the **Display mode** combo on the General tab, so this argument is
   weaker for grouping than for sort. The decision is made on sort and applied to both for
   consistency.)
2. *Write the whole current record to the global default on any manual change.* **This was the
   first draft of this spec and it is wrong.** It reintroduces the reported bug in miniature:
   activate a Conversations view, change only the sort, and the view's grouping — which the user
   never chose as a preference — is promoted to the global default for every uncustomised folder.
   Rejected.
3. *Update the global default only when no folder memory exists.* Con: the same gesture has two
   different outcomes depending on invisible state. Unexplainable.

**Rationale:** The field you touched is the field you chose; moving its default is honest, and
moving anything else is not. Per-field scoping also costs less code than the whole-record write —
each handler already owns exactly one field.

**Scope of the global default:** it is consulted **only** by folders with no stored state of their
own. A folder that has been customised never sees it again. "Changing a setting becomes the
default for all folders" is therefore not accurate; the accurate statement is "becomes the default
for folders that have not been customised."

**Worked example:** set Inbox → Conversations (Inbox's record is stored; `cfg.ViewMode` becomes
conversations). Open the receipts folder → no stored record, so it inherits conversations. Set it →
Messages (receipts' record is stored; `cfg.ViewMode` becomes messages). Inbox is unaffected — it
has its own record. From then on Inbox is Conversations and receipts is Messages, permanently and
independently.

---

**Decision 4: `ApplyListState` sets an `_applyingListState` guard; nothing persists while it is set.**

This is the whole fix for the reported bug. `OnViewModeChanged` / `OnActiveSortChanged` /
`OnActiveFilterChanged` / `OnActiveDayLimitChanged` / `SetActiveFlagFilterId` all funnel into:

```csharp
private void NoteListStateChanged()
{
    if (_applyingListState) return;              // programmatic — never a preference
    if (ActiveView != null) ActiveView = null;   // detach-to-custom
    RememberCurrentListState();                  // whole record; no-op when the setting is off
}

partial void OnViewModeChanged(ViewMode value)
{
    // …existing notifications and group-rebuild scheduling…
    if (!_applyingListState) PersistGlobalViewMode(value);   // this field only — Decision 3
    NoteListStateChanged();
}
```

`OnActiveSortChanged` mirrors this with `PersistGlobalSort`. `OnActiveFilterChanged`,
`OnActiveDayLimitChanged` and `SetActiveFlagFilterId` call `NoteListStateChanged()` only — those
three fields have no global-default representation.

Applying a view, navigating to a folder, and clearing a view all run inside the guard.
Only a user gesture reaches either persistence branch.

The guard and `_suppressFilterRebuild` are **saved and restored**, not set-and-cleared, because
`SelectFolderAsync` already holds `_suppressFilterRebuild` when it calls `ApplyListState`.

---

**Decision 5: Detach-to-custom, implemented as `ActiveView = null` inside `NoteListStateChanged`.**

Changing grouping/filter/sort/day-limit while a view is active deactivates the view and starts
remembering against the current folder. The window title drops the view name; the Views menu
check mark clears (`MainWindow.xaml.cs:6357` already reacts to `ActiveView` changing).

**Rationale:** A "modified view" state would need a dirty flag, a modified title form, a
Save-changes prompt, and a discard path — four concepts to solve a problem the user can solve by
pressing the view's hotkey again.

---

**Decision 6: `folderviews.json`, keyed `{AccountId:N}|{FullName}`, string-valued.**

Modelled directly on `ViewService`: profile-dir JSON, `AtomicFile` write, `catch → empty` on load.
Values are the same strings `SavedView` uses (`"conversations"`, `"dateDesc"`, `"unread"`), not
enum ordinals, so reordering an enum cannot silently repoint existing entries.

Virtual folders (`AccountId == Guid.Empty`, `FullName` starting `\x00`) and multi-folder view
sentinels (`\0View:{id}`) key like anything else. That is deliberate: it means a multi-folder
view's folder set can carry its own remembered presentation with no special case.

**Runtime cost:** one small atomic write per user-initiated presentation change. Same class of
write as `SetListDensity` does today.

**Consequence — a customised folder is pinned on all five fields, not just the one you changed.**
The stored value is the whole `ListState`, so the first time you change anything in a folder, that
folder also captures the grouping/sort/filter it happened to have at that moment and stops
consulting the global default entirely. Change the global sort later and that folder keeps its own.
The alternative — a sparse per-field override with presence tracking — is more faithful but adds
exactly the machinery Principle 3 rules out, and it makes "what will this folder do?" a question
you have to compute rather than read. `view.resetFolderView` is the escape hatch. **Accepted
trade-off, called out here because it will surprise someone eventually.**

---

**Decision 7: Clear View on a multi-folder view navigates to All Mail.**

Single-folder views leave `SelectedFolder` on a real folder, so clearing restores the resolved
state in place. Multi-folder views leave it on a `\0View:{id}` sentinel whose *message set is the
view* — restoring presentation alone changes nothing but the title. Clearing therefore routes
through `SelectFolderAsync(AllMailFolder)`, matching both the app's startup home and the menu
item's existing automation name ("return to folder navigation").

One conditional, and it reuses the existing navigation path rather than adding a second one.

### 5.2 Runtime mode compatibility

| Mode | Behaviour |
|---|---|
| Normal | Full feature. `folderviews.json` in `%APPDATA%\QuickMail`. |
| `--online` | Unchanged. `FolderViewStateService` touches only the profile directory — it never calls `LocalStoreService`, so there is no SQLite dependency and no fallback to design. |
| `--profileDir <path>` | `folderviews.json` resolves under the supplied profile, via `ProfileContext.ProfileDir`, exactly as `views.json` does. |

### 5.3 Code reuse and duplication risks

- **Filter string ↔ enum mapping exists in three places today:** `ApplyViewAsync`'s inline switch
  ([:1600-1611](../../QuickMail/ViewModels/MainViewModel.cs)), `SetFilterAsync`'s inline switch
  ([:7576-7587](../../QuickMail/ViewModels/MainViewModel.cs)), and `ViewManagerViewModel.FilterKey`
  ([:710-721](../../QuickMail/ViewModels/ViewManagerViewModel.cs)). The store would be a fourth.
  **Plan:** add `ConfigModel.ParseFilter(string?)` and `ConfigModel.ToConfigString(MessageFilter)`
  next to the existing `ParseViewMode`/`ParseSort` pair, and route all four through them.
- **Sort string ↔ enum mapping is duplicated** between `ConfigModel.ParseSort`/`ToConfigString` and
  `ViewManagerViewModel.SortKey` — and the duplicate is the one missing `FlaggedFirst`. **Plan:**
  delete `SortKey`, call `ConfigModel.ToConfigString(MessageSort)`. This is the FlaggedFirst fix.
- **`ViewService` is the template for `FolderViewStateService`** — same constructor shape, same
  atomic write, same swallow-on-parse-failure. Deliberate mirroring, not accidental duplication.

### 5.4 Shared component audit (mandatory)

| Component | File | Other consumers | Change needed | Risk |
|---|---|---|---|---|
| `MainViewModel.ViewMode` | `ViewModels/MainViewModel.cs:646` | `MainWindow.xaml` toolbar button + context menu (`ViewModeLabel`, `IsMessagesView`…), View menu check marks, `SetViewModeCommand`, `view.toggleConversation` (Ctrl+Shift+V), `MainViewModelTabTests` | Setter unchanged; `OnViewModeChanged` keeps its `cfg.ViewMode` write but behind `_applyingListState`, and gains `NoteListStateChanged()` | Ctrl+Shift+V cycling must still persist. It must write **only** `cfg.ViewMode` — never `cfg.Sort`. Covered by §8 scenario 1 and a dedicated per-field test. |
| `MainViewModel.ActiveSort` | `:652` | Sort menu (7 items), `IsSort*` check marks, `SortLabel`, `CaptureBugReportContext` | Same treatment, writing **only** `cfg.Sort` | Bug-report Environment string must be unchanged. Verified in §8 scenario 6. |
| `MainViewModel.ActiveFilter` | `:649` | Filter menu (9 items), `IsFilter*`, `WindowTitle`, `ApplyFiltersAndSearch`, `SetFilterAsync`, `SetFlagFilterAsync` | `OnActiveFilterChanged` gains `NoteListStateChanged()` behind the existing `_suppressFilterRebuild` early-return | The early return at `:3895` runs *before* the rebuild; the note call must go **before** that return or folder-navigation writes are skipped. Explicit test. |
| `SetActiveFlagFilterId` | `:7566` | `ApplyViewAsync`, `SelectFolderAsync`, `SelectCalendarAsync`, `SetFilterAsync`, `SetFlagFilterAsync` | Gains `NoteListStateChanged()` | `SetFilterAsync` sets filter *then* flag id → two writes per gesture. Harmless (idempotent, tiny file); noted rather than optimised. |
| `ClearViewAsync` | `:4588` | View → Views → Clear View menu item only (no palette entry today) | Rewritten; registered as `view.clearView` | Menu item keeps `ClearViewCommand`; the registry entry invokes the same command. `SavedViewsTests` has 4 existing tests over it — all must still pass. |
| `SelectFolderAsync` | `:4132` | Folder tree selection, `SelectCalendarAsync` fallthrough, view sentinels, account/folder deletion paths, `RebuildFolderListFromCache` | Reset block replaced by `ApplyListState(ResolveListState(folder))` | The view-sentinel early return at `:4138-4147` must stay **above** the reset — it is what lets multi-folder views set their own state. Comment preserved. |
| `ApplyViewAsync` | `:1593` | `SelectViewCommand`, `view.saved.*` hotkeys, Views menu, folder-tree sentinel, `RefreshAsync` re-apply, startup default view | State block replaced by `ApplyListState`; ordering (mode before search clear) preserved | `RefreshAsync` re-applies the active view at `:4534-4538`; with the guard this must not detach. Test. |
| `ViewManagerViewModel.FilterKey` / `SortKey` | `ViewModels/ViewManagerViewModel.cs:710`, `:724` | `BuildName`, `SelectedStateSummary`, `Persist` | Replaced by `ConfigModel` helpers | `BuildName`'s generated view names must be unchanged for non-FlaggedFirst sorts. Assert on the existing strings. |
| `ConfigService` parser/writer | `Services/ConfigService.cs:192`, `:370` | Every setting in the app | Add `sort` and `rememberviewperfolder` cases + writer lines | INI round-trip. `SettingsViewModelTests`/`WindowingPreferencesTests` are the existing round-trip suites. |
| `SettingsDialog.xaml` General tab | `Views/SettingsDialog.xaml:31-360` | The one Settings dialog | One `CheckBox` added near the message-list settings | Adds a tab stop on the General tab. Access key must not collide with the 20 existing ones on that tab — verified by reading the tab's `_` mnemonics. |
| `App.xaml.cs` composition root | `:413` region | — | Construct `FolderViewStateService`, pass to `MainViewModel` | New optional ctor parameter, defaulted null, so the 30+ `new MainViewModel(...)` calls in the test suite compile unchanged. |

**Components with no other consumers:** `ListState`, `IFolderViewStateService`,
`FolderViewStateService` are all new. `StubFolderViewStateService` will be consumed only by tests.

---

## Section 6: Keyboard Walkthrough (Mandatory)

### Path A: The reported scenario — two folders, two groupings

**Setup:** `RememberViewPerFolder` on (default). Inbox and a "Receipts" folder. Fresh profile,
global default `messages`/`dateDesc`.

1. User arrows to **Inbox** in the folder tree and presses Enter. **Expected:** Message list loads
   flat. Toolbar **View mode** button reads "Messages". No new announcement.
2. User presses **Ctrl+Shift+V** three times to reach Conversations. **Expected:** List regroups.
   Toolbar button reads "Conversations". Inbox's memory is written; global default becomes
   `conversations`.
3. User arrows to **Receipts** and presses Enter. **Expected:** No memory for Receipts, so it
   inherits the global default — Conversations. Toolbar reads "Conversations". *(This is the one
   inherited-state moment; step 4 is the fix and it is permanent.)*
4. User presses **Ctrl+Shift+V** until the toolbar reads "Messages". **Expected:** 151 receipts
   listed individually. Receipts' memory written.
5. User returns to **Inbox**. **Expected:** Conversations, from Inbox's memory. Toolbar reads
   "Conversations".
6. User returns to **Receipts**. **Expected:** Messages, from Receipts' memory.
7. User quits and relaunches. **Expected:** Steps 5 and 6 still hold.

### Path B: Apply and clear a saved view — symmetry

**Setup:** Inbox remembered as Conversations. A saved view "Flagged this week"
(`filter=flagged`, `mode=messages`, `sort=dateAsc`, `daysOfMail=7`) bound to Ctrl+1.

1. From Inbox, user presses **Ctrl+1**. **Expected:** View applies. Window title
   "Flagged this week — Flagged - QuickMail". Toolbar reads "Messages". Views menu shows the view
   checked. Inbox's memory is **not** written; global default is **not** written.
2. User opens **View → Views → Clear View** (or runs it from **Ctrl+Shift+P**). **Expected:** The
   view deactivates. Grouping returns to **Conversations**, sort to **Newest First**, filter to
   **All**, day limit cleared — all of it, from Inbox's memory. Title returns to
   "Inbox - <account> - QuickMail". Views menu check mark clears.
3. User presses **Ctrl+1** again, then presses **Ctrl+Shift+V** once. **Expected:** The view
   detaches — title loses the view name, Views menu check clears — and the new grouping is written
   to Inbox's memory *and* to the global grouping default (the user chose that grouping). Nothing
   prompts, nothing is saved into the view definition.
4. User presses **Ctrl+1** once more, then changes only the **sort** from the Sort menu.
   **Expected:** The view detaches and Inbox's memory records the whole state, but the **global
   grouping default is untouched** — only the global sort moves. The view's grouping was never
   chosen by the user and must not become anyone's default.

### Path C: Clear View from a multi-folder view

1. User activates a multi-folder saved view. **Expected:** Message list shows the union; title is
   the view name.
2. User runs **Clear View**. **Expected:** Navigates to **All Mail**; the message list changes to
   All Mail's contents; presentation resolves from All Mail's own memory (or the global default);
   focus stays where it was (the menu closes and returns focus per existing behaviour).

### Path D: Reset this folder

1. In Receipts (remembered as Messages, sorted Oldest First), user presses **Ctrl+Shift+P**, types
   "reset", and presses Enter on **Reset Folder View**. **Expected:** Grouping and sort return to
   the global default; the stored entry for Receipts is deleted. Screen reader announces
   "Folder view reset." (Result category — nothing else confirms the deletion.)
2. User navigates away and back. **Expected:** Receipts now inherits the global default, confirming
   the entry is gone rather than merely overwritten.

### Path E: Turning the setting off

1. User opens **Settings (Ctrl+,) → General**, Tabs to **Remember view settings for each folder**,
   presses **Space** to clear it, activates **OK**. **Expected:** Checkbox reports unchecked.
   Applies immediately, no restart.
2. User moves between Inbox and Receipts. **Expected:** Grouping no longer changes per folder;
   whatever is set stays set — today's behaviour. `folderviews.json` is left on disk untouched.
3. User re-checks the setting. **Expected:** Per-folder state resumes from the preserved file.

### Path F: Empty and edge states

1. First launch, no `folderviews.json`. **Expected:** Every folder resolves to the global default.
   No error, no empty list.
2. `folderviews.json` corrupt or unreadable. **Expected:** Treated as empty (matching
   `ViewService.Load`). The app starts normally at the global default; nothing is announced.
3. A remembered flag sub-filter whose `FlagDefinition` has since been deleted. **Expected:** Same
   degradation `ApplyViewAsync` already performs — validated against live definitions, dropped to
   "all flagged" rather than showing an unexplained empty list.
4. Folder selected while the **Calendar** node is chosen. **Expected:** `ListState` resets to
   default (message list is hidden anyway); returning to a mail folder resolves normally.

---

## Section 7: Accessibility Checklist (Mandatory)

- **`AutomationProperties.Name` introduced:** one, on the new checkbox —
  `"Remember view settings for each folder"`. Short label, no role name, no shortcut, no sentence.
  The existing Clear View menu item's name is corrected from *"Deactivate the current view and
  return to folder navigation"* (a sentence, and only accurate for multi-folder views) to
  **"Clear View"**.
- **Announcements added:** exactly one — `"Folder view reset."`, `AnnouncementCategory.Result`,
  from `ResetFolderView`. Justified because the command deletes stored state and no control
  reflects that deletion.
- **Announcements deliberately NOT added:** nothing on folder change, nothing on view detach,
  nothing on grouping change. The toolbar **View mode** button
  ([MainWindow.xaml:777-781](../../QuickMail/Views/MainWindow.xaml)) already displays the current
  grouping, is in the F6 ring, and is named "View mode". Speaking the grouping on every folder
  change would override the user's own verbosity decision — the defect the CLAUDE.md announcement
  rules describe.
- **Screen reader browse mode:** unaffected. No WebView2 involvement.
- **Focus restoration:** no new dialogs. `ResetFolderView` and `ClearView` run from a menu or the
  palette; both already restore focus to the message list on close.
- **F6 ring:** unchanged. No new panes.
- **Radio/checkbox groups:** one standalone `CheckBox`, not a group — no `TabNavigation="Once"`
  needed. Its access key must not collide with the General tab's existing mnemonics.
- **Colour-only information:** none. Grouping is disclosed as text on the toolbar button.
- **`Selector`-bound item types:** none introduced, so no `SelectorItemAccessibilityTests` entry.

---

## Section 8: Acceptance Walkthrough (Mandatory)

### Scenario 1: The reported bug — per-folder grouping

**Setup:** App running, two folders, `RememberViewPerFolder` on.

1. Set Inbox to Conversations via Ctrl+Shift+V. **Verify:** toolbar reads "Conversations".
2. Navigate to Receipts, set it to Messages. **Verify:** toolbar reads "Messages"; receipts listed
   individually, not as one 151-message conversation.
3. Return to Inbox. **Verify:** toolbar reads "Conversations".
4. Quit and relaunch; visit both. **Verify:** each folder opens as left.

### Scenario 2: Apply/clear symmetry

**Setup:** Inbox remembered as Conversations, sort Newest First.

1. Activate a view with `mode=messages`, `filter=unread`, `sort=dateAsc`, `days=7`. **Verify:**
   all four applied; title shows the view name.
2. Run Clear View. **Verify:** grouping Conversations, sort Newest First, filter All, and the View
   menu's day-limit state cleared — **all four**, not two.
3. Quit and relaunch. **Verify:** the global default is still what it was before step 1 — activating
   the view did not change it.
4. **Per-field default scoping.** Activate a Conversations view from a folder whose global default
   grouping is Messages. Change only the **sort**. **Verify:** the view detaches; then visit a
   folder that has never been customised and confirm it still opens in **Messages**. The view's
   grouping must not have become the global default. *(This is the failure mode Decision 3
   alternative 2 describes; it is the single highest-value check in this scenario.)*

### Scenario 3: Detach-to-custom

1. Activate a saved view. **Verify:** Views menu shows it checked; title shows its name.
2. Press Ctrl+Shift+V. **Verify:** check mark clears, title loses the view name, grouping changed,
   and reopening the View Manager shows the saved view's own definition **unmodified**.

### Scenario 4: The new setting (toggle both ways, no restart)

1. Settings → General → clear **Remember view settings for each folder** → OK. **Verify:** moving
   between folders no longer changes grouping.
2. Re-check it → OK. **Verify:** per-folder grouping resumes with the previously stored values.

### Scenario 5: Reset Folder View

1. From a customised folder, run **Reset Folder View** from the palette. **Verify:** presentation
   returns to the global default; "Folder view reset." is announced.
2. Navigate away and back. **Verify:** the folder still shows the default (entry deleted, not
   overwritten).

### Scenario 6: Shared-component regressions (one per §5.4 consumer)

1. **View menu check marks** — open View → each of Messages/Conversations/From/To. **Verify:** the
   check mark tracks the toolbar label in both directions.
2. **Filter menu** — apply Unread, then Flagged, then a named flag. **Verify:** correct filtering;
   `WindowTitle` shows the filter suffix.
3. **Sort menu** — choose **Flagged First**, then **Save View…**. **Verify:** the created view's
   sort reads back as Flagged First (today it silently becomes Newest First).
4. **Sort persistence** — set Oldest First, quit, relaunch. **Verify:** still Oldest First (today
   it resets to Newest First).
5. **Ctrl+Shift+V cycling** — press four times. **Verify:** cycles all four modes and the last one
   survives a restart.
6. **Bug report Environment block** — Help → Report a Bug. **Verify:** the View and Sort lines read
   exactly as before this change.
7. **Multi-folder view** — activate one, run Clear View. **Verify:** lands on All Mail with All
   Mail's contents.
8. **Folder/account deletion** — delete a folder that had remembered state. **Verify:** no crash;
   the stale entry is simply never resolved.

### Scenario 7: `--online` mode

1. Launch with `--online`. **Verify:** per-folder grouping works exactly as in normal mode
   (the store never touches SQLite).

### Scenario 8: Screen reader

1. Tab to the new checkbox in Settings → General. **Verify:** announced as
   "Remember view settings for each folder, checkbox, checked". No trailing instructions.
2. Move between three folders with different remembered groupings. **Verify:** **no** grouping
   announcement fires on folder change; the toolbar View mode button reports it on F6.
3. Run Reset Folder View. **Verify:** "Folder view reset." is heard once.

---

## Section 9: Success Metrics

- **Behavioural:** two folders hold two different groupings across a restart.
- **No leakage:** activating and clearing a saved view leaves `config.ini` byte-identical.
- **Symmetry:** a test asserts every field of `ListState` is restored by Clear View.
- **Keyboard:** Clear View and Reset Folder View are both reachable from Ctrl+Shift+P and bindable
  in Settings → Keyboard.
- **No regressions:** all four existing `SavedViewsTests` Clear View tests pass unchanged.
- **Online mode:** Scenario 7 passes.
- **Knob count:** exactly one new setting and two new commands ship.

---

## Section 10: Implementation Phases

### Phase 1 — `ListState`, resolver, symmetric apply/clear (no persistence yet)

**Goal:** The reported bug is fixed. Behaviour is otherwise identical to today.

**Deliverables:** `Models/ListState.cs`; `ConfigModel.ParseFilter`/`ToConfigString(MessageFilter)`;
`MainViewModel` — `ApplyListState`, `ResolveListState`, `NoteListStateChanged`,
`_applyingListState`, `_defaultListState`; `ApplyViewAsync`, `SelectFolderAsync`,
`SelectCalendarAsync`, `ClearViewAsync` rewritten onto them; the config write removed from
`OnViewModeChanged`/`OnActiveSortChanged` and replaced by `PersistGlobalDefault()`.

**Tests:** `ListStateTests` (construction, equality, `Default`); new `SavedViewsTests` cases —
Clear View restores mode/filter/sort/flag/day-limit; applying a view does not write config;
`RefreshAsync` re-applying a view does not detach.

**Risk:** the `_suppressFilterRebuild` early return in `OnActiveFilterChanged` swallows the note
call if placed after it → per-folder writes silently skipped in phase 2. Mitigation: note call
goes before the return, with a test that navigates and asserts a write.

### Phase 2 — Per-folder store and the setting

**Goal:** #520 delivered.

**Deliverables:** `Services/IFolderViewStateService.cs`, `Services/FolderViewStateService.cs`;
`ConfigModel.RememberViewPerFolder` + `ConfigService` reader/writer; `App.xaml.cs` wiring;
optional `MainViewModel` ctor parameter; `SettingsDialog.xaml` checkbox + `SettingsViewModel`
property; `StubFolderViewStateService`.

**Tests:** `FolderViewStateServiceTests` (round-trip, missing file, corrupt file, virtual-folder
and view-sentinel keys); `MainViewModel` tests for resolve order and for the setting being off.

**Risk:** key collisions between a real folder and a virtual sentinel. Mitigation: `AccountId` is
part of the key and virtual folders carry `Guid.Empty`; explicit test.

### Phase 3 — Commands, menu, and the adjacent bugs

**Goal:** Discoverable, and the three verified defects fixed.

**Deliverables:** register `view.clearView` and `view.resetFolderView`; `ResetFolderViewAsync`;
View menu item + corrected automation name; `ConfigService` `Sort` reader/writer;
`ViewManagerViewModel.SortKey` deleted in favour of `ConfigModel.ToConfigString`.

**Tests:** `CommandRegistryTests` — both ids registered with category `View`;
`ViewManagerViewModel` — saving with Flagged First round-trips; `ConfigService` — `Sort`
round-trips through a real file.

**Risk:** `BuildName`'s generated view names change if the helper's strings drift from `SortKey`'s.
Mitigation: assert the existing generated names verbatim.

---

## Section 11: Files to Create / Modify

### Create

| File | Purpose | Lines (est.) |
|---|---|---|
| `Models/ListState.cs` | The record + `Default` | 30 |
| `Services/IFolderViewStateService.cs` | Get / Set / Clear | 20 |
| `Services/FolderViewStateService.cs` | JSON store, mirrors `ViewService` | 90 |
| `QuickMail.Tests/ListStateTests.cs` | Record behaviour | 40 |
| `QuickMail.Tests/FolderViewStateServiceTests.cs` | Persistence | 120 |

### Modify

| File | Changes | Lines (est.) |
|---|---|---|
| `ViewModels/MainViewModel.cs` | Resolver, guard, four rewritten call sites, two commands | +170 / −60 |
| `Models/ConfigModel.cs` | `RememberViewPerFolder`, `ParseFilter`, `ToConfigString(MessageFilter)` | +40 |
| `Services/ConfigService.cs` | `sort` + `rememberviewperfolder` read/write | +20 |
| `ViewModels/ViewManagerViewModel.cs` | `FilterKey`/`SortKey` → `ConfigModel` helpers | +5 / −25 |
| `Views/MainWindow.xaml` | Reset menu item; Clear View automation name | +6 |
| `Views/SettingsDialog.xaml` | One checkbox | +6 |
| `ViewModels/SettingsViewModel.cs` | One bound property | +10 |
| `App.xaml.cs` | Construct and inject the service | +4 |
| `QuickMail.Tests/StubServices.cs` | `StubFolderViewStateService` | +25 |
| `QuickMail.Tests/SavedViewsTests.cs` | Symmetry + no-leak cases | +120 |
| `docs/USER-GUIDE.md` | Message List Views + Saved Views sections | +25 |

---

## Section 12: Tests to Add

| Test class | Methods | Coverage |
|---|---|---|
| `ListStateTests` | Default values; value equality; `with` produces a distinct value | Record semantics |
| `FolderViewStateServiceTests` | Round-trip; missing file → null; corrupt file → empty, no throw; real vs virtual key isolation; view-sentinel key; `Clear` removes; overwrite | Persistence + the key-collision risk |
| `SavedViewsTests` (extend) | `ClearView_RestoresEveryListStateField`; `ApplyView_DoesNotWriteConfig`; `ApplyView_DoesNotWriteFolderMemory`; `Refresh_ReapplyingView_DoesNotDetach`; `ChangingGroupingWithViewActive_Detaches`; `ClearView_OnMultiFolderView_NavigatesToAllMail` | The reported bug, pinned |
| `MainViewModel` per-folder tests | Resolve order (view > folder > default); setting off skips the folder layer; navigating writes memory; `ResetFolderView` deletes the entry | The feature |
| `MainViewModel` default-scoping tests | `ChangingSort_DoesNotWriteConfigViewMode`; `ChangingGrouping_DoesNotWriteConfigSort`; `ChangingSortWhileViewActive_LeavesConfigViewModeUnchanged`; `CustomisedFolder_IgnoresLaterGlobalDefaultChange` | Decision 3 and the Decision 6 pinning consequence |
| `CommandRegistryTests` (extend) | `view.clearView` and `view.resetFolderView` registered, category `View` | Registry rule |
| `ViewManagerViewModelTests` (extend) | Saving with `FlaggedFirst` round-trips; `BuildName` output unchanged for existing sorts | The FlaggedFirst fix, no name drift |
| `ConfigService` round-trip | `Sort` and `RememberViewPerFolder` survive write→read | The Sort persistence fix |

---

## Section 13: Known Risks & Open Questions

### 13.1 Risks

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| The `_applyingListState` guard is missed on a path that sets a property directly, so applying a view still writes a preference | Medium | Blocker (the original bug returns) | Every write funnels through `NoteListStateChanged`; a test asserts `config.ini` is unchanged across apply→clear |
| `_suppressFilterRebuild` clobbered by nesting — `SelectFolderAsync` holds it when calling `ApplyListState` | High | Major (double rebuild or a missing one) | Save/restore rather than set/clear; called out in Decision 4 |
| Note call placed after the early return in `OnActiveFilterChanged` | Medium | Major (silent no-write) | Named in Phase 1 risk with a dedicated test |
| Key collision between a real folder and a virtual sentinel | Low | Major (wrong state applied) | `AccountId` in the key; explicit test |
| A handler writes the whole record to the global default instead of its own field, leaking a view's grouping into the default | Medium | Blocker (the reported bug, reintroduced) | Decision 3; `PersistGlobalViewMode`/`PersistGlobalSort` are separate one-field methods, and four `MainViewModel` default-scoping tests pin it |
| A folder inherits a surprising grouping on first visit (Decision 3) | High by design | Minor | Documented in the walkthrough as the single inherited-state moment; self-correcting on first change |
| A customised folder stops following a later change to the global default (Decision 6 pinning) | High by design | Minor | Documented in Decision 6; `view.resetFolderView` is the escape hatch |
| Per-gesture disk write becomes noticeable | Low | Minor | Same write class as `SetListDensity`; file is a few hundred bytes |
| `SetFilterAsync` writes twice per gesture | High | Trivial | Accepted; noted in §5.4 rather than optimised |
| Feature grows into a knob panel | Medium | Major (Kelly's stated concern) | Principle 3 is a review gate: one setting, two commands, no dialog. Anything beyond that needs a new decision. |

### 13.2 Open questions

None outstanding. Three were resolved before drafting:

- **What does a folder remember?** All five `ListState` fields. *(Decided.)*
- **Editing while a view is active?** Detach to custom. *(Decided — Decision 5.)*
- **Automatic or opt-in?** Automatic, with one Settings checkbox to disable. *(Decided.)*

Three resolved during drafting and worth explicit sign-off:

- **Does a manual change still update the global default?** Yes, but **only for the field that
  changed** — Decision 3. Without any global write, the "default for new folders" becomes
  unreachable, since `ViewMode` and `Sort` have no Settings UI. With a whole-record write, a view's
  grouping leaks into the default the moment you change the sort — the reported bug, reintroduced.
  Per-field is the only option that avoids both.
- **Does a customised folder keep following the global default for fields it never changed?** No —
  the stored record pins all five. Sparse overrides rejected under Principle 3; see Decision 6.
- **What does Clear View do on a multi-folder view?** Navigates to All Mail — Decision 7.

---

## Section 14: Appendix — Keyboard Reference

| Key | Action | Notes |
|---|---|---|
| `Ctrl+Shift+V` | Cycle grouping | Existing. Now writes per-folder rather than globally. |
| — | **Clear View** | Newly registered as `view.clearView`. No default key; palette + View menu. |
| — | **Reset Folder View** | New: `view.resetFolderView`. No default key; palette + View menu. |

No default key is assigned to either — both are infrequent, and the unassigned-key surface is
already crowded. Both are bindable in Settings → Keyboard.

---

## Section 15: Implementation Guidance for AI

### 15.1 Adjustments expected

- The spec does not fix the exact JSON shape of `folderviews.json` beyond "string-valued, keyed
  `{AccountId:N}|{FullName}`". Choose the DTO layout; keep enum values as the same strings
  `SavedView` uses.
- Whether `ApplyListState` ends with `ApplyFiltersAndSearch()` or leaves the rebuild to the caller
  is an implementation call. Every current call site is followed by a fetch except
  `ResetFolderView`; whichever you choose, make it uniform and say so in the code comment.
- The Settings checkbox's access key is not specified — pick one that does not collide with the
  General tab's existing mnemonics, and verify by reading them.

### 15.2 When to stop and ask

- **The keyboard walkthrough in §6 is normative.** If an implementation detail forces a different
  user-visible sequence, stop and ask rather than adjusting the walkthrough.
- If Decision 3 (manual change updates the global default) turns out to produce a case where a
  folder's remembered state is overwritten by inheritance, stop — that is a resolver bug, not a
  tuning question.
- **If the design starts needing a second setting, stop and ask.** Principle 3 is the constraint
  Kelly named explicitly.

### 15.3 Highest-risk acceptance steps

The steps most likely to catch bugs in this implementation:

1. §8 Scenario 2 step 4 — changing the sort while a Conversations view is active must not make
   Conversations the global default. This is the reported bug's subtlest form and the one the
   first draft of this spec got wrong.
2. §8 Scenario 2 step 3 — `config.ini` unchanged across apply→clear. This is the reported bug in
   its plain form.
3. §8 Scenario 6 step 4 — sort survives a restart. The `ConfigService` gap means this has never
   worked; it is easy to "fix" the model and forget the parser.
4. §8 Scenario 1 step 4 — both folders correct after a relaunch. Proves the store, the keys, and
   the resolver together.
