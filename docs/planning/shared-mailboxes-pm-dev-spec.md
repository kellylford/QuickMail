# Shared Mailboxes — PM/Dev Spec

**Status:** Draft (rev 2, 2026-08-01). Supersedes closed PR #59. Tracks #461 (design)
and #31 (feature request). Rev 2 restructures onto `SPEC-TEMPLATE.md` and resolves
the review on PR #468.

---

## 1. Executive Summary

QuickMail cannot open a shared/delegated Exchange mailbox — the automapped "Support",
"Sales", "info@" mailboxes a worker is granted access to. Outlook surfaces them
automatically; no accessible client does. This spec adds shared mailboxes as
**first-class linked accounts** (Approach B): you add one by address, it appears as its
own top-level node in the tree, and you read and send from it. Access rides **whatever
backend the parent account uses** — Microsoft Graph delegated `.Shared` permissions for a
Graph parent, XOAUTH2 `user=` for an IMAP parent. Existing users see zero change until
they add one.

## 2. User Problem & Opportunity

### 2.1 Current state (verified)

| Surface | Today | Pain | Who feels it |
|---|---|---|---|
| Add account | Only directly-signed-in mailboxes; `AccountModel` has no parent/shared concept (`Models/AccountModel.cs`, no such field) | Can't add a mailbox you access *through* your account | Anyone with a team/shared mailbox |
| Graph read | `/me/…` only (`GraphMailService`) | No path to another user's mailbox | M365 worker on a shared support box |
| Send identity | Compose picks from `SenderAccounts` (`ComposeViewModel.cs:220-224`) — only real accounts | Can't send *as* a shared address | Support/role-mailbox users |

### 2.2 Target personas

- **Blind support-desk agent (primary).** Works a shared `support@` box on a corporate
  M365 tenant. Needs to read and reply from it with a screen reader. Today: no accessible
  option; Outlook is the only thing that automaps it, and imperfectly.
- **Team-mailbox member.** Shares `info@` / `sales@` with colleagues on Exchange. Wants it
  alongside personal mail but clearly separate.
- **Small-business IMAP user.** A generic-IMAP host with a shared role mailbox exposed via
  RFC 2342 NAMESPACE. Wants the same experience without Microsoft.

### 2.3 Why now

