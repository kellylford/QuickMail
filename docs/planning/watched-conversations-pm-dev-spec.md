# Watched Conversations — PM & Dev Specification

**Status:** Approved for implementation
**Author:** Kelly with Claude
**Applies to:** All accounts, all backends (IMAP and Microsoft Graph). Purely local state — nothing is written to any server.
**Depends on:** The virtual-folder machinery (`\u0000All*` sentinels, `FetchVirtualAsync`, `OnFolderSynced`) and `ConversationBuilder.NormalizeSubject`. Both already exist.

---

## Table of Contents

1. Executive Summary
2. User Problem & Opportunity
3. Design Principles
4. Feature Scope & Acceptance Criteria
5. Architecture & Technical Decisions
6. **Keyboard Walkthrough** (required)
7. **Accessibility Checklist** (required)
8. **Acceptance Walkthrough** (required)
9. Success Metrics
10. Implementation Phases
11. Files to Create / Modify
12. Tests to Add
13. Known Risks & Resolved Questions
14. Appendix — Keyboard Reference
15. **Out of Scope** (required)

---

## 1. Executive Summary

A conversation you care about — a release announcement, a bug thread, a trip itinerary — arrives once and then keeps arriving, scattered across folders and accounts, mixed into everything else. Today the only way to keep an eye on it is to flag each message as it lands, which means you have to notice it first. This spec adds **watched conversations**: press `Ctrl+Shift+W` on any message and its whole conversation is watched from then on. A new **Watched Conversations** virtual folder, sitting alongside All Mail and All Flagged, shows every message in every watched conversation — including replies that have not arrived yet, which join automatically as they sync. All the usual machinery (view modes, filters, sorts, saved views) works there because it is an ordinary aggregate folder.

The distinction that makes this worth building: a flag is a mark on a message you already have; a watch is a standing subscription to a conversation you expect more of.

---

## 2. User Problem & Opportunity

### 2.1 Current state (verified)

| Surface | Today | Pain | Who feels it |
|---|---|---|---|
| Following a thread | Flag each message individually (`K`, or `mail.toggleFlag` at `MainWindow.xaml.cs:1028`) | Purely reactive — you must already have found the new message before you can flag it. Flagging is not a subscription. | Anyone tracking an ongoing thread |
| All Flagged folder | `\u0000AllFlagged`, predicate `m => m.IsFlagged` (`MainViewModel.FetchAllFlaggedAsync`, ~line 4526) | Mixes every purpose flags serve — to-do, follow-up, important — into one list. Adding thread-following makes it useless for all of them. | Flag users |
| Saved views | `SavedView.VirtualFolderKey` / `Folders` (`Models/SavedView.cs`) | A saved view selects *folders*, optionally with a filter. There is no folder whose membership is "conversations I chose". | Power users |
| Conversations view mode | `ConversationBuilder.Build` groups by normalized subject (`Services/ConversationBuilder.cs:39`) | Groups whatever is in the current folder. There is no way to pin a group so it persists across folders and across time. | Everyone |
| Search | Ad-hoc, per-session | You can search a subject, but you have to remember to, and re-type it, every time. | Everyone |

Verified absences: no `IsWatched`, no `watch` service, no `watches.json`, no `Ctrl+Shift+W` registration anywhere in the solution. `Ctrl+W` is registered three times (`tabs.close`, `mail.closeMessage`, `preview.close`/`window.close` in child windows), all availability-gated; `Ctrl+Shift+W` is free.

### 2.2 Target personas

- **The release-watcher.** Subscribes to a product's announcement list. Wants the thread about *this* release, not the whole list. Presses `Ctrl+Shift+W` on the announcement; every follow-up lands in one folder.
- **The bug-thread participant.** A long back-and-forth spanning weeks, interleaved with hundreds of other messages. Wants a durable place the thread lives without moving it out of its folder.
- **The trip planner.** Confirmations, changes, and questions all share a subject. Wants them collected until the trip is over, then to stop watching in one keystroke.
- **The screen-reader user (Kelly).** Needs the watch state to be discoverable by keyboard, announced on change, and never communicated by colour alone. Needs the folder to behave exactly like the aggregate folders already learned.

### 2.3 Why now

Every piece is already built. The aggregate-folder framework has seven working instances and a clean dispatch point (`FetchVirtualAsync`). `ConversationBuilder.NormalizeSubject` already defines what "the same conversation" means in this app. `OnFolderSynced` already has the branch structure for "which live arrivals belong in the current aggregate". This feature is a new predicate plus a small JSON store — it adds no new architecture.

---

## 3. Design Principles

1. **A watch is a subscription, not a mark.** Future messages must join without any further user action. Any design where the user has to re-mark each new message has failed the feature's purpose.
2. **Zero server footprint.** Watches are local state. Nothing is written to IMAP, nothing to Graph, no keyword, no flag. A watch never changes what another mail client sees.
3. **It is an ordinary aggregate folder.** Not a special surface. Every view mode, filter, sort, saved view, and row-speech setting works because it goes through the same code path as All Flagged.
4. **One key does both directions.** `Ctrl+Shift+W` watches an unwatched conversation and unwatches a watched one. No separate unwatch command to discover.
5. **Never announce what the platform already reports; always announce what it cannot.** Watch state has no platform representation, so the toggle announces its outcome, and the row exposes it as a user-configurable spoken field — not as a colour.

