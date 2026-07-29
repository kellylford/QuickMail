# Connection drops: diagnostic plan

**Issue:** accounts repeatedly show as disconnected in the account list.
**Branch:** `diag/connection-drops`
**Relates:** #312 (architecture review), #314 (single source of truth), #126, #311, #278

## Why the previous fixes didn't hold

Every prior fix targeted a *specific* failure we happened to catch: the #126 watcher
restart storm, the #311 hot-reuse dead socket, the #268 long-idle drop. Each was real
and each was fixed. The disconnects continued because we were never measuring the
thing the user actually sees — we were fixing failures we could reason about and
hoping they were the same failures the user was hitting.

Three facts make that guesswork inevitable today:

1. **`EnableLogging = off`** in the live profile. Every disconnect the user has hit
   has left no trace at all. There is nothing to read after the fact.
2. **Connection status is not derived from connection state.** `ImapMailService.IsConnected`
   returns `_pools.ContainsKey(accountId)` — whether a pool *object* exists, which has
   nothing to do with whether a socket is alive. Meanwhile `AccountModel.IsConnected`
   is a plain bool written from eight places in `MainViewModel` with no record of who
   wrote it or why.
3. **Reachability has exactly one source: the IDLE watcher.** `AccountReachabilityChanged`
   is raised only from `RunIdleWatcherAsync` (`ImapMailService.cs:870, :922`). Every
   other IMAP operation — fetch, folder counts, flags, delete, sync — succeeds or fails
   without ever touching account status. One transient IDLE hiccup marks an account
   disconnected while everything else about it works fine, and nothing marks it back.

The user's report is precise about this and worth quoting as the design driver:

> I notice this for sure because the accounts show disconnected in the account list.
> I'm not sure if they are really disconnected or not and have not checked other areas.

So the first question instrumentation must answer is not "why did the connection drop"
but **"was there a drop at all, or is the label lying?"** Those need completely
different fixes, and we cannot currently tell them apart.

## A concrete environmental suspect

`mail.kellford.com` and `mail.theideaplace.net` both resolve to **50.87.253.68** — the
same shared-hosting IMAP server. Our connection cap is per *account*
(`MaxImapConnectionsPerAccount = 6`); a Dovecot/cPanel server's limit
(`mail_max_userip_connections`) is per *user+IP*, and shared hosts also apply a global
per-IP cap. Those two accounts together can open 12 pooled command connections plus 2
held IDLE sockets against a single host, and nothing in our code knows they are the
same host.

This is a *hypothesis*, not a diagnosis. It predicts a specific, checkable signature
in the journal — connect failures clustered on one host, correlated across two
accounts, with the server's own rejection text. The instrumentation below is designed
to confirm or kill it rather than assume it.

## Design principles for this build

- **No behavior changes.** This build only observes. If the disconnects change
  frequency, that is signal about the environment, not about our fixes — and we would
  not be able to tell the difference if we changed connection behavior at the same time.
- **Cannot be turned off.** The journal writes regardless of `EnableLogging`. The one
  thing we cannot afford is another month of disconnects with no data.
- **Answers questions, not just records events.** A raw event log still requires
  someone to reconstruct what happened. The journal records explicit *verdicts*
  ("label says disconnected, account is actually reachable") so the answer is
  greppable.
- **Self-service.** The user must be able to see the state and export a report at the
  moment the symptom appears, without a debugger, a command line flag, or a rebuild.

## What gets built

### 1. `ConnectionJournal` — an always-on, bounded, structured journal

A dedicated `connection.log` in the profile directory, independent of `LogService` and
of the `EnableLogging` setting. Also an in-memory ring buffer (last 2000 events) that
backs the diagnostics window.

Every entry carries: timestamp, account label, host, category, phase, outcome, and
detail (full inner-exception chain, including `SocketError` and native code).

Rotation at 5 MB to `connection.log.1`, so it is bounded but keeps a long history.

### 2. Instrumented call sites

| Where | What is recorded |
| --- | --- |
| `CreateAuthenticatedClientAsync` | connect attempt / success / failure, host, port, TLS mode, elapsed ms, full error chain, **server rejection text** |
| `AccountConnectionPool.RentAsync` | hot reuse, probed reuse, probe failure + discard, unusable discard, new connection created, and a pool census (in use / idle / total / max) |
| pool return & disconnect | client returned, pool torn down and why |
| `RunIdleWatcherAsync` | watcher start, IDLE enter/return, 25-min NOOP keepalive, error with retry count and backoff, permanent exits (no IDLE support, account removed) |
| reachability | every raise, with an explicit **reason string** — replaces bare `AccountReachabilityChanged?.Invoke` |
| `ExecuteWithReconnectAsync` | first-attempt drop detected, retry outcome |
| `ApplyAccountStatus` (VM) | every write to `AccountModel.IsConnected`, tagged with the calling site |

That last row is the one that catches a lying label: we will know exactly which of the
eight call sites set the account to disconnected, and when.

### 3. Per-host census

Connections are tracked per resolved host, and the host's IP is resolved once and
recorded. Every connect logs how many live sockets already exist to that host and
which accounts own them.

