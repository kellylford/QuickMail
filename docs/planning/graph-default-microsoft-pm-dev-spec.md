# Graph as the Default for New Microsoft Accounts — PM & Dev Specification

**Status:** Draft for review (Tim → Kelly)
**Date:** August 15, 2026
**Tracks:** #529 (personal-Graph migration plan), step 3
**Depends on:** #527/#539 (honor an explicit Graph pick for personal accounts) — merged. Composes with
#544 (fold personal-Graph contact/calendar consent into the mail sign-in) — in review.
**Resolved decisions:** flag name `MicrosoftGraphDefault`; no custom announcement (§7); ships default
**off** (§4.1).

## Table of Contents

1. Executive Summary
2. User Problem & Opportunity
3. Design Principles
4. Feature Scope & Acceptance Criteria
5. Architecture & Technical Decisions
6. Keyboard Walkthrough (Mandatory)
7. Accessibility Checklist (Mandatory)
8. Acceptance Walkthrough (Mandatory)

## 1. Executive Summary

Make **Microsoft 365 (Graph)** the default connection method for a **new** Microsoft account —
personal outlook.com included, not just work/school — with IMAP/SMTP still reachable as an explicit
choice under Advanced. The change ships behind a new feature flag, **`MicrosoftGraphDefault`**,
defaulted **off**, so the broad release keeps today's behavior; testers run with the flag on, and its
default is flipped in a later release once personal-Graph has real mileage. Implementation is two
gated lines in `AddAccountViewModel` plus the flag definition — small, contained, and fully reversible
by turning the flag off.

## 2. User Problem & Opportunity

### 2.1 Current state (verified against code)

Three things pick IMAP for a Microsoft account, in order:

1. **Provider default** — selecting "Outlook.com / Microsoft 365" sets
   `BackendKind = provider.DefaultBackend`, which is `ImapSmtp` (`AccountEditorViewModel.cs:222`,
   `ProviderCatalog.cs` Microsoft entry). A transient initial value, overridden below once a username
   is known.
2. **`ChooseBackendForMicrosoftAccount`** (`AddAccountViewModel.cs:263`), run on username-commit:
   `wantGraph = !provider.MatchesEmail(Username) && IsPersonalMicrosoftAccount != true`. A consumer
   domain → `MatchesEmail` true → `wantGraph` false → **IMAP**. A work domain → **Graph**.