---

## 4. Feature Scope & Acceptance Criteria

### 4.1 In scope (v1)

| Feature | Setting / Shortcut | Default | Notes |
|---|---|---|---|
| Watch/unwatch a conversation | `Ctrl+Shift+W`, command `mail.toggleWatch` | — | Category `Mail`. Available when a message is selected. |
| Message menu item | **Message → _Watch Conversation** | — | `InputGestureText="Ctrl+Shift+W"`, checkable, reflects current state. |
| Watched Conversations folder | Sentinel `\u0000AllWatched` | — | Child of the **All Mail** tree group, after All Flagged. Also in the flat folder list and the folder picker. |
| Watch persistence | `%AppData%\QuickMail\watches.json` | `[]` | New `WatchService`, atomic write, same shape as `ViewService`. |
| Row spoken field | Row field id `watched` | **disabled** | Appended to `RowFieldCatalog.MessageFields`; user enables it in Message List Fields. |
| Saved view support | `VirtualFolderKey = "AllWatched"` | — | Works via the existing save-view path; needs the display-name map entry. |
| `--online` support | — | — | Sweeps IMAP folders client-side exactly as All Flagged does. |

**Acceptance criteria**

- AC1. `Ctrl+Shift+W` on a message with an unwatched conversation adds a watch; the same keystroke on any message of that conversation removes it.
- AC2. A message that arrives during sync whose normalized subject matches a watch appears in the Watched Conversations folder without user action, while that folder is open.
- AC3. The folder lists every cached message across all accounts and folders whose normalized subject matches a watch, newest first.
- AC4. Watches survive app restart, cache clear (`ClearCachedMailAsync`), and running in `--online` mode.
- AC5. A message with a blank or whitespace-only subject cannot be watched; the attempt announces why and changes nothing.
- AC6. Unwatching from inside the Watched Conversations folder removes that conversation's messages from the visible list immediately.
- AC7. `ViewMode = Conversations` inside the folder yields exactly one group per watched conversation that has messages.

### 4.2 Explicitly out of scope (v1)

See §15 for the full list with rationale.

---

## 5. Architecture & Technical Decisions

### 5.1 Key architectural decisions

---

**Decision 1: A watch is a stored *subscription* keyed on normalized subject, not a per-message boolean column.**

**Alternatives:**
1. `is_watched INTEGER` column on `MessageSummary`, toggled per message. Pro: mirrors `is_read`/`flag_id`, no new service. Con: **fails Principle 1** — a reply that arrives tomorrow has `is_watched = 0` and never appears. Also needs a preserve rule in `UpsertSummariesAsync`'s `ON CONFLICT DO UPDATE` (which already has bespoke logic for `preview_text`, `internet_message_id`, and three-way `flag_id` reconciliation) or every sync would clobber it, and it dies with the cache on `ClearCachedMailAsync`.
2. Store the watch as a `SavedView` with a search term. Pro: reuses saved views. Con: saved views select folders and apply a filter; they have no free-text predicate, and one view per watched thread would flood the Views menu and the hotkey namespace.
3. **Chosen:** a JSON list of watch entries, each holding a normalized subject. Folder membership is computed at fetch time by matching each message's normalized subject against the set.

**Rationale:** Membership computed from a stored predicate is the only shape where a message that does not exist yet can already be a member — which is the entire feature. It also gets the other properties for free: no schema migration, no upsert clobber risk, survives cache clear, works identically in `--online` mode, and the watch list is a tiny human-readable file. Matching cost is one `HashSet` lookup per message against a set that will realistically hold tens of entries.

---

**Decision 2: The matching key is `ConversationBuilder.NormalizeSubject(subject)`, compared case-insensitively.**

**Alternatives:**
1. RFC 5322 `References` / `In-Reply-To` threading. Pro: correct threading; immune to subject collisions. Con: **those headers are not fetched or stored anywhere in this codebase** — verified: no reference to either header in the solution, and `MessageDetail` stores only `to_addr, cc, reply_to, plain_body, html_body, attachments_json, calendar_ics`. Adding them means new IMAP/Graph fetch fields, two schema columns, a backfill for every cached message, and a rewrite of `ConversationBuilder`. That is a larger feature than this one, and it would change how the existing Conversations view groups mail.
2. Exact subject string. Con: `Re:` and `Fwd:` prefixes would each start a new "conversation", so replies would not be watched — failing Principle 1.
3. **Chosen:** normalized subject, the key `ConversationBuilder` already uses.

**Rationale:** Consistency is the deciding argument. The app already tells the user "these messages are one conversation" using this exact key, in the Conversations view mode. A watch that used a different definition of "same conversation" than the view mode the user can see would be a bug the user could not explain. It also makes AC7 fall out for free.

