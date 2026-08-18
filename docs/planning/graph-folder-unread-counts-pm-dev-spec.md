# Live Folder Unread Counts on Microsoft 365 (Graph) — PM & Dev Specification

**Status:** Draft for review (Tim → Kelly)
**Date:** August 18, 2026
**Fixes:** #491 (Microsoft 365 folder unread counts only update on a manual refresh); folds in #487.
**Recommended decision (§9):** keep the badge **server-authoritative** (option 2 in the issue) — let
Graph accounts into the existing debounced folder-count refresh, rather than deriving the count from the
local cache.

## Table of Contents

1. Executive Summary
2. User Problem & Opportunity
3. Feature Scope & Acceptance Criteria
4. Architecture & Technical Decisions
5. Keyboard Walkthrough (Mandatory)
6. Accessibility Checklist (Mandatory)
7. Acceptance Walkthrough (Mandatory)
8. Resolved Decision

## 1. Executive Summary

On a Microsoft 365 (Graph) account, the unread count beside a folder in the folder tree is frozen at
whatever the last full folder-list fetch returned, for the whole session — it does not move when you
read, delete, or move mail, even inside QuickMail. Because that count is part of the folder's
**accessible name** ("Newsletters, 3 unread"), a screen reader speaks the wrong number every time the
user arrows past the folder until they press Refresh. The fix is small and keeps the two backends
consistent: remove the one guard that excludes Graph accounts from the existing debounced,
server-authoritative folder-count refresh. IMAP already runs through that path; Graph has simply been
locked out of it.

## 2. User Problem & Opportunity

### 2.1 Current state (verified against code)

- **`UnreadCount` is refreshed live only for IMAP.** `MainViewModel.ScheduleFolderCountRefresh`
  (`MainViewModel.cs:7732`) is the single entry to the in-place count update, and it returns early for
  anything that is not IMAP: `if (account is null || account.BackendKind != BackendKind.ImapSmtp) return;`
  (`:7738`). So all the events that call it — mark-read, delete, move, new-mail arrival, sync end,
  read-state reconcile (nine sites, e.g. `:3538`, `:3558`, `:3614`, `:5098`) — are **no-ops for Graph**.
  A Graph folder's count is therefore whatever the last `GetFoldersAsync` returned (startup, reconnect,
  a folder create/rename/delete, or manual Refresh).
