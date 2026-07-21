# Connection Handling — Architecture Review

Tracking issue: [#312](https://github.com/kellylford/QuickMail/issues/312). Status: **review / proposal — not yet implemented.**

## Why this document exists

We have fixed IMAP connection problems one at a time — #268 (forcibly closed after idle), #219 (re-consent leaves account not connected), #278 (RefreshFolderCounts "not connected" noise), #311 (delete "may not have completed") — and they keep reappearing in new forms. A closed bug, #126 ("adding a new account disconnects the other accounts"), has regressed. The pattern across all of them is the same: **connection state is unreliable, recovery is inconsistent, and the UI's picture of "connected" drifts from the real socket.**

This review maps our current model, root-causes the recurring failures, compares against how mature clients (Thunderbird, MailKit's own guidance, the IMAP IDLE RFC, isync/OfflineIMAP) actually work, and proposes a target architecture so we fix the model instead of the next symptom.

---

## 1. Current architecture (as built)

Primary file: `QuickMail/Services/ImapMailService.cs`. Supporting: `SyncService.cs`, `ViewModels/MainViewModel.cs`, `ChangeNotifierRouter.cs`, `WatcherStartGate.cs`.

### 1.1 Per-account connection pool
- One `AccountConnectionPool` per account (`_pools`, keyed by `Guid`). Each IMAP operation **rents one authenticated `ImapClient`, does exactly one folder operation, returns it** — MailKit clients are treated as single-command objects (`ImapMailService.cs:946-949`).
- Sizing: `DefaultMaxConnectionsPerAccount = 6`, absolute max 15, `ForegroundReservedConnectionCount = 2` (`ImapMailService.cs:24-26`). Configurable via `MaxImapConnectionsPerAccount`, clamped `[1,15]` (`:1012-1017`).
- Two semaphores: `_slots` (hard cap on live clients) and `_backgroundSlots` (background-priority rents must also acquire this; at cap 6, background work is limited to 4 concurrent, reserving 2 slots for foreground). Priority is set per call site (foreground: message summaries `:202`; background: inbox status `:189`, prefetch `:305`).
- Rent (`RentAsync`, `:1170-1284`): pop from idle `Stack`, filter dead clients via `IsClientUsable` (**cached** `IsConnected && IsAuthenticated`, `:1052-1056`), and **only NOOP-probe a client idle > 30s** (`StaleProbeThreshold`, `:1135`, `:1209-1210`, `:1229`). Hot reuse (< 30s) **skips the probe.** If nothing reusable, create fresh (`:1261`).
- Return (`:1286-1311`): if still usable, push back and stamp `_returnedUtc`; else dispose.

### 1.2 Connect / reconnect / auth
- `ConnectAsync` (`:55-97`): per-account lock; if a pool exists and `Matches(account, maxConnections)`, update in place and warm one lease — **no reconnect**; otherwise rebuild the pool (disconnecting its clients, `:82-83`).
- **There is no reconnect-and-retry wrapper around command operations.** The pool self-heals only at *rent* time. Once leased, a mid-operation drop throws straight to the caller. Retry/backoff exists only in startup connect (`MainViewModel.ConnectOneAccountAsync`, 3 attempts w/ jitter) and inside the IDLE watcher loop.
- NOOP appears three ways: pool stale-probe (`:1064-1077`), a 10-minute app heartbeat (`MainViewModel.cs:1821`), and a per-host NOOP before sync (`SyncService.cs:88-97`).

### 1.3 IDLE watchers
- IMAP IDLE **is** used: one long-lived watcher connection per IMAP account (`StartWatchers` → `RunIdleWatcherAsync`, `:762-899`), 25-minute re-IDLE cycle, backoff-with-jitter on error.
- **The IDLE connection is created directly (`:807`) and is NOT counted by the pool.** Real per-account socket count is therefore `pool clients (≤6) + 1 IDLE`.
- On first failure the watcher fires `AccountReachabilityChanged(id, false)`; on recovery, `(id, true)` (`:822-826`, `:876-877`).

### 1.4 UI connection-state source of truth
- The badge is `AccountModel.IsConnected`, a **separately tracked flag**, set only by `ApplyAccountStatus(account, folders)` (non-null folders ⇒ connected) (`MainViewModel.cs:2632-2645`).
- `ImapMailService.IsConnected(accountId)` is merely `_pools.ContainsKey(id)` (`:99`) — "a pool object exists," **not** socket liveness.
- Status labels derive from `_cachedFolders.Count`. All of this is a tracked snapshot, so it can drift from reality in both directions.

### 1.5 Cancellation model
- `MainViewModel` holds one CTS per operation *type* (`_messageActionCts`, `_folderCts`, `_bgSyncCts`, …). `ReplaceCts` (`:90-96`) atomically swaps and **cancels the previous** one.
- Because the token is threaded into MailKit, **a new user action of the same type cancels the in-flight IMAP command mid-network**, tearing down the pooled connection (it fails `IsClientUsable` on return and is discarded). Rapid navigation/deletes therefore continuously churn connections.

---

## 2. Failure-mode inventory (what's actually going wrong)

| Symptom / issue | Root cause in current design |
|---|---|
| **#126 regression** — adding an account disconnects the others | `StartWatchers` unconditionally cancels + restarts **every** account's IDLE watcher whenever the connected set changes (details in §3). |
| **#311** — delete "may not have completed" during a series | Hot-reused pooled connection (< 30s) is never probed; if dropped, `MoveToTrashBatchAsync` throws `ServiceNotConnectedException`. No reconnect-retry, so the first drop is surfaced. `ReplaceCts` on `_messageActionCts` can also cancel the prior delete mid-command, killing its connection. |
| **#268** — "forcibly closed" after idle | Mitigated only for *stale* (>30s) connections via the NOOP probe; hot-reused connections have the same exposure. |
| **#278** — RefreshFolderCounts "not connected" noise | Same dead-hot-connection class; swallowed with a log, leaving stale counts. |
| **#219** — re-consent leaves account not connected until restart | Connection/pool state not refreshed after mid-session re-auth. |
| General "flakiness" | UI `IsConnected` flag drifts from socket truth; a transient IDLE reachability blip flips accounts to disconnected even when the pool is healthy; a silently dead pool still reads connected. |

**Cross-cutting weaknesses**
- **No transparent reconnect-and-retry** around live commands — the single most impactful gap.
- **Untyped failure handling:** broad `catch (Exception)` everywhere; no distinction between a recoverable drop (`IOException` / `ServiceNotConnectedException`) and a genuine server rejection (`ImapProtocolException`), so drops can't be auto-retried distinctly.
- **IDLE socket is uncounted** by the pool cap, so true usage is `cap + 1` against per-IP server limits.
- **Cancellation churns connections** by design.
- **UI status is a drift-prone flag,** not derived from the last real I/O outcome.
- **Watcher leak on account removal:** `StartWatchers` never stops a watcher for an account absent from the passed list; teardown relies entirely on `DisconnectAsync` being called for that id.

---

## 3. #126 regression — exact root cause

The pool layer is **not** the culprit. When an account is added, `RefreshAccountList` (`MainViewModel.cs:5961-6007`) only connects accounts the backend lacks; existing accounts' pools are untouched.

The regression is in the **IDLE-watcher restart**. After connecting the new account, `RefreshAccountList` calls `WireUpWatchers` (`:6005`), which recomputes the connected set; because the set **grew**, `WatcherStartGate.HasChanged` returns true (it only suppresses restarts when the set is *identical* — `WatcherStartGate.cs:19` `SetEquals`), so it calls `StartWatchers(connected, …)` with the **full** list (`:1889`). `StartWatchers` then, for **every** account in the list, unconditionally:

```csharp
if (_idleCts.TryRemove(account.Id, out var old))
{
    try { old.Cancel(); old.Dispose(); } catch { }   // ImapMailService.cs:769-772
}
...
_ = Task.Run(() => RunIdleWatcherAsync(accountId, linked.Token), linked.Token);  // :779
```

So **adding one account cancels and rebuilds the held IDLE connection of every already-connected account at once.** Each restarted watcher opens a fresh `ImapClient` (`:807`) simultaneously; against any server with a per-IP connection limit (the exact hazard the codebase guards elsewhere), the concurrent reconnect burst trips the cap, the watcher lands in its catch and fires `AccountReachabilityChanged(false)` (`:876-877`) → `ApplyAccountStatus(account, null)` → `IsConnected = false`. The previously-fine accounts render as disconnected. That is #126, reproduced.

The earlier #126 fix corrected *stale handler snapshots* but reintroduced the disconnect via the unconditional full-restart.

**Fix direction:** make `StartWatchers` **diff** against `_idleCts` — start watchers only for newly-added ids, stop only removed ids, and **leave already-running watchers untouched.** Adding an account must never disturb existing accounts' IDLE connections. (Small, well-scoped; can ship ahead of the larger redesign.)

---

## 4. What mature clients actually do

### Thunderbird (open source; verified against comm-central)
- **Up to 5 cached connections per server** by default. The value is hardcoded in `GetMaximumConnectionsNumber()` (`nsImapIncomingServer.cpp`): reads per-server `max_cached_connections`; if 0/absent → **5**; negative → 1. There is **no** `mail.server.default.max_cached_connections` default pref line — the 5 comes from code.
- IDLE on by default (`mail.server.default.use_idle = true`). No separate pref isolating a watch connection; a connection parked in IDLE is unavailable for commands, so other work uses the pool.
- **Transparent retry:** a failed URL gets "a second chance to run" (retry-once on timeout / unexpected termination); stale connections reaped by `ConnectionTimeOut()`; requests queued when no connection is free. TCP keepalive on by default (`mail.imap.tcp_keepalive.enabled = true`). A hard error is shown to the user **only when retries ultimately fail.**

### IMAP IDLE — RFC 2177
- You **MUST NOT** send a command while a connection is idling — send `DONE` first. IDLE is one-folder, one-connection, no multiplexing.
- Servers MAY log off idlers; clients are advised to **re-issue IDLE at least every 29 minutes** (assumes a 30-min timeout). In practice clients renew every ~9–10 min for margin; Gmail is stricter. Re-issuing IDLE is itself the keep-alive; no separate NOOP needed while idling.
- "One idle + separate command connection" is a protocol-driven convention, not an RFC requirement.

### MailKit (this app's library; Jeffrey Stedfast's guidance) — most actionable
- **Reuse the client; reconnect only when needed:** `if (!client.IsConnected) Connect(); if (!client.IsAuthenticated) Authenticate();`.
- **No auto-reconnect — you own it:** *"You will need to manually re-connect once you've been disconnected. It will not auto-reconnect for you."*
- **`IsConnected` is a cached flag** — stays `true` after a silent drop until the next I/O fails. Never pre-check it for liveness; drive reconnect off the **caught exception**.
- **NOOP every few minutes** to keep an idle command connection warm and to force an I/O that reveals death — "even then … the server can drop you whenever it wants."
- **Reconnect-and-retry pattern** (author's own IDLE example): catch → reconnect (gated on IsConnected/IsAuthenticated) → retry, in a bounded loop. `IOException` **always** disconnects; `ImapProtocolException` **often** does; `ServiceNotConnectedException` takes the same fix.
- **`ImapClient` is NOT thread-safe:** one client cannot run concurrent commands — serialize per client. (Our one-client-per-operation pool already respects this.)
- **IDLE monopolizes the client → ~2 connections:** *"At most I'd probably have 2 connections, 1 for idle and 1 for processing."*

### isync/mbsync & OfflineIMAP (the minimalist contrast)
- mbsync: stateless connect → sync → disconnect, **no IDLE**. OfflineIMAP: **one connection per account** by default; IDLE and extra connections are opt-in and connection-expensive. Both treat many/long-lived connections as a deliberate cost, not a baseline.

### Mature baseline (synthesis)
- **~2 connections per account:** 1 IDLE/watch + 1 for commands. A small pool (2–3) is plenty; we do not need 6.
- **Keep IDLE and commands on separate connections;** renew IDLE every ~9–10 min; gate on the IDLE capability with a NOOP-loop fallback.
- **Never trust `IsConnected` alone;** liveness = periodic NOOP on the command connection + the re-IDLE cycle + TCP keepalive.
- **Transparent reconnect-and-retry** on `IOException`/`ImapProtocolException`/`ServiceNotConnectedException`, bounded, surfacing a visible error (not a blank pane) only when retries are exhausted.
- **Derive UI status from the last observed I/O outcome,** not a cached boolean — update the badge on the same events that drive reconnect so status and reality can't drift. Route through `AccessibilityHelper.Announce(..., AnnouncementCategory.Status, …)`.

---

## 5. Gap analysis — us vs. the baseline

| Dimension | QuickMail today | Mature baseline | Gap |
|---|---|---|---|
| Connections/account | Pool ≤6 **+1 uncounted IDLE** | ~2 (1 idle + 1 command) | Over-provisioned; IDLE uncounted vs. per-IP caps |
| Reconnect on command drop | None — throws to caller | Bounded reconnect-and-retry | **Biggest gap** |
| Liveness detection | NOOP only if idle >30s | NOOP every few min + exception-driven | Hot connections never probed |
| Exception handling | Broad `catch (Exception)`, untyped | Typed drop vs. rejection → targeted retry | No auto-retry distinction |
| UI status | Cached `IsConnected` flag | Derived from last I/O outcome | Drifts both ways |
| Add-account behavior | Restarts **all** watchers | Touch only the changed account | #126 regression |
| Cancellation | Same-type action cancels in-flight command | Don't cancel committed writes | Connection churn |

---

## 6. Recommended target architecture

A **keep-and-harden** direction (not a rewrite). The pool model is defensible and already respects MailKit's thread-safety rule; the failures are in recovery, counting, and status truth.

1. **Reconnect-and-retry wrapper for command operations.** A single helper that, on `IOException` / `ServiceNotConnectedException` / `ImapProtocolException`, discards the dead client, rents/creates a fresh one, and retries once (bounded). Route all mutating and fetch operations through it. Resolves #311, #268 (hot path), #278.
2. **Count the IDLE socket against the account's budget** (or right-size the pool to ~2–3 and treat IDLE as one of them). Prevents `cap+1` from tripping per-IP limits.
3. **Diff watchers in `StartWatchers`** — start added, stop removed, leave the rest running. Resolves the #126 regression. *(Shippable now, independently.)*
4. **Single source of truth for connection status.** Drive `IsConnected`/status text from the last observed I/O outcome (success = connected, caught drop = reconnecting, exhausted retries = error), updated on the same events as reconnect. Stop letting a transient IDLE blip flip the badge on its own.
5. **Type the failure handling.** Distinguish recoverable drops from genuine rejections so #1 can auto-retry the former and surface the latter.
6. **Don't cancel committed writes.** Give delete/move their own per-operation token (or a small serialized queue) instead of sharing `_messageActionCts`, so a second keystroke can't abort an in-flight write mid-command.
7. **Reconsider the 30s stale-probe threshold** for foreground writes (probe regardless, or lower it) — largely subsumed by #1, but a cheap belt-and-suspenders.

---

## 7. Proposed follow-up work items

- **[P0] Watcher diffing** — fix #126 regression in `StartWatchers`. Small, isolated. *(Can precede the rest.)*
- **[P0] Reconnect-and-retry wrapper** — resolves #311, #268 (hot path), #278.
- **[P1] Connection-status source of truth** — resolves the general "flaky/drifting" complaint and #219's symptom.
- **[P1] Typed exception handling** — enabler for the retry wrapper.
- **[P2] Right-size the pool + count IDLE** — relates to #152 (per-host cap).
- **[P2] Cancellation policy for writes** — secondary #311 contributor.

Maps existing open issues: **#311, #278, #152** (and regressed **#126**, symptom of **#219**) onto the plan above.

---

## 8. Open questions for Kelly

1. **Keep-and-harden vs. redesign to ~2 connections/account?** The audit supports keep-and-harden; a redesign to the Thunderbird/MailKit ~2-connection shape is cleaner long-term but a bigger change. Which appetite?
2. **Order of operations:** ship the isolated #126 watcher fix and the #311 retry wrapper first (fast, high value), then the status-truth refactor? Or do the status refactor as one deliberate pass?
3. **Status semantics you want surfaced:** "Connected / Reconnecting… / Disconnected (reason)" — is that the right vocabulary for the badge and the `AnnouncementCategory.Status` announcements?

---

### Primary sources
- Thunderbird: `nsImapIncomingServer.cpp`, `mailnews.js` (searchfox.org/comm-central)
- RFC 2177 (IMAP IDLE): https://www.rfc-editor.org/rfc/rfc2177
- MailKit: `IsConnected` docs (mimekit.net), `Documentation/Examples/ImapIdleExample.cs`, jstedfast issues #988/#665/#502/#76/#613/#936, discussion #1723
- isync/mbsync: https://isync.sourceforge.io/mbsync.html — OfflineIMAP: https://www.offlineimap.org/doc/FAQ.html
