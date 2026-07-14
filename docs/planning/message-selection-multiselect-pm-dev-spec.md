# Message Selection & Multi-Select — Combined PM & Dev Specification

**Status:** Draft for review
**Version:** 0.1
**Date:** 2026-07-11
**Tracking issue:** #238 (supersedes #1)
**Prior art:** `docs/planning/selection-shortcuts-spec.md` (v0.6.8 — shipped Ctrl+A, Ctrl+Shift+Home/End; explicitly deferred TreeView multi-select, Ctrl+Space toggle, Shift+PgUp/PgDn — this spec picks those up)
**Target release:** TBD (phased)

> **Reviewer's shortcut:** the one decision this spec needs from you is in
> [Section 4 — The tree strategy](#4-the-central-decision--how-do-the-grouped-views-get-multi-select).
> Everything else follows from that choice. It carries an **accessibility tradeoff**
> (native tree level/expand announcements vs. native multi-select announcements) that
> only you can adjudicate — I have flagged it explicitly rather than guessing.

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Current State (verified)](#2-current-state-verified)
3. [Design Principles](#3-design-principles)
4. [The Central Decision — how do the grouped views get multi-select?](#4-the-central-decision--how-do-the-grouped-views-get-multi-select)
5. [The Selection Model (three concepts)](#5-the-selection-model-three-concepts)
6. [Keyboard Map](#6-keyboard-map)
7. [Group-node & collapsed-group semantics](#7-group-node--collapsed-group-semantics)
8. [Per-View Keyboard Walkthroughs](#8-per-view-keyboard-walkthroughs)
9. [Status Bar — selection count](#9-status-bar--selection-count)
10. [Accessibility](#10-accessibility)
11. [Infrastructure Changes](#11-infrastructure-changes)
12. [Files to Modify](#12-files-to-modify)
13. [Test Matrix](#13-test-matrix)
14. [Out of Scope](#14-out-of-scope)
15. [Open Questions for Review](#15-open-questions-for-review)

---

## 1. Executive Summary

QuickMail's message selection works well in one of its four views and not at all in the
other three. The flat **Messages** list is a `ListView` with native extended selection
(plus custom range handlers); the **Conversations**, **From**, and **To** views are WPF
`TreeView`s, which are single-select by construction and have no multi-item selection
model. On top of that, even the flat list is missing standard Windows range gestures
(`Shift`+`Home`/`End`, `Shift`+`PgUp`/`PgDn`) and the entire non-contiguous "file explorer"
model (`Ctrl`+arrow to move the cursor, `Ctrl`+`Space` to toggle) that the product owner
wants. There is no persistent indication of how many messages are selected.

This spec defines one coherent selection model — keyboard-first and accessibility-first —
and specifies how to bring **all four views** to parity. Because the trees cannot
multi-select as they stand, the spec's central decision is *how* to give the grouped
views a real selection model, and that decision turns on an accessibility tradeoff
(Section 4). The user-visible target is: the same selection gestures behave the same way
in every view, the selected count is always visible and spoken, and every batch operation
(Delete, Move, Copy, Flag) acts on the whole selection from every entry point.

---

## 2. Current State (verified)

### 2.1 Controls per view

| View | Control | `x:Name` | Selection today |
|---|---|---|---|
| Flat **Messages** | `ListView` `SelectionMode="Extended"` | `MessageList` | Native extended + custom handlers |
| **Conversations** | `TreeView` | `ConversationTree` | Single-select only |
| **From** | `TreeView` | `SenderGroupTree` | Single-select only |
| **To** | `TreeView` | `ToGroupTree` | Single-select only |

`MainWindow.xaml`: `MessageList` at line ~1111, the three trees at ~1311 / ~1450 / ~1589.
The three trees share `GroupedMessageTreeController` for focus/type-ahead/context-menu
logic, but **each wires its own `PreviewKeyDown`** because delete/group semantics differ.

### 2.2 What each gesture does today

| Gesture | Flat list | Conversations / From / To |
|---|---|---|
| `Ctrl`+`A` (select all) | ✅ `SelectAllMessages()` | ❌ n/a (single-select) |
| `Shift`+`Up`/`Down` | ⚠️ custom `ExtendMessageSelection()` — reportedly unreliable | ❌ moves cursor only |
| `Shift`+`Home`/`End` | ❌ missing (only `Ctrl`+`Shift`+`Home`/`End` exists) | ❌ |
| `Shift`+`PgUp`/`PgDn` | ❌ missing | ❌ |
| `Ctrl`+`Up`/`Down` (move without select) | ❌ | ❌ |
| `Ctrl`+`Space` (toggle) | ❌ | ❌ |
| `Ctrl`+click | ✅ native Extended | ❌ |
| `Delete` on selection | ✅ deletes all selected | ⚠️ deletes the one message, **or** all messages of a selected group node |
| Move / Copy to folder | ✅ via `GetSelectedMessages()` | single message, or whole group node |

### 2.3 The root constraint

WPF `TreeView` exposes `SelectedItem` (singular) and has **no** `SelectionMode` or
`SelectedItems`. There is nowhere to accumulate a multi-item selection. `GetSelectedMessages()`
(`MainWindow.xaml.cs:4391`) reflects this — from a tree it returns a one-element list, and
it does not handle `ToGroupTree` at all (returns the VM fallback). The trees were chosen
deliberately because they let screen readers announce **hierarchy level** and
**expanded/collapsed** state natively (see the comment at `MainWindow.xaml:776`). That
native tree accessibility is exactly what makes adding multi-select hard — see Section 4.

### 2.4 No selection-count surface

The status bar (`MainStatusBar`) has `StatusTextItem` (bound to `MainViewModel.StatusText`),
connection, update, rules, and progress segments. There is **no** selection-count segment.
Selection count is only spoken transiently by `SelectAllMessages()` / `ExtendSelectionTo*()`
via `AccessibilityHelper.Announce(... Result)`, and never shown.

---

## 3. Design Principles

1. **One model, four views.** A gesture means the same thing everywhere. `Shift`+`Down`
   extends a contiguous range in the flat list *and* in Conversations. No per-view surprises.
2. **File-explorer semantics are the north star.** Focus (the moving cursor) is distinct
   from selection. `Ctrl`+arrows move focus without changing selection; `Ctrl`+`Space`
   toggles the focused item; `Shift`+arrows/`Home`/`End`/`PgUp`/`PgDn` extend a contiguous
   range from an anchor. This matches Windows Explorer and File Explorer exactly.
3. **Accessibility is a gate, not a follow-up.** Selected/unselected state and the running
   count must be exposed through a real UIA pattern and announced per the user's
   announcement preferences. A gesture that a screen reader cannot perceive is not "done."
   Per project rule, **we do not assert how screen readers behave** — the AT-expert review
   (you) is authoritative on the announcement experience.
4. **Prefer native control behavior over hand-rolled reimplementation.** The flat list's
   current custom range handlers exist because native Extended behavior was overridden at
   some point; where native behavior is correct and accessible, restore it rather than
   pile on more custom code. Less custom key handling = less bug surface.
5. **Every batch op honors the whole selection from every entry point.** Delete, Move,
   Copy, Flag — from the keyboard, the context menu, and the toolbar — all act on the full
   selection. (This is the #221 fix generalized.)

---

## 4. The Central Decision — how do the grouped views get multi-select?

The three grouped views cannot multi-select as `TreeView`s. There are three viable paths.
**This is the decision the review must settle**, because implementation detail, effort,
and the accessibility experience all flow from it.

### Option A — Custom multi-select overlay on the existing `TreeView`s

Keep the trees. Maintain an explicit `HashSet<MailMessageSummary>` selection in code (or a
shared selection service), drive a visual "selected" state via `TreeViewItem` style
triggers, and reimplement anchor tracking + every keyboard gesture by hand, coping with
virtualization and expand/collapse.

- **Pro:** keeps native tree level / expand-collapse announcements.
- **Con:** we own the entire selection state machine for a hierarchical, virtualized
  control — the classic WPF "bug farm." **Accessibility risk (needs your judgment):** a
  `TreeView` does not expose the UIA multi-`Selection` pattern; a style-trigger highlight
  is visual only. Whether a screen reader announces "selected"/"3 selected" on tree items
  under this scheme is exactly the kind of thing we must not assume — and the likely answer
  is that it will not, cleanly.

### Option B — Replace the three trees with one flat, indented, grouped `ListView` (recommended, pending your a11y call)

Model the grouped views the way Outlook does: a flat `ListView` (`SelectionMode="Extended"`)
where group headers are rows and messages are indented rows; expand/collapse shows/hides the
child rows. All four views then share the **same** control type and the **same** selection
code path as the flat list.

- **Pro:** native extended multi-select with a first-class UIA `Selection` pattern (the
  same one the flat list already uses and that you have validated); one selection code path
  for all four views; the file-explorer gestures come essentially for free; `GetSelectedMessages()`
  collapses to one branch.
- **Con / the tradeoff for you:** a flat `ListView` is not a tree. We would reproduce
  **level** and **expanded/collapsed** semantics via `ListView` grouping + an expander in
  the group header (or `AutomationProperties.Level` + `SetItemStatus`). This **changes the
  tree accessibility that you rely on today.** Whether that reproduction is as good as the
  native `TreeView` experience is a question only you can answer — I will not claim it is.
  Also a real refactor: data templates, expand/collapse state, `GroupedMessageTreeController`,
  type-ahead, context-menu targeting, and focus-landing all move to the list model.

### Option C — Keep trees single-select; document the limitation

Multi-select stays a flat-Messages-view feature. `Ctrl`+`Shift`+`C` switches to the flat
list for batch work. This is effectively today's behavior, made explicit. Lowest effort;
does not satisfy the product goal of parity.

### Recommendation

**Pursue Option B, but treat the tree-accessibility tradeoff as a blocking review question.**
Rationale: the product goal is *the same selection everywhere*, and only a control with a
native UIA multi-selection pattern delivers that to screen-reader users without us inventing
selection announcements the platform won't back. Option B also shrinks long-term bug surface
by unifying four views onto one selection path. **But** Option B willingly trades the native
tree's level/expand-collapse announcements for the list's native multi-select announcements,
and you are the authority on whether that trade is acceptable. Suggested de-risking: a
throwaway spike of the Conversations view as a grouped `ListView`, which you drive with a
screen reader before we commit — comparing level/expand/collapse announcements against
today's tree.

The rest of this spec is written to be **strategy-neutral where possible** and calls out
Option-A-vs-B specifics where they diverge.

---

## 5. The Selection Model (three concepts)

Windows list selection has three distinct pieces of state. Getting these named and separated
is what prevents the bugs.

1. **Focus / cursor** — the single item the keyboard is "on." Exactly one at a time. Moved
   by any arrow/Home/End/PgUp/PgDn. In file-explorer semantics, focus can move *without*
   changing selection (that's what `Ctrl`+arrow does).
2. **Anchor** — the fixed end of a contiguous range. Set when a plain click / plain arrow
   move establishes a new single selection, and when `Ctrl`+`Space` toggles an item on. All
   `Shift`+extend gestures compute the range as *anchor → focus* and replace the transient
   range with it (while preserving items selected in a *previous* independent block — see below).
3. **Selection** — the set of selected items (`SelectedItems`). May be contiguous or not.

**Contiguous within a block, additive across blocks.** Standard Windows behavior: `Shift`
gestures rewrite the current contiguous block (anchor→focus); `Ctrl`+`Space` or `Ctrl`+click
commits the current block and starts a new independent selection, moving the anchor. Our
implementation must preserve items selected in prior blocks when a new `Shift` range is drawn
(this is why a naive "clear then select range" is wrong). Native `ListView` Extended mode
already does this correctly — a strong argument for Options B and for leaning on native
behavior in the flat list.

---

## 6. Keyboard Map

The full target map. Applies to **all four views** (behavior identical; the tree strategy
in Section 4 determines whether it's native or reimplemented).

| Gesture | Action |
|---|---|
| `Up` / `Down` | Move focus **and** selection to the previous/next row (single-select; sets anchor). |
| `Home` / `End` | Move focus + single-select to first/last row (sets anchor). |
| `PgUp` / `PgDn` | Move focus + single-select one page up/down (sets anchor). |
| `Shift`+`Up`/`Down` | Extend contiguous range by one row toward focus movement. |
| `Shift`+`Home`/`End` | Extend contiguous range from anchor to first/last row. |
| `Shift`+`PgUp`/`PgDn` | Extend contiguous range by one page. |
| `Ctrl`+`Up`/`Down` | Move **focus only** — selection unchanged (the "cursor" moves independently). |
| `Ctrl`+`Home`/`End` | Move focus only to first/last row. |
| `Ctrl`+`Space` | Toggle selection of the focused row; commit block; move anchor here. |
| `Ctrl`+`A` | Select all (all messages; group headers implied — see Section 7). |
| `Space` | (No-op for selection when not combined with `Ctrl`; reserved.) |
| `Enter` | Open the focused message (unchanged). |
| `Delete` | Delete the whole selection (unchanged intent; generalized to all views). |
| `Ctrl`+click | Toggle a row. |
| `Shift`+click | Extend contiguous range from anchor to clicked row. |

Notes:
- **Investigate the flat list's existing custom `Shift`+`Up`/`Down` first.** If native
  Extended selection is being suppressed (e.g. by the reading-pane auto-load on selection
  change, or by `MessageList_PreviewKeyDown` consuming keys), prefer restoring native
  behavior over extending `ExtendMessageSelection`. The current `Ctrl`+`Shift`+`Home`/`End`
  handlers can then be replaced by the standard `Shift`+`Home`/`End` above.
- `Ctrl`+`Up`/`Down` moving focus without selection is the gesture the product owner
  specifically called out; it is the crux of non-contiguous selection and must land the
  focus visual on a row that is *not* selected, which the trees cannot express today.

---

## 7. Group-node & collapsed-group semantics

The grouped views add rows that are **not messages** (conversation / sender / recipient
headers). Selection semantics must be defined for them explicitly — this is where the
grouped views earn their complexity.

### 7.1 What "selected" means for a group header

- **A group header is a selectable row** and participates in focus and range gestures like
  any other row.
- **Selecting a group header contributes all of that group's messages to the effective
  selection** for batch operations (Delete/Move/Copy/Flag), deduplicated against any of its
  child messages that are also individually selected. This matches today's tree `Delete`,
  where deleting a selected `ConversationGroup` deletes all its messages.
- The **status-bar count is a message count**, not a row count. A selected header contributes
  its message count; a header plus two of its own children still counts each message once.

### 7.2 Collapsed vs. expanded

- **Collapsed group, header selected:** the group's (hidden) child messages are all part of
  the effective selection. Deleting/moving acts on all of them. (This is today's behavior for
  a single collapsed group; we generalize it to multi-selection.)
- **Expanded group:** children are visible rows. A contiguous `Shift` range that spans the
  header and its children behaves row-by-row over the visible rows.
- **Contiguous range crossing a collapsed group:** the header stands in for the whole group;
  the range does not silently reach into hidden children individually — selecting the header
  already implies them.

### 7.3 Cross-group contiguous selection

`Shift`+`Down` from the last visible row of group 1 into the header of group 2 extends the
range across the boundary, exactly as rows flow in the flat list. The existing `<` / `>`
group-jump keys remain for navigation and are unaffected.

### 7.4 `Ctrl`+`A`

Selects every message in the view. In grouped views this is equivalent to selecting every
group (and therefore every message). Announce the **message** count.

> **These rules are the highest-risk part of the design.** They are written to be explicit
> so that Section 13's test matrix can verify each cell. If any rule here feels wrong to you,
> that is the thing to flag in review — it is far cheaper to change here than in code.

---

## 8. Per-View Keyboard Walkthroughs

Each walkthrough is the numbered "what the user does / what they hear" sequence the project's
spec rules require. Written against the **target** behavior (Option B assumed for the trees;
under Option A the same steps must hold, reimplemented).

### 8.1 Flat Messages list

1. Focus enters the list (F6 or `Ctrl`+3). Screen reader: *"Messages, list. [first message], 1 of N, selected."* Focus + selection + anchor on row 1.
2. `Down` three times. Each step: *"[message], k of N, selected."* Selection follows focus; anchor now on row 4.
3. `Shift`+`Down` twice. Selection extends to rows 4–6. Screen reader announces the newly selected row and — per your preference — the running count. Status bar: *"3 selected."*
4. `Ctrl`+`Down` twice. Focus moves to row 8 **without** selecting it. Screen reader: *"[row 8], 8 of N, not selected."* Rows 4–6 remain selected. Status bar still *"3 selected."*
5. `Ctrl`+`Space`. Row 8 toggles on; anchor moves to row 8. Status bar: *"4 selected."*
6. `Shift`+`End`. Range from anchor (row 8) to last row is added; rows 4–6 remain. Status bar updates.
7. `Delete`. Whole selection is deleted; focus lands on the next surviving row in place.

### 8.2 Conversations

1. Focus enters `ConversationTree`/list. Screen reader announces the first row (a conversation header or a single message) with its level/expanded state (Option A: native; Option B: reproduced via grouping).
2. `Down` to a conversation header. `Shift`+`Down` — selection extends to include the header and the next visible row. Selected count reflects **messages**, so a header for a 3-message conversation contributes 3.
3. Collapse the header (Left / `Enter` toggle). It remains selected; its 3 messages remain in the effective selection. Status bar unchanged.
4. `Ctrl`+`Down` to another header, `Ctrl`+`Space` to add it. Non-contiguous multi-group selection. Status bar sums both groups' message counts.
5. `Delete` removes every message in the effective selection; focus lands on the next surviving row.

### 8.3 From (sender groups)

Same as 8.2 with `SenderGroup` headers. Verify `Ctrl`+`Space` on a sender header selects all
that sender's messages, and that a mixed selection (one whole sender + two individual messages
from another sender) reports the correct deduped message count and deletes/moves correctly.

### 8.4 To (recipient groups)

Same as 8.3 for `ToGroupTree`. **Note:** `GetSelectedMessages()` does not currently handle
`ToGroupTree` at all — this view must be brought into the unified selection path as part of
the work.

---

## 9. Status Bar — selection count

- Add a **persistent selection-count segment** to `MainStatusBar` (e.g. `SelectionStatusItem`),
  positioned near `StatusTextItem`.
- Bind it to a new `MainViewModel` property (e.g. `SelectionSummary` → `"3 selected"`, empty
  when 0 or 1 so it does not add noise for the common single-selection case — **confirm this
  threshold in review**).
- Update it on every selection change (the `SelectionChanged` handler in the flat list; the
  selection-service change event under the grouped-view strategy). Count is **messages**, per
  Section 7.
- The count is a `StatusBarItem` focus stop already-supported by the status bar's per-item
  focus model; expose the value in its `AutomationProperties.Name`/text so it is readable when
  the user tabs the status bar, and announce changes as `AnnouncementCategory.Status`
  (debounced, so drag/`Shift`-hold does not chatter) — gated by the user's `AnnounceStatus`
  preference. The visible text is always present for sighted users regardless of announcement
  settings.

---

## 10. Accessibility

Per `CLAUDE.md`, all custom announcements go through `AccessibilityHelper.Announce(text,
category, force)` with an explicit category, and we **do not assert screen-reader behavior** —
the following are the hooks; you validate the actual experience.

- **Selection state per item.** Selected/unselected must be exposed via the control's native
  UIA `SelectionItem` pattern. This is the heart of the Section 4 decision: `ListView`
  Extended (flat list today, and grouped views under Option B) provides it; a custom
  `TreeView` overlay (Option A) does not, and any announcement of "selected" there would be
  something we synthesize — which we should not do without your confirmation that it's needed
  and correct.
- **Count announcements.** Reuse the existing pattern: selection-count changes announced as
  `AnnouncementCategory.Status` (debounced), gated by `AnnounceStatus`. Bulk operations that
  complete (e.g. "3 messages moved") stay `AnnouncementCategory.Result`.
- **Level / expand-collapse (grouped views).** Under Option A these remain native. Under
  Option B they must be reproduced (grouping + header expander, or `AutomationProperties.Level`
  + item status) and **A/B-compared with a screen reader by you before commit.**
- **No instructions baked into `AutomationProperties.Name`.** Any "press Ctrl+Space to select"
  guidance is a `Hint` announce on first focus, gated by `AnnounceHints` — never part of the
  accessible name.
- **`SelectorItemAccessibilityTests`.** If the grouped views become `Selector`-derived
  (Option B), every bound item type (headers + `MailMessageSummary`) must override `ToString()`
  and be added to `SelectorItemAccessibilityTests`, per the project's Selector-accessibility rule.

---

## 11. Infrastructure Changes

Explicit per the project's spec rule. Final list depends on the Section 4 choice; both
variants noted.

**F6 pane ring**
- No new panes. The message-area control(s) stay at ring position 3. Under Option B the
  three trees collapse into grouped-list variants of one control but occupy the same ring slot.

**CommandRegistry**
- `mail.selectAll` already exists (`Ctrl`+`A`). Confirm its `isAvailable` guard covers all
  four views (today it is scoped to `MessageList`; grouped views must be included).
- New commands to consider (category `Mail`): `mail.selectToTop` / `mail.selectToBottom`
  (currently unregistered `Ctrl`+`Shift`+`Home`/`End` handlers), and possibly
  `mail.toggleSelection` (`Ctrl`+`Space`). Per prior-spec precedent, pure range-navigation
  modifiers (`Shift`+arrow/`Home`/`End`/`PgUp`/`PgDn`) are **not** registered — they stay in
  the control's key handler. Decide `Ctrl`+`Space`'s status in review.

**AutomationProperties.Name**
- No new instruction text. If Option B, new group-header rows need short label names and,
  where applicable, `AutomationProperties.Level`. No role words, no shortcuts (project rule).

**AccessibilityHelper.Announce calls**
- Selection-count change → `Status` (debounced).
- Keep existing bulk-op result announces as `Result`.
- Any first-focus usage hint → `Hint`.

**VM state**
- New `MainViewModel.SelectionSummary` (string) + backing selected-count, raised on selection
  change, bound by the status bar.
- Under Option B, a shared selection source (the grouped `ListView`'s `SelectedItems`, or a
  small selection service) replaces the per-tree `SelectedItem` reads in `GetSelectedMessages()`,
  `ToggleFlagCommandAsync`, `IsGroupRowSelected`, and the three trees' `PreviewKeyDown`.
- `IsMessageOpen` and related flags: unaffected, but verify the reading-pane auto-load on
  selection change does not fire once per row during a `Shift`-drag (debounce or suppress
  during range extension).

---

## 12. Files to Modify

Indicative; Option B touches more.

| File | Change |
|---|---|
| `QuickMail/Views/MainWindow.xaml` | Status-bar selection segment; (Option B) replace the three `TreeView`s with grouped `ListView`s + templates + expand/collapse. |
| `QuickMail/Views/MainWindow.xaml.cs` | Unify key handling toward the Section 6 map; `GetSelectedMessages()` for all four views incl. `ToGroupTree` and group headers; selection-count wiring; investigate/replace custom `Shift` handlers. |
| `QuickMail/Views/GroupedMessageTreeController.cs` | (Option A) add multi-select state machine; (Option B) retire or re-home into a grouped-list controller. |
| `QuickMail/ViewModels/MainViewModel.cs` | `SelectionSummary` property + change notification; group-message expansion helpers for header selection. |
| `QuickMail.Tests/` | New `MessageSelectionTests` / extend `SelectorItemAccessibilityTests`; see Section 13. |

---

## 13. Test Matrix

Every gesture in Section 6 × every cell below must be verified. This matrix **is** the
acceptance criteria.

**Dimensions**
- **View:** flat Messages · Conversations · From · To
- **Row type:** individual message · group header
- **Group state:** expanded · collapsed
- **Input:** keyboard · mouse
- **Mode:** normal · `--online`
- **A11y:** UIA `SelectionItem` state present · count announced · level/expand announced (grouped)
- **Downstream:** Delete · Move to Folder · Copy to Folder · Flag — from keyboard, context menu, toolbar — act on the full effective selection; focus lands correctly after.

**Representative must-pass cases**
1. Flat list: `Shift`+`Down`×3 then `Delete` removes 4, focus lands in place. (regression of #221 generalized)
2. Flat list: `Ctrl`+`Down`×2 lands focus on an **unselected** row (focus≠selection proven).
3. Flat list: `Ctrl`+`Space` builds a non-contiguous selection; count correct; Move moves exactly those.
4. Flat list: `Shift`+`Home`, `Shift`+`End`, `Shift`+`PgUp`, `Shift`+`PgDn` each extend correctly.
5. Conversations: `Ctrl`+`Space` on a collapsed 3-message header → count reports 3; Delete removes 3.
6. From: mixed selection (whole sender + 2 individual messages elsewhere) → deduped count; Move correct.
7. To: any multi-select works at all (currently impossible; `GetSelectedMessages` ignores `ToGroupTree`).
8. `Ctrl`+`A` in the search box still selects search text, not messages (guard regression).
9. Reading-pane auto-load does not fire per row during a `Shift`-drag.
10. Empty view: every gesture is a silent no-op, no announcement noise.

**Automated (xUnit / StaFact)**
- Selection-count summary computes deduped message counts for mixed header+message selections.
- `GetSelectedMessages()` returns the full effective selection for all four views.
- (Option B) new bound item types added to `SelectorItemAccessibilityTests` with `ToString()`.

---

## 14. Out of Scope

- **Rubber-band / rectangular selection** (mouse drag box). Keyboard + click only.
- **Persisting selection across view switches or folder changes.** Switching view or folder
  clears the selection (define and test the clear, but do not carry selection across).
- **Selection in the folder tree or any dialog list** beyond what `selection-shortcuts-spec.md`
  already shipped (Address Book `Ctrl`+`A`). This spec is message-views only.
- **Type-to-select / checkbox "mark" mode** as an alternative selection UI. If Option A is
  chosen and native selection announcement proves inadequate, a checkbox mode may be revisited
  as a *separate* spec — not here.
- **Changing what Delete/Move/Copy/Flag do**; only *which set* they act on.
- **The tree strategy prototype** (Option B spike) is a decision aid, not a deliverable of the
  implementation phase.

---

## 15. Open Questions for Review

1. **Tree strategy (Section 4): A, B, or C?** This gates everything. My recommendation is B
   with a screen-reader spike first — but the level/expand-collapse-vs-native-multiselect
   accessibility tradeoff is your call, not mine.
2. **Status-bar count threshold:** show only when ≥2 selected (my proposal), or always?
3. **`Ctrl`+`Space` as a registered command** (palette-visible, rebindable) or a raw key
   handler like the other range modifiers?
4. **Reading-pane behavior during multi-select:** confirm the pane should *not* reload while a
   `Shift`/`Ctrl` selection is being built, and what it should show when the focused row is
   unselected (blank? last-opened? unchanged?).
5. **Selection on view/folder switch:** confirm "always clear" is the desired rule.
6. **Announcement verbosity:** should every `Shift`+arrow step speak the running count, or only
   speak the count on settle (and let per-item selected/unselected state carry the rest)? This
   is an AT-experience question for you.
