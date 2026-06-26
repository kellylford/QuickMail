# Entra (Azure AD) App Registration — Setup & Troubleshooting

QuickMail authenticates to Microsoft (both the Graph backend and IMAP/SMTP-over-OAuth) as a
**public client / desktop application** using MSAL.NET. The client id is hard-coded in
`QuickMail/Services/OAuthService.cs` (`ClientId`), and the authority is `/common`
(work-or-school **and** personal Microsoft accounts).

This document is the source of truth for how that one app registration must be configured. Most
QuickMail sign-in problems are registration problems, not code problems.

---

## 1. Supported account types

**Accounts in any organizational directory and personal Microsoft accounts** (i.e. the `/common`
audience). This is required because QuickMail serves both M365 tenants and consumer Outlook.com
accounts through the same client.

---

## 2. Authentication / Redirect URIs

Under **Authentication**:

- **Platform: "Mobile and desktop applications"** (NOT "Web", NOT "Single-page application").
- **Redirect URI: `http://localhost`** — bare, **no port number**.
  MSAL opens a temporary loopback listener on a *random* port at sign-in time; Azure's loopback
  exception matches any port against a bare `http://localhost`. A fixed port
  (`http://localhost:12345`) will **not** match the random runtime port and breaks the round-trip.
- **Allow public client flows: Yes** (Advanced settings → "Enable the following mobile and desktop
  flows").

**Do not** register:
- `http://localhost` under the **Web** platform — Azure treats Web redirects as confidential-client
  and the desktop loopback flow fails.
- A fixed-port loopback (`http://localhost:NNNN`).
- The legacy `https://login.microsoftonline.com/common/oauth2/nativeclient` redirect as the
  *primary* one when using the system browser — the system browser lands on it and cannot hand the
  auth code back to the desktop app.

The code side pairs with this: `OAuthService` calls `.WithRedirectUri("http://localhost")` and
`.WithUseEmbeddedWebView(false)` (system browser). With the registration above, a successful sign-in
shows a clean "authentication complete — return to the application" page and QuickMail proceeds; no
second prompt, no unreachable-localhost page.

---

## 3. API permissions (delegated, Microsoft Graph + Exchange Online)

Adding a scope to `OAuthService.GraphMailScopes` / `ImapSmtpScopes` in code is **not sufficient** on
its own. For tenants that require admin consent (see §4), each delegated permission must also be
**declared here** or admin consent cannot grant it.

**Microsoft Graph (delegated):**

| Permission | Used for |
| --- | --- |
| `Mail.ReadWrite` | Read/move/flag/delete mail; save drafts (Graph backend) |
| `Mail.Send` | Send mail (Graph backend) |
| `MailboxSettings.ReadWrite` | Server-side Inbox rules (messageRule API). **Superset of `MailboxSettings.Read`** — declare ReadWrite, not Read. |
| `User.Read` | Resolve the signed-in user's address/profile |
| `User.ReadBasic.All` | Resolve other users (recipient display names) |

**Office 365 Exchange Online (delegated)** — for the IMAP/SMTP backend:

| Permission | Used for |
| --- | --- |
| `IMAP.AccessAsUser.All` | IMAP access (XOAUTH2) |
| `SMTP.Send` | SMTP send (XOAUTH2) |

Plus `offline_access` (refresh tokens) — granted implicitly via the scope request.

> When a new scope is added to the code (e.g. `MailboxSettings.ReadWrite` was added for server-side
> rules), it must be added to this list **and** admin consent re-granted in each admin-consent tenant.
> Missing this is the most common cause of a "prompted but cannot consent" dead end (§5).

---

## 4. Admin consent (tenants that disable user consent)

Many M365 tenants disable user self-consent ("admin approval required"). In those tenants a user who
hits an un-consented scope sees a prompt **with no consent button** — only a "needs admin approval"
message. To fix, a tenant administrator grants consent **once, tenant-wide**:

- **Entra admin center → Identity → Applications → Enterprise applications → QuickMail →
  Permissions → "Grant admin consent for &lt;tenant&gt;".**
- Or the one-shot URL (sign in as a tenant admin):
  `https://login.microsoftonline.com/<tenant-id>/adminconsent?client_id=<client-id>`

Admin consent only covers the permissions **declared in §3 at the time it is granted**. After adding
a new scope, the admin must **re-grant** consent — "already shows consented" reflects the *old* set.

---

## 5. Troubleshooting

### "Prompted for consent, but there's no way to consent" (dead-end prompt)
The tenant disables user consent **and** the build is requesting a scope that is not in the
admin-consented set. Almost always this means a scope was added in code but not declared in §3 and/or
admin consent was not re-granted afterward.
**Fix:** declare the scope (§3) → re-grant admin consent (§4). Confirm on the consent screen which
permission lacks prior approval — that's the missing one.

Note: builds requesting only a subset of the consented scopes (e.g. `MailboxSettings.Read` when
`ReadWrite` is consented) sign in silently — so the same account can work on one build and dead-end
on another purely because of which scopes that build requests.

### Sign-in lands on an unreachable `http://localhost` page, then re-prompts
A redirect-URI misconfiguration (§2): the loopback redirect is under the wrong platform, uses a
fixed port, or only the `nativeclient` redirect is registered. Fix per §2 — bare `http://localhost`
under **Mobile and desktop applications**, public client flows enabled.

### Changing scopes forces existing accounts to re-consent
Expected: the consented permission set is part of the cached token's identity. Adding a scope
invalidates silent acquisition until the user (or admin) consents to the new set. Capturing a broad
scope **before** GA — while no production account has consented yet — avoids a re-consent wave; see
`docs/planning/server-rules-pm-dev-spec.md` §4.