If the shared-IP hypothesis is right, the journal will show connect failures on
50.87.253.68 at a consistent socket count, hitting both accounts. If it is wrong, that
pattern simply won't be there, and we will have spent nothing to find out.

### 4. Truth probe — the piece that answers the user's actual question

When an account's displayed status flips to disconnected, an **independent**
verification runs: a brand-new `ImapClient` (never from the pool) connects,
authenticates, selects INBOX, NOOPs, and logs out. The journal records a verdict:

```
VERDICT account=Kelly label=DISCONNECTED actual=REACHABLE  ← the label is wrong
VERDICT account=Kelly label=DISCONNECTED actual=UNREACHABLE (SocketError=...)
```

While an account stays labeled disconnected the probe repeats on a 60-second cadence,
so we see whether the connection recovered long before the label did.

**Cost control:** because a per-IP connection limit is an active suspect, the probe
must not become part of the problem. Probes are serialized globally (one at a time),
rate-limited to one per account per 60 s, and always fully disconnect and dispose.
Each probe is journaled, so its own connections are never mistaken for application
traffic.

This single mechanism splits the problem in two:

- If verdicts say **actual=REACHABLE**, this is a UI/state-tracking bug. The fix is
  #314 — one owner for connection status, derived from real I/O outcomes.
- If verdicts say **actual=UNREACHABLE**, connections really are dropping, and the
  host/socket-count/error data in the same journal tells us why.

### 5. Connection Diagnostics window

Reachable from **Help → Connection Diagnostics**, registered in `CommandRegistry`
(category `Help`) like every other user-facing command.

- Per-account list: current status, when it last changed, **why** it changed, and the
  latest probe verdict.
- Live event journal, newest first, filterable by account.
- **Test this account now** — runs a truth probe on demand.
- **Copy report** / **Save report…** — a self-contained text report to send back.

Built to the New Window Checklist: F6 ring, `Ctrl+Shift+P` palette, Escape to close,
focus restoration on close, `ToString()` on every `Selector`-bound item type, short
`AutomationProperties.Name` labels with hints delivered via `AccessibilityHelper.Announce`.

## Keyboard walkthrough

1. User presses the Connection Diagnostics shortcut (or Help → Connection Diagnostics).
   Window opens with focus on the account list. Screen reader announces:
   "Accounts. Kelly, disconnected since 2:14 PM, list, 1 of 4."
2. User presses Down. Focus moves through accounts; each announces label, status,
   and how long it has been in that status.
3. User presses Enter (or the Test button) on an account. A probe runs. Screen reader
   announces the result: "Kelly is reachable. The disconnected status is incorrect."
   or "Kelly is not reachable: connection refused."
4. User presses F6. Focus moves to the event journal. Announces "Connection events."
5. User arrows through events; each reads as one line of plain text.
6. User presses F6 again. Focus moves to the buttons row. Announces "Copy report."
7. User presses Enter on Copy report. Announces "Report copied. 412 events."
8. User presses Escape. Window closes; focus returns to where it was in the main window.

## Infrastructure changes

- **F6 ring:** new window only — account list → event journal → button row. No change
  to `MainWindow`'s ring.
- **CommandRegistry:** one new command, `help.connectiondiagnostics`, category `Help`,
  no default key assigned (discoverable via the palette and the Help menu; we are not
  spending a hotkey on a diagnostic).
- **`AutomationProperties.Name`:** `"Accounts"`, `"Connection events"`,
  `"Test this account"`, `"Copy report"`, `"Save report"`. Short labels only.
- **`AccessibilityHelper.Announce`:** probe results as `Result`; the "press Enter to
  test" hint on first focus of the account list as `Hint`; nothing as `Status`.
- **VM state:** no new `MainViewModel` state. `ApplyAccountStatus` gains a `reason`
  parameter used only for journaling.

## Out of scope for this build

- **No connection behavior changes.** No per-host cap, no pool resizing, no change to
  retry or backoff, no change to when reachability is raised. Deliberately — see
  "Design principles".
- **No fix for the status-drift bug**, even though it is already visible in the code.
  #314 lands after we have a report confirming which of the two failure modes we are
  actually in.
- **No Graph-side work.** `GraphChangeNotifier` never raises reachability at all
  (`CS0067` suppressed) and its `StartWatchers` restarts every poller on any account
  change — the same shape as the #126 bug. Both are real, neither affects this user's
  four IMAP accounts. Filed separately rather than fixed here.
- **No SMTP/send-path diagnostics.** Send failures are #396's territory.
- **No automated CI test of the drop scenario.** The GreenMail integration tier (#304)
  is where that belongs once we know what we are reproducing.

## Noted in passing, not fixed here

`accounts.json` has `"SmtpHost": "maill.theideaplace.net"` — a doubled "l". If that
account has send trouble, that typo is the reason. Flagged rather than silently
corrected, since it is live user configuration.

## How we use the output

1. User runs this build normally for a day or two.
2. When accounts show disconnected, user opens Connection Diagnostics and copies the
   report (or simply sends `connection.log` — the verdicts are in both).
3. `grep VERDICT` answers the central question in one line.
4. The fix follows from which answer we get, and we will have the evidence to know
   whether it worked.
