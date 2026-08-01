# Shared Mailboxes — PM/Dev Spec

**Status:** Draft for review (2026-08-01). Supersedes the closed PR #59 spec. Tracks
issue #461 (settled design decisions) and #31 (the feature request). The access,
detection, and account-model decisions below are settled; this document exists to
ground them in the current codebase and to make the UX/accessibility decisions that
were open.

---

## 1. Problem statement (unchanged from #59)

Exchange/Microsoft 365 automatically surfaces mailboxes a user has been granted
access to (shared mailboxes, delegated mailboxes) via *automapping*. Outlook shows
them in the folder tree with no setup. No accessible mail client does this well, and
for a blind or low-vision worker a shared support/team mailbox is often a core part of
the job. That is the reason to build this.

## 2. Why this needed re-grounding

The #59 spec (written 2026-06-09) assumed shared-mailbox **access** rides the existing
IMAP/OAuth stack and is independent of the Graph backend. That was true when written and
is **false now**: since #393, a work/school M365 account on a custom domain routes onto
the **Graph** backend by default, and `OAuthService.DefaultScopesFor`
(`Services/OAuthService.cs:104-113`) returns the IMAP scopes **only** when
`BackendKind != MicrosoftGraph`. So a Graph-backend parent account never receives
`IMAP.AccessAsUser.All`, and there is no IMAP token to hang a linked shared mailbox off.
For the target persona there is no IMAP connection at all. The access mechanism therefore
depends on the **parent account's backend**, which the old spec's §3/§6/§7/§8 did not
account for.

---

## 3. Settled design decisions

### 3.1 Approach B — the linked-account model

Each shared mailbox is a **first-class account** with its own `Guid Id`, linked to the
parent account it is accessed through. Chosen because it is the only model that absorbs
**two backends** (Graph and IMAP) without a second migration: the shared mailbox routes
through `MailServiceRouter` (`Services/MailServiceRouter.cs`) exactly like any other
account, keyed by its own `Id`. The alternative (a `mailbox` cache-key dimension threaded
through every store call) would have to be plumbed through both backends.

### 3.2 Access = whatever backend the parent account uses

- **Graph parent** → `GET /users/{sharedAddress}/messages` (and the sibling folder/message
  endpoints) using the **delegated** scopes `Mail.ReadWrite.Shared` and `Mail.Send.Shared`.
  These need **no admin consent** — the deciding factor that makes Graph *lower* friction
  than IMAP (which is what #393 moved away from because tenants had not consented to the
  IMAP scopes). The signed-in delegate needs Full Access on the shared mailbox and a
  mailbox of their own — the same precondition the IMAP path has.
- **IMAP parent** → the existing XOAUTH2 `user=` impersonation mechanism, unchanged from
  the #59 spec. The shared mailbox authenticates through the parent's IMAP token with the
  shared address as the `user=` value.
- **Generic (non-Microsoft) IMAP** → RFC 2342 `NAMESPACE` enumeration for discovery; access
  is ordinary IMAP.

Because access is per-parent-backend, shared mailboxes are **not backend-uniform** — this
is the load-bearing reason Approach B (own account id + router routing) was chosen.

### 3.3 Detection = Autodiscover SOAP `GetUserSettings` → `AlternativeMailbox`

Graph **cannot** enumerate the mailboxes a user has access to (Microsoft's guidance is
explicit that enumerating Exchange permissions is not a Graph scenario; the alternatives
need admin rights). So detection stays on Exchange **Autodiscover SOAP**
(`GetUserSettings`), whose response carries `AlternativeMailbox` entries — the automapped
list. This requires the `EWS.AccessAsUser.All` scope (detection only).