**Accepted limitation, stated plainly:** two unrelated messages with the same subject are the same conversation to this app. Watching "Re: Hi" would collect every "Hi" thread from every account. This is inherent to subject threading and already visible in the Conversations view. Mitigations in v1: blank subjects are refused outright (§5.5), the watch label shown to the user is the full original subject, and unwatching is one keystroke. Header-based threading is the v2 upgrade path (§15) and — importantly — it would be a change to `NormalizeSubject`'s replacement, not to this feature's design, because the matching key is looked up through one method.

---

**Decision 3: `MailMessageSummary.IsWatched` is a transient observable stamped from the watch set, never persisted.**

The model gets `[ObservableProperty] private bool _isWatched;` with no database column and no serialization. It is stamped in two places — after any load, and on toggle — by a single VM helper that re-reads the watch set. Rationale: the watch set is the sole source of truth. A persisted per-message copy could disagree with it, and any disagreement would be a bug with no correct resolution. Making it observable is required so the row's spoken field refreshes in place when the user toggles, without rebuilding the list and losing focus — the same reason `IsRead` and `FlagId` are observable.

**Consequence to handle:** `ReconcileMessageState(existing, fresh)` (`MainViewModel.cs:3067`) copies mutable state from a freshly fetched copy onto the shown row. A freshly fetched summary has `IsWatched = false` (nothing stamped it), so **`IsWatched` must NOT be added to `ReconcileMessageState`** — adding it would wipe the flag on every aggregate merge. This is the opposite of the rule for a persisted field, and is called out here because the surrounding lines make the wrong choice look right.

---

**Decision 4: `WatchService` holds an in-memory `HashSet<string>` (OrdinalIgnoreCase) as the match index, rebuilt on every mutation.**

`IsWatched(string subject)` is called once per message per fetch — potentially 50,000 times on a large All-Watched load and again on every sync batch. A `List.Any(...)` scan would be O(watches) per message. The set is rebuilt (not incrementally patched) on add/remove because the list is tiny and rebuilding removes a whole class of index-drift bug.

### 5.2 Runtime mode compatibility

| Mode | `LocalStoreService` available? | What the folder does | Fallback |
|---|---|---|---|
| Normal | ✓ | `LoadAllSummariesAsync()` then filter by watch predicate | — |
| `--online` | ✗ | Sweeps every non-`ExcludeFromAllMail` folder of every account via IMAP, filtering client-side | ✓ — identical structure to `FetchAllFlaggedAsync`'s online branch |
| `--profileDir <path>` | ✓ | `watches.json` lives in the alternate profile dir (via `ProfileContext`) | — |

`WatchService` itself is independent of `LocalStoreService` and works in all three modes. `watches.json` is read once at construction and written on every mutation.

### 5.3 Code reuse and duplication risks

- **`FetchWatchedAsync` will look almost exactly like `FetchAllFlaggedAsync` and `FetchContactMailAsync`.** Those two are already near-duplicates of each other (~60 lines each, differing only in the predicate and the status strings). Adding a third copy is the honest risk here. **Plan:** implement `FetchWatchedAsync` as a third instance in v1 rather than refactoring all three under one parameterized helper. Rationale: the three differ in their status text, their online-sweep logging tags, and (for contact mail) an extra return-folder concept; a shared helper would need three parameters and a callback, and refactoring two working, shipped fetch paths while adding a third is exactly the change most likely to break something invisibly. **This duplication is logged as a deliberate debt in §13.1 with a named follow-up.**
- **`WatchService` mirrors `ViewService`** (load/save one JSON file in the profile dir, atomic write, swallow deserialization errors). Deliberate: matching the established shape is worth more than the ~15 lines saved by a generic JSON-store base.

### 5.4 Shared component audit (mandatory)

