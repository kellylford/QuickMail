# Entra (Azure AD) App Registration — Setup & Troubleshooting

QuickMail authenticates to Microsoft (both the Graph backend and IMAP/SMTP-over-OAuth) as a
**public client / desktop application** using MSAL.NET. The client id is hard-coded in
`QuickMail/Services/OAuthService.cs` (`ClientId`), and the authority is `/common`
(work-or-school **and** personal Microsoft accounts).

This document is the source of truth for how that one app registration must be configured. Most
QuickMail sign-in problems are registration problems, not code problems.

> **AI agents:** to audit or change the live registration, go straight to **§7 (Agent runbook)** —
> it has the tenant/object ids, the sign-in recipe that actually works on this machine, and the
> known dead ends. §§1–3 define the target state; §7 is how you get a session to enforce it.

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
| `Calendars.ReadWrite` | Calendar sync — read/create/update/delete events (full-calendar spec M4). Added 2026-07-16 via the §7 device-code runbook (scope id `1ec239c2-d7c9-4623-a91a-a9775856bb36`). Code side: `OAuthService.GraphCalendarScopes` (explicit scope for personal accounts; work/school gets it via `.default`). |
| `Contacts.ReadWrite` | **Forward declaration — no feature consumes it yet.** Reserved for server/personal contact sync into `ContactService`. Added 2026-07-21 via the §7 device-code runbook (scope id `d56682ec-c09e-4743-aaf4-1a3aac4caa21`). No code side yet: work/school accounts request it automatically via `.default`; personal accounts won't request it until it is added to `OAuthService.GraphMailScopesPersonal`. |
| `People.Read` | **Forward declaration — no feature consumes it yet.** Reserved for relevance-ranked recipient autocomplete (People API). Added 2026-07-21 via the §7 device-code runbook (scope id `ba47897c-39ec-4d83-8086-ee8256fa737d`). No code side yet; same `.default`-vs-personal-scope note as `Contacts.ReadWrite`. |
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
> not declared+granted here surfaces as a feature-level `403`, not a consent dialog (§6). Declaring
> a new scope **before** GA (while no account has consented yet) means the first consent captures it
> and no one is ever re-prompted.