**Sizing warning (the estimate the #59 spec got wrong):** the existing
`AutoDiscoverService` (`Services/AutoDiscoverService.cs`) is **much less reusable** than
§7 assumed. It shares only HTTPS hardening. Concretely, verified in code:
- It speaks **POX v1** (`POST /autodiscover/autodiscover.xml`, 2006 request schema,
  `BuildAutodiscoverRequest` `:533-542`). Shared-mailbox discovery needs the **SOAP**
  `GetUserSettings` service at `autodiscover.svc` — a different endpoint, envelope, and
  response schema.
- It is **anonymous** — no `Authorization` header anywhere (`CreateDefaultClient`
  `:66-81`), and its constructor takes `IProviderCatalog` + `IConfigService`, not
  `IOAuthService`. Automapping data is only returned to an **authenticated** caller.
- It **never parses `AlternativeMailbox`** — `ParseAutodiscover` (`:552-617`) only reads
  `Protocol` blocks. `DiscoveredSettings` (`Models/DiscoveredSettings.cs:46-56`) is a flat
  imap/smtp/provider record that cannot carry a mailbox list.
- **A 401 is swallowed as "found nothing"** (`SendFollowingHttpsRedirectsAsync` `:192`
  returns `null` on any non-success) so the next tier runs. Fine for settings discovery;
  for shared mailboxes it makes a missing scope or a tenant block **silent and
  undiagnosable**. Discovery must distinguish "no mailboxes" from "not allowed to ask."
- **Different lifecycle:** settings discovery runs *before* sign-in, from the add-account
  dialog, with no token. Shared-mailbox discovery runs *after* sign-in, per account, with
  a token, and wants periodic refresh.

Therefore detection needs its **own** method and result type on `IAutoDiscoverService`
(e.g. `DiscoverSharedMailboxesAsync(account, ct) → IReadOnlyList<SharedMailboxHint>`),
authenticated, SOAP-speaking — not an extension of `DiscoverAsync`. Any PR breakdown
resting on "reuse the existing Autodiscover client" is undersized.

### 3.4 No live watcher — the sweep is the freshness mechanism

`Mail.Read.Shared` / `Mail.ReadWrite.Shared` **do not support change-notification
subscriptions** on shared/delegated folders (subscribing would need the *application*
permission `Mail.Read` — a different, admin-consented, app-only trust model, out of scope).
Practical consequence: **a shared mailbox has no delta subscription and no IMAP IDLE.** New
mail is discovered only by **polling**.

The periodic all-folder sweep that landed in #456 (`MainViewModel.StartFallbackSyncAsync`
`:2317-2400`) is therefore the **primary** freshness mechanism for shared mailboxes, not a
fallback. Verified: the sweep iterates **every account in `Accounts`** × every non-excluded
folder with a 250 ms pace, gated only by `MailSyncPollMinutes` (default 5, range 1–120) —
there is **no per-account opt-out**. So a shared mailbox added to `Accounts` is swept
automatically at the same interval, for free.