| Component | File | Other consumers | Change needed | Risk |
|---|---|---|---|---|
| `MainViewModel.IsVirtualFolder` | `ViewModels/MainViewModel.cs:465` | `OnFolderSynced` (dedup key choice), `ReconcileCurrentFolderAsync` (skips virtual), `ApplyFolderDisplayNames` | Add `AllWatched` ordinal equality | Low — adding one term to an OR chain. If omitted, the folder would silently dedupe by per-folder UID and try to reconcile against a real IMAP folder. |
| `MainViewModel.BuildFolderTree` | `~3497` | Folder tree UI, expansion-state preservation via `NodeKey` | Add one `FolderTreeNode` child | Low — `NodeKey` keys on `FullName`, unique by construction |
| `MainViewModel.RebuildFolderListFromCache` | `~3351` | Flat `Folders` list; `ApplyViewAsync` resolves `VirtualFolderKey` against it | Add `AllWatchedFolder` to the seed list | Low. **Note:** `AllFlaggedFolder` is absent from this list today — a pre-existing inconsistency, masked by `ApplyViewAsync`'s fallback that constructs a fresh `MailFolderModel`. Not fixed here (out of scope); noted in §13.2. |
| `MainViewModel.FetchVirtualAsync` | `4507` | Every aggregate entry point: `SelectFolderAsync`, `RefreshAsync`, `ClearViewAsync`, `SetSyncDaysAsync`, `ApplyViewAsync`, `SetArchiveFolderAsync` | Add one branch | Low — ordered `if` chain, new branch is disjoint from all existing tests |
| `MainViewModel.OnFolderSynced` | `2440` | `SyncService.FolderSynced` on every folder sync | Add one `else if` branch | **Medium** — this is the live-arrival path and the payoff for AC2. Must be placed among the aggregate branches, before the real-folder branch. Covered by a dedicated test. |
| `ViewManagerViewModel.VirtualFolderDisplayName` | `ViewModels/ViewManagerViewModel.cs:117` | View Manager's folder-summary label | Add `"AllWatched" => "Watched Conversations"` | Low — pure switch; without it the label reads "Virtual folder" |
| `RowFieldCatalog.MessageFields` | `Models/RowFieldCatalog.cs:89` | `Views.RowSpeech` (binding superset, **positional**), `Helpers.RowSpeechBuilder`, Message List Fields chooser, `RowFieldCatalog.ValuesFor` | Append one `RowFieldDef` **at the end** | **Medium** — the array order is the binding order and values arrive positionally in the converter. The file's own comment says "Append new fields at the END of this list." Inserting anywhere else would misalign every field after it. Not added to `MessageDefaultOrder`, so it ships disabled and existing users' spoken rows are byte-identical. |
| `MailMessageSummary` | `Models/MailMessageSummary.cs` | Everything | Add one transient observable | Low — new property, no existing consumer reads it |
| `App.xaml.cs` DI root | `App.xaml.cs` | — | Construct `WatchService`, pass to `MainViewModel` | Low — additive constructor parameter |
| `StubServices.cs` | `QuickMail.Tests/StubServices.cs` | Every test that constructs a VM | Add `StubWatchService` | Low, but **every** `MainViewModel` construction in tests must be updated if the ctor parameter is required — prefer an optional parameter defaulting to `null`, matching how `_flagService` is handled (`ResolveFlagNamesAsync` early-returns when null). |

**Summary:** this feature modifies `MainViewModel` (six sites), `ViewManagerViewModel` (one), `RowFieldCatalog` (one), `MailMessageSummary` (one), `MainWindow.xaml` (one menu item), and `App.xaml.cs` (wiring). Changes to `RowFieldCatalog` are backward-compatible because the new field is appended in canonical order and left out of the default layout, so `DefaultLayout` seeds it disabled and no existing spoken row changes. Changes to `OnFolderSynced` are additive branches that cannot be reached unless the Watched folder is selected. `MainViewModel`'s new constructor parameter is optional so no existing test construction breaks.

### 5.5 Data model

```csharp
// Models/WatchedConversation.cs
public class WatchedConversation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The matching key: ConversationBuilder.NormalizeSubject of the subject at watch time.
    /// Compared case-insensitively. Never empty — WatchService refuses blank keys.</summary>
    public string NormalizedSubject { get; set; } = string.Empty;

    /// <summary>The full original subject as it appeared on the watched message, for display and
    /// announcement. Never used for matching.</summary>
    public string Label { get; set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
```

**Blank-subject rule (AC5).** `NormalizeSubject("")` and `NormalizeSubject("Re:")` both return `""`. A watch with an empty key would match every blank-subject message in every account — a trap that would look like the feature is broken. `WatchService.Watch` returns `false` for a blank normalized subject and stores nothing; the VM announces "Cannot watch a conversation with no subject." (`Result`).

### 5.6 Service interface

```csharp
// Services/IWatchService.cs
public interface IWatchService
{
    IReadOnlyList<WatchedConversation> GetAll();

    /// <summary>True when a message with this subject belongs to a watched conversation.</summary>
    bool IsWatched(string subject);

    /// <summary>Adds a watch for this subject's conversation. Returns false when the subject
    /// normalizes to empty (nothing is stored) or the conversation is already watched.</summary>
    bool Watch(string subject);

    /// <summary>Removes the watch covering this subject. Returns false when it was not watched.</summary>
    bool Unwatch(string subject);

    /// <summary>Raised after any mutation, so the VM can re-stamp rows and refresh the folder.</summary>
    event EventHandler? WatchesChanged;
}
```

---

## 6. Keyboard Walkthrough (Mandatory)

### Path A — watch a conversation from the message list

1. User is in the message list (any folder), focus on a message row whose subject is "QuickMail 1.4 released". User presses `Ctrl+Shift+W`.
   **Expected:** The watch is stored. Screen reader announces: *"Watching conversation: QuickMail 1.4 released."* (`Result`). Focus does not move; the list does not scroll; the row is not rebuilt. If the user has enabled the **Watched** row field, re-focusing the row now speaks "watched" among its fields.
2. User presses `Ctrl+Shift+W` again on the same row.
   **Expected:** The watch is removed. Screen reader announces: *"Stopped watching: QuickMail 1.4 released."* (`Result`). Focus unchanged.

### Path B — open the Watched Conversations folder

1. User presses `Ctrl+2` to focus the folder tree, then arrows to the **All Mail** group and expands it.
   **Expected:** Children in order: All Mail, All Inboxes, All Drafts, All Sent, All Archive, All Trash, All Flagged, **Watched Conversations**.