3. **`OnMicrosoftSignInCompleted`** (`AddAccountViewModel.cs:287`): a personal account the domain guess
   had put on Graph is reverted to IMAP after sign-in — unless the user picked Graph by hand
   (`_backendUserChosen`, #527).

Net today: work/school → Graph; personal → IMAP, with Graph reachable for personal only by an explicit
Advanced pick.

### 2.2 Why now

Graph is the strategic Microsoft backend (richer API, immutable ids, delta sync, server rules for
work/school). Personal-Graph is now correct and live-verified end-to-end (mail, contacts, calendar,
rules, single consent), but no one exercises it: a new outlook.com account lands on IMAP, and almost
no one opens Advanced to change it. The only way to accumulate real confidence in personal-Graph is to
put real accounts on it by default — reversibly, behind a flag, testers first.

## 3. Design Principles

- **Reversible and gated.** The entire behavior sits behind one flag. Off = today's behavior,
  byte-for-byte. No user is moved to Graph by default until the flag's default is deliberately flipped
  in a later release.
- **Additive, never subtractive.** The flag only ever turns *more* accounts on to Graph; it never
  moves an account off Graph or removes the IMAP option. Work/school (already Graph) is untouched.
- **The escape hatch stays.** IMAP/SMTP remains a first-class, hand-selectable connection method under
  Advanced, always honored (`_backendUserChosen`). #529 keeps Microsoft IMAP as a permanent, demoted
  fallback.
- **New accounts only.** This changes the default for accounts being added. Existing accounts are not
  touched (that is #529 step 4).

## 4. Feature Scope & Acceptance Criteria

### 4.1 In scope (v1)

- A new feature flag `MicrosoftGraphDefault`, default **false**, effective only when `GraphBackend` is
  also on (Graph must be an offered backend at all).
- When on: every new Microsoft account — personal and work/school — defaults to the Graph backend, and
  the personal→IMAP post-sign-in revert is suppressed.
- IMAP remains selectable under Advanced → Connection method; a hand-pick is honored.

### 4.2 Out of scope

- **Migrating existing Microsoft IMAP accounts to Graph** — #529 step 4 (opt-in convert with
  token-before-purge, cache purge + re-sync, idempotency marker). New accounts only here.
- **Removing the IMAP/SMTP option or the Exchange scopes from the app registration** — #529 keeps
  them; nothing is removed.
- **Flipping the flag's default to true** — a later release, gated on accumulated mileage.
- **Any work/school behavior change** — already Graph; this flag does not alter that path.
- **A one-time migration or announcement UX** — new accounts only; no existing account is touched.

### 4.3 Acceptance criteria

- Flag on → a new `me@outlook.com` account is created on the Graph backend without touching Advanced.
- Flag on → a hand-picked "Standard IMAP/SMTP" in Advanced still creates an IMAP account.
- Flag on → a personal account stays on Graph after sign-in (no revert).
- Flag off → every path behaves exactly as the current release (consumer → IMAP; auto-inferred
  personal Graph reverts after sign-in).
- `ConfigFeatureGate` returns false for `MicrosoftGraphDefault` by default.

## 5. Architecture & Technical Decisions

### 5.1 Key architectural decisions

The flag gates the two Microsoft-specific decision points; the provider default (force 1) needs no
change because `ChooseBackendForMicrosoftAccount` already overrides it once the address is entered.

| `GraphBackend` | `MicrosoftGraphDefault` | New outlook.com defaults to | Work/school defaults to |
|---|---|---|---|
| off | (any) | IMAP (Graph not offered) | IMAP¹ |
| on | off (today) | IMAP | Graph |
| on | on (step 3) | **Graph** | Graph |

¹ With `GraphBackend` off a work/school account has no Graph option (pre-#511 state, out of scope).

**Decision point 1 — `AddAccountViewModel.ChooseBackendForMicrosoftAccount`:**
```csharp
var graphIsDefault = _gate.IsEnabled(FeatureFlag.GraphBackend)
                     && _gate.IsEnabled(FeatureFlag.MicrosoftGraphDefault);
var wantGraph = graphIsDefault
                || (!provider.MatchesEmail(Username) && IsPersonalMicrosoftAccount != true);
MoveToBackend(wantGraph ? BackendKind.MicrosoftGraph : BackendKind.ImapSmtp);
```
The `_backendUserChosen` / `HostsUserEdited` early-returns above are unchanged, so a hand-picked IMAP
still wins.

**Decision point 2 — `AddAccountViewModel.OnMicrosoftSignInCompleted`:** suppress the personal→IMAP
revert when Graph is the default, else it would undo the new default for exactly the personal accounts
we want on Graph:
```csharp
if (_gate.IsEnabled(FeatureFlag.GraphBackend)
    && _gate.IsEnabled(FeatureFlag.MicrosoftGraphDefault)) return;
```
Flag off → the existing #527 revert is untouched.

**The flag:** `FeatureFlag.MicrosoftGraphDefault` (Models/FeatureFlag.cs) with a doc comment;
`ConfigFeatureGate.Defaults` entry `= false`; `config.ini` `[features] MicrosoftGraphDefault=true`;
launch overrides `--feature MicrosoftGraphDefault` / `--no-feature MicrosoftGraphDefault` (inherited
from the existing gate plumbing — no new parsing).

### 5.2 Runtime mode compatibility

No interaction with `--online` or the local-store path — this is add-account backend selection only,
before any sync. No startup-state implication (the change applies only while adding an account, not at
launch).

### 5.3 Code reuse and duplication risks

The two-flag read (`GraphBackend && MicrosoftGraphDefault`) appears in both decision points. It is a
two-line local in each and reads naturally in place; if a third caller ever needs it, extract a
`bool GraphIsDefaultBackend` helper then. No new duplication of backend-selection logic — both edits
are inside the single existing `ChooseBackendForMicrosoftAccount` / `OnMicrosoftSignInCompleted` pair
that already owns that decision.

### 5.4 Shared component audit

- **`AddAccountViewModel`** — the only VM whose add-flow selects a Microsoft backend. `AccountManager`
  edits existing accounts and does not re-run `ChooseBackendForMicrosoftAccount` for a new default, so
  it is unaffected (an existing account keeps its stored backend).
- **`ConfigFeatureGate` / `FeatureFlag`** — adding a member follows the established pattern
  (`GraphBackend`, `Pop3Backend`, …); no plumbing change.
- **Connection method combo (`BackendKindOption`)** — already exists and is used unchanged; no new
  Selector-bound type, so no new `ToString()` obligation (per the Accessibility Checklist).

## 6. Keyboard Walkthrough (Mandatory)

### Path A — Flag ON, add a personal outlook.com account (the new default)

1. User opens **Add Account**. Focus on the provider combo; reads "Outlook.com / Microsoft 365".
2. User Tabs to the address field, types `me@outlook.com`, Tabs away (username commits).
   `ChooseBackendForMicrosoftAccount` runs; `graphIsDefault` true → backend set to Graph. Nothing is
   spoken — the connection method is a standard combo whose value the platform reports if focused (§7).
3. User activates **Sign in**. Embedded Microsoft window opens; with #544 also in, one consent screen.
   Sign-in completes; `OnMicrosoftSignInCompleted` returns early (flag on) → account **stays on
   Graph**. Reads "Signed in as me@outlook.com".
4. User activates **Add** → account created on Graph. Nothing else in the flow changes.

### Path B — Flag ON, override to IMAP (the escape hatch)

1–2. As A, through typing the address.
3. Before signing in, user Tabs to **Advanced settings**, expands it (Space/Enter); focus moves in.
4. User navigates to the **Connection method** combo (reads "Microsoft 365 (Graph)") and selects
   **"Standard IMAP/SMTP"** → `_backendUserChosen = true`; IMAP host/port fields populate.
5. User signs in and Adds. The hand-pick is honored by both decision points → account created on
   **IMAP**.

### Path C — Flag OFF, add a personal outlook.com account (today's behavior, unchanged)

1–2. As A. On username-commit `graphIsDefault` is false and the address is a consumer domain →
   `wantGraph` false → backend stays **IMAP**.
3. Sign in; `OnMicrosoftSignInCompleted` runs (flag off) and reverts an auto-inferred vanity-domain
   Graph to IMAP as today. Add → account on IMAP. Identical to the current release.

## 7. Accessibility Checklist (Mandatory)

- **No custom announcement (decided).** The default flipping to Graph is silent, exactly as the
  work-domain → Graph flip is silent today. The connection method is a standard combo; its value is
  platform-reported when focused, and per CLAUDE.md we do not announce what the platform already
  reports. Adding a cue would override the user's own screen-reader verbosity choice for a mechanism
  they do not need to think about, and is not added.
- **`AutomationProperties.Name`** — unchanged. No new or relabeled controls.
- **F6 ring** — unchanged. No new panes or windows.
- **CommandRegistry / shortcuts** — unchanged. No new user command.
- **Selector-bound items** — `BackendKindOption` is reused as-is; no new bound type, so no new
  `ToString()` requirement.

## 8. Acceptance Walkthrough (Mandatory)

Unit tests in `AddAccountViewModelProviderTests` (backend selection) and `PersonalGraphBackendChoiceTests`
(sign-in path):

1. **Flag on → outlook.com defaults to Graph.**
2. **Flag on → work domain defaults to Graph** (unchanged, still correct).
3. **Flag off → outlook.com defaults to IMAP** (regression pin of today's default).
4. **Flag on → a hand-picked IMAP in Advanced survives** (`_backendUserChosen` honored).
5. **Flag on → a personal account stays on Graph after sign-in** (`OnMicrosoftSignInCompleted` no-ops).
6. **Flag off → an auto-inferred personal Graph reverts to IMAP after sign-in** (existing #527 path
   still fires when the flag is off).
7. **Gate default:** `ConfigFeatureGate` returns false for `MicrosoftGraphDefault` by default.

Manual (testers, flag on): add a real outlook.com account, confirm `accounts.json` shows
`BackendKind=1` (Graph) without touching Advanced; confirm the Advanced override to IMAP still yields
`BackendKind=0`.