The Graph backend, per-account model (#364), the immutable-id work (#419), and — crucially
— the periodic all-folder sweep (#456) have all shipped. The sweep is what makes a
poll-only Graph shared mailbox viable at all (§5.1). This is the prerequisite that was
missing when #59 was written.

## 3. Design Principles

1. **Zero-change until opted in.** No shared mailbox exists until the user adds one; nothing
   about existing accounts changes.
2. **A shared mailbox is "not my primary mail."** It is separate from the unified views, the
   global unread total, contact scraping, and default-account semantics — reachable under
   its own node, never blended into "my" mail.
3. **Access follows the parent's backend.** There is no backend-uniform shared-mailbox path;
   Approach B routes each one by its own account id.
4. **The screen-reader user is the authority on the a11y behavior.** Recurring context lives
   in the accessible name; one-time caveats live at the decision point; announcements respect
   the user's config (no ungated live regions — the #333 lesson).
5. **Don't build on a retiring foundation.** EWS is being blocked for third-party apps
   (§13); auto-detection is deferred, and the durable path (manual add) stands alone.

## 4. Feature Scope & Acceptance Criteria

### 4.1 In scope (v1)

| Feature | Setting / Shortcut | Default | Notes |
|---|---|---|---|
| Add shared mailbox by address | Command `account.addShared` (Account category, no default key) + button in Manage Accounts | — | The durable entry point; no EWS. |
| Read shared mail | — | — | Graph `/users/{shared}/…` or IMAP `user=`, by parent backend. |
| Send *from* shared mailbox | Shared address appears in compose `SenderAccounts` | — | Graph `sendMail` on `/users/{shared}` (`Mail.Send.Shared`) or IMAP-parent SMTP XOAUTH2 `user=`. |
| Own top-level tree node | — | — | Sibling of real accounts; label "{name} (shared)". |
| New-mail toast for shared mailbox | new per-account `NotifyOnNewMail` opt-in | **off** for shared | Global `NotifyOnNewMail` (`MainViewModel.cs:2678`) still governs real accounts; shared adds a per-account gate defaulting off. |
| Generic-IMAP discovery | RFC 2342 `NAMESPACE` | — | For non-Microsoft IMAP only. |

### 4.2 Explicitly out of scope (v1)

- **EWS auto-detection of automapped mailboxes** — deferred (EWS retirement, §13). Manual
  add-by-address is the v1 path. Revisit only if Microsoft ships a Graph enumeration API.
- **No setting to include shared mail in the unified All Inboxes / All Mail views** — the
  exclusion is fixed in v1 (no toggle).
- **Calendar and contacts of a shared mailbox** — mail only.
- **Contact scraping/sync from shared mail** — a shared mailbox never contributes to the
  contact store.
- **Client mail rules (`RuleService`) on shared-mailbox mail** — rules act on your own
  Inbox only (the #336/#427 model); shared mail is not rule-processed in v1.
- **Shared mailbox as the default account** — a credential-less account cannot be default.
- **Shared unread in the global status-line total** — excluded, consistent with principle 2.
- **Per-account sweep throttle** — tracked in #462; v1 shared mailboxes use the standard
  interval.
- **Nested/hierarchical display under the parent** — flat top-level node in v1.

**Cross-account surfaces — explicit in/out (review item 11):**

| Surface | v1 | Rationale |
|---|---|---|
| Full-text / message search | **in** — shared folders are searchable | It's an account; searching it is expected. |
| Saved views | **in** — a view may include shared folders | Natural; no extra work. |
| All Inboxes / All Mail aggregates | **out** | Principle 2; via `IsShared` predicate (§5.4), never `ExcludeFromAllMail`. |
| Status-line global unread total | **out** | Principle 2. |
| Client mail rules | **out** | Rules are Inbox-only for your own accounts. |
| Contact scraping / sync | **out** | Not "your" mail. |
| Default-account semantics | **out** | Credential-less. |

**Account Manager editor for a selected shared mailbox (added PR 1):** a shared account has no
credentials or connection of its own, so when it is the selected account the editor hides the entire
connection/auth surface — connection method, Authentication, Password, IMAP/SMTP servers (the whole
Advanced expander), the OAuth **Sign-in** buttons, **Test Connection** — and the **Sync contacts /
Sync calendar** checkboxes (mail only, per the out-of-scope rows above). In their place a **read-only
summary** names the parent it reads through (*"Reads through {parent}. Mail only — no separate
sign-in."*), and the shared **email address is read-only** (editing it would break the link). The
account **label stays editable**. Gated on `AccountManagerViewModel.IsSharedSelected`, the same way a
Graph account already hides IMAP/SMTP on `IsImapBackend`.

## 5. Architecture & Technical Decisions

### 5.1 Key architectural decisions

**Decision: Approach B — each shared mailbox is a first-class `AccountModel` with its own
`Guid Id`, linked to its parent.**
Alternatives: (A) a `mailbox` cache-key dimension threaded through every store/backend call —
rejected, it must be plumbed through *both* backends and every LocalStore call.
Rationale: B is the only model that absorbs two backends without a second migration; the
shared account routes through `MailServiceRouter` by its own id (`Services/MailServiceRouter.cs`
`RegisterAccount`), and the existing `RegisterAccountBackend` hook (`App.xaml.cs:460`,
`MainViewModel.cs:1259`) already fires per account.

**Decision: access follows the parent's backend.**
- **Graph parent** → `/users/{SharedAddress}/…` with delegated `Mail.ReadWrite.Shared` and
  `Mail.Send.Shared` (no admin consent). New scope constants in `OAuthService`; because
  work/school uses `.default` (`OAuthService.cs:33-38`) these must **also** be declared on
  the app registration or they're never granted. **Work/school only** — personal MS accounts
  use `GraphMailScopesPersonal` and don't have Exchange shared mailboxes.
- **IMAP parent** → XOAUTH2 `user={SharedAddress}` over the parent's token (read *and* SMTP send).
- **Generic IMAP** → ordinary IMAP; RFC 2342 NAMESPACE for discovery.

**Decision: freshness is backend-conditional (corrects rev-1's blanket "no live watcher").**
- **Graph parent:** `.Shared` scopes **do not support change-notification subscriptions**
  (that needs the app-only `Mail.Read`, admin-consented — out of scope). So a Graph shared
  mailbox has **no delta, no live watcher**; its only freshness is the #456 sweep
  (`StartFallbackSyncAsync`), which already iterates every account. Poll-interval, not instant.
- **IMAP parent / generic IMAP:** an ordinary IMAP account with its own connection — it gets
  **IMAP IDLE** like any account (`ImapMailService.cs:932`). Live, not poll-only.
- Consequence: the "updates every few minutes, not instantly" caveat (§7) applies **only to
  Graph-backed** shared mailboxes; the add dialog shows it conditionally on the resolved
  backend.

**Decision: token resolution for a credential-less shared account.** A shared account stores
no credentials and has no MSAL entry. `IOAuthService.GetAccessTokenAsync` (and the IMAP/SMTP
auth path) must, for `IsShared` accounts, **resolve `ParentAccountId` → the parent account →
the parent's token**, requesting the `.Shared` scopes. If the parent is signed out or its
token fails, the shared account shows **disconnected** and surfaces the parent's auth error
(no silent empty state — CLAUDE.md).

**Decision: aggregate exclusion via a new `IsShared` predicate, NEVER `ExcludeFromAllMail`.**
`ExcludeFromAllMail` is *both* the aggregate filter (`MainViewModel.cs:4437,4468,4540,4651`;
`SyncService.cs:108`) **and** the sweep's own filter (`MainViewModel.cs:2359`). Using it to
hide shared mail from All Inboxes would also remove it from the one freshness mechanism a
Graph shared mailbox has. The exclusion happens at the tree/aggregate-query layer via an
`IsShared` check; the sweep continues to include shared accounts.

**Decision: defer EWS auto-detection.** See §13 — EWS is blocked for third-party apps on
2026-10-01, and Graph cannot enumerate delegated mailboxes. Building detection now means
shipping a feature that breaks in ~2 months. Manual add-by-address is the v1 path.

### 5.2 Runtime mode compatibility

| Mode | Shared-mailbox behavior | Fallback |
|---|---|---|
| Normal | Reads via backend; **Graph** shared boxes refresh on the #456 sweep, **IMAP** on IDLE + sweep. Store-backed. | — |
| `--online` | No local store; reads go straight to the backend. **`StartFallbackSyncAsync` does not run in online mode** (`MainViewModel.cs:2308`), so a **Graph** shared mailbox refreshes **only on manual navigation** (no background poll). IMAP IDLE still delivers for IMAP parents. | Manual navigation refetches. Documented; acceptable (online mode is opt-in, no background sync for any folder). |
| `--profileDir <path>` | Identical to Normal, alternate data dir. | — |

### 5.3 Code reuse and duplication risks

- The **add dialog** should reuse the existing `AddAccountDialog` shell pattern (modeless,
  focus, validation) rather than clone it. Plan: a small dedicated `AddSharedMailboxDialog`
  or a mode on `AddAccountViewModel`; decide in PR 1.
- **Backend send routing** for a shared account extends `GraphMailService`/`SmtpService`;
  no duplication if the `IsShared` → parent-token resolution lives in one place (the auth
  resolver, §5.1), not in each backend.

### 5.4 Shared component audit (mandatory)

| Component | File | Other consumers | Change | Risk / verify |
|---|---|---|---|---|
| `AccountModel` | `Models/AccountModel.cs` | Everything | +`IsShared`, `ParentAccountId`, `SharedAddress`, per-account `NotifyOnNewMail` (opt-in) | Optional fields default cleanly (no migration); verify existing account round-trip unchanged. |
| `AccountModel.AccessibleName` | `:181` | Account list, tree header | Insert "shared mailbox" qualifier for shared | Exact composition in §7; verify non-shared string unchanged. |
| `MailServiceRouter` | `Services/MailServiceRouter.cs` | All mail ops | Register shared account's backend (existing hook) | No API change; shared account must be in `Accounts` before first use. |
| `BuildFolderTree` / `NodeKey` | `MainViewModel.cs:3377-3541,:3543` | Folder pane | Add shared top-level node; **fix header node-key to include account id** | `H:{Label}` collides for any two same-named headers (latent bug beyond shared); fix for all header nodes. |
| `StartFallbackSyncAsync` | `MainViewModel.cs:2317-2400` | The sweep | **No change** — shared accounts must stay swept | Verify the `IsShared` aggregate exclusion does NOT touch this path. |
| `MaybeNotifyNewMail` | `MainViewModel.cs:2675-2678` | Toasts | Gate on new per-account `NotifyOnNewMail` for shared (default off) | Global `NotifyOnNewMail` still governs real accounts unchanged. |
| Aggregate query filters | `MainViewModel.cs:4437,4468,4540,4651`; `SyncService.cs:108` | All Inboxes / All Mail | Add `!IsShared` predicate | Verify real accounts unaffected; verify shared still swept (row above). |
| `AccountManagerViewModel.DeleteAccountAsync` | `:387` | Manage Accounts | Cascade-remove shared children of a removed parent (naming confirmation); shared-only removal is ordinary | Verify removing a normal account with no children is unchanged. |
| `ComposeViewModel` | `:220-224` (`SenderAccounts`/`SenderAccount`) | Compose | Shared address appears as a send identity; route its send by backend | Verify a normal compose's sender list/order is unchanged. |
| `OAuthService` | `Services/OAuthService.cs:27-113` | Auth | +`.Shared` scope consts; `IsShared` → parent-token resolution | Verify normal scope selection unchanged. |
| Add dialog (new `Window`) | new | — | New Window Checklist (§7) | Editable TextBox over live WebView2 → `Show()` not `ShowDialog()`. |

## 6. Keyboard Walkthrough (mandatory)

### Path A: Add a shared mailbox by address
1. User opens **Manage Accounts** (existing). **Expected:** dialog opens, focus on account list.
2. User Tabs to and activates **"Add shared mailbox"**. **Expected:** SR: *"Add shared mailbox, button."*
3. A modeless dialog (`Show()`) opens, focus on **Address**. **Expected:** SR: *"Shared mailbox address, edit."*
4. User Tabs to **Parent account** combo (defaulted to current account; only shared-capable accounts listed). **Expected:** SR reads the selected parent. If the resolved parent is **Graph**, a static (non-tab-stop) note reads: *"Shared mailboxes update every few minutes, not instantly."* For an **IMAP** parent, the note is absent.
5. User presses **Add** (or Enter). **Expected:** address validated; shared `AccountModel` created (`IsShared`, `ParentAccountId`, `SharedAddress`, `BackendKind`=parent's); persisted; backend registered; **no password prompt**.
6. **Error:** invalid address / parent can't host it. **Expected:** dialog stays open, SR reads the inline error (Result category), focus stays on Address.
7. Dialog **Escape/Cancel**. **Expected:** closes without creating; focus returns to the account list (explicit restoration — modeless has no auto-return).
8. On success the dialog closes; focus returns to the account list on the new item. **Expected:** SR: *"Support, shared mailbox, …"* (§7 composition).

### Path B: Navigate into a shared mailbox
1. F6 to **Folders** (index 2) or **Accounts** (index 1). **Expected:** shared mailbox is its own top-level node; SR reads its accessible name (§7). **No new F6 pane.**
2. Expand, arrow to the shared Inbox, Enter. **Expected:** messages load; existing *"N messages loaded"* (Status) fires. Not present in All Inboxes / All Mail.
3. New mail arrives. **Graph parent:** appears on the next sweep (poll), no toast unless the per-account opt-in is on. **IMAP parent:** appears via IDLE.

### Path C: Send from a shared mailbox
1. From the shared Inbox, user Reply/New. **Expected:** compose opens; the **From / Sender** picker (`SenderAccounts`) includes the shared address, preselected when replying from the shared box.
2. User confirms sender = the shared address, composes, Send. **Expected:** send routes by the shared account's backend — Graph `sendMail` on `/users/{shared}` (`Mail.Send.Shared`) or IMAP-parent SMTP XOAUTH2 `user=`; SR: *"Message sent"* (existing Result).
3. **Error:** parent token invalid. **Expected:** send fails with a surfaced error (not silent); draft preserved.

## 7. Accessibility Checklist (mandatory)

- **AutomationProperties.Name — exact composition.** `AccountModel.AccessibleName` today returns
  `"{label}, connected, N unread"` / `"{label}, disconnected"` (`:181`). For a shared account,
  insert the qualifier right after the label: **`"{label}, shared mailbox, {connected|disconnected}[, N unread]"`**
  → e.g. *"Support, shared mailbox, connected, 12 unread."* The **folder-tree header node** is a
  **separate** string (`FolderTreeNode.AutomationName`) → **`"{label}, shared mailbox[, N unread]"`**.
  Add-dialog controls: **"Add shared mailbox"**, **"Shared mailbox address"**, **"Parent account"**
  (short labels only).
- **AnnouncementCategory.** No new recurring announces — context is in the accessible name
  (principle 4). Add-flow errors reuse the existing Result path; send reuses the existing
  *"Message sent"* Result. The Graph poll-interval caveat is **static dialog text**, not an
  announce.
- **F6 ring & command palette — deliberately none on the add dialog.** **No new main-window
  pane** — the shared mailbox lives in the existing Folders (2) and Accounts (1) panes. The
  **add dialog is a new `Window` but a leaf single-form dialog** (Address → Parent →
  Add/Cancel — one linear Tab group, no distinct panes to cycle between), so it gets **no F6
  ring and no `Ctrl+Shift+P` command palette**. This is a deliberate New-Window-Checklist
  exception, identical to the `ServerRuleEditorWindow` decision (#333): F6 has nothing to jump
  between on a single form, and a palette would only duplicate the two visible buttons. What
  the dialog *does* take from the checklist: `Show()` not `ShowDialog()` (the modal rule
  below), explicit Escape/Cancel wiring, focus restoration on close, and a
  `CancellationTokenSource` **only if** it performs async work (e.g. a parent-capability
  check) — purely synchronous validation needs none.
- **Modal rule.** The add dialog has an editable TextBox and can open over a live WebView2 →
  **`Show()` (modeless)**, Escape/Cancel wired explicitly (the GrabAddresses lesson).
- **Selector test.** The Parent-account combo binds `AccountModel` (`ToString()` overridden,
  `:192`) → add it to `SelectorItemAccessibilityTests`.
- **Color-only info.** The "(shared)" state is textual (label + accessible name), never
  color-only.

## 8. Acceptance Walkthrough (mandatory)

### Scenario: Add + read a Graph shared mailbox
**Setup:** app on qm-graph-test (Graph parent signed in).
1. Manage Accounts → Add shared mailbox → type a shared address, parent = the Graph account, Add. **Verify:** no password prompt; item appears as *"…, shared mailbox"* in the account list and as a top-level tree node.
2. Open its Inbox. **Verify:** messages load; it is **absent** from All Inboxes / All Mail.
3. Wait one sweep interval with new mail in it (or force a sweep). **Verify:** new mail appears; **no toast** (opt-in off).
4. **Edge:** sign the parent out. **Verify:** shared mailbox shows disconnected + a surfaced error, not a blank list.

### Scenario: Send from the shared mailbox
1. Reply from a shared-Inbox message. **Verify:** sender picker preselects the shared address.
2. Send. **Verify:** *"Message sent"*; the message shows as sent from the shared address (confirm in Outlook/web).

### Scenario: Remove (cascade)
1. Remove the **parent** account. **Verify:** confirmation names the shared child; both are removed; no orphan remains.
2. Re-add parent + shared; remove **only** the shared mailbox. **Verify:** parent untouched.

### Scenario: No-regression (shared component callers)
- All Inboxes / All Mail with only real accounts. **Verify:** unchanged.
- A normal compose. **Verify:** sender list unchanged (plus the shared identity if present).
- `--online`: open a Graph shared mailbox. **Verify:** loads on navigation; no crash from the absent store.

### Scenario: Screen reader
- Tab the add dialog. **Verify:** each control's name reads correctly; Graph-parent note present, IMAP-parent note absent.

## 9. Success Metrics

- Add a shared mailbox by address and read it, keyboard-only, with a screen reader.
- Send from the shared address; recipient sees it from the shared mailbox.
- Graph shared mail refreshes on the sweep; IMAP shared mail via IDLE.
- Zero regression: existing account/aggregate/compose tests pass unchanged; the shared box is
  absent from unified views and the global unread total.
- `--online`: a Graph shared mailbox loads on navigation without error.

## 10. Implementation Phases

1. **Linked-account model + manual add.** `AccountModel` fields, persistence, router
   registration, the add-by-address dialog (New Window Checklist) + `account.addShared`
   command + Manage-Accounts button, the top-level tree node + node-key fix, the aggregate
   `IsShared` exclusion, cascade removal. **No backend access yet** (the account exists and
   is navigable; folders empty until PR 2/3). Proves Approach B.
2. **Graph read access.** `/users/{SharedAddress}/…` in `GraphMailService`; `.Shared` scope
   consts; the `IsShared` → parent-token resolver. Behind the manual-add path; needs the scope
   declared to run end-to-end.
3. **IMAP read access + RFC 2342 NAMESPACE.** XOAUTH2 `user=` for an IMAP parent; NAMESPACE
   for generic IMAP.
4. **Send.** Graph `sendMail` on `/users/{shared}` (`Mail.Send.Shared`) + IMAP-parent SMTP
   XOAUTH2 `user=`; compose sender-identity wiring; send keyboard walkthrough (§6 Path C).
5. **Toast opt-in + polish.** Per-account `NotifyOnNewMail` gate, docs (User Guide page + the
   poll-interval note), accessibility pass.

*(EWS auto-detection — formerly PR 4 — is dropped; see §13.)*

## 11. Files to Create / Modify

**Create:** `Views/AddSharedMailboxDialog.xaml(.cs)` (or an `AddAccountViewModel` mode),
`ViewModels/AddSharedMailboxViewModel.cs`; User Guide shared-mailbox page.
**Modify:** `Models/AccountModel.cs` (+fields, AccessibleName); `ViewModels/MainViewModel.cs`
(tree node, node-key, aggregate predicate, `account.addShared` registration, toast gate);
`Services/OAuthService.cs` (+scopes, parent-token resolver); `Services/GraphMailService.cs`
(`/users/{shared}` routing + sendMail); `Services/ImapMailService.cs` / `SmtpService.cs`
(`user=` for shared); `ViewModels/AccountManagerViewModel.cs` (`DeleteAccountAsync` cascade);
`ViewModels/ComposeViewModel.cs` (sender identity); `docs/ARCHITECTURE.md`.

## 12. Tests to Add

| Test class | Methods | Coverage |
|---|---|---|
| `SharedMailboxModelTests` | round-trip persistence of the new fields; AccessibleName composition (shared vs normal) | model + a11y string |
| `SharedMailboxRoutingTests` | Graph shared → `/users/{addr}`; IMAP shared → `user=`; token resolves via parent | access by backend |
| `SharedMailboxAggregateTests` | shared inbox excluded from All Inboxes/All Mail **and** from global unread; **still included in the sweep** | the item-2 trap, pinned |
| `SharedMailboxRemovalTests` | remove parent → cascade children (named); remove shared-only → parent intact | lifecycle |
| `SharedMailboxSendTests` | send routes by backend; sender identity = shared address | send path |
| `NodeKeyTests` | two same-named header nodes get distinct keys | the node-key fix |
| `SelectorItemAccessibilityTests` (existing) | add the Parent-account combo item type | rule compliance |

## 13. Known Risks & Open Questions

### 13.1 Risks

| Risk | Prob | Impact | Mitigation |
|---|---|---|---|
| **EWS retirement blocks third-party apps 2026-10-01** (full shutdown 2027-04-01); Graph can't enumerate delegated mailboxes → **no durable auto-detection path** | High | Major (for detection only) | **Auto-detection dropped from v1.** Manual add-by-address uses no EWS and stands alone. Revisit only if a Graph enumeration API appears. Verified via Microsoft 365 Dev Blog / Learn (Aug 2026). |
| A dev implements the aggregate exclusion with `ExcludeFromAllMail`, silently killing the sweep (the only Graph freshness) | Med | Major | §5.1/§5.4 mandate the `IsShared` predicate; `SharedMailboxAggregateTests` pins "still swept". |
| Graph shared mailbox read as "instant" by users, felt as a bug on delay | Med | Minor | Add-dialog note (Graph only) + User Guide; principle 2 framing. |
| Credential-less token resolution fails silently when parent signed out | Med | Major | §5.1 rule: show disconnected + surface parent error; acceptance scenario covers it. |
| Modal add dialog over live WebView2 hangs (GrabAddresses) | Low | Blocker | `Show()` not `ShowDialog()`; §7. |

### 13.2 Open questions

- **Node-key fix scope:** fix `NodeKey` header collision for **all** header nodes (chosen), or
  only shared? → Chosen: all (it's a latent bug for duplicate-named ordinary accounts too).
- **Personal MS accounts:** confirmed **out** — they use `GraphMailScopesPersonal` and have no
  Exchange shared mailboxes; work/school only.
- **EWS consent check** (former §12 item): **moot** now that detection is dropped; revive only
  if detection is ever reconsidered.