2. User presses Enter on **Watched Conversations**.
   **Expected:** Status text shows "Loading watched conversations…", then "N watched messages." Message list holds every message across all accounts and folders whose conversation is watched, newest first. Each row's source folder is available in its accessible name (aggregate views stamp `FolderDisplayName`, account-qualified when more than one account is involved).
3. User presses `Ctrl+3` to move to the message list and arrows through rows.
   **Expected:** Normal message-row speech. No extra announcement per row beyond the user's configured fields.

### Path C — a reply arrives while the folder is open (the payoff, AC2)

1. Watched Conversations is the selected folder. A background sync delivers a new message whose subject is "Re: QuickMail 1.4 released".
   **Expected:** The message is inserted into the list in date order. No announcement is made by this feature (the existing batched-insert behaviour applies; screen readers are deliberately not interrupted per-arrival). Focus and selection are unchanged.
2. User navigates to the new row.
   **Expected:** It reads as a normal message row, and speaks "watched" if that field is enabled.

### Path D — unwatch from inside the folder (AC6)

1. User is in the Watched Conversations folder, focus on a row belonging to the "QuickMail 1.4 released" conversation. User presses `Ctrl+Shift+W`.
   **Expected:** Screen reader announces *"Stopped watching: QuickMail 1.4 released."* (`Result`). **Every** message of that conversation is removed from the visible list, not just the focused one. Focus lands on the row that follows the removed block (or the last row if the removed block was at the end; or the empty-list state if it was the only conversation).
2. User presses `Ctrl+Shift+W` on a row in the now-focused, different conversation.
   **Expected:** That conversation is unwatched and its rows leave too. Same focus rule.

### Path E — empty state

1. Watched Conversations is opened with no watches stored.
   **Expected:** Message list is empty. Status text reads: "No watched conversations. Press Ctrl+Shift+W on a message to watch its conversation."
2. User presses `Ctrl+3` (focus message list).
   **Expected:** Focus moves to the empty list; nothing is announced beyond the list's own empty state. The user is not stranded — `F6` and `Ctrl+2` still work.

### Path F — blank subject refused (AC5)

1. User presses `Ctrl+Shift+W` on a message with an empty subject.
   **Expected:** Nothing is stored. Screen reader announces: *"Cannot watch a conversation with no subject."* (`Result`). The row does not change.

### Path G — Conversations view mode inside the folder (AC7)

1. In Watched Conversations, user opens **View → View Mode → Conversations**.
   **Expected:** One group per watched conversation that has at least one message. A watched conversation with no cached messages produces no group (and no empty placeholder row). Group headers speak per the Conversation row layout.

### Path H — save it as a view

1. In Watched Conversations, user saves the current view (View → Views → Save Current View…), names it "Watching".
   **Expected:** The view is stored with `VirtualFolderKey = "AllWatched"` and no `Folders` entries. In the View Manager, its folder summary reads **"Watched Conversations"**, not "Virtual folder".
2. User applies the saved view later.
   **Expected:** Watched Conversations is selected and fetched, with the view's mode/filter/sort applied.

### Path I — menu discovery

1. User presses `Alt+M` to open the **Message** menu with a message selected.
   **Expected:** A **Watch Conversation** item is present, showing `Ctrl+Shift+W`, checked when the selected message's conversation is watched. Activating it does exactly what the keystroke does.

---

## 7. Accessibility Checklist (Mandatory)

- **`AutomationProperties.Name`** — One new value: the folder tree node label **"Watched Conversations"** (short label, no role name, no instruction). No new controls otherwise.
- **`AnnouncementCategory`** — Three announcements, all `Result`, all through `AccessibilityHelper.Announce`:
  - "Watching conversation: {subject}" — outcome of an explicit user action.
  - "Stopped watching: {subject}" — same.
  - "Cannot watch a conversation with no subject." — outcome (a refusal) of an explicit user action.

  No `Status` announcement is added for the folder load; the existing `StatusText` mechanism covers it, matching All Flagged. **No announcement fires on live arrival** — that would interrupt the user on every sync, and the batched-insert design in `OnFolderSynced` exists specifically to avoid re-announcing rows.
- **Nothing is announced that the platform already reports.** Watch state has no platform representation on a list row, so it is exposed as a user-configurable **spoken row field** (`watched`, ships **disabled**) rather than as an unconditional announcement or an override of `StatusDisplay`. `StatusDisplay` is deliberately left alone: it already prioritises Flag > Replied > Fwd > New, and inserting "Watched" would displace flag information the user chose to see.
- **Screen reader browse mode / WebView2** — Not touched. No reading-pane change.
- **Focus restoration** — No dialog is opened by this feature. The one focus decision is Path D: after unwatching from inside the folder, focus moves to the row following the removed block.
- **F6 ring** — **No change.** No new pane; the folder is a node in the existing folder tree and the existing message list.
- **Checkbox / radio groups** — None introduced. The Message menu item is a standard checkable `MenuItem`.
- **Colour-only information** — **None.** Watch state is text (spoken field) and a menu check state. No swatch, no colour, no icon-only indicator.
- **New Window Checklist** — Not applicable; v1 creates no `Window` subclass.
- **Modal dialog rules** — Not applicable; v1 opens no dialog.