**User-visible consequence (must be stated — Kelly's requirement):** a shared mailbox
updates on the poll interval, **not instantly**. Someone who adds a busy support mailbox and
sees mail arrive minutes late will read it as a bug unless we say otherwise. Where we say it
(see §7 for the exact wording decision).

### 3.5 Send is in v1

`Mail.Send.Shared` makes Graph-native send from the shared address cheap enough that
splitting read-first/send-later is not worth the extra release. v1 includes sending *from*
a shared mailbox. (Kelly deferred this to scheduling; stated here as the position, subject
to his veto.)

---

## 4. Data model changes

`AccountModel` (`Models/AccountModel.cs`) has **no** parent/child/shared concept today and
**no** schema-version field — but `AccountService` (`Services/AccountService.cs:23-46`)
round-trips plain JSON, and missing properties deserialize to defaults, so **new optional
fields need no migration** (the pattern `ProviderId` and `RequireStartTls` already rely on).

Add three optional, persisted fields:

| Field | Type | Meaning |
|---|---|---|
| `IsShared` | `bool` (default false) | This account is a shared/delegated mailbox, not a directly-signed-in one. |
| `ParentAccountId` | `Guid?` (default null) | The account whose token/connection this mailbox is accessed through. Null for normal accounts. |
| `SharedAddress` | `string?` (default null) | The shared mailbox's SMTP address — the `{sharedAddress}` in the Graph URL and the IMAP `user=` value. For a shared account, equals `Username`. |

`BackendKind` on the shared account is set to **match the parent's backend** at creation
(the field is already "fixed at account creation"). No credentials are stored for a shared
account — it authenticates through the parent (`ParentAccountId`), so `CredentialService` is
never called for it. This must be explicit in the add flow so a shared account never prompts
for a password.

**Removal (lifecycle — resolved):** a shared account cannot function without its parent's
token, so orphaning one produces a dead, broken account. Removing a **parent** therefore
**cascade-removes its linked shared mailboxes**, behind a confirmation that **names them**
(e.g. *"Removing this account will also remove 2 shared mailboxes: Support, Sales."*).
Removing a **shared mailbox on its own** is an ordinary account removal — drop it from
`Accounts`, re-save, unregister its backend; there are no credentials to clean up. Implemented
in `AccountManagerViewModel` removal (`:257-276` region for the add counterpart) by expanding
the delete path to find `Accounts.Where(a => a.ParentAccountId == removed.Id)` and removing
them together.

---

## 5. Access implementation notes

- **Graph** (`Services/GraphMailService.cs`): every mailbox-scoped call for a shared account
  must target `/users/{SharedAddress}/…` instead of `/me/…`. The immutable-id header (#419)
  and the 404-tolerance (#416) apply unchanged. New scope constants in `OAuthService`
  (`Mail.ReadWrite.Shared`, `Mail.Send.Shared`) — and because the work/school path uses
  `.default` (`OAuthService.cs:33-38`), these must **also be declared on the app
  registration** or they are never granted. `.default` is per-resource, so the EWS detection
  scope (§6) is requested separately.
- **IMAP**: XOAUTH2 with `user={SharedAddress}` over the parent's token — unchanged from #59.
- **Routing**: the shared account registers its backend through the existing
  `RegisterAccountBackend` hook (`MainViewModel.cs:1215,1259-1260`,
  `App.xaml.cs:460`) — `BackendFor(sharedAccount)` picks by its `BackendKind`, same as any
  account. No router changes needed beyond ensuring the shared account is in `Accounts`.

## 6. Entra app registration — one declaration pass

Held until now deliberately (so existing users aren't re-consented weeks before anything
uses the scopes). When implementation reaches **detection** (PR 4), Kelly declares, in one
pass:

| Scope | Resource | Purpose | Admin consent |
|---|---|---|---|
| `EWS.AccessAsUser.All` | Office 365 Exchange Online | detection only | **needs checking** — the riskier one |
| `Mail.ReadWrite.Shared` | Microsoft Graph | read + mutate shared mail | no |
| `Mail.Send.Shared` | Microsoft Graph | send from the shared address | no |

`Mail.Read.Shared` is subsumed by `Mail.ReadWrite.Shared` — declare ReadWrite only. The two
Graph scopes ride the existing `.default` grant once declared (a re-consent to widen the
set, no new consent *mechanism*). The EWS scope is a separate resource and requested on its
own. **The registration edit is Kelly's** (tenant action); ping #461 when PR 4 is ready.

---

## 7. Freshness UX & the "not instant" wording (DECISION)

Kelly requires the UI and User Guide to state plainly that shared mailboxes update on the
poll interval. Decided, with the screen-reader user:

- **The recurring node label stays clean** — the shared mailbox's accessible name is
  **"{name}, shared mailbox"** (e.g. *"Support, shared mailbox"*), with **no timing
  number**. Reason: it is read on every focus, and the interval is the configurable
  `MailSyncPollMinutes` (1–120) — pinning a number in the label is both noisy and brittle.
- **The "not instant" expectation is set once, at add time and in the guide:**
  - The **Add-shared-mailbox dialog** carries a short note: *"Shared mailboxes update every
    few minutes, not instantly."*
  - The **User Guide** shared-mailbox page states the same and explains it is governed by
    the mail poll interval.
- No live-region / auto-announce for the interval (consistent with the #333 status-line
  cleanup: announcements respect user config; recurring context lives in the accessible
  name, one-time caveats live at the decision point).

Related: #462 (measure the sweep's real cost and scope it) — a busy shared mailbox adds
sweep load with no per-account throttle today; if #462 concludes a throttle is needed, a
shared mailbox is the first candidate for a longer interval.

---

## 8. Keyboard walkthrough (REQUIRED)

### 8a. Adding a shared mailbox by address (the manual path — always available)

1. User opens **Manage Accounts** (`account.manage`, existing). Focus is on the account
   list. Screen reader announces the dialog and the list.
2. User activates a new **"Add shared mailbox…"** button (Tab-reachable, beside the existing
   **New** button). Screen reader: *"Add shared mailbox, button."*
3. A dialog opens with focus on an **Address** field. Screen reader: *"Shared mailbox
   address, edit."* Below it, static text (not a tab stop, but readable): *"Shared mailboxes
   update every few minutes, not instantly."*
4. User types the shared address and Tab moves to a **Parent account** combo (defaulted to
   the account they were on; only accounts that can host a shared mailbox are listed).
   Screen reader reads the selected parent.
5. User presses **Add** (or Enter). The dialog validates the address, creates the shared
   `AccountModel` (`IsShared=true`, `ParentAccountId`, `SharedAddress`, `BackendKind` =
   parent's), persists it, and registers its backend. No password prompt.
6. Dialog closes; focus returns to the account list, now including the shared mailbox.
   Screen reader announces the reselected item: *"Support, shared mailbox."*

### 8b. Navigating into a shared mailbox

1. In the main window, user cycles to the **Folders** pane (F6 to index 2) or the
   **Accounts** pane (index 1). The shared mailbox appears as its **own top-level node**
   (§3.1 decision), accessible name **"Support, shared mailbox"**.
2. User arrows to it and expands. Screen reader reads its folders (Inbox, Sent, etc.) the
   same way as any account's.
3. User selects the shared Inbox and presses Enter / arrows into the message list. Messages
   load; the existing post-fetch count status announces *"N messages loaded"* (Status
   category — unchanged behavior, `MainViewModel.cs:2166`).
4. The shared mailbox's inbox is **not** part of **All Inboxes / All Mail** (§ aggregates
   decision) — it is reachable only under its own node, keeping the unified views "my mail."
5. New mail arrives in the shared mailbox: it appears on the next **poll** (§3.4), silently,
   the same way the #456 sweep surfaces any non-inbox folder — no toast (toasts are
   inbox-of-a-signed-in-account only, `MainViewModel.cs:2382-2385`).

## 9. Infrastructure changes (REQUIRED)

- **F6 ring:** **no new pane.** A shared mailbox lives inside the existing **Folders** pane
  (index 2) and **Accounts** pane (index 1) as another top-level node — it does not get its
  own F6 stop. `GetFocusedPaneIndex` / `CycleFocusAsync` (`MainWindow.xaml.cs:3780-3839`)
  are unchanged. (Stated explicitly so it reads as decided, not missed.)
- **CommandRegistry:** one new command — `id: "account.addShared"`, category **"Account"**,
  title **"Add shared mailbox"**, `execute` → open the add-shared dialog, **no default key**
  (discoverable via the palette and the Manage Accounts button). Registered in
  `MainViewModel.RegisterCommands` beside `account.manage` (`MainViewModel.cs:1863`).
- **`AutomationProperties.Name` values introduced/changed:**
  - Shared account node / account-list item accessible name → **"{name}, shared mailbox"**
    (new; via `AccountModel.AccessibleName` `:181` gaining a shared-mailbox branch, and the
    folder-tree header node `Label`/`AutomationName`). Visible label carries a **"(shared)"**
    tag for on-screen parity.
  - **Disambiguation (resolved):** the "shared mailbox" accessible suffix already separates a
    shared "Support" (*"Support, shared mailbox"*) from a real account "Support" audibly, and
    "(shared)" separates them visually. Only when two mailboxes are still indistinguishable
    (e.g. two shared mailboxes with the same name) is the **address appended** to the
    accessible name. The node-key-by-id fix below handles expansion state in all cases.
  - "Add shared mailbox" button → **"Add shared mailbox"** (short label only).
  - Address field → **"Shared mailbox address"**; parent combo → **"Parent account"**.
- **`AccessibilityHelper.Announce` calls added:** **none required** for the recurring path
  (context is carried by the accessible name per §7). One optional **Hint** on first entry
  is explicitly *not* added, to respect announcements-off users. The add-flow's success uses
  the existing account-reselect path; no new announce.
- **VM state:** `AccountModel` gains `IsShared` / `ParentAccountId` / `SharedAddress` (§4).
  `BuildFolderTree` (`MainViewModel.cs:3377-3541`) and the aggregate-group builder
  (`allMailGroup` `:3489-3500`) gain a filter so a shared mailbox is a top-level node but its
  inbox is excluded from All Inboxes / All Mail. The node-key scheme (`NodeKey` `:3543`) must
  key the shared account header by its **account id**, not just its label, to avoid
  expansion-state collisions with a same-named account (current header keying `"H:{Label}"`
  is a latent bug for duplicate labels — fix it for shared nodes).

## 10. PR breakdown (Kelly's access-first sequence)

1. **Linked-account model.** `AccountModel` additions, persistence, `MailServiceRouter`
   registration, the manual **"Add shared mailbox by address"** entry point (dialog +
   command + button), the folder-tree top-level node, and the aggregate-view exclusion. **No
   detection, no new scopes.** Proves Approach B carries its weight. Lands first.
2. **Graph access.** `/users/{sharedAddress}/…` routing in `GraphMailService`; the new
   `Mail.ReadWrite.Shared` / `Mail.Send.Shared` scope constants. Code lands behind the
   manual-add path; needs the scope declared to run end-to-end against a real mailbox.
3. **IMAP access.** XOAUTH2 `user=` for an IMAP-backend parent. Independent of 1–2.
4. **Detection.** Authenticated Autodiscover **SOAP** `GetUserSettings` → `AlternativeMailbox`
   → auto-offer discovered mailboxes. **Gated on the Entra registration edit** (§6). Size it
   from §3.3, not "reuse the existing client."
5. **RFC 2342 `NAMESPACE`** for generic IMAP accounts. Low marginal cost; slot anywhere.

## 11. Out of scope (v1)

- **Discovering shared mailboxes on non-Exchange servers** beyond RFC 2342 NAMESPACE.
- **Change-notification / instant delivery** for shared mailboxes — impossible with the
  delegated scopes (§3.4); freshness is the poll interval, by design.
- **Calendar / contacts of a shared mailbox** — mail only in v1.
- **Per-account sweep throttle** — tracked separately in #462; v1 shared mailboxes inherit
  the standard sweep interval.
- **Application-permission (app-only) access** to shared mail — different trust model,
  requires admin consent, not an end-user desktop scenario.
- **Nested/hierarchical display** under the parent account — v1 uses a flat top-level node
  (§3.1).

## 12. Open questions

- **Exact "needs checking" status of `EWS.AccessAsUser.All`** against a tenant that disables
  user consent — confirm in the Azure portal before PR 4 (does it hit an admin-consent-only
  policy?). This is the **only** open item and it is Kelly's tenant action, resolved at PR 4;
  duplicate-label handling and the removal flow (formerly open here) are now settled in §4
  and §9.
