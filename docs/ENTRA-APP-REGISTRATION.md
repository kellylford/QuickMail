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

The code side pairs with this: `OAuthService` calls `.WithRedirectUri("http://localhost")` and signs
in with the **embedded WebView2** window (`.WithUseEmbeddedWebView(true)` + `.WithWindowsEmbeddedBrowserSupport()`,
which needs the `Microsoft.Identity.Client.Desktop` package). Sign-in renders **in-app** and the
window **closes itself on completion**, returning focus to QuickMail — no separate browser tab and no
"authentication complete" page. The `http://localhost` redirect is still required and registered as
above; the embedded view intercepts that navigation internally (no loopback listener is opened).

---

## 3. API permissions (delegated, Microsoft Graph + Exchange Online)

Only the **Graph** backend uses the per-resource **`.default` scope** at sign-in
(`https://graph.microsoft.com/.default`), which asks for **exactly the delegated Graph permissions
declared here** — nothing more, no runtime incremental consent. See
`docs/planning/oauth-default-scope-pm-dev-spec.md`.

The **IMAP/SMTP-over-OAuth** path requests **explicit** scopes (`IMAP.AccessAsUser.All` + `SMTP.Send`),
**not** `.default` — `.default` on `outlook.office.com` is invalid for personal Microsoft accounts and
broke consumer sign-in on the IMAP path entirely (#239). Explicit scopes work for personal and work
accounts alike, and `.default` bought the IMAP path nothing (its resource only needs those two
declared scopes). Both must still be declared here.

> **Exception — personal Microsoft accounts (Outlook.com/MSA).** `.default` is honored only through
> the AAD admin-consent model, which consumer accounts don't have, so their `.default` token comes
> back read-only (delete/move → 403). Personal accounts instead request the **explicit** scopes
> `Mail.ReadWrite` / `Mail.Send` / `User.Read` (`OAuthService.GraphMailScopesPersonal`), so the user
> is prompted to consent to write. The Graph permissions below still need to be declared here for
> AAD; the personal-account path just requests them explicitly rather than via `.default`. See #217.

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

Plus `offline_access` (refresh tokens) — a standard OIDC scope that **MSAL adds automatically** for
the public-client desktop flow, so refresh tokens still issue even though the code now requests only
`.default` (verified live). It is not listed in `OAuthService.cs` and needs no separate portal entry.

> Because the app requests `.default`, adding a permission is **entirely a registration action**:
> declare it in this list **and** re-grant admin consent in each admin-consent tenant. There is no
> code scope list to update and no runtime consent prompt — a permission the code needs but that is
> not declared+granted here surfaces as a feature-level `403`, not a consent dialog (§5). Declaring
> a new scope **before** GA (while no account has consented yet) means the first consent captures it
> and no one is ever re-prompted.

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
The tenant disables user consent **and** at least one **declared** permission (§3) has not been
admin-granted for the tenant. Because QuickMail requests `.default` (the whole declared set), sign-in
requires the full set to be consented — so any un-granted declared permission blocks sign-in, not
just a single feature.
**Fix:** ensure every needed permission is declared (§3) → **grant admin consent for the whole set**
(§4). After adding a new permission, the admin must **re-grant** (an existing grant reflects the
*old* set). With `.default` this is purely a registration/consent action — there is no code scope
list to change.

Note: with `.default`, "same account works on one build but dead-ends on another because of which
scopes the build requests" **no longer happens** — every build requests the same full declared set.
A dead-end is always a registration/consent gap (a declared scope not yet granted), never a
per-build scope difference.

### Server-rules write fails with `403` even though sign-in worked
A residual case under `.default`: the account's cached token predates a newly-declared scope, or the
tenant granted only part of the declared set. **Fix:** confirm `MailboxSettings.ReadWrite` is
declared (§3) and admin-granted (§4), then **sign in again** — a fresh `.default` acquisition
refreshes the token to include it. QuickMail surfaces this as an admin-directed message, not an
in-app re-consent (see `docs/planning/oauth-default-scope-pm-dev-spec.md` §5).

### Sign-in lands on an unreachable `http://localhost` page, then re-prompts
A redirect-URI misconfiguration (§2): the loopback redirect is under the wrong platform, uses a
fixed port, or only the `nativeclient` redirect is registered. Fix per §2 — bare `http://localhost`
under **Mobile and desktop applications**, public client flows enabled.

### Changing scopes forces existing accounts to re-consent
Expected: the consented permission set is part of the cached token's identity. Adding a scope
invalidates silent acquisition until the user (or admin) consents to the new set. Capturing a broad
scope **before** GA — while no production account has consented yet — avoids a re-consent wave; see
`docs/planning/server-rules-pm-dev-spec.md` §4.