---

## 8. Acceptance Walkthrough (Mandatory)

### Scenario 1 — Primary happy path

**Setup:** App running, normal mode, at least two accounts with cached mail, All Mail selected.

1. Arrow to a message with a real subject. Press `Ctrl+Shift+W`. **Verify:** "Watching conversation: {subject}" is heard; status/UI otherwise unchanged; focus still on the same row.
2. Open the folder tree, expand **All Mail**. **Verify:** **Watched Conversations** is the last child, after All Flagged.
3. Enter on it. **Verify:** The list contains the watched message *and* every other cached message sharing its normalized subject, across both accounts, newest first. Status reads "N watched messages."
4. Switch to **Conversations** view mode. **Verify:** Exactly one group, titled with the normalized subject, count matching step 3.
5. Press `Ctrl+Shift+W` on a row. **Verify:** "Stopped watching: {subject}"; the list empties; status reads the empty-state text.

### Scenario 2 — Live arrival (highest-risk step)

**Setup:** Watched Conversations open, one watch active for a conversation you can send to.

1. From another client (or another account), send a reply to the watched thread. Wait for sync (or press F5). **Verify:** The new message appears in the list, in date order, **without** re-selecting the folder. No announcement interrupts. Focus and selection unchanged.
2. **Edge case:** Do the same while a *different* folder is selected. **Verify:** No crash; the message goes to its own folder as normal. Re-select Watched Conversations. **Verify:** The new message is present.

### Scenario 3 — Persistence

1. Note the watch list. Close the app. Reopen. **Verify:** Watched Conversations still lists the same messages.
2. Inspect `%AppData%\QuickMail\watches.json`. **Verify:** Valid JSON, one entry per watch, `NormalizedSubject` has no `Re:`/`Fwd:` prefix, `Label` holds the original subject.
3. Clear the local mail cache (Settings → clear cached mail). **Verify:** `watches.json` is untouched; after re-sync the folder repopulates.

### Scenario 4 — `--online` mode

**Setup:** Launch with `--online`.

1. Open Watched Conversations. **Verify:** It populates from IMAP (slower), does not show a blank list, does not throw. Status reaches "N watched messages." or the empty-state text — never stays on "Loading…".
2. Press `Ctrl+Shift+W` on a message. **Verify:** Announcement fires and the watch persists; reopening the folder reflects it. (`WatchService` does not touch `LocalStoreService`.)

### Scenario 5 — Blank subject and duplicate-watch edges

1. Find/create a message with an empty subject. Press `Ctrl+Shift+W`. **Verify:** "Cannot watch a conversation with no subject."; `watches.json` gains no entry.
2. Press `Ctrl+Shift+W` on a message, then on a *different* message of the same conversation. **Verify:** The second press **unwatches** (it is the same conversation), it does not create a second entry. `watches.json` returns to its prior size.
3. Watch a conversation whose subject differs only in case from another watched one. **Verify:** Treated as the same conversation; no duplicate entry.

### Scenario 6 — Shared-component regressions (one per §5.4 consumer)

1. **All Flagged** — open it, flag/unflag a message. **Verify:** Unchanged behaviour; flagged messages appear/disappear as before.
2. **All Inboxes** — open it, let a sync deliver a message. **Verify:** Live arrival still works (the `OnFolderSynced` branch chain is intact).
3. **Message List Fields chooser** — open it. **Verify:** A **Watched** field is listed, **unchecked**. Existing enabled fields and their order are unchanged. Enable Watched, close, arrow a watched row. **Verify:** "watched" is spoken; arrow an unwatched row — nothing extra is spoken (SpeakMode WhenTrue).
4. **Row speech with the field disabled** — with a fresh profile, arrow rows. **Verify:** Spoken text is identical to before the feature (positional binding not misaligned).
5. **View Manager** — save Watched Conversations as a view, open View Manager, select it. **Verify:** Folder summary reads "Watched Conversations".
6. **Saved views over real folders** — apply a pre-existing saved view. **Verify:** Unchanged.
7. **Folder picker (Go to Folder…)** — open it. **Verify:** Watched Conversations is listed and selecting it navigates there.

### Scenario 7 — Screen reader pass

1. With a screen reader running, tab/F6 through the app with Watched Conversations selected. **Verify:** The tree node reads "Watched Conversations" (not a type name, not "virtual folder"), the message list reads rows normally, `F6` cycles all existing panes with no new stop and none missing.
2. Toggle watch on and off three times. **Verify:** Each announcement is heard, once, with no duplication and no re-announcement of the focused row.
3. In Settings, turn **Announce results** off. Toggle watch. **Verify:** **No** announcement (the category is respected). Turn it back on. **Verify:** Announcement returns.

---

## 9. Success Metrics

