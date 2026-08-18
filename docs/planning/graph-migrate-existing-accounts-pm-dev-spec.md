# Migrate an Existing Microsoft IMAP Account to Graph (opt-in) — PM & Dev Specification

**Status:** Draft for review (Tim → Kelly)
**Date:** August 18, 2026
**Tracks:** #529 (personal-Graph migration plan), step 4
**Depends on:** #527/#539, #544, #571 (Graph-default flag) — all merged. Builds on the same
`ClearCachedMailAsync` cache-rebuild primitive as the #366 immutable-id work.
**Resolved decisions:** a dedicated **`MicrosoftGraphMigration`** flag, default off; #454 proper is a
**separate follow-up** PR (step 4 only must not reproduce it); folder-reference remap matches **exact
path, then a unique leaf name, else leaves the reference unmatched** (never guesses between candidates).
Convert is fully opt-in and per-account; the conversion is made crash-safe with a persisted per-account
marker (the #454 lesson applied). See §9.

## Table of Contents

1. Executive Summary
2. User Problem & Opportunity
3. Design Principles
4. Feature Scope & Acceptance Criteria
5. Architecture & Technical Decisions
6. Keyboard Walkthrough (Mandatory)
7. Accessibility Checklist (Mandatory)
8. Acceptance Walkthrough (Mandatory)
9. Resolved Decisions

## 1. Executive Summary

Offer the user an **opt-in** action that converts an existing **Microsoft IMAP** account to the
**Microsoft Graph** backend, in place, keeping the same account and its history on the server. Step 3
(#571) changed the default for *new* Microsoft accounts; this step lets someone move an account they
*already have* onto Graph without deleting and re-adding it. It ships behind a new
`MicrosoftGraphMigration` flag, default off — testers first, no one is converted automatically, and
Microsoft IMAP is retained as the escape hatch (#529). The work is a per-account convert command in
the Account Manager plus a crash-safe purge-and-resync, and its single hardest requirement is not
reproducing the #454 rule-refire bug that lives in exactly the wipe-then-resync path this feature uses.

## 2. User Problem & Opportunity

### 2.1 Current state (verified against code)

- **No conversion exists.** `AccountModel.BackendKind` is documented "Fixed at account creation"
  (`AccountModel.cs:52`), and a codebase sweep for `.BackendKind =` finds only equality comparisons and
  object-initializers on freshly-constructed accounts — nothing reassigns `BackendKind` on a persisted
  account. Today the only way from IMAP to Graph is delete-and-re-add, which loses the account's local
  state and its place in the account list.
- **The flip itself is small.** Setting `BackendKind = MicrosoftGraph` routes the account through the
  Graph stack; `AuthType` is already `OAuth2Microsoft` for a Microsoft IMAP-OAuth account. The
  `ImapHost`/`SmtpHost` fields become inert once flipped — `IncomingHost`/`IncomingPort`/SMTP/Provider
  all switch on `BackendKind` and ignore them for Graph (`AccountModel.cs:116-148`,
  `ProviderCatalog.cs:193`) — so they need not be cleared for correctness.
- **The cache cannot carry over.** IMAP mail is keyed by UID/folder-name; Graph by immutable message id
  and opaque folder id. `LocalStoreService.ClearCachedMailAsync(accountIds)` (`LocalStoreService.cs:550`)
  is the exact primitive to purge one account's cached mail without removing the account: it deletes
  `MessageDetail`, `MessageSummary`, and `DeltaToken` for the account, and deliberately leaves
  `CalendarEvent` and the `Folder` table intact (folders are replace-all on next connect via
  `SaveFoldersAsync`, `:572`). (`DeleteAccountDataAsync`, `:528`, is the full-removal purge — wrong for a
  convert, it drops calendar and folder rows.)
- **The schema/cache-rebuild precedent is app-layer, not a DB migration.** `CurrentSchemaVersion` is
  still `5` (`LocalStoreService.cs:212`); the #366 immutable-id rebuild was done as a marker-gated,
  account-scoped `ClearCachedMailAsync` at startup (`App.xaml.cs:319-390`), explicitly *not* a version
  bump, so it never touches IMAP bodies. Step 4 reuses that same pattern per converted account.
- **Some persisted settings reference a folder by IMAP name and will not resolve against Graph ids**
  (a Graph folder's `FullName` is an opaque server id — `ConfigModel.cs:77`, `ServerRuleModel.cs:72`):
  - **Mail rules** — `MailRule.TargetFolder` is a folder name/path (`MailRule.cs:86`), passed straight to
    the move backend (`RuleService.cs:329`). A move-to-folder rule **breaks** on Graph.
  - **Saved views** — `ViewFolder.FolderFullName` stores the folder `FullName` (`ViewFolder.cs:7`); a
    view naming a *real* IMAP folder **breaks**. Virtual-folder views (`VirtualFolderKey`, `SavedView.cs:50`)
    are account-agnostic and **survive**.
  - **Startup folder (#516)** — `ConfigModel.StartupFolder` holds a real folder's `FullName` when it is a
    real folder (`ConfigModel.cs:66`); it **breaks** and silently falls back to "sync wide"
    (`SyncService.cs:104-118`).
  - **Server rules** reference folders by id (`ServerRuleModel.MoveToFolderId`), but they exist only for
    work/school Graph accounts and are created *after* the account is Graph — **not** a remap target for a
    personal IMAP→Graph convert.

### 2.2 Why now, and why opt-in

Step 3 puts *new* Microsoft accounts on Graph, but the people most able to give personal-Graph real
mileage are the ones already running a Microsoft account daily — and today they'd have to delete and
re-add it to try Graph. An in-place convert removes that friction. It stays opt-in and flag-gated for
the same reason step 3's default stays off: personal-Graph still has open friction (notably #491, stale
unread counts) and the convert touches an account with real mail and rules, so the bar is "a willing
tester benefits," not "safe to do to everyone." The retained IMAP escape hatch is the safety net — a
convert that disappoints can be undone by re-adding on IMAP.

## 3. Design Principles

- **Opt-in, per-account, reversible-in-spirit.** Nothing converts without an explicit action on a
  specific account; the account is never removed; Microsoft IMAP remains available to re-add if the
  Graph experience disappoints.
- **Never lose server mail.** The only thing a convert destroys is the *local cache*, which is
  re-downloadable. The safeguards below guarantee that a failure or crash never leaves an account whose
  server mail or rules are corrupted — worst case is a re-sync or a stay-on-IMAP.
- **Crash-safe by construction.** The wipe-then-resync path is the exact path #454 identifies as
  fragile. Step 4 must persist its in-progress state so a crash between purge and first sync cannot
  re-fire rules over pre-existing mail (§5.3).
- **Resolve folder references, don't silently break them.** Settings that point at a folder by IMAP
  name are detected before the convert and remapped or clearly invalidated after — never left pointing
  at nothing without the user knowing (§5.2).
- **Honest messaging.** The user is told, before consenting, that the mailbox re-downloads once and
  that older-than-sync-window mail won't be in the local store (it stays on the server); and which of
  their rules/views/startup-folder need attention.

## 4. Feature Scope & Acceptance Criteria

### 4.1 In scope (v1)

- A `MicrosoftGraphMigration` feature flag (default **off**), effective only when `GraphBackend` is on.
- A per-account **Convert to Microsoft 365 (Graph)** command in the Account Manager, shown only for an
  account where `BackendKind == ImapSmtp && AuthType == OAuth2Microsoft` and the flag is on.
- The convert sequence with all four #529 safeguards: token-before-purge, folder-reference resolution,
  a persisted crash-safe idempotency marker, and a failure stopping rule (§5.1).
- Post-convert remap of move-to-folder rules, real-folder saved views, and the startup folder by folder
  name; unmatched references invalidated with a surfaced report (§5.2).
- User-facing messaging: a pre-convert confirmation describing the one-time re-download and the affected
  settings, and a result announcement.

### 4.2 Out of scope

- **Automatic / forced migration.** Never; opt-in only.
- **Flipping the `MicrosoftGraphDefault` default to true.** Separate decision, gated on mileage (#529).
- **Converting non-Microsoft accounts, or Microsoft *password*/Basic accounts.** Only Microsoft
  OAuth IMAP accounts convert (they already hold a Microsoft identity).
- **Graph → IMAP (reverse) conversion.** The escape hatch is delete-and-re-add on IMAP, not an in-place
  reverse convert.
- **Fixing #491 (stale unread counts) or the pre-existing #454 immutable-id-rebuild crash path.** Step 4
  must not *reproduce* #454 (§5.3), but repairing the existing #366 rebuild path is a separate change.
  #491 is a readiness gate for *recommending* migration, tracked separately, not built here.

### 4.3 Acceptance criteria

- Convert command is present only for a Microsoft OAuth IMAP account with the flag on; absent otherwise.
- A convert that cannot obtain a Graph token leaves the account **entirely unchanged** on IMAP.
- After a successful convert: `BackendKind == MicrosoftGraph`, the account's cached mail is purged and
  re-synced from Graph, and the account keeps its Id, name, and list position.
- A crash at any point between purge and the first completed Graph Inbox sync does **not** cause client
  rules to run over pre-existing mail on the next launch.
- A move-to-folder rule / real-folder saved view / startup folder that named an IMAP folder is either
  remapped to the matching Graph folder or invalidated with a message; never left silently pointing at a
  non-existent folder.
- With the flag off, none of this is reachable and no account can be converted.

## 5. Architecture & Technical Decisions

### 5.1 The convert sequence (per account)

A new `[RelayCommand] ConvertToGraphAsync` on `AccountManagerViewModel` (which already holds `_oauth`,
`_localStore`, `_configService`, `_accountService`, `_featureGate`, and the `SelectedAccount`), gated as
in §4.1. Steps, in order, each a barrier for the next:

1. **Detect folder-referencing settings** for this account (move-to-folder rules, real-folder saved
   views, startup folder). Compute what will need remap. Surface the list in the confirmation.
2. **Confirm with the user** (modeless, per the modal-dialog rules): "This re-downloads *account* once
   from Microsoft 365. Mail older than your sync window stays on the server. These rules/views may need
   a folder reset: …. Continue?" No side effects until Yes.
3. **Token before purge.** Acquire a **Graph** token for the account
   (`OAuthService.GetAccessTokenAsync(account, GraphMailScopes…)`; interactive if needed — an IMAP-OAuth
   account holds IMAP scopes, not Graph, so this generally prompts once for Graph consent). If it fails
   or is declined, **stop: the account is untouched, still on IMAP.**
4. **Persist the crash-safe marker** (§5.3) recording "account X is converting; rule-refire baseline
   pending" — *before* the purge, and cleared only after step 7.
5. **Flip and purge.** `BackendKind = MicrosoftGraph`; persist via `_accountService` (the
   `AccountStartupRepair` precedent shows the save path). Then
   `ClearCachedMailAsync([account.Id])`. Register/rebind the account's backend so the router sends it to
   the Graph service.
6. **Resync from Graph.** Seed the rule-refire baseline for this account from the persisted marker
   (§5.3), then run the initial Graph sync (bounded by `SyncDays`/initial count). The baseline makes the
   first sync of each folder cache messages **without** running client rules over them.
7. **Remap folder references** (§5.2) now that Graph folders exist, then **clear the marker** — this is
   the point the conversion is "done" and crash-safe state is released.

**Failure stopping rule.** A failure in steps 1–3 leaves the account fully on IMAP (nothing changed). A
failure in 5–6 leaves the marker in place, so the next launch resumes at "baseline pending, resync
needed" rather than re-firing rules or re-purging. The convert is **idempotent**: re-running it on an
account already flipped resumes from the marker instead of double-purging.

### 5.2 Folder-reference remapping

Remap can only run *after* the first Graph folder sync (Graph folder ids don't exist until then), so it
is step 7, not a precondition — the *detection* in step 1 is the precondition. Strategy:

- Match each broken reference's IMAP folder name/path to a Graph folder by **display name/path**
  (well-known folders — Inbox, Archive, Sent, etc. — match cleanly; a nested custom path matches on leaf
  name, else full path).
- **Matched** → rewrite the reference to the Graph folder's id/`FullName`: `MailRule.TargetFolder`,
  `ViewFolder.FolderFullName`, `ConfigModel.StartupFolder` (+ `StartupFolderLabel`).
- **Unmatched** → invalidate safely and report: a move-to-folder rule whose target can't be matched is
  **disabled** (not deleted) with its name in the result; a saved-view folder or startup folder that
  can't be matched falls back to its existing safe default (the view drops that folder; startup falls
  back to All Mail) and is named in the report. Nothing is silently broken.

The precedent for a gated, persisted settings rewrite is `StartupFolderMigration`
(`QuickMail/Services/StartupFolderMigration.cs`).

### 5.3 The #454 crash-window safeguard (the load-bearing decision)

**The bug this must not reproduce (verified):** the rule-refire baseline that stops client rules from
running over pre-existing mail after a wipe is **in-memory and single-launch** — `SyncService`'s
`_rebuildAccounts` / `_rebuildBaselined` dictionaries (`SyncService.cs:62-63`), seeded by
`SeedRebuildBaseline` (`:73`) and consumed at the rules chokepoint (`:338-353`). In the #366 startup
path, the marker file `.immutable-id-rebuilt` is written **immediately after the purge**
(`App.xaml.cs:352-355`), but `SeedRebuildBaseline` runs only when the purge happened *this same launch*
(`:390`). So a crash after purge+marker but before the first baselined sync means: next launch sees the
marker, skips the purge block, never seeds the baseline — and the now-empty store makes every
pre-existing message read as new, so **client rules fire over already-processed mail** (spurious
moves/marks/deletes). `maxKey == "0"` for every Graph folder (`SyncService.cs:492`) means a wiped Graph
folder always looks fresh, so nothing else catches it.

**Step 4's requirement:** the "baseline pending" state must be **persisted per account** and seeded on
**every** launch until the first baselined Graph sync of each folder completes — not written-and-forgotten
before the sync runs. Concretely:

- The marker (step 4 of §5.1) is a persisted per-account record: "converting / rebuild-baseline pending."
- On startup **and** immediately post-convert, `SeedRebuildBaseline` is seeded from any account with the
  marker set — so a crash-and-relaunch still suppresses rules over pre-existing mail.
- The marker is cleared only after the first baselined sync has run for the account's folders (step 7),
  so it is impossible to have an empty store with no baseline.

This is strictly stronger than the current #366 ordering, and the same persisted-marker mechanism would
also fix #454 for the immutable-id-rebuild path — noted as a **recommended companion fix**, but the
in-scope requirement here is only that step 4 does not reproduce the bug.

### 5.4 Runtime mode compatibility

- **`--online`**: the convert purges the local store then re-syncs; in `--online` the account already
  reads from the server, so the purge is a no-op for reads and the re-sync is the normal first Graph
  fetch. The token-before-purge and marker steps are unchanged.
- **Startup state**: the resume-from-marker logic runs in `InitialLoadAsync`-adjacent startup (alongside
  the existing #366 rebuild and `AccountStartupRepair`), so a mid-convert account is corrected before the
  user sees stale content.

### 5.5 Shared component audit

- **`LocalStoreService.ClearCachedMailAsync`** — reused as-is; already account-scoped and idempotent.
  No change.
- **`SyncService` rebuild baseline** — the seeding source changes from "purged this launch" to "marker
  set"; `SeedRebuildBaseline` / the chokepoint logic are reused, not rewritten. This is the one shared
  component the feature actually modifies, and the modification is what makes it crash-safe.
- **`AccountManagerViewModel`** — gains one `[RelayCommand]`; existing per-account commands
  (`SaveAccount`, `DeleteAccountAsync`, `SetDefault`) and their gating pattern are the template.
- **`OAuthService`** — reused unchanged (`GetAccessTokenAsync` / `DefaultScopesFor`); no scope or flow
  change, the convert just requests the account's Graph scopes.
- **`RuleService` / `ViewService` / `ConfigService`** — the remap rewrites their persisted models via
  their existing save paths; no schema change.

## 6. Keyboard Walkthrough (Mandatory)

### Path A — Convert a Microsoft IMAP account (happy path)

1. User opens **Manage Accounts**, arrows to their Microsoft IMAP account. Screen reader reads the
   account name and that it is selected.
2. User opens the account's actions (the same actions area as Save / Delete / Set default) and activates
   **Convert to Microsoft 365 (Graph)**. (Present only because the flag is on and the account is a
   Microsoft OAuth IMAP account.)
3. A confirmation appears (modeless). Screen reader reads: "Convert *account* to Microsoft 365. This
   re-downloads the mailbox once; mail older than your sync window stays on the server. These need a
   folder reset after: *rule X, view Y*. Convert, or Cancel." Focus on the message.
4. User activates **Convert**. The Microsoft sign-in window opens for Graph consent (one time). User
   completes it.
5. Progress is announced as `Status` (respects the user's status-announcement setting): "Converting…",
   then "Re-downloading *account*…".
6. On completion, a `Result` announcement: "*account* now connects through Microsoft 365. *N* rules and
   *M* views were updated; *rule Z* was turned off because its folder could not be matched — set its
   folder again to re-enable it." Focus returns to the account in the list.

### Path B — Token declined / sign-in closed

1–4. As A, but at the Graph sign-in the user closes the window or declines consent.
5. `Result` announcement: "Convert cancelled — *account* is unchanged and still connects the way it did."
   The account stays on IMAP; nothing was purged. Focus returns to the account.

### Path C — Crash mid-convert, next launch

1. A convert reached the purge but the app closed before the first Graph sync finished.
2. On next launch, the persisted marker is found. The rule-refire baseline is seeded for the account, the
   Graph sync resumes, and no client rules run over the pre-existing mail. The user is not asked to do
   anything. (If the flip hadn't persisted yet, the account simply resumes on IMAP.)

## 7. Accessibility Checklist (Mandatory)

- **Confirmation is modeless**, per the modal-dialog rules (it is launched over the Account Manager, may
  carry editable focus, and the app hosts a WebView2 sign-in). Escape/Cancel wired explicitly.
- **Announcements go through `AccessibilityHelper.Announce` with categories**: progress as `Status`,
  the outcome (including the remap report and any invalidated rule) as `Result`. No `force`. Nothing is
  announced that a standard control already reports.
- **The convert command has a short `AutomationProperties.Name`** ("Convert to Microsoft 365"), no
  instructional text baked in; any "what this does" guidance is in the confirmation body, not the name.
- **The result naming an invalidated rule is actionable**, not just informative — it tells the user the
  rule was turned off and how to re-enable it (set its folder again).
- **No new F6 pane or Selector-bound type**; the command lives in the existing Account Manager actions.

## 8. Acceptance Walkthrough (Mandatory)

Unit / integration tests:

- **Command gating** — present only for `ImapSmtp && OAuth2Microsoft && flag on`; absent for a Graph
  account, a password account, a non-Microsoft account, and with the flag off.
- **Token-before-purge** — a stubbed OAuth that fails the Graph-token acquisition leaves `BackendKind`,
  hosts, and the cache untouched (`ClearCachedMailAsync` never called).
- **Flip + purge** — on success, `BackendKind == MicrosoftGraph` and `ClearCachedMailAsync` was called
  for exactly this account.
- **Crash-safe marker** — with the marker set and no completed sync, startup seeds the rebuild baseline
  for the account (so rules are suppressed); once the baselined sync completes, the marker is cleared and
  a subsequent sync runs rules normally. Pin the ordering: marker cleared only after the first baselined
  sync.
- **Folder remap** — a move-to-folder rule targeting an IMAP folder name is rewritten to the matching
  Graph folder id; an unmatchable target leaves the rule **disabled** and named in the result; a
  real-folder saved view and the startup folder are remapped or safely defaulted.
- **Gate default** — `ConfigFeatureGate` returns false for `MicrosoftGraphMigration` by default.

Manual (testers, flag on): convert a real Microsoft IMAP account, confirm it re-syncs over Graph, its
rules/views/startup folder still point where intended, and — the #491 watch item — note whether unread
counts behave. Kill the app mid-convert and relaunch; confirm no rule re-fires over old mail.

## 9. Resolved Decisions

1. **Flag — dedicated.** A new **`MicrosoftGraphMigration`** flag (default off), separate from
   `MicrosoftGraphDefault`: "convert existing" and "default for new" are different risks, and a tester
   may want one without the other.
2. **#454 — separate follow-up.** Step 4 must not reproduce the bug (§5.3), and its persisted-marker
   mechanism is built general enough that the #366 immutable-id-rebuild path can adopt it — but #454's
   own fix lands as its own PR, to keep this one scoped and reviewable.
3. **Remap matching — no guessing.** Exact full-path match first; then a leaf-name match only when it is
   unique; if two folders share the name, the reference is left unmatched (the move-to-folder rule
   disabled and named in the result), never retargeted to a guessed folder.
4. **#491 is a tester-pool gate, not a merge gate.** #491 is the primary-mailbox folder-count badge going
   stale on Graph (the delta poll updates message rows, not the folder badge; the count is spoken via the
   folder's accessible name). A converted daily-driver inherits it, so it is worth fixing before
   *widening* who runs the flag — but it does not block building the opt-in convert. A #491 fix would also
   make the personal-Graph soak that generates step-4 coverage less irritating, so it may be worth doing
   ahead of the soak rather than after.