- **The refresh path is already backend-agnostic.** `_imap` is the `IMailService` router
  (`MainViewModel.cs:22`), so `_imap.GetFoldersAsync(accountId)` in `RefreshFolderCountsDebouncedAsync`
  (`:7765`) already routes to `GraphMailService` for a Graph account and returns Graph's authoritative
  `unreadItemCount` (per #491's own analysis, `GraphMailService.GetFoldersAsync` maps it). And
  `ApplyFolderCounts` (`:7781`) maps counts onto cached folders by `FullName` — the opaque folder id for
  Graph — and notifies the tree nodes in place. Neither is IMAP-specific. **Only the guard stops Graph.**
- **The debounce/throttle is already account-keyed and backend-neutral.** A burst of mark-reads
  collapses to one refresh via `_folderCountCts` (cancel-and-reschedule, `:7740`), a 1-second delay
  (`FolderCountRefreshDelay`, `:253`), and a 6-second minimum interval per account
  (`FolderCountMinInterval`, `:256`). This is exactly the "debounced `GetFoldersAsync` per account"
  the issue's option 2 describes — it already exists; Graph is just not allowed in.
- **Why it became visible (relationship to #486).** Before #486 a quiet Graph folder's rows were stale
  too, so the row and the badge were wrong together. #486 made the rows correct live (read state
  reconciled from the sweep's id listing), which is what exposes the disagreement — the row reads "read"
  while the number beside the folder still counts it. `OnFolderReadStatesReconciled` (`:3486`) already
  calls `ScheduleFolderCountRefresh` and hits the guard.

### 2.2 The product decision (from the issue)

The issue names two options and the question behind them: should the badge mean **"unread in this
mailbox"** (server-authoritative) or **"unread in what QuickMail has cached"** (cache-derived)?

- **Option 1 — derive locally.** Count unread among cached rows. Live and free of round-trips, but it
  **under-counts** when unread mail exists beyond the sync window, and it makes the Graph badge mean
  something *different* from the IMAP badge.
- **Option 2 — let Graph into the debounced server refresh.** Keeps the badge server-authoritative and
  **identical in meaning to IMAP's**, at the cost of one `GetFoldersAsync` per debounced refresh — which
  IMAP already pays, and which the throttle already bounds.

**This spec recommends option 2** (§4, §8). The complaint is "the spoken number is wrong"; option 2
makes it correct and consistent with IMAP, and it is a near-trivial change because the entire path
already exists. Option 1 trades correctness for saving a round-trip the IMAP path already spends, and
would leave the two backends meaning different things by the same badge.

## 3. Feature Scope & Acceptance Criteria

### 3.1 In scope

- Let Microsoft 365 (Graph) accounts into `ScheduleFolderCountRefresh`, so the nine existing triggers
  update Graph folder counts through the same debounced, throttled, server-authoritative path IMAP uses.

### 3.2 Out of scope

- Any change to how counts are displayed or announced (they already flow through `ApplyFolderCounts` →
  `NotifyUnreadChanged`).
- A per-folder `unreadItemCount` fetch (a possible future optimization to avoid a full folder-list
  fetch); reusing the existing `GetFoldersAsync` path is simpler and correct, and the throttle bounds
  the cost.
- Instant pre-server local adjustment of the count (a hybrid). The ~1-second debounce matches IMAP's
  behavior; adding an optimistic local decrement is a separate enhancement, not needed to fix #491.

### 3.3 Acceptance criteria

- On a Graph account, reading / deleting / moving a message — including inside QuickMail — updates the
  folder's unread count within the debounce+throttle window, without a manual Refresh.
- New mail arriving on a Graph account (via the delta poll) updates the folder's unread count.
- The count shown/spoken matches the server's `unreadItemCount` (authoritative), the same meaning as an
  IMAP folder's badge — not a cache-derived subset.
- A burst of actions on a Graph account collapses to a bounded number of `GetFoldersAsync` calls (the
  existing debounce + 6-second throttle), not one per action.
- IMAP behavior is unchanged.

## 4. Architecture & Technical Decisions

The change is the guard in `ScheduleFolderCountRefresh` (`MainViewModel.cs:7738`). Today:

```csharp
if (account is null || account.BackendKind != BackendKind.ImapSmtp) return;
```

Becomes: keep the null guard (an unknown id has no backend to route), but admit Graph alongside IMAP —
e.g. gate on "is a real, count-bearing backend" rather than "is IMAP":

```csharp
if (account is null) return;
if (account.BackendKind is not (BackendKind.ImapSmtp or BackendKind.MicrosoftGraph)) return;
```

(POP3 stays excluded — it has a single maildrop, no per-folder server counts.) Everything downstream is
already correct for Graph:

- `RefreshFolderCountsDebouncedAsync` calls `_imap.GetFoldersAsync` (the router) → `GraphMailService`
  returns `unreadItemCount`. No change.
- `ApplyFolderCounts` matches by `FullName` (the Graph folder id) onto the cached models and notifies
  nodes in place. No change.
- The debounce (`_folderCountCts`), delay, and 6-second throttle (`_lastFolderCountSweep`) are
  account-keyed and backend-neutral. No change.

**Cost.** For a Graph account a refresh is one `GetFoldersAsync` (a folder-list call returning all
folders with their counts), debounced to at most one per second and throttled to one per six seconds per
account during activity — the same profile the IMAP path already runs at. The delta poll's own 60-second
cadence is unaffected; this rides the existing triggers, it does not add a new timer.

**Why not a smaller/local fix.** Deriving the count from cached rows (option 1) would avoid the fetch but
under-count beyond the sync window and diverge from IMAP's meaning; the round-trip option 2 spends is one
the app already spends for IMAP, and the throttle already bounds it. Consistency and correctness win over
saving a bounded, already-paid cost.

## 5. Keyboard Walkthrough (Mandatory)

### Path A — read a message on a Graph account

1. User is on a Graph account. The **Newsletters** folder shows/says "Newsletters, 3 unread."
2. User opens one of its unread messages and reads it (or marks it read). The row updates to read (already
   correct since #486).
3. Within about a second, the folder badge updates: arrowing back to **Newsletters** now says
   "Newsletters, 2 unread." Before this change it kept saying "3 unread" until a manual Refresh.

### Path B — new mail arrives on a Graph account

1. The 60-second delta poll brings in a new unread message to the Inbox.
2. The message appears in the list, and the Inbox folder badge increments — e.g. "Inbox, 5 unread" →
   "Inbox, 6 unread" — without the user pressing Refresh.

### Path C — a burst of mark-reads

1. User selects several unread messages and marks all read.
2. The badge does not thrash: the refresh is debounced and throttled, so it settles on the correct count
   after the burst, with one server fetch — the same as IMAP.

## 6. Accessibility Checklist (Mandatory)

- **No new announcement.** The count is already part of the folder node's accessible name
  (`MailFolderModel.AutomationName`), refreshed in place via `NotifyUnreadChanged`. This change only makes
  that existing name *correct* for Graph; it introduces no new `Announce` call and speaks nothing extra.
- **No `AutomationProperties.Name` change, no new control, no F6 or Selector change.** Purely a data-
  freshness fix to an existing spoken value.
- **The win is accessibility-specific.** A stale count is not merely shown; it is spoken on every pass of
  the folder. Making it live is the point — the screen-reader user stops being told a wrong number.

## 7. Acceptance Walkthrough (Mandatory)

Tests:

- **Guard admits Graph, excludes POP3.** `ScheduleFolderCountRefresh` schedules a refresh for a Graph
  account and for an IMAP account, and does not for a POP3 account or an unknown id. (Pin the guard.)
- **`ApplyFolderCounts` applies Graph counts by folder id.** Given fresh folder models carrying
  `unreadItemCount` keyed by the Graph `FullName`, the cached models' `UnreadCount` and the account's
  `TotalUnread` update, and the tree nodes are notified. (Already backend-agnostic; pin it for Graph.)
- **Debounce/throttle unchanged.** A burst of schedule calls for a Graph account results in one fetch
  within the window (reuse the existing debounce test shape for Graph).
- No IMAP test changes.

Manual (Graph account): read a message and confirm the folder badge drops within ~1s without Refresh;
let new mail arrive and confirm the Inbox badge increments; confirm the spoken folder name reflects the
new count.

## 8. Resolved Decision

**Option 2 — server-authoritative.** Admit Graph accounts to the existing debounced folder-count
refresh, keeping the badge's meaning identical to IMAP's ("unread in this mailbox") and its value correct
against the server. This is recommended over option 1 (cache-derived) because the complaint is a *wrong*
spoken number, option 2 makes it correct and consistent across backends, the round-trip it costs is one
IMAP already pays and the throttle already bounds, and the entire path already exists — the change is
admitting Graph to the guard. (Product call is ultimately Kelly's; this spec recommends option 2 and is
written to it.)