- **Behavioural:** A user can watch three conversations, open one folder, and see all of their messages together — and a reply that arrives an hour later is there without any further action.
- **Keyboard-centric:** Watch, unwatch, navigate to the folder, and change its view mode are all reachable keyboard-only. Verified in §6 Paths A–I.
- **No regressions:** Full test suite passes. Spoken row text for a user who never opens the fields chooser is byte-identical to before.
- **Accessibility:** No colour-only state; every announcement respects `AnnounceResults`; no new F6 stop; no announcement on live arrival.
- **Online mode:** §8 Scenario 4 passes.

---

## 10. Implementation Phases

### Phase 1 — Watch model, service, persistence

**Goal:** Watches can be added, removed, queried, and survive restart. No UI.
**Deliverables:** `Models/WatchedConversation.cs`, `Services/IWatchService.cs`, `Services/WatchService.cs`, DI wiring in `App.xaml.cs`, `StubWatchService` in `StubServices.cs`.
**Tests:** `WatchServiceTests` — round-trip, normalization, case-insensitive match, blank refusal, duplicate handling, corrupt-file tolerance.
**Risk:** Corrupt/absent `watches.json` must degrade to an empty list, never throw at startup (matches `ViewService`). Caught by test.

### Phase 2 — The virtual folder

**Goal:** Watched Conversations exists in the tree and lists the right messages, in both runtime modes.
**Deliverables:** Sentinel + `IsVirtualFolder` + `BuildFolderTree` + flat `Folders` list + folder picker + `FetchVirtualAsync` branch + `FetchWatchedAsync` + `VirtualFolderDisplayName`.
**Tests:** `WatchedConversationsTests` — sentinel recognised as virtual, display-name map.
**Risk:** Missing the `IsVirtualFolder` entry would make dedup and reconciliation take the real-folder path — silent and confusing. Explicit test.

### Phase 3 — Live arrivals

**Goal:** AC2. New matching messages join the open folder during sync.
**Deliverables:** `OnFolderSynced` branch.
**Tests:** Matching arrival is accepted, non-matching rejected, branch ordering (an aggregate branch must be evaluated before the real-folder branch).
**Risk:** Branch placed after the real-folder test would never run. Test asserts acceptance directly.

### Phase 4 — Toggle command, menu, row state

**Goal:** AC1, AC5, AC6. The user can actually use it.
**Deliverables:** `mail.toggleWatch` registration, Message menu item, `MailMessageSummary.IsWatched`, the re-stamp helper, removal-from-list on unwatch, announcements, `RowFieldCatalog` entry, `docs/KEYBOARD-SHORTCUTS.md` row.
**Tests:** Command registered with the right gesture/category; toggle add/remove; blank refusal; re-stamp; unwatch removes rows.
**Risk:** `RowFieldCatalog` insertion position (must be last). Test asserts the catalog's binding order is unchanged for pre-existing fields.

---

## 11. Files to Create / Modify

### Create

| File | Purpose |
|---|---|
| `QuickMail/Models/WatchedConversation.cs` | Watch entry data class |
| `QuickMail/Services/IWatchService.cs` | Service interface |
| `QuickMail/Services/WatchService.cs` | `watches.json` persistence + match index |
| `QuickMail.Tests/WatchServiceTests.cs` | Service tests |
| `QuickMail.Tests/WatchedConversationsTests.cs` | Folder/command/VM tests |

### Modify

| File | Changes |
|---|---|
| `QuickMail/Models/MailMessageSummary.cs` | `IsWatched` transient observable |
| `QuickMail/Models/RowFieldCatalog.cs` | Append `watched` field (end of `MessageFields`; **not** in `MessageDefaultOrder`) |
| `QuickMail/ViewModels/MainViewModel.cs` | Sentinel, `IsVirtualFolder`, `BuildFolderTree`, `RebuildFolderListFromCache`, `FetchVirtualAsync`, `FetchWatchedAsync`, `OnFolderSynced`, `RegisterCommands`, toggle method, re-stamp helper |
| `QuickMail/ViewModels/ViewManagerViewModel.cs` | `VirtualFolderDisplayName` entry |
| `QuickMail/Views/MainWindow.xaml` | Message menu item |
| `QuickMail/Views/MainWindow.xaml.cs` | Folder-picker aggregate list |
| `QuickMail/App.xaml.cs` | Construct and inject `WatchService` |
| `QuickMail.Tests/StubServices.cs` | `StubWatchService` |
| `docs/KEYBOARD-SHORTCUTS.md` | `Ctrl+Shift+W` / `mail.toggleWatch` row |
| `docs/USER-GUIDE.md` | Watched Conversations section |

---

## 12. Tests to Add

| Test Class | Test Methods | Coverage |
|---|---|---|
| `WatchServiceTests` | Round-trip through `watches.json`; `Watch` normalizes `Re:`/`Fwd:` chains; `IsWatched` matches a reply subject; case-insensitive; blank subject refused and nothing stored; watching an already-watched conversation returns false without duplicating; `Unwatch` by any member subject; corrupt file yields empty list; `WatchesChanged` fires on mutation only | Happy path + every branch |
| `WatchedConversationsTests` | `AllWatchedFolder` sentinel starts with NUL and is recognised by the virtual-folder test; `VirtualFolderDisplayName("AllWatched")` returns "Watched Conversations"; `mail.toggleWatch` is registered with `Ctrl+Shift+W` in category `Mail`; toggle adds then removes; blank subject refused; `RowFieldCatalog` contains `watched`, it is last in `MessageFields`, it is absent from the default enabled order, and the pre-existing binding order is unchanged | Feature surface + regression guards |

