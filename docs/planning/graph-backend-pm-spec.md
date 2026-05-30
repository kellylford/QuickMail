# Microsoft Graph Backend — Product Management Specification

**Status:** Proposed
**Version:** 0.2 (incorporates maintainer feedback on feature gating)
**Date:** 2026-05-24
**Author:** Tim Spaulding (proposing contributor)
**Target release:** v0.7 (refactor PRs 1-3), v0.8+ (Graph backend PRs 4-8, gated)
**Implementation:** Not started

**Changelog from v0.1:**
- §5, §9.7, §10, §12: added Feature Gate mechanism per maintainer request. Graph backend code ships disabled by default; flip is a future joint-decision PR independent of any release version.

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [User Problem & Opportunity](#2-user-problem--opportunity)
3. [Competitive Landscape](#3-competitive-landscape)
4. [Design Principles](#4-design-principles)
5. [Feature Scope](#5-feature-scope)
6. [Data Model Changes](#6-data-model-changes)
7. [User Experience Design](#7-user-experience-design)
8. [Accessibility (WCAG 2.2)](#8-accessibility-wcag-22)
9. [Technical Architecture](#9-technical-architecture)
10. [Implementation Phases](#10-implementation-phases)
11. [Success Metrics](#11-success-metrics)
12. [Open Questions & Risks](#12-open-questions--risks)

---

## 1. Executive Summary

QuickMail today speaks IMAP and SMTP. Microsoft 365 mailboxes are reachable via that path — `OAuthService` already authenticates against `login.microsoftonline.com` with `IMAP.AccessAsUser.All` and `SMTP.Send` scopes, and `ImapService` uses `SaslMechanismOAuth2` for those accounts. For the **default** M365 tenant configuration, QuickMail already works.

What QuickMail cannot do today is connect to an M365 mailbox where the tenant administrator has **disabled the IMAP and SMTP services** at the org level. This is an increasingly common enterprise security baseline. For users on those tenants, QuickMail is unreachable — there is no fallback. They are forced to use Outlook (with its known accessibility friction) or a web client.

This spec proposes adding **Microsoft Graph** as a second mail backend alongside the existing IMAP/SMTP stack. A new account type ("Microsoft 365") is offered in the Add Account dialog; selecting it routes that account through a new `GraphMailService` instead of `ImapService`. Existing IMAP/SMTP accounts are untouched.

The work is sequenced into nine PRs over two release cycles, with the first three landing as pure refactor (no behavior change) so that risk is decoupled from the new Graph code.

---

## 2. User Problem & Opportunity

### Current state

Microsoft has spent the last three years steering Exchange Online customers away from IMAP and SMTP:

- **Basic Auth** for IMAP/SMTP was disabled across most tenants in late 2023.
- **OAuth-based** IMAP/SMTP works, but Microsoft now permits tenant admins to disable those protocols entirely via the org-level `Set-CASMailbox -ImapEnabled $false` / `-SmtpClientAuthenticationDisabled $true` controls.
- The Microsoft 365 documentation now positions Graph as the only long-term API for mail.

The practical effect for QuickMail: a user whose company has hardened its tenant cannot connect, regardless of whether QuickMail supports OAuth. The connection succeeds at the TCP/TLS layer, the OAuth handshake completes, and the IMAP `LOGIN` returns `AUTHENTICATIONFAILED` with no recourse from the client side.

### Target personas

| Persona | Need |
|---|---|
| **Accessibility-first user on a hardened M365 tenant** | Their company disabled IMAP. Outlook is the only option; Outlook's accessibility is uneven. QuickMail's accessibility is a known good. They need a backend that works. |
| **Power user managing both work (M365) and personal (Gmail/Fastmail) mail** | Wants one client for everything. Today they must keep Outlook open just for work mail. |
| **Privacy-conscious user with a personal Outlook.com account** | Already works today via IMAP/OAuth, but Graph is faster (no IDLE connection pool to manage) and exposes richer features over time. |
| **System administrator evaluating QuickMail for org-wide deployment** | Needs the client to work without enabling legacy protocols on the tenant. IMAP being a hard requirement is a non-starter for many security baselines. |

### Opportunity

There is no Windows-first, accessibility-first, free, open-source desktop mail client that speaks Microsoft Graph natively. The accessible-on-M365 options today are:

1. **Outlook** (free / paid) — full Graph, full features, well-known accessibility friction (focus management, virtualization of large message lists, modal dialog patterns)
2. **Web Outlook** — better than desktop Outlook on some axes; worse on others; tied to the browser
3. **Thunderbird** — IMAP only; if your tenant disabled IMAP, you cannot use it
4. **eM Client** — IMAP + EWS; no Graph; paid; closed source

Adding Graph to QuickMail makes it the only entrant in the "accessible Windows client that works on a locked-down M365 tenant" category.

---

## 3. Competitive Landscape

| Product | M365 protocol | IMAP-disabled tenant? | Accessibility | License |
|---|---|---|---|---|
| **Outlook (Win32)** | Graph + MAPI | Yes | Known friction | Paid (M365) |
| **Outlook (new / web)** | Graph | Yes | Mixed | Paid (M365) |
| **Thunderbird** | IMAP/SMTP | **No** | Decent | Free |
| **eM Client** | IMAP + EWS | Partial (EWS) | Limited | Paid |
| **Mailspring** | IMAP only | **No** | Limited | Free + paid |
| **Apple Mail** | IMAP + EWS | Partial (EWS) | Good | macOS only |
| **QuickMail (today)** | IMAP/SMTP | **No** | Excellent | Free, MIT |
| **QuickMail + Graph (proposed)** | IMAP/SMTP + Graph | **Yes** | Excellent | Free, MIT |

### QuickMail's positioning

The proposal does not chase Outlook on feature breadth (categories, focused inbox, mailbox-settings UI, Teams hand-off). The goal is **functional parity** with the existing IMAP backend, but reachable on tenants where IMAP isn't. Feature breadth is a follow-up.

| Dimension | QuickMail target |
|---|---|
| Connect to IMAP-disabled M365 tenant | Yes |
| Send and receive mail | Yes |
| Folders, move, copy, delete | Yes |
| Drafts, attachments, conversation grouping | Yes |
| New-mail notifications | Yes (Graph `delta` polling, not webhooks) |
| Categories, focused inbox, mailbox settings | Future |
| Calendar, Contacts | Out of scope (QuickMail is mail-only) |
| EWS / on-prem Exchange | Out of scope (Microsoft removing EWS Oct 2026) |

---

## 4. Design Principles

1. **Protocol invisible to users.** The Add Account dialog offers "Microsoft 365" as an account type. Nowhere else in the UI does the word "Graph" or "IMAP" appear unless the user opens Settings → Advanced. Account list rows look identical regardless of backend.

2. **No regression for existing accounts.** IMAP users see no UI change, no behavior change, no startup-time change. The router has a fast path for the single-backend case.

3. **Per-account backend, fixed at creation.** An account's backend is selected when the account is added. You cannot migrate an IMAP account to a Graph account; you re-add it. This avoids a class of dual-state bugs and matches the user's mental model ("I'm setting up an account").

4. **Refactor first, feature second.** The first three PRs are pure mechanical refactor that leave the IMAP backend identical in behavior. The Graph backend lands in later PRs. Risk is staged.

5. **Reuse, do not duplicate.** OAuth/MSAL, the SQLite cache, `MimeMessageBuilder`, `ConversationBuilder`, every ViewModel, every dialog, every accessibility helper — all unchanged. Only the protocol layer is new.

6. **Polling, not webhooks.** Graph supports webhook subscriptions for change notifications, but webhooks require a publicly reachable callback URL — impractical for a desktop client. QuickMail polls the Graph `delta` endpoint instead. Same UX as IMAP IDLE; different mechanism.

7. **Accessibility unchanged.** The Add Account dialog grows one control. All other UI is untouched. WCAG 2.2 AA is preserved.

---

## 5. Feature Scope

### In scope (v0.7 refactor + v0.8 Graph backend)

**v0.7 — Refactor + feature-gate infrastructure (no user-visible change):**
- Rename `IImapService` → `IMailService` (no signature changes initially)
- Migrate `uint UniqueId` to `string MessageId` across models, services, SQLite schema
- Introduce `BackendKind` enum on `AccountModel`
- Introduce `MailServiceRouter` that dispatches per-account
- **Introduce `IFeatureGate` infrastructure** — `[features]` section in `config.ini` + `--feature` CLI flag; enum-keyed `FeatureFlag` so new flags don't change the interface. Lands in PR 3 alongside the first consumer (the Add Account combo).

**v0.8+ — Graph backend (gated, opt-in):**
- "Microsoft 365" option in Add Account dialog (**only visible when `FeatureFlag.GraphBackend` is enabled**)
- Full `GraphMailService` implementation: connect, list folders, list messages, get message detail, mark read/unread, move/copy, delete (to Trash), download attachments
- `GraphSmtpService`: send via `/me/sendMail` (accepts MIME from `MimeMessageBuilder`)
- Drafts: append/replace/delete via `/me/messages`
- Folder CRUD: create/rename/delete/move folders via `/me/mailFolders`
- New-mail notifications via `delta` polling (replacing IDLE for Graph accounts)
- OAuth scopes added: `Mail.ReadWrite`, `Mail.Send`, `MailboxSettings.Read`, `offline_access`
- Default gate state: **OFF**. Adventurous users enable it manually in `config.ini`. Default flips to ON via a future joint-decision PR (see §12).

### Out of scope (future)

- **EWS / on-prem Exchange Server.** Microsoft is removing EWS from Exchange Online in October 2026. New EWS code in 2026 is building on quicksand.
- **MAPI / RPC.** Proprietary, not viable for a third-party client.
- **Mixed-mode accounts** (same mailbox accessed via both IMAP and Graph). Pick one at creation; re-add to change.
- **Migrating an existing IMAP account to Graph in place.** User re-adds; the local cache for the old account remains until the account is deleted.
- **Categories, focused inbox, mailbox-settings UI.** Graph-only features that have no IMAP analogue. Worth their own future spec.
- **Webhook-based change notifications.** Requires a public callback URL or relay. Polling is sufficient for v1.
- **Shared mailboxes, delegate access, on-behalf-of send.** Future; not part of v1 parity.
- **Calendar, Contacts, Tasks.** QuickMail is a mail client; expanding scope is a separate product decision.
- **Per-tenant client app registration.** v1 uses the existing MSAL client ID with `common` authority, same as the current IMAP/OAuth path.

---

## 6. Data Model Changes

### 6.1 New: `BackendKind` enum

```csharp
namespace QuickMail.Models;

public enum BackendKind
{
    /// <summary>Standard IMAP for receive + SMTP for send. Default for all existing accounts.</summary>
    ImapSmtp,

    /// <summary>Microsoft Graph for receive + send. Used for M365 / Outlook.com when IMAP/SMTP is unavailable.</summary>
    MicrosoftGraph,
}
```

### 6.2 Modified: `AccountModel`

Adds two fields. All existing fields remain. IMAP-specific fields are unused when `BackendKind == MicrosoftGraph` but kept on the model to avoid a polymorphic JSON shape.

```csharp
public BackendKind BackendKind { get; set; } = BackendKind.ImapSmtp;

/// <summary>
/// Azure AD tenant for Graph accounts. Null = "common" authority (default).
/// Used only when BackendKind == MicrosoftGraph; ignored otherwise.
/// </summary>
public string? TenantId { get; set; }
```

**Migration:** existing `accounts.json` files have no `backendKind` field. `System.Text.Json` will use the default (`ImapSmtp`) on read — backwards-compatible without code changes.

### 6.3 Modified: `MailMessageSummary.UniqueId` (uint → string)

The IMAP `uint` UID does not fit Graph's opaque string IDs (e.g. `AAMkAGI2T...==`, ~150 chars). The whole pipeline migrates.

| Old | New |
|---|---|
| `public uint UniqueId { get; set; }` | `public string MessageId { get; set; } = string.Empty;` |
| SQLite: `unique_id INTEGER NOT NULL` | `unique_id TEXT NOT NULL` |
| IMAP impl writes `client.Uid` | IMAP impl writes `client.Uid.ToString(CultureInfo.InvariantCulture)` |
| Graph impl writes | message.Id directly |

**Schema migration:** one-time on first launch of v0.7. New `CurrentSchemaVersion = 2`. Migration converts `unique_id INTEGER` to `unique_id TEXT` for both `MessageSummary` and `MessageDetail` tables. Existing values become `CAST(unique_id AS TEXT)`.

**Sort behavior:** IMAP code relied on `MAX(unique_id)` for incremental sync (`GetMaxUidAsync`). For Graph this is replaced by a per-folder `delta_token` column. For IMAP, the impl issues `MAX(CAST(unique_id AS INTEGER))` to preserve numeric sort. Spec'd in §6.5.

### 6.4 Modified: `AttachmentModel.PartSpecifier`

Rename to `AttachmentRef` (string, opaque per backend):

- IMAP: stores the body-part specifier (`"2"`, `"2.1"`)
- Graph: stores the attachment ID returned by `/messages/{id}/attachments`

Type and nullability unchanged.

### 6.5 New: per-folder delta tokens for Graph

A new column on `MailFolderModel` (in-memory only, persisted in a small new SQLite table):

```sql
CREATE TABLE IF NOT EXISTS DeltaToken (
    account_id   TEXT NOT NULL,
    folder_id    TEXT NOT NULL,
    delta_token  TEXT NOT NULL,
    updated_utc  INTEGER NOT NULL,
    PRIMARY KEY (account_id, folder_id)
);
```

Used only by Graph backend. IMAP rows never exist here.

---

## 7. User Experience Design

### 7.1 Add Account dialog

One new control at the top of the dialog: an "Account type" combo box.

```
┌──────────────────────────────────────────────────────┐
│ Add Account                                  [✕]    │
├──────────────────────────────────────────────────────┤
│ Account type:  [Standard IMAP/SMTP            ▾]   │
│                                                      │
│ Account name:  [_______________________]            │
│ Display name:  [_______________________]            │
│ Email address: [_______________________]            │
│                                                      │
│ Authentication: ( ) Password  (•) OAuth (Microsoft) │
│                                                      │
│ ── IMAP ──────────────────────────────────────────── │
│ Host: [_______________]  Port: [993]  [✓] SSL       │
│                                                      │
│ ── SMTP ──────────────────────────────────────────── │
│ Host: [_______________]  Port: [587]  [ ] SSL       │
│                                                      │
│ [Sign in]  [Test connection]                         │
│                                                      │
│                              [Cancel]  [Save]        │
└──────────────────────────────────────────────────────┘
```

When **Account type** is "Microsoft 365 / Outlook.com":

```
┌──────────────────────────────────────────────────────┐
│ Add Account                                  [✕]    │
├──────────────────────────────────────────────────────┤
│ Account type:  [Microsoft 365 / Outlook.com   ▾]   │
│                                                      │
│ Account name:  [_______________________]            │
│ Display name:  [_______________________]            │
│ Email address: [_______________________]            │
│                                                      │
│ Authentication: OAuth (Microsoft) — required        │
│                                                      │
│ Selecting this account type will sign you in with   │
│ your Microsoft 365 or personal Outlook.com account. │
│ Mail will be accessed via Microsoft Graph.          │
│                                                      │
│ [Sign in]                                            │
│                                                      │
│                              [Cancel]  [Save]        │
└──────────────────────────────────────────────────────┘
```

The IMAP and SMTP host/port fields are collapsed (not visible) when Microsoft 365 is selected. The Authentication radio group is hidden because OAuth is the only option.

**Rationale:** the user does not need to know what protocol they're picking. They pick "their account type" and the right thing happens.

### 7.2 Account type combo options

| Display value | Backend | Auth options | Visible when |
|---|---|---|---|
| "Standard IMAP/SMTP" (default) | `ImapSmtp` | Password, OAuth (Microsoft) | Always |
| "Microsoft 365 / Outlook.com" | `MicrosoftGraph` | OAuth (Microsoft) only | `FeatureFlag.GraphBackend == true` |

Account type is **fixed after creation** — the field is read-only when editing an existing account. To switch, delete and re-add.

**When the gate is off** (the default state shipped to all users), the combo contains only one option ("Standard IMAP/SMTP") and may be rendered as a static label rather than a combo. See §9.7 for the gate mechanism.

### 7.3 Account list

No visible change for IMAP accounts. M365 accounts look identical in the list — same name, same unread badge, same status text. Behind the scenes, every operation routes through `GraphMailService` instead of `ImapMailService`.

The account's accessible name (`AutomationProperties.Name`) is unchanged — no "Microsoft 365" suffix. Users who care can read it in the Account Manager dialog.

### 7.4 Sign-in flow for new M365 accounts

Identical to the existing OAuth flow:

1. User selects "Microsoft 365 / Outlook.com" account type.
2. User enters email address and clicks **Sign in**.
3. System browser opens to `login.microsoftonline.com`.
4. User completes Microsoft sign-in (password, MFA, conditional access).
5. Browser redirects to `http://localhost:<port>` — MSAL captures the token.
6. Account is saved. Connection test runs in the background. UI confirms "Connected".

**Difference from existing IMAP/OAuth flow:** the scopes requested are `Mail.ReadWrite`, `Mail.Send`, `MailboxSettings.Read`, `offline_access` instead of `IMAP.AccessAsUser.All`, `SMTP.Send`, `offline_access`.

### 7.5 Error states unique to Graph

| Error | User-facing message |
|---|---|
| Tenant policy denies Graph access | "Your organization's Microsoft 365 settings do not permit this app to read mail. Contact your administrator." |
| Conditional access blocks sign-in | "Microsoft sign-in was blocked by your organization's security policy. Try again from a managed device, or contact your administrator." |
| Throttled (429 Too Many Requests) | Transparent — backoff and retry. Status bar: "Microsoft 365 is rate-limiting. Retrying…" |
| Mailbox not found | "No mailbox is associated with this account. If you signed in with a different account than intended, sign out and try again." |

All messages routed through `AccessibilityHelper.Announce(category: Result)`.

---

## 8. Accessibility (WCAG 2.2)

The user-visible surface change is **one new combo box** in the Add Account dialog. The accessibility burden is small but non-zero.

### 8.1 Compliance targets

WCAG 2.2 Level AA for the new combo and the conditional field visibility.

| SC | Description | How we meet it |
|---|---|---|
| 1.3.1 Info and Relationships | Hidden fields are not exposed to AT | When fields collapse on backend change, they are `Visibility="Collapsed"` (not `Hidden`), removing them from the tab order and the UIA tree |
| 1.3.2 Meaningful Sequence | Tab order is logical | Account type combo is first in tab order; remaining fields follow in visual order |
| 2.4.3 Focus Order | Focus moves meaningfully when fields change | When user switches from IMAP to M365 and hidden fields lose focus, focus moves to the next visible field, not lost |
| 4.1.2 Name, Role, Value | Combo has correct UIA role | Standard WPF `ComboBox` exposes `UIA.ControlType.ComboBox` natively |
| 4.1.3 Status Messages | Field-set change is announced | `AccessibilityHelper.Announce("Switched to Microsoft 365 account. IMAP and SMTP fields are no longer required.", AnnouncementCategory.Hint)` when the user changes backend type |

### 8.2 Screen-reader announcements

| Event | Announcement | Category |
|---|---|---|
| User switches account type to "Microsoft 365" | "Switched to Microsoft 365. IMAP and SMTP fields are not needed for this account type." | `Hint` |
| User switches account type back to "Standard IMAP/SMTP" | "Switched to standard IMAP and SMTP. Enter your server settings below." | `Hint` |
| OAuth sign-in started | "Opening Microsoft sign-in in your browser." | `Status` |
| Sign-in succeeded (Graph) | "Signed in as {Username}. Microsoft 365 account ready." | `Result` |
| Tenant denies scopes | "Your organization's Microsoft 365 settings do not permit this app to read mail." | `Result` |

### 8.3 Keyboard navigation

No new shortcuts. The Add Account dialog already supports Tab, Enter, and Escape; the new combo participates in the existing flow.

---

## 9. Technical Architecture

### 9.1 Target architecture

```
                          ┌─────────────────────────────────┐
   MainViewModel ─────→   │       IMailService              │  ←── per-account dispatch
   SyncService    ─────→  │  (was IImapService, renamed)    │       on BackendKind
   RuleService    ─────→  └────────────────┬────────────────┘
                                            │
                          ┌─────────────────┴─────────────────┐
                          │                                   │
                ┌─────────▼──────────┐              ┌─────────▼──────────┐
                │  ImapMailService   │              │  GraphMailService  │
                │  (existing logic)  │              │       (new)        │
                └─────────┬──────────┘              └─────────┬──────────┘
                          │                                   │
                       MailKit                       Microsoft.Graph or
                                                     raw HttpClient
                ─────────────────────────────────────────────────────
                                            │
                          ┌─────────────────┴─────────────────┐
                          │       MailServiceRouter           │
                          │  Dictionary<Guid, IMailService>   │
                          └───────────────────────────────────┘
```

`MailServiceRouter` itself implements `IMailService`. Every method takes an `accountId` (or `AccountModel`) — the router looks up the right backend instance and delegates. Consumers don't know two backends exist.

For events that don't take an account ID (e.g. `event Action<Guid>? InboxNewMailDetected`), the router multiplexes events from all underlying backends.

### 9.2 Data-flow diagram — incoming message

This diagram traces a single new message from arrival to UI render. **Both** backends produce the same shape at the `MailMessageSummary` boundary; everything downstream is backend-agnostic.

```
                IMAP path                                 Graph path
                ─────────                                 ──────────

  ImapClient IDLE detects new INBOX msg         GraphChangeNotifier delta-poll tick
              │                                              │
              │ event InboxNewMailDetected(accountId)        │
              └──────────────────────┬───────────────────────┘
                                     │
                                     ▼
                        MainViewModel.OnInboxNewMailDetected
                                     │
                                     ▼
                     SyncService.SyncOneFolderOnlineAsync
                                     │
                          IMailService.GetMessagesSinceAsync   ← router dispatches
                                     │
                  ┌──────────────────┴──────────────────┐
                  │                                     │
        ImapMailService                       GraphMailService
        IMAP UID FETCH range            GET /me/mailFolders/{id}/messages/delta
                  │                                     │
                  │   List<MailMessageSummary>          │
                  └──────────────────┬──────────────────┘
                                     ▼
                  ILocalStoreService.UpsertSummariesAsync
                       (SQLite, string PK on MessageId)
                                     │
                                     ▼
                      IRuleService.ApplyRulesAsync
                       (matches against summary fields)
                                     │
                                     ▼
                       SyncService.FolderSynced event
                                     │
                                     ▼
                  MainViewModel merges into Messages collection
                                     │
                                     ▼
                            ListBox renders
                  AccessibilityHelper.Announce(Status)
                  "New message from {Sender}: {Subject}"
```

**Boundary contract:** both backends MUST return `MailMessageSummary` with `MessageId` (string, non-empty), `AccountId` (Guid), `FolderName` (Graph: folder ID; IMAP: full IMAP name), `From`, `Subject`, `Date`, and where available `Preview`, `IsRead`, `HasAttachments`. Anything else can be empty.

### 9.3 Authentication

The MSAL infrastructure stays. `OAuthService.GetAccessTokenAsync` already handles silent-then-interactive with a DPAPI-encrypted cache.

What changes: `OAuthService` accepts scopes per call instead of hardcoding the IMAP scopes. The caller passes the right scopes for the backend.

```csharp
// before:
private static readonly string[] Scopes =
[
    "https://outlook.office.com/IMAP.AccessAsUser.All",
    "https://outlook.office.com/SMTP.Send",
    "offline_access"
];

// after — scopes selected by caller:
public Task<string> GetAccessTokenAsync(AccountModel account, string[] scopes, CancellationToken ct = default);

// IMAP caller passes the IMAP scopes (above)
// Graph caller passes:
//   ["https://graph.microsoft.com/Mail.ReadWrite",
//    "https://graph.microsoft.com/Mail.Send",
//    "https://graph.microsoft.com/MailboxSettings.Read",
//    "offline_access"]
```

The MSAL token cache stores tokens per-scope automatically — no token-cache collision between an account that has both IMAP and Graph scopes.

**App registration:** the existing MSAL `ClientId = "bcdc84f1-d37c-4581-b14a-a01f7b3a1312"` must have `Mail.ReadWrite`, `Mail.Send`, `MailboxSettings.Read` added as delegated permissions in its Azure registration. Maintainer action required before PR 4 lands.

### 9.4 New-mail notifications: polling, not webhooks

IMAP uses IDLE — a persistent TCP connection that the server pushes notifications onto. Graph offers two notification mechanisms:

1. **Webhook subscriptions** — Microsoft POSTs to a URL when mail arrives. Requires a publicly reachable HTTPS URL. **Not viable for a desktop app** without a relay service.

2. **Delta queries** — client polls `GET /me/mailFolders/{id}/messages/delta?$skipToken=…`, server returns only changes since the last token. **Right choice for a desktop app.**

`GraphChangeNotifier` polls every account's INBOX delta endpoint on a 60-second cadence (configurable in `config.ini`). When the delta returns ≥1 new message, it raises `InboxNewMailDetected(accountId)` — the same event consumers already handle.

Polling is slower than IDLE (60s worst case vs near-instant) but adequate for a mail client. If users complain, webhook-via-relay is a future feature.

### 9.5 Throttling

Graph throttles per user (~10,000 requests / 10 min for most endpoints) and returns `429 Too Many Requests` with a `Retry-After` header when exceeded. `GraphMailService` honors `Retry-After`. Background sync respects throttling and surfaces a "Rate-limited, retrying in {N}s" status to the user.

### 9.6 Dependency choice: SDK vs raw HttpClient

Two options for the Graph layer:

| Option | Pros | Cons |
|---|---|---|
| **`Microsoft.Graph` 5.x SDK** | Strongly-typed model objects, fluent request builder, automatic retry/throttling | +~10MB to published exe, brings Azure Identity transitive deps |
| **Raw `HttpClient`** | Minimal dep footprint, full control over wire format | More code to write; we re-implement model classes for messages, folders, attachments |

**Recommendation: raw `HttpClient`.** QuickMail is small (one solo dev, published size matters for accessibility users on metered connections), and the Graph mail surface is narrow — maybe 12 endpoints. The SDK's value is on Graph's huge surface area; we use a sliver. Hand-rolled DTOs are ~200 lines.

This is the v1 decision; the SDK can be adopted later if the dep footprint stops mattering.

### 9.7 Feature gate

Per maintainer request, the Graph backend (and any future user-facing surface that ships before it's broadly ready) sits behind a runtime feature gate. The gate mechanism is **new infrastructure** introduced by this work and intended to be reused for future experimental features.

**Design:**

```csharp
// Models/FeatureFlag.cs
public enum FeatureFlag
{
    GraphBackend,
    // Future flags add an enum value here.
}

// Services/IFeatureGate.cs
public interface IFeatureGate
{
    bool IsEnabled(FeatureFlag flag);
}

// Services/ConfigFeatureGate.cs
// Reads [features] section of config.ini; merges --feature CLI flags from App.xaml.cs.
public class ConfigFeatureGate : IFeatureGate { /* impl */ }
```

**Storage:** new `[features]` section in `%APPDATA%\QuickMail\config.ini`:

```ini
[features]
GraphBackend=true
```

The file is human-editable; `ConfigService` already parses INI, so the addition is small.

**CLI override:** `QuickMail.exe --feature GraphBackend` enables the flag for one launch without writing to disk. Useful for quick dev iteration. Multiple flags: `--feature GraphBackend --feature OtherFlag`. CLI flags take precedence over `config.ini`.

**Defaults:** every flag defaults to `false` until explicitly enabled. The defaults table lives in `ConfigFeatureGate` and is updated by future PRs when a feature graduates. For `FeatureFlag.GraphBackend`, the default stays `false` for the duration of v0.7 and v0.8 — adventurous users opt in by editing `config.ini`.

**Default-flip decision:** changing the in-code default from `false` to `true` is a one-line PR that requires explicit maintainer approval. It is independent of any specific release version — the code can sit dormant across multiple v0.x releases until both contributor and maintainer agree the feature is GA-ready. See §12 question 5.

**What the gate controls (and doesn't):**

| Surface | Gated? |
|---|---|
| "Microsoft 365 / Outlook.com" option in Add Account combo | **Yes** — hidden when gate is off |
| `BackendKind` enum existence | No — internal type, always present |
| `MailServiceRouter` construction | No — cheap; routes IMAP traffic correctly with no Graph accounts present |
| `GraphMailService` instantiation in DI | No — instantiated but never called when no Graph accounts exist |
| Existing Graph-backed accounts (after a flag flip) | No — once an account exists, it remains functional regardless of the flag |
| Graph endpoint network calls | No direct gate — but no Graph accounts means no calls happen |

The single gated surface is the **Add Account combo option**. Everything else is internal plumbing that has no observable behavior absent a Graph account, which can't be created with the gate off.

**Testing:** unit tests inject `StubFeatureGate` with explicit per-test state. CI runs both gate-on and gate-off variants of relevant tests so we never ship a regression in the off path. Documented in dev spec §7.

**Future:** a "Lab / Preview features" tab in the Settings dialog is out of scope for this work but explicitly anticipated. The enum-keyed design makes it a future enhancement without breaking the existing call sites.

### 9.8 Files

**New files:**

| File | Purpose |
|---|---|
| `QuickMail/Models/BackendKind.cs` | Enum: ImapSmtp \| MicrosoftGraph |
| `QuickMail/Models/FeatureFlag.cs` | Enum for feature-gate keys |
| `QuickMail/Services/IFeatureGate.cs` | Feature-gate interface |
| `QuickMail/Services/ConfigFeatureGate.cs` | Config-file + CLI implementation |
| `QuickMail/Services/IMailService.cs` | Renamed `IImapService`, identical surface initially |
| `QuickMail/Services/ImapMailService.cs` | Renamed `ImapService` |
| `QuickMail/Services/MailServiceRouter.cs` | Per-account dispatcher implementing `IMailService` |
| `QuickMail/Services/GraphMailService.cs` | New Graph backend |
| `QuickMail/Services/GraphSmtpService.cs` | New Graph send (via `/me/sendMail`) |
| `QuickMail/Services/IChangeNotifier.cs` | Abstraction over IDLE / delta polling |
| `QuickMail/Services/ImapChangeNotifier.cs` | Extracted from `ImapService.StartIdleWatchers` |
| `QuickMail/Services/GraphChangeNotifier.cs` | Delta polling loop |
| `QuickMail/Services/Graph/*.cs` | DTOs for Graph wire format (Message, MessageBody, MailFolder, FileAttachment, etc.) |

**Modified files (high level):**

| File | Change |
|---|---|
| `QuickMail/Models/AccountModel.cs` | Add `BackendKind`, `TenantId` |
| `QuickMail/Models/MailMessageSummary.cs` | `UniqueId: uint` → `MessageId: string` |
| `QuickMail/Models/AttachmentModel.cs` | `PartSpecifier` → `AttachmentRef` |
| `QuickMail/Models/ConfigModel.cs` | Add `Features` dictionary (parsed `[features]` section) |
| `QuickMail/Services/ConfigService.cs` | Parse `[features]` section into `ConfigModel.Features` |
| `QuickMail/Services/ILocalStoreService.cs` | All `uint uid` → `string messageId` |
| `QuickMail/Services/LocalStoreService.cs` | Schema migration v2: `unique_id INTEGER` → `TEXT`; add `DeltaToken` table |
| `QuickMail/Services/OAuthService.cs` | Accept scopes per call, not hardcoded |
| `QuickMail/Services/SyncService.cs` | Talks to `IMailService` (was `IImapService`) |
| `QuickMail/Services/RuleService.cs` | string IDs |
| `QuickMail/ViewModels/MainViewModel.cs` | string IDs (44 call sites) |
| `QuickMail/ViewModels/AddAccountViewModel.cs` | `BackendKind` property + conditional defaults; consults `IFeatureGate` to populate combo |
| `QuickMail/Views/AddAccountDialog.xaml` | New combo (one-option fallback when gate is off); conditional field visibility |
| `QuickMail/App.xaml.cs` | Parse `--feature` CLI flags; wire `MailServiceRouter` and `ConfigFeatureGate` |
| `QuickMail/QuickMail.csproj` | No new packages if raw HttpClient |
| `QuickMail.Tests/StubServices.cs` | `StubGraphMailService`, `StubFeatureGate` |

---

## 10. Implementation Phases

Sequenced so that each phase is independently mergeable and reversible until the schema migration in Phase 2 lands. After Phase 2, downgrade requires a SQLite restore from backup.

| Phase | Title | Behavior change | Estimated effort (solo, evenings) |
|---|---|---|---|
| 0 | This PM spec + matching dev spec, opened as a draft PR for maintainer review | None | 1 |
| 1 | Interface rename — `IImapService` → `IMailService`, `ImapService` → `ImapMailService`. Pure rename. | None | 1-2 |
| 2 | `uint UniqueId` → `string MessageId`. SQLite schema migration v2. | None (behavioral parity verified by full test suite + manual smoke) | **3-5** (the painful one) |
| 3 | `BackendKind` enum + `MailServiceRouter` + `IFeatureGate` infrastructure + Add Account UI combo (Microsoft 365 option present but gated off by default) | New UI element when gate is on; no functional change when gate is off (default) | 2-3 |
| 4 | `GraphMailService` read path: connect, GetFolders, GetMessageSummaries, GetMessagesSince*, GetMessageDetail, MarkRead | First end-to-end Graph read (only reachable when gate is on) | 4-6 |
| 5 | `GraphSmtpService`: send via `/me/sendMail`. `AppendToSent` is no-op for Graph. | First Graph send | 1-2 |
| 6 | Attachments + folder CRUD + move/copy/delete on Graph | Feature parity for daily use | 3-5 |
| 7 | `GraphChangeNotifier`: delta polling + `IChangeNotifier` abstraction; extract `ImapChangeNotifier` from existing IDLE code | New-mail notifications for Graph accounts | 3-4 |
| 8 | Tests: `GraphMailServiceTests` with `HttpMessageHandler` stub, `MailServiceRouterTests`, `FeatureGateTests`, migration round-trip test | None (coverage) | 2-3 |
| **Future** | One-line PR flipping `FeatureFlag.GraphBackend` default to `true`. Requires explicit maintainer approval and release-notes entry. Independent of any specific release version. | Graph backend becomes generally available | <1 |

**Total: 20-30 evenings of focused work.**

Phases 1-3 ship as v0.7 (no user-visible Graph; gate present but defaulted off). Phases 4-8 ship as v0.8 or later — Graph code is in the binary but unreachable from the UI without the user flipping the gate. The GA flip is a separate PR with no fixed schedule.

---

## 11. Success Metrics

| Metric | Target | Measurement |
|---|---|---|
| **Reach** — users on IMAP-disabled M365 tenants can now connect | Manual verification on a test tenant where `Set-CASMailbox -ImapEnabled $false` is applied | One-time |
| **Functional parity** — every IMAP operation has a working Graph equivalent | 100% of `IMailService` methods covered by `GraphMailService` | Test matrix at PR 6 |
| **No regression** — existing IMAP users see identical behavior | All existing tests pass; `MainViewModel` smoke test on a Gmail account passes pre/post each PR | Per PR |
| **Startup time** — no measurable regression for IMAP-only users | < 5% increase in time-to-first-folder-render with one IMAP account | Manual benchmark on PR 3 and PR 7 |
| **Schema migration safety** — no data loss on first launch of v0.7 | Migration tested on a 500MB mail.db with 100k messages; all rows present after migration | Test in Phase 2 |
| **Graph throttling handled gracefully** — 429 responses don't crash sync | Stress test: fire 200 message-detail requests in 10s, verify backoff and eventual completion | Test in Phase 4 |
| **Accessibility** — zero new a11y bugs in Add Account dialog | Screen-reader pass on the new combo and conditional fields | Manual in Phase 3 |

---

## 12. Open Questions & Risks

### Open questions for the maintainer

Questions 1, 6, 7 resolved in v0.2 per Kelly's Issue #24 response (2026-05-24). Remaining open:

1. ~~Take this on at all?~~ **Resolved (v0.2):** Kelly approved direction; pending detailed doc review.

2. **MSAL app registration:** the existing `ClientId = "bcdc84f1-d37c-4581-b14a-a01f7b3a1312"` is owned by Kelly. Graph scopes (`Mail.ReadWrite`, `Mail.Send`, `MailboxSettings.Read`) must be added in its Azure portal registration. Who does it? Coordinate timing with Phase 4.

3. **Tenant authority:** stay on `common` (personal + work/school)? Or expose a per-account tenant ID field for organizations that require it? **Proposed: stay on `common` for v1.** Per-tenant authority is a follow-up.

4. **Microsoft.Graph SDK vs raw HttpClient:** §9.6 recommends raw HttpClient (~10MB smaller binary, narrower surface). Maintainer override welcomed.

5. **GA flip — when does `FeatureFlag.GraphBackend` default to `true`?** **Proposed (v0.2):** the in-code default stays `false` for the lifetime of v0.7 and v0.8. The flip is a one-line PR opened by the contributor and merged only with explicit maintainer approval. No specific release version is committed; the flip happens when both parties agree the feature is GA-ready based on real-world testing. Until the flip, adventurous users enable it in `config.ini`.

6. ~~EWS / on-prem Exchange?~~ **Resolved (v0.2):** confirmed out of scope.

7. ~~Per-feature spec docs first?~~ **Resolved (v0.1):** confirmed; this PR *is* the spec.

8. **One-way schema migration:** unique_id INTEGER → TEXT is one-way without a SQLite backup. The plan includes a pre-migration backup of `mail.db` to `mail.db.pre-v2`. Comfortable with that, or do we need additional protection (e.g. a settings toggle to defer migration)?

### Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Tenant policy denies the requested Graph scopes for some users | Medium | Affected users cannot use Graph backend | Clear error message routing user to admin; document required scopes in USERGUIDE.md |
| Throttling under heavy initial sync of a large mailbox (50k+ messages) | Medium | First-time sync is slow | Honor `Retry-After`; surface "Rate-limited, retrying" in status bar; throttle initial fetch batch size |
| Schema migration corrupts an existing mail.db | Low | Data loss; users must re-sync | Migration is wrapped in a transaction; pre-migration backup of mail.db → mail.db.pre-v2; recover script documented |
| MSAL token cache collision between IMAP-scope and Graph-scope tokens for the same user | Low | User gets unexpected sign-in prompts | MSAL stores tokens per scope-set; no collision in normal use; explicitly tested |
| 44 call sites in MainViewModel = high blast radius for the UID→string refactor | High | Regressions slip in | Full test suite + manual smoke on Gmail account after every PR in Phase 2; lean on the compiler — `uint` → `string` is a compile-time-enforced change |
| Graph SDK / wire format changes between draft and merge | Low | Need to update DTOs | Pin to Graph v1.0 endpoint (not beta); test against the published OpenAPI spec |
| Webhook expectations from power users | Low | Users want instant notifications | Polling cadence is configurable; webhook-via-relay is a documented future feature |
| Maintainer rejects the proposal | Medium | All work wasted | **Open as a GitHub issue first, draft PR second, code only after thumbs-up** |

---

## Appendix A: Graph endpoint map

Reference for Phase 4-6 implementers. All endpoints are `https://graph.microsoft.com/v1.0/me/...`.

| `IMailService` method | Graph endpoint(s) |
|---|---|
| `ConnectAsync` | `GET /me?$select=id,userPrincipalName` (validates token; no real connection) |
| `GetFoldersAsync` | `GET /mailFolders?$top=100&$select=id,displayName,parentFolderId,totalItemCount,unreadItemCount` (paginated; flattens to existing tree) |
| `GetMessageSummariesAsync` | `GET /mailFolders/{id}/messages?$top={N}&$orderby=receivedDateTime desc&$select=id,subject,from,toRecipients,receivedDateTime,isRead,bodyPreview,hasAttachments` |
| `GetMessagesSinceAsync` | `GET /mailFolders/{id}/messages/delta?$skipToken={token}` (first call: no skipToken) |
| `GetMessagesSinceDateAsync` | `GET /mailFolders/{id}/messages?$filter=receivedDateTime ge {iso}&$top=1000` |
| `GetMessageDetailAsync` | `GET /messages/{id}?$select=id,subject,body,from,toRecipients,ccRecipients,internetMessageId,attachments&$expand=attachments($select=id,name,contentType,size,isInline)` |
| `MarkReadAsync` | `PATCH /messages/{id}` with `{ "isRead": true }` |
| `MoveMessagesAsync` | `POST /messages/{id}/move` with `{ "destinationId": "{folderId}" }` (one call per message; consider batch via `$batch`) |
| `MoveToTrashBatchAsync` | Same as Move, target = Deleted Items folder ID |
| `PermanentlyDeleteBatchAsync` | `DELETE /messages/{id}` |
| `CopyMessagesAsync` | `POST /messages/{id}/copy` |
| `DownloadAttachmentAsync` | `GET /messages/{messageId}/attachments/{attachmentId}/$value` (returns raw bytes) |
| `FetchPreviewsAsync` | No-op for Graph — `bodyPreview` already in summary response |
| `CreateFolderAsync` | `POST /mailFolders/{parentId}/childFolders` (root: `POST /mailFolders`) |
| `DeleteFolderAsync` | `DELETE /mailFolders/{id}` |
| `RenameFolderAsync` | `PATCH /mailFolders/{id}` with `{ "displayName": "..." }` |
| `CopyFolderAsync` | `POST /mailFolders/{id}/copy` |
| `AppendDraftAsync` | `POST /messages` with full draft body; if `replaceUid`: `DELETE` old then `POST` new |
| `AppendToSentAsync` | No-op for Graph — `/sendMail` automatically saves to Sent |
| `EmptyTrashAsync` | `POST /mailFolders/{deletedItemsId}/messages` listing + `DELETE` each, or `POST /mailFolders/{id}/permanentlyDelete` (preview-only at writing) |
| `GetInboxStatusAsync` | `GET /mailFolders/Inbox?$select=totalItemCount,unreadItemCount` |
| `FindDraftsFolderNameAsync` | `GET /mailFolders/Drafts?$select=id` (Graph well-known name) |
| `NoOpAsync` | No-op for Graph (HTTP is stateless) |
| `SendAsync` (SMTP path) | `POST /me/sendMail` with `{ "message": { ... }, "saveToSentItems": true }` (accepts MIME via `Content-Type: text/plain` body with base64-encoded MIME) |

## Appendix B: Scope comparison

| Today (IMAP/OAuth) | Proposed (Graph) |
|---|---|
| `https://outlook.office.com/IMAP.AccessAsUser.All` | `https://graph.microsoft.com/Mail.ReadWrite` |
| `https://outlook.office.com/SMTP.Send` | `https://graph.microsoft.com/Mail.Send` |
| (none) | `https://graph.microsoft.com/MailboxSettings.Read` (used to discover well-known folder IDs) |
| `offline_access` | `offline_access` |

## Appendix C: Schema migration SQL

```sql
-- v2: change unique_id from INTEGER to TEXT
-- Run inside a single transaction. On failure, rollback leaves v1 intact.

BEGIN;

-- MessageSummary
CREATE TABLE MessageSummary_v2 (
    unique_id    TEXT    NOT NULL,
    account_id   TEXT    NOT NULL,
    folder_name  TEXT    NOT NULL,
    from_disp    TEXT    NOT NULL DEFAULT '',
    to_addr      TEXT    NOT NULL DEFAULT '',
    subject      TEXT    NOT NULL DEFAULT '',
    date_ticks   INTEGER NOT NULL,
    is_read      INTEGER NOT NULL DEFAULT 0,
    preview_text TEXT    NOT NULL DEFAULT '',
    is_replied   INTEGER NOT NULL DEFAULT 0,
    is_forwarded INTEGER NOT NULL DEFAULT 0,
    has_attachments INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (unique_id, account_id, folder_name)
);

INSERT INTO MessageSummary_v2
SELECT CAST(unique_id AS TEXT), account_id, folder_name, from_disp, to_addr,
       subject, date_ticks, is_read, preview_text, is_replied, is_forwarded,
       has_attachments
FROM MessageSummary;

DROP TABLE MessageSummary;
ALTER TABLE MessageSummary_v2 RENAME TO MessageSummary;

CREATE INDEX idx_summary_date ON MessageSummary(date_ticks DESC);

-- MessageDetail (parallel migration)
CREATE TABLE MessageDetail_v2 (
    unique_id   TEXT NOT NULL,
    account_id  TEXT NOT NULL,
    folder_name TEXT NOT NULL,
    to_addr     TEXT NOT NULL DEFAULT '',
    cc          TEXT NOT NULL DEFAULT '',
    reply_to    TEXT NOT NULL DEFAULT '',
    plain_body  TEXT NOT NULL DEFAULT '',
    html_body   TEXT NOT NULL DEFAULT '',
    attachments_json TEXT DEFAULT NULL,
    PRIMARY KEY (unique_id, account_id, folder_name)
);

INSERT INTO MessageDetail_v2
SELECT CAST(unique_id AS TEXT), account_id, folder_name, to_addr, cc,
       reply_to, plain_body, html_body, attachments_json
FROM MessageDetail;

DROP TABLE MessageDetail;
ALTER TABLE MessageDetail_v2 RENAME TO MessageDetail;

-- New table for Graph delta tokens
CREATE TABLE DeltaToken (
    account_id   TEXT NOT NULL,
    folder_id    TEXT NOT NULL,
    delta_token  TEXT NOT NULL,
    updated_utc  INTEGER NOT NULL,
    PRIMARY KEY (account_id, folder_id)
);

PRAGMA user_version = 2;

COMMIT;
```

**Pre-migration backup:** `LocalStoreService.Initialize` copies `mail.db` to `mail.db.pre-v2` before opening the connection if `user_version < 2`. Backup is preserved indefinitely; user can manually delete after they're confident the migration is good.

## Appendix D: Comparison to mail-rules feature retrospective

The mail-rules retrospective ([mail-rules-pm-spec.md §Appendix D](../docs/planning/mail-rules-pm-spec.md)) identified four bugs that all lived at component boundaries. The fix was: data-flow diagrams, integration tests, manual test checklists.

This proposal includes:

| Lesson from mail-rules | Where it appears here |
|---|---|
| Data-flow diagram required | §9.2 — single diagram covering both backends |
| Integration test specified | §11 — "no regression" smoke test on a Gmail account between every PR; "schema migration safety" test on a real-sized DB |
| Manual test checklist | Dev spec §6 (forthcoming) will include the per-PR manual smoke list |
| Different agent for spec review vs implementation | This document is the proposal; the maintainer (or an independent agent) reviews; a third agent (or the contributor) implements |
| Existing-data path explicit | §6.3 — schema migration is one-way and explicit; existing IMAP rows become text via CAST |

The same retrospective noted: "**specs describe structure; bugs live in flow.**" Phase 2 (the UID → string migration) is where this proposal is most exposed to that risk — the change is mechanical but touches 12 files and 128 sites. The per-PR smoke test on a real Gmail account is the primary mitigation; the compiler is the secondary.