**Live registration reconciled (2026-07-14):** the actual registration (tenant
`793722c2-ea04-49a3-8183-af139211b24f`, app object `c9ca6292-3799-4b87-8e88-48ea7d04e692`) was
audited via Graph and had drifted from this doc: it declared `Mail.Read` + granular scopes
(`Mail-Advanced.*`, `MailboxFolder.*`, `People.Read`) instead of `Mail.ReadWrite`; it still had
`MailboxSettings.Read` (the registration half of PR #137 had never been applied); the entire
Exchange Online resource (IMAP/SMTP) was missing; and "Allow public client flows" was unset. All
of it was corrected via `PATCH /applications/{id}` to match the tables above exactly, plus
`isFallbackPublicClient: true`. Note: the Exchange Online service principal is not instantiated
in the publishing tenant (no Exchange license there), so portal/CLI views of the EXO permissions
may show bare GUIDs (`652390e4-…` = IMAP.AccessAsUser.All, `258f6531-…` = SMTP.Send) instead of
names — this is cosmetic. Because the declared set changed, accounts that consented before this
date will be re-prompted on their next interactive sign-in.

**Forward scopes added (2026-07-21):** `Contacts.ReadWrite` and `People.Read` were added via the §7
device-code runbook (`PATCH /applications/{id}`, whole-set replace) as deliberate forward
declarations — no code path consumes either yet (see the §3 table rows). This makes the full
declared Graph set nine scopes (the six functional ones + `offline_access` + `Calendars.ReadWrite`
+ these two). Note: `People.Read` had been present as *drift* at the 2026-07-14 reconciliation and
was removed then; its reappearance here is intentional, not a regression. As with any set change,
already-consented work/school accounts (which use `.default`) get one re-consent prompt on next
interactive sign-in; personal accounts are unaffected until the scopes are added to
`OAuthService.GraphMailScopesPersonal`.

**Code audit (2026-07-14):** this list was checked against every Graph, IMAP, and SMTP call in
the codebase. It is **complete** — no code path needs a permission that is missing here. Two
entries are deliberate forward declarations for features that are specced but not yet implemented
(the "declare before GA" strategy above): `MailboxSettings.ReadWrite` (server-side rules —
`RuleService` is currently 100% local, no `messageRule` call exists) and `User.ReadBasic.All`
(directory recipient lookup — `ContactService` is currently 100% local, no `/users` or `/people`
call exists). No POP3 or EWS code exists, so `POP.AccessAsUser.All` and `EWS.AccessAsUser.All`
are correctly absent. Re-run this audit when a feature adds a new Graph endpoint category.

---

## 4. Admin consent (tenants that disable user consent)

Many M365 tenants disable user self-consent ("admin approval required"). In those tenants a user who
hits an un-consented scope sees a prompt **with no consent button** — only a "needs admin approval"
message. To fix, a tenant administrator grants consent **once, tenant-wide**:

- **Entra admin center → Entra ID → Enterprise apps → QuickMail → Security → Permissions →
  "Grant admin consent for &lt;tenant&gt;".**
- Or the one-shot URL (sign in as a tenant admin):
  `https://login.microsoftonline.com/<tenant-id>/adminconsent?client_id=<client-id>`
  (`<tenant-id>` may also be a verified domain name, or the literal `organizations` to consent in
  the signing admin's home tenant.)

**Which admin can grant it:** QuickMail declares only **delegated** permissions (no application
permissions / app roles), so tenant-wide admin consent does **not** require Global Administrator.
Any of **Cloud Application Administrator**, **Application Administrator**, or **AI Administrator**
can grant the full set. (Privileged Role Administrator is only needed for Microsoft Graph
*application* permissions, which QuickMail has none of.) A custom directory role holding the
consent-grant permissions also works — see §5.3.

Admin consent only covers the permissions **declared in §3 at the time it is granted**. After adding
a new scope, the admin must **re-grant** consent — "already shows consented" reflects the *old* set.

Consent can also be granted programmatically (no portal round-trip) — see §5.2. Tenants can
additionally enable the **admin consent workflow** (Entra ID → Enterprise apps → Consent and
permissions → Admin consent settings), which turns the dead-end "needs admin approval" prompt into
a "Request approval" flow routed to designated reviewers.

---

## 5. Managing the registration — direct links, CLI, delegation

The registration lives in one **publishing tenant** (the tenant that owns the app). Everything in
§§1–3 is edited there; §4 happens separately in **each customer tenant** that signs in with
admin-consent policies.

### 5.1 Direct portal URLs

Entra admin center deep links for this registration (client id
`bcdc84f1-d37c-4581-b14a-a01f7b3a1312`) — sign in with an account in the publishing tenant:

- **Overview:**
  `https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/Overview/appId/bcdc84f1-d37c-4581-b14a-a01f7b3a1312`
- **Authentication (§2):**
  `https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/Authentication/appId/bcdc84f1-d37c-4581-b14a-a01f7b3a1312`
- **API permissions (§3):**
  `https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/CallAnApi/appId/bcdc84f1-d37c-4581-b14a-a01f7b3a1312`
- **App registrations list:** `https://entra.microsoft.com/#view/Microsoft_AAD_IAM/ActiveDirectoryMenuBlade/~/RegisteredApps`
  (or the stable shortlink `https://aka.ms/appregistrations`).

If your account is in multiple tenants, force the right one with the directory switcher or by
prefixing the tenant: `https://entra.microsoft.com/<tenant-domain-or-id>#view/…`. The `#view/…`
fragments are the portal's internal routing, not a documented contract — if one stops working,
navigate manually: **entra.microsoft.com → Entra ID → App registrations → QuickMail**. The admin
*consent* URL (§4) is different: it is a documented endpoint and is run in the customer tenant,
not the publishing tenant.

### 5.2 CLI / scripted management

Yes — everything in this doc can be managed without the portal. Two supported tools (both wrap the
same Microsoft Graph `applications` API):

**Azure CLI** (`az`) — sign in to the publishing tenant first (`az login --tenant <tenant>`):

```powershell
$clientId = "bcdc84f1-d37c-4581-b14a-a01f7b3a1312"
$graph = "00000003-0000-0000-c000-000000000000"    # Microsoft Graph resource appId (well-known)
$exo   = "00000002-0000-0ff1-ce00-000000000000"    # Office 365 Exchange Online resource appId (well-known)

# Inspect the registration (declared permissions = requiredResourceAccess)
az ad app show --id $clientId

# Declare a new delegated Graph permission. Permission GUIDs vary per scope —
# resolve them from the resource service principal instead of hardcoding:
$scopeId = az ad sp show --id $graph --query "oauth2PermissionScopes[?value=='Mail.ReadWrite'].id" -o tsv
az ad app permission add --id $clientId --api $graph --api-permissions "$scopeId=Scope"
# Same pattern for Exchange Online scopes (IMAP.AccessAsUser.All, SMTP.Send) against $exo.

# §2 settings: bare-localhost public-client redirect + "Allow public client flows"
az ad app update --id $clientId --public-client-redirect-uris "http://localhost" --is-fallback-public-client true

# Grant tenant-wide admin consent for the whole declared set (run in the CONSENTING tenant,
# signed in as a role from §4):
az ad app permission admin-consent --id $clientId
```

**Microsoft Graph PowerShell** (`Microsoft.Graph` module) — the registration:
`Connect-MgGraph -Scopes "Application.ReadWrite.All"`, then `Get-MgApplication -Filter "appId eq
'$clientId'"` / `Update-MgApplication` (edit `RequiredResourceAccess`, `PublicClient`,
`IsFallbackPublicClient`). Tenant-wide consent without the portal: `New-MgOauth2PermissionGrant`
with `ConsentType = "AllPrincipals"` (or raw Graph `POST /oauth2PermissionGrants`) — note
programmatic grants take effect immediately with no confirmation screen.

Do **not** build scripts on the standalone **Microsoft Graph CLI (`mgc`)** — Microsoft announced
its retirement (announced 2025-08-29; shutdown 2026-08-28). Azure CLI and Graph PowerShell are the
supported paths. The old `AzureAD`/`MSOnline` PowerShell modules are likewise deprecated.

### 5.3 Delegating management

Yes, on both sides, without handing anyone Global Administrator:

**Publishing tenant — editing the registration (§§1–3):**

- **Owners** (least privilege, this app only): App registration → **Owners** → add user. An owner
  can edit everything in this doc for this one app — redirect URIs, declared permissions, secrets —
  with no directory role at all. This is the right grant for a co-maintainer.
- **Application Administrator** / **Cloud Application Administrator**: manage *all* app
  registrations and enterprise apps in the tenant (Cloud App Admin = same minus app-proxy). Broader
  than needed for one app; use for an IT admin role, not a single collaborator.
- **Application Developer**: lets someone *create* registrations when the tenant's default
  "users can register applications" is switched off; they become owner of what they create.
- **Custom roles** can scope specific app-management permissions to this single registration.
- Caution from Microsoft's own docs: anyone who can edit the registration can add credentials to it
  and authenticate *as* the app — treat owner/admin grants as sensitive.

**Customer tenants — granting admin consent (§4):** Cloud Application Administrator, Application
Administrator, or AI Administrator suffices (delegated-only app; see §4). Tenants that want an even
narrower grant can build a custom role with the `managePermissionGrantsForAll` consent permissions,
or enable the admin consent workflow so end users route requests to designated reviewers.

---

## 6. Troubleshooting

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

---

## 7. Agent runbook — scripted access to this registration

This section is the recipe for an AI agent (or a human in a terminal) to audit or modify the
registration. It encodes everything learned doing this on 2026-07-14, including the dead ends.
Point the agent here and say what change is needed.

### 7.1 Key identifiers

| What | Value |
| --- | --- |
| Publishing tenant | `793722c2-ea04-49a3-8183-af139211b24f` |
| Sign-in account | `kelly@kellford.com` |
| Client (app) id | `bcdc84f1-d37c-4581-b14a-a01f7b3a1312` |
| Application **object** id (PATCH target) | `c9ca6292-3799-4b87-8e88-48ea7d04e692` |
| Graph resource appId | `00000003-0000-0000-c000-000000000000` |
| Exchange Online resource appId | `00000002-0000-0ff1-ce00-000000000000` |

**Decoy tenants — do not use:** `7d527b20-…` (theideaplace.net) and `727309b0-…` (kellford.com's
domain-resolved default) both look plausible and are wrong. The registration lives only in
`793722c2-…`. If an app lookup returns empty, you are in the wrong tenant — stop and re-check
before concluding the app is gone.

### 7.2 What NOT to use (verified failures on this machine)

- **`Connect-MgGraph`** (Microsoft.Graph.Authentication 2.38.1 on Windows PowerShell 5.1) fails
  with *"InteractiveBrowserCredential… A window handle must be configured"* headless, and
  *"An error occurred when writing to a listener"* with `-UseDeviceCode` — even in a visible
  console. Do not spend time debugging it; go straight to the raw REST flow below.
- **`mgc` (Microsoft Graph CLI)** — retiring 2026-08-28 (§5.2).
- **`az`** — not installed here (fine to use if that changes; commands in §5.2).
- PowerShell 7 (`pwsh`) is not installed; everything below runs on Windows PowerShell 5.1.

### 7.3 Sign-in: raw REST device-code flow (the path that works)

Uses the pre-consented public client **"Microsoft Graph Command Line Tools"**
(`14d82eec-204b-4c2f-b7e8-296a70dab67e`). Kelly consented to it in the tenant on 2026-07-14 with
`Application.ReadWrite.All`, so sign-in normally shows **no consent page** — just code + account.

```powershell
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$tenant   = '793722c2-ea04-49a3-8183-af139211b24f'
$clientId = '14d82eec-204b-4c2f-b7e8-296a70dab67e'
$scope    = 'https://graph.microsoft.com/Application.ReadWrite.All'

$dc = Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$tenant/oauth2/v2.0/devicecode" `
    -ContentType 'application/x-www-form-urlencoded' -Body @{ client_id = $clientId; scope = $scope }
"Give the user this code: $($dc.user_code)  (enter at https://login.microsoft.com/device)"

$deadline = (Get-Date).AddSeconds($dc.expires_in)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds $dc.interval
    try {
        $tok = Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$tenant/oauth2/v2.0/token" `
            -ContentType 'application/x-www-form-urlencoded' -Body @{
                grant_type = 'urn:ietf:params:oauth:grant-type:device_code'
                client_id = $clientId; device_code = $dc.device_code }
        break   # $tok.access_token is live (~60 min)
    } catch {
        $err = $null; $body = $_.ErrorDetails.Message
        if ($body) { try { $err = ($body | ConvertFrom-Json).error } catch {} }
        if ($err -eq 'authorization_pending' -or $null -eq $err) { continue }
        if ($err -eq 'slow_down') { Start-Sleep -Seconds 5; continue }
        throw "Device flow failed: $err"
    }
}
$headers = @{ Authorization = "Bearer $($tok.access_token)"; 'Content-Type' = 'application/json' }
```

Agent-specific gotchas learned the hard way:
- **PS 5.1 error parsing:** read the poll error from `$_.ErrorDetails.Message` (as above). Reading
  `$_.Exception.Response.GetResponseStream()` returns an already-drained stream → empty error →
  the loop misreads `authorization_pending` as failure and aborts in seconds.
- **Driving the code-entry page in an embedded browser:** the login page ignores programmatic
  `value` injection (its view-model listens for key events). Click the field by accessibility
  **ref**, then send **real keystrokes**. Coordinate-based clicks are unreliable there
  (screenshots can be 2× scaled); ref-based clicks are not. The user must perform any
  password/passkey step themselves; entering the device code and email is safe for the agent.
- If the token must survive across separate PowerShell processes, DPAPI-protect it:
  `ConvertFrom-SecureString (ConvertTo-SecureString $tok.access_token -AsPlainText -Force) | Out-File token.dpapi`
  and reverse with `ConvertTo-SecureString` + Marshal on the way back in.
- The token carries only `Application.ReadWrite.All` — `/organization`, `/me`, mail, etc. will 403.
  Identify the session by decoding the JWT payload (claims `tid`, `upn`), not by calling Graph.

### 7.4 Audit the registration

```powershell
$app = (Invoke-RestMethod -Headers $headers `
    "https://graph.microsoft.com/v1.0/applications?`$filter=appId eq 'bcdc84f1-d37c-4581-b14a-a01f7b3a1312'").value[0]
$app.signInAudience            # expect: AzureADandPersonalMicrosoftAccount     (§1)
$app.isFallbackPublicClient    # expect: True                                    (§2)
$app.publicClient.redirectUris # expect: exactly 'http://localhost'             (§2)
$app.web.redirectUris          # expect: empty                                   (§2)
$app.requiredResourceAccess | ConvertTo-Json -Depth 5   # compare against §3
```

To resolve permission GUIDs → names, read `oauth2PermissionScopes` from the resource's service
principal (`/servicePrincipals?$filter=appId eq '<resourceAppId>'`). **Exception:** the Exchange
Online SP does **not exist in this tenant** (no Exchange license), so its two GUIDs resolve to
nothing locally and the portal shows them bare. That is expected, not drift:
`652390e4-393a-48de-9484-05f9b1212954` = `IMAP.AccessAsUser.All`,
`258f6531-6087-4cc4-bb90-092c5fb3ed3f` = `SMTP.Send`.

### 7.5 Modify the registration

`PATCH https://graph.microsoft.com/v1.0/applications/c9ca6292-3799-4b87-8e88-48ea7d04e692` with a
JSON body containing only the properties to change. **`requiredResourceAccess` is replaced whole**,
not merged — always send the complete §3 set (all Graph scopes + both EXO scopes), never just the
addition. Resolve Graph scope GUIDs live from the Graph SP; use the two hardcoded EXO GUIDs above.
Delegated permissions use `"type": "Scope"` (application permissions would be `"Role"` — QuickMail
has none and should stay that way).

After any change: re-run the §7.4 audit to verify, update §3 of this doc if the permission set
changed, and remember every already-consented account gets **one** re-consent prompt (§6).

### 7.6 Boundaries for agents

- The tenant is effectively single-user (Kelly, via personal MSA). Adding owners/users requires
  guest invitation flows that have not worked here; don't burn time on it without her direction.
- Access-control changes (owners, role assignments) and any credential entry are the **user's**
  actions — prepare commands, let her run them / type passwords herself.
- Granting admin consent or accepting OAuth consent screens needs her explicit go-ahead each time.