**Key rule applied:** every new public method gets a test; every branch of `Watch`/`Unwatch`/`IsWatched` gets a case.

---

## 13. Known Risks & Resolved Questions

### 13.1 Risks

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| Subject-only threading collects unrelated same-subject mail | **High** (inherent) | Minor | Accepted and documented (§5.2 Decision 2). Blank subjects refused; unwatch is one keystroke; the matching key is behind one method so header threading can replace it later without touching this feature's design. |
| `IsWatched` wrongly added to `ReconcileMessageState`, wiping the flag on every merge | Medium | Major | Called out explicitly in §5.1 Decision 3 as a trap. Test asserts the flag survives a merge. |
| `RowFieldCatalog` entry inserted mid-array, misaligning positional bindings | Medium | **Blocker** (silently wrong speech for every row) | Append-at-end rule stated in three places; regression test asserts the pre-existing binding order. |
| `OnFolderSynced` branch ordered after the real-folder test → live arrivals never appear | Medium | Major | Dedicated test; Phase 3 exists solely for this. |
| Third near-duplicate of the aggregate fetch method (`FetchAllFlagged` / `FetchContactMail` / `FetchWatched`) | High (certain) | Minor | **Accepted debt.** Refactoring two shipped fetch paths while adding a third is the riskier change. Follow-up issue to unify all three under one predicate-parameterized helper, to be filed at merge. |
| Watch list grows unbounded | Low | Minor | Entries are tiny; matching is a hash lookup. No manager UI in v1 (§15) — `watches.json` is hand-editable if it ever matters. |

### 13.2 Resolved questions

| Question | Decision | Rationale |
|---|---|---|
| Watch a message or a conversation? | **Conversation** | A per-message flag cannot include messages that do not exist yet — that is the whole feature. |
| Match across all accounts, or only the originating one? | **All accounts** | Matches the unified-inbox model of every other aggregate folder. A thread that continues on another account still shows up. |
| Do watches expire? | **Never; unwatch manually** | Predictable. Nothing disappears without the user asking. A stale watch costs one hash lookup. |
| How much management UI in v1? | **None beyond the folder and the toggle** | Smallest correct feature. A manager can follow once the shape is proven in use. |
| Should `StatusDisplay` show "Watched"? | **No** | It would displace flag/replied/unread text the user already relies on. The configurable row field is the right home. |
| Should live arrivals announce? | **No** | It would interrupt on every sync; the batched-insert design exists to prevent exactly that. |
| Is `AllFlagged`'s absence from the flat `Folders` list and the folder picker fixed here? | **No** | Pre-existing and out of scope for this feature. Noted for a separate issue. `AllWatched` is added to both, which is the correct behaviour for a new folder. |

---

## 14. Appendix — Keyboard Reference

| Key | Command ID | Action | Notes |
|---|---|---|---|
| `Ctrl+Shift+W` | `mail.toggleWatch` | Watch / unwatch the selected message's conversation | Category `Mail`. Available when a message is selected. Free — `Ctrl+W` (close tab/message) is a different gesture. |

Reachable without a hotkey: **Message → Watch Conversation**, and the Command Palette (`Ctrl+Shift+P` → "Watch Conversation"), since every registered command appears there automatically.

---

## 15. Out of Scope (Mandatory)

v1 explicitly does **not** include:

- **Header-based threading.** `References` / `In-Reply-To` are not fetched or stored by this app. Adding them is a larger, separate feature that would change the existing Conversations view mode too. Matching stays on normalized subject.
- **A watch manager window or dialog.** No list-of-watches UI, no rename, no bulk delete. Unwatch is done from any message in the conversation. `watches.json` is human-readable if manual surgery is ever needed.
- **Per-watch account scoping.** Every watch matches all accounts. No per-watch narrowing control.
- **Watch expiry or auto-cleanup.** No idle timeout, no maximum count, no pruning.
- **Server-side sync of watch state.** Watches are local to this profile on this machine. No IMAP keyword, no Graph category, no roaming between machines.
- **Notifications for watched conversations.** A new message in a watched conversation raises no toast and no sound. The existing notification settings are untouched.
- **Watching from the Conversations / From / To group headers.** v1's toggle acts on a selected *message*. Pressing `Ctrl+Shift+W` on a group header row does nothing.
- **An unread-count badge on the Watched Conversations node.** The node shows no count; `SuppressUnreadCount` behaviour matches the other aggregates.
- **A `MessageFilter` value for watched.** Watch is a folder, not a filter. You cannot apply "watched" as a filter on top of some other folder in v1.
- **Fixing `AllFlagged`'s absence from the flat folder list and folder picker.** Pre-existing inconsistency, separate issue.
- **Refactoring `FetchAllFlaggedAsync` / `FetchContactMailAsync` / `FetchWatchedAsync` into one helper.** Deliberate debt, follow-up issue (§13.1).
