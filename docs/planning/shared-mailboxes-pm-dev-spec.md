# Shared Mailboxes (M365 / Exchange) — PM + Dev Specification

**Issue:** [#31](https://github.com/kellylford/QuickMail/issues/31)
**Status:** Proposed — **parked behind the Microsoft Graph backend work.** This spec is submitted for review now so the design is agreed; implementation does not start until the Graph backend PRs (dev spec PRs 4–8) and the rest of the in-flight roadmap are complete.

---

## Table of Contents

- [1. Executive Summary](#1-executive-summary)
- [2. User Problem & Opportunity](#2-user-problem--opportunity)
- [3. How It Works (the three mechanisms)](#3-how-it-works-the-three-mechanisms)
  - [3.1 Access — existing IMAP/OAuth stack, no Graph](#31-access--existing-imapoauth-stack-no-graph)
  - [3.2 Auto-detection — Exchange Autodiscover](#32-auto-detection--exchange-autodiscover)
  - [3.3 Generic IMAP — RFC 2342 NAMESPACE](#33-generic-imap--rfc-2342-namespace)
  - [3.4 Why not Graph](#34-why-not-graph)
- [4. Scope](#4-scope)
- [5. Data Model & Storage](#5-data-model--storage)
  - [5.1 Representing a shared mailbox — two approaches](#51-representing-a-shared-mailbox--two-approaches)
  - [5.2 Recommended: linked-account model](#52-recommended-linked-account-model)
  - [5.3 SQLite impact](#53-sqlite-impact)
- [6. Authentication, Scopes & the Microsoft Consent Model](#6-authentication-scopes--the-microsoft-consent-model)
  - [6.1 The scopes](#61-the-scopes)
  - [6.2 Who owns the app registration — declaration vs. consent](#62-who-owns-the-app-registration--declaration-vs-consent)
  - [6.3 Friction for end users — and how we handle it](#63-friction-for-end-users--and-how-we-handle-it)
- [7. Autodiscover Client](#7-autodiscover-client)
- [8. IMAP Connection for a Shared Mailbox](#8-imap-connection-for-a-shared-mailbox)
- [9. User Experience](#9-user-experience)
- [10. Accessibility](#10-accessibility)
- [11. PR Breakdown](#11-pr-breakdown)
- [12. Edge Cases & Error Handling](#12-edge-cases--error-handling)
- [13. Testing](#13-testing)
- [14. Open Questions for Review](#14-open-questions-for-review)

---

## 1. Executive Summary

Add support for **Microsoft 365 / Exchange Online shared mailboxes** (e.g. `support@company.com`, `info@company.com`) so they appear alongside the signed-in user's own account — and **auto-detect** which shared mailboxes the user already has access to, rather than making them type addresses by hand.

The headline finding from #31: **shared-mailbox *access* needs no new technology** — it works on QuickMail's existing IMAP + OAuth2 stack today. Only **auto-detection** requires new plumbing (Exchange Autodiscover and one additional OAuth scope). A manual "add by address" fallback covers the cases auto-detection can't reach.

This is **independent of the Graph backend** (it neither needs nor is helped by Graph — see §3.4), so it can ship on the IMAP stack whenever the roadmap reaches it.

## 2. User Problem & Opportunity

Shared mailboxes are ubiquitous in workplaces — support/info/sales aliases that a team monitors collectively. Today a QuickMail user with Full Access to `support@company.com` has no way to open it. Outlook surfaces such mailboxes automatically (via automapping); a screen-reader-friendly client that does the same — **and announces them clearly** — is a strong differentiator versus both Outlook (accessibility friction) and Thunderbird (no automapping).

**Target persona:** a blind/low-vision worker on a corporate M365 tenant who is a delegate on one or more team mailboxes and wants them to "just appear" after sign-in.

## 3. How It Works (the three mechanisms)

### 3.1 Access — existing IMAP/OAuth stack, no Graph

An O365 shared mailbox is opened over IMAP with OAuth2 by authenticating with the **delegate user's own access token** while putting the **shared mailbox's SMTP address** in the XOAUTH2 `user=` field:

```csharp
// token = the signed-in delegate user's access token (already obtained today)
new SaslMechanismOAuth2(sharedMailboxAddress, accessToken)
```

The scope QuickMail already requests — `IMAP.AccessAsUser.All` — is sufficient for access, **provided the user has Full Access** to the mailbox. No new scope is needed for access itself. (refs: [MailKit #1486](https://github.com/jstedfast/MailKit/issues/1486), [Microsoft: OAuth for IMAP/POP/SMTP](https://learn.microsoft.com/en-us/exchange/client-developer/legacy-protocols/how-to-authenticate-an-imap-pop-smtp-application-by-using-oauth)).

Practically: connect to `outlook.office365.com:993`, SSL, authenticate with `SaslMechanismOAuth2(sharedAddress, token)`, then enumerate folders normally. Sending uses the analogous SMTP XOAUTH2 with the shared address as `user=`, so replies come *from* the shared mailbox.

### 3.2 Auto-detection — Exchange Autodiscover

Discover the user's shared mailboxes by calling Exchange **Autodiscover** and parsing the `AlternativeMailbox` entries (display name + SMTP address) — the same automapping data Outlook consumes.

- **Requires the `EWS.AccessAsUser.All` OAuth scope** — *for discovery only*; access itself (§3.1) needs no new scope. **This also requires the Azure app registration behind the shared client ID to grant `EWS.AccessAsUser.All`.** That is an external prerequisite owned by the app-registration maintainer (Kelly) — see §14.
- Autodiscover endpoint (SOAP): `POST https://autodiscover-s.outlook.com/autodiscover/autodiscover.svc` with the user's OAuth bearer token, requesting the user settings that include the automapped `AlternativeMailbox` collection.
- Mailboxes granted with **automapping disabled** are *not* returned by Autodiscover — hence the manual fallback (§9).

### 3.3 Generic IMAP — RFC 2342 NAMESPACE

Non-O365 IMAP servers expose shared / other-user mailboxes via the **RFC 2342 NAMESPACE** extension: the `SharedNamespaces` and `OtherUsers` namespaces. MailKit surfaces these on `ImapClient.PersonalNamespaces` / `SharedNamespaces` / `OtherUsers`. Less reliable and less common than Autodiscover, but worth enumerating where the server advertises NAMESPACE — it gives the same feature to non-Microsoft IMAP accounts at low marginal cost.

### 3.4 Why not Graph

The **Graph API cannot return auto-mapped mailboxes** — confirmed in the issue. Autodiscover is the only path to the automapping list, even after the planned Graph backend lands. So this feature deliberately sits on the IMAP stack and does **not** wait on, or benefit from, the Graph work. (Graph accounts could later gain shared-mailbox *access* via Graph's `/users/{shared}/messages` with delegated permissions, but detection still routes through Autodiscover.)

## 4. Scope

**In scope**
- Auto-detect O365 shared mailboxes for OAuth2 accounts via Autodiscover.
- Open and read shared mailboxes over IMAP (delegate token + shared address in XOAUTH2 `user=`).
- Send *from* a shared mailbox over SMTP XOAUTH2.
- Manual "Add shared mailbox by address" fallback.
- Enumerate RFC 2342 `SharedNamespaces` / `OtherUsers` for generic IMAP accounts where advertised.
- Folder-tree UI surfacing each shared mailbox, with screen-reader announcements.

**Out of scope (future)**
- Permission management (granting/revoking Full Access) — read/use only.
- Shared-mailbox calendar/contacts.
- POP3 shared access.
- Per-shared-mailbox rules (rules continue to operate per owning account; revisit if requested).

## 5. Data Model & Storage

### 5.1 Representing a shared mailbox — two approaches

**Approach A — mailbox dimension on the cache key (issue's suggestion).** Add a `mailbox` column to the SQLite PK `(message_id, account_id, folder_name)` → `(message_id, account_id, mailbox, folder_name)`, and nest shared mailboxes under the parent account in the folder tree. Requires a schema migration (v3 → v4) and threading a `mailbox` argument through `IMailService`/`ILocalStoreService`/sync — a wide change, echoing PR 2.

**Approach B — linked-account model (recommended).** Model each shared mailbox as its own account-like entity with its **own `Guid` Id** but `AuthLinkedAccountId` pointing at the parent for token acquisition. The existing `(message_id, account_id, folder_name)` key then separates mailboxes for free (each has a distinct `account_id`), and the entire per-account pipeline — folder tree grouping, sync loop, `MailServiceRouter` registration, IMAP connection pool — is reused unchanged. **No schema migration.**

### 5.2 Recommended: linked-account model

```csharp
// AccountModel additions:
public bool IsShared { get; set; }              // true for a discovered/added shared mailbox
public Guid? ParentAccountId { get; set; }      // the delegate account whose token authorizes access
public string SharedAddress { get; set; } = ""; // SMTP address placed in the XOAUTH2 user= field
```

- A shared mailbox is persisted in `accounts.json` like any account but flagged `IsShared` and linked to its parent. It holds **no credentials of its own**; `OAuthService` acquires the token for `ParentAccountId` and the IMAP/SMTP layers pass `SharedAddress` as the XOAUTH2 username.
- `ImapMailService` already pools connections by `accountId`; a shared mailbox gets its own pool keyed by its own Id, connecting with `SaslMechanismOAuth2(SharedAddress, parentToken)`.
- `MailServiceRouter.RegisterAccount` registers shared mailboxes to the IMAP backend exactly like primary accounts.

**Trade-off to confirm in review:** Approach B shows shared mailboxes in the account list as first-class entries (grouped/indented under the parent in the folder tree). Approach A nests them more tightly but is a far larger change. Recommendation: **B**, for the dramatically smaller blast radius and reuse of proven machinery. (This is the main open design question — §14.)

### 5.3 SQLite impact

- **Approach B: none** — no schema change; shared mailboxes are just more `account_id`s.
- Approach A (if chosen): schema v3 → v4 rebuild-and-rename migration adding `mailbox` to both tables' PK, plus a `mailbox` parameter across the store/service interfaces. (The v2 migration in `LocalStoreService` is the template.)

## 6. Authentication, Scopes & the Microsoft Consent Model

### 6.1 The scopes

`OAuthService` requests a single static `Scopes` array. Add the discovery scope:

```csharp
private static readonly string[] Scopes =
[
    "https://outlook.office.com/IMAP.AccessAsUser.All",
    "https://outlook.office.com/SMTP.Send",
    "https://outlook.office365.com/EWS.AccessAsUser.All", // NEW — Autodiscover only
    "offline_access",
];
```

- **Consent impact:** adding a scope changes the consent screen. Existing OAuth users will see a re-consent prompt the next time their refresh token expires (scope lists are global per app registration). Worth a one-line release note.
- Access (§3.1) does not depend on this scope; if discovery is unavailable (scope absent / tenant blocks EWS), the feature degrades gracefully to the manual fallback.

### 6.2 Who owns the app registration — declaration vs. consent

This is the most misunderstood part, so stating it plainly: **the app registration is QuickMail's own identity as an application — there is exactly one, it lives in the maintainer's tenant, and it is NOT per-mailbox or per-user-tenant.** App registrations are also a **Microsoft-only** concept; they have nothing to do with Gmail, Fastmail, or any non-Microsoft IMAP server (those use password/app-password, or in Gmail's case a separate Google OAuth client). Everything in this section applies **only** to the Microsoft OAuth account type.

Two distinct things happen in two different places:

| | Where | Who | What |
|---|---|---|---|
| **Declaration** | QuickMail's app registration (maintainer's tenant) | Maintainer, once | "QuickMail *may request* these scopes" |
| **Consent / grant** | The signing-in user's home tenant (e.g. an `icanbrew.net` O365 tenant) | The user, at sign-in | "I allow QuickMail to access my mail" |

When a user signs in, MSAL runs the OAuth flow against **their** home tenant using QuickMail's client ID; Entra auto-creates a *service principal* (a local stub of QuickMail) in that tenant on first consent. **End users never create or edit an app registration** — they click "allow." So adding `EWS.AccessAsUser.All` is a **one-time, app-wide** action by the maintainer (declaration); each user's tenant merely consents. Testing against a personal O365 mailbox therefore needs no registration work by the tester — just sign in and consent.

**Prerequisite to verify:** QuickMail's registration must be configured **multi-tenant + personal Microsoft accounts** ("accounts in any organizational directory and personal Microsoft accounts"). If it were single-tenant, only the maintainer's own tenant could connect at all — a blocker for every other user. This already governs the existing IMAP-OAuth path, so it is presumably set correctly; worth confirming once.

### 6.3 Friction for end users — and how we handle it

Because users never touch a registration, the only real friction is tenant policy, not user capability:

1. **Tenants that require admin consent.** `EWS.AccessAsUser.All` and the Graph `Mail.*` scopes are user-consentable by default, but many organizations disable user consent for third-party apps, so an IT admin must approve QuickMail once for the whole org. We cannot bypass a tenant's security policy, but we **handle it gracefully**: detect the "admin approval required" error (e.g. `AADSTS65001` / `AADSTS90094`), show a clear message, and offer an **admin-consent URL** the user can forward to IT. One admin approval then covers every user in the org.
2. **"Unverified publisher" warning** (and tenants that block unverified apps). Resolved by **publisher verification** on the maintainer's registration — a one-time maintainer action; afterwards the consent prompt reads "QuickMail (verified)".
3. **Hostile / locked-down tenant that blocks the shared app entirely.** Escape hatch: an optional **per-account custom client ID** (a power user pastes their own org's app-registration client ID). This stays a rare advanced fallback — already deferred in the Graph PM spec — not the default path.

**Net effect:** personal accounts and ordinary tenants → sign in and consent, nothing else; locked-down org → one admin approval (we surface the prompt + link); truly hostile tenant → optional own-client-ID override. No end user is ever asked to create an app registration.

## 7. Autodiscover Client

New `Services/AutodiscoverClient.cs` (raw `HttpClient`, consistent with the Graph dev spec's SDK-avoidance rationale):

- Input: parent `AccountModel` + delegate access token (with EWS scope).
- `POST` the SOAP `GetUserSettings` request to `https://autodiscover-s.outlook.com/autodiscover/autodiscover.svc`, `Authorization: Bearer {token}`, requesting `AlternativeMailbox` (and the EXternal/EWS URL settings as needed).
- Parse the `AlternativeMailbox` entries → list of `(DisplayName, SmtpAddress, Type)`; keep `Type == "Delegate"`/shared entries.
- Honor `Retry-After` on 429; treat 401/403 (no EWS consent) as "discovery unavailable" → fall back to manual, do not error the account.
- Returns an empty list (not an exception) when nothing is auto-mapped.

## 8. IMAP Connection for a Shared Mailbox

- Reuse `ImapMailService`'s per-account pool, keyed by the shared mailbox's own Id.
- On connect: acquire the token for `ParentAccountId` via `OAuthService`; build `new SaslMechanismOAuth2(account.SharedAddress, token)`; connect to the parent's IMAP host (`outlook.office365.com:993`, SSL).
- Folder enumeration, sync, message open, mark-read, move, etc. all flow through the existing IMAP code unchanged — the only difference is the SASL username.
- **SMTP:** sending from a shared mailbox uses the same delegate token with `SaslMechanismOAuth2(sharedAddress, token)` so the `From` is the shared address. Confirm the tenant permits "Send As"/"Send on Behalf"; surface a clear error if the server rejects the From.

## 9. User Experience

- **Folder tree:** each shared mailbox appears as its own top-level node (Approach B) labelled with its display name + address, with its folders nested beneath — visually and in the accessibility tree grouped after the owning account.
- **Auto-detection timing:** run Autodiscover after a successful OAuth connect; newly discovered mailboxes are added to `accounts.json` (flagged `IsShared`) and the folder tree, with a status announcement ("2 shared mailboxes detected: Support, Info").
- **Manual fallback:** an "Add shared mailbox…" action (Account Manager / folder-tree context menu) prompts for an SMTP address and the parent account, then connects directly — covering automapping-disabled grants.
- **Removal:** a shared mailbox can be removed from the view without affecting the parent account.
- **No duplicate auth:** shared mailboxes never prompt for their own password/sign-in; they piggyback the parent's token.

## 10. Accessibility

- Each shared mailbox node carries a full `AutomationProperties.Name` ("Support, shared mailbox, 3 unread"), mirroring the account-node pattern.
- Detection results announced via `AccessibilityHelper.Announce(..., AnnouncementCategory.Status)`; the manual-add outcome as `Result`.
- The "Add shared mailbox" dialog follows the existing dialog accessibility rules (labelled fields via `LabeledBy`, focus on open, no instructional text baked into `AutomationProperties.Name`).
- No screen-reader product names in any UI text or docs.

## 11. PR Breakdown

Sequenced for small, independently reviewable PRs (estimates assume the linked-account model, Approach B):

1. **Scope + model (1 evening):** add `EWS.AccessAsUser.All`; add `IsShared`/`ParentAccountId`/`SharedAddress` to `AccountModel`; serialization defaults keep existing accounts unchanged.
2. **Shared-mailbox IMAP access (2–3 evenings):** `ImapMailService` connects with `SaslMechanismOAuth2(SharedAddress, parentToken)` when `IsShared`; `OAuthService` token lookup follows `ParentAccountId`. Manual "add by address" path end-to-end (no detection yet).
3. **Autodiscover client + detection (3–4 evenings):** `AutodiscoverClient`; post-connect detection; merge results into the account list; graceful degradation.
4. **Folder-tree UI + announcements (2–3 evenings):** render shared mailboxes, manual-add affordance, removal, accessibility.
5. **Generic IMAP NAMESPACE (1–2 evenings):** enumerate RFC 2342 `SharedNamespaces`/`OtherUsers` where advertised.
6. **Tests + smoke (1–2 evenings):** Autodiscover parsing (canned SOAP responses via `HttpMessageHandler` stub), model serialization, router registration of shared mailboxes; manual smoke on a real tenant.

(If Approach A is chosen instead, insert a schema-migration PR after #1 and widen #2 — adds ~3–5 evenings.)

## 12. Edge Cases & Error Handling

| Situation | Behavior |
|---|---|
| User lacks Full Access to a discovered mailbox | IMAP connect fails; mark the shared node errored with a clear message; don't block the parent account |
| Automapping disabled for a grant | Not returned by Autodiscover; user adds it via manual fallback |
| EWS scope not consented / tenant blocks EWS | Discovery returns "unavailable"; feature silently falls back to manual; no account error |
| Shared address already added (auto + manual) | De-dupe by `SharedAddress` (case-insensitive) |
| Parent account removed | Cascade-remove its shared mailboxes from the view |
| Token refresh while a shared pool is open | Existing per-account refresh applies; shared pool uses the parent's refreshed token |
| Non-Exchange IMAP without NAMESPACE | No shared mailboxes offered; manual add still available |
| "Send As" denied by tenant | Surface the SMTP rejection clearly; the mailbox remains readable |

## 13. Testing

- **Unit:** `AutodiscoverClient` parsing against canned SOAP `AlternativeMailbox` payloads via an `HttpMessageHandler` stub (mirrors the planned `GraphMailServiceTests` pattern); `AccountModel` serialization round-trip with the new fields; de-dup logic; `MailServiceRouter` registration of `IsShared` accounts.
- **No live credentials in tests** — all network paths stubbed, per `StubServices` conventions.
- **Manual smoke (per the dev-spec checklist style):** on a real tenant with a Full-Access shared mailbox — sign in, confirm auto-detection, open the shared mailbox, read/mark-read/move a message, reply (verify `From` = shared address), add a second mailbox manually, remove one.

## 14. Open Questions for Review

1. **Azure app registration (blocking):** are you able to add `EWS.AccessAsUser.All` (Office 365 Exchange Online, delegated) to the shared client ID's app registration, and is admin consent needed for your tenant? Discovery can't be built end-to-end without it (access works regardless).
2. **Model approach:** OK to go with the **linked-account model (Approach B)** — shared mailboxes as first-class, parent-linked accounts, no schema migration — rather than the cache-key `mailbox` dimension from the issue? This is the single biggest design decision.
3. **Send-As policy:** is sending *from* a shared mailbox in scope for v1, or read-only first with send as a fast-follow?
4. **Sequencing:** confirmed parked behind the Graph backend PRs; implement after the rest of the roadmap is done.
