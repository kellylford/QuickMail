# OAuth `.default` Scope Migration — PM/Dev Spec

**Status:** Proposed (2026-07-08)
**Depends on:** Graph backend (merged), embedded-WebView sign-in (merged, #139), Entra app
registration (`docs/ENTRA-APP-REGISTRATION.md`).
**Related:** #202 (silent identity rebind — part a merged/pending), #203 (sign-in window timeout).

---

## 1. Summary

QuickMail currently requests an **explicit list** of delegated scopes at sign-in — a different
list per backend (`GraphMailScopes` for Graph, `ImapSmtpScopes` for IMAP/SMTP). This spec changes
the request to the **per-resource `.default` scope**:

- Graph accounts request `https://graph.microsoft.com/.default`
- IMAP/SMTP-over-OAuth accounts request `https://outlook.office.com/.default`

`.default` asks for **exactly the delegated permissions declared on the app registration for that
resource** — no more, no less. The app registration (`docs/ENTRA-APP-REGISTRATION.md` §3) becomes
the single source of truth for the permission set.

This is a **pure `.default`** decision: there is no runtime incremental/step-up consent for any
Graph permission, including the server-rules write scope.

---

## 2. Problem being solved

With explicit dynamic scopes, the set the app *requests* can drift from the set the app
registration *declares*. In a tenant that requires **admin consent** (the common enterprise M365
configuration, where user self-consent is disabled), that drift is fatal:

1. The user signs in; the app requests a scope that isn't in the admin-consented set.
2. The user sees a "needs admin approval" prompt **with no consent button**.
3. An admin grants consent **via the portal** — which covers the permissions **declared on the app
   registration**, not a dynamically-requested-but-undeclared scope.
4. The user retries and hits the **same prompt** — the grant didn't cover the requested scope.

This is the observed "admin granted consent but the user is still prompted" loop. Its root cause is
always a mismatch between requested scopes and declared-and-granted scopes (most recently:
`MailboxSettings.ReadWrite`, added in code for server rules, not declared on the registration).

`.default` removes the mismatch **by construction**: the request set is defined by the declaration,
so "what the app asks for" and "what admin consent grants" are the same set.

---

## 3. Decision & rationale (pure `.default`)

**Decision:** request `<resource>/.default` for every OAuth backend. Do **not** keep a runtime
incremental-consent fallback for the elevated server-rules scope.

**Why pure, not a `.default`-core + step-up-rules hybrid:**

- **In-app self-heal is illusory for the users who hit the wall.** In an admin-consent tenant, a
  non-admin user cannot grant a missing scope no matter how it's requested — an "in-app
  Reauthorize" just re-throws the same admin-approval wall. Since most users are **not** tenant
  admins, the hybrid's step-up self-heal helps only the minority in user-consent-permissive tenants
  (and personal Microsoft accounts). It is not worth the extra token-management complexity.
- **Consistent with a decision already made.** `server-rules-pm-dev-spec.md` §4 already chose to
  **capture `MailboxSettings.ReadWrite` up front, pre-GA** (fold it into every user's first
  consent) and explicitly labeled the on-`403` reauthorize path as *belt-and-suspenders* behind
  that. Pure `.default` formalizes the up-front-capture decision and drops a fallback that was both
  secondary and non-functional for the admin-consent majority.
- **One source of truth.** The permission set lives in exactly one place — the app registration —
  instead of being split between code scope arrays and the portal declaration that must be kept in
  sync by hand.

**Costs we accept:**

1. **A new permission can no longer be introduced by a code change alone.** Adding any Graph
   permission requires **declaring it on the app registration and re-granting consent** before the
   token will carry it. There is no runtime prompt to fall back on.
2. **Consent is all-or-nothing per resource.** Requesting `graph.microsoft.com/.default` means a
   tenant consents to the **entire declared Graph permission set** — including
   `MailboxSettings.ReadWrite` — to use the Graph backend at all. The write scope rides in the
   single sign-in consent rather than a separate later prompt; that is exactly the up-front-capture
   decision from `server-rules-pm-dev-spec.md` §4, now made structural. A tenant unwilling to grant
   the full set cannot use the Graph backend — though it can still use the **IMAP backend**, which
   is a separate resource (`outlook.office.com`) with its own, smaller `.default` (IMAP + SMTP
   only, no mailbox-settings write).

Because the write scope is part of the sign-in consent, the elevated permission is normally gated at
**sign-in**, not at feature use — so the "mail works but rules fail" state is a **narrow residual**,
not the common path (see §5).

---

## 4. Code changes

**`QuickMail/Services/OAuthService.cs`** — the per-call scope overload
(`GetAccessTokenAsync(account, string[] scopes, ct)`) and `DefaultScopesFor(account)` **stay**.
What changes is the *value* they resolve to:

```csharp
// Per-resource .default. Kept as named constants for readability; each is a single entry.
public static readonly string[] GraphMailScopes   = ["https://graph.microsoft.com/.default"];
public static readonly string[] ImapSmtpScopes    = ["https://outlook.office.com/.default"];

private static string[] DefaultScopesFor(AccountModel account)
    => account.BackendKind == BackendKind.MicrosoftGraph ? GraphMailScopes : ImapSmtpScopes;
```

Notes:

- **`.default` is per-resource.** You cannot mix `.default` with other scopes in one request, and
  you cannot combine two resources. Graph and IMAP each request their own resource's `.default`;
  they are independent consent surfaces. This maps cleanly onto the existing per-backend selection.
- `offline_access` is implied by the framework for public-client flows and does not need to appear
  alongside `.default` (MSAL requests refresh tokens for the confidential/public desktop flow
  regardless). Verify refresh-token issuance during the live test.
- The MSAL token cache still keys tokens per requested scope-set, so a user who has both an IMAP and
  a Graph account holds two independent cache entries (`outlook.office.com/.default` vs
  `graph.microsoft.com/.default`) — no collision.
- The interactive-vs-silent logic, embedded-WebView configuration, and redirect URI are unchanged.

---

## 5. Where consent is gated, and the `403` residual

**Primary gate is at sign-in.** Because `.default` requests the full declared set, the elevated
`MailboxSettings.ReadWrite` permission is consented (or refused) as part of the **sign-in** consent,
not lazily at feature use. In an admin-consent tenant the admin grants the full declared set once
(`docs/ENTRA-APP-REGISTRATION.md` §4); until then, **sign-in itself** is gated — the same gate as
core mail — and shows the tenant's "needs admin approval" screen (Entra doc §5). So in steady state
there is *no* feature-level `403`: either the account is fully consented (and rules work) or it
can't sign in.

**The `403` residual.** A `403`/insufficient-scope can still occur transiently — e.g. a scope
declared on the registration *after* an account's token was already cached (the cached token predates
the declaration), or a tenant that granted only part of the declared set. Handle it defensively:

- The Graph service maps `403`/insufficient-scope to a typed exception
  (`ServerRuleConsentRequiredException` for the rules path), as today.
- The View surfaces an **actionable, admin-directed** message — never an in-app "Reauthorize" that
  cannot succeed in an admin-consent tenant. Example: *"QuickMail can't change server rules because
  your organization hasn't granted it permission. Ask your administrator to grant it, then sign in
  again."* A fresh interactive `.default` sign-in **after** the grant refreshes the token to include
  the now-granted scope — no special in-app consent flow needed.
- Announce as a **Hint** (respects `AnnounceHints`); never a silent catch (CLAUDE.md).

---

## 6. Impact on existing specs

- **`graph-backend-dev-spec.md` §6.8** and **`graph-backend-pm-spec.md` §9.3 / Appendix B** — the
  per-call scope machinery they describe is retained, but the requested value changes from an
  explicit scope *list* to `<resource>/.default`. Those sections carry a superseding pointer to this
  doc.
- **`server-rules-pm-dev-spec.md` §4 / Path E / §6.2 / §15** — the "graceful in-app re-consent"
  (Path E "Reauthorize?") is replaced by the admin-directed `403` message above. The up-front
  pre-GA capture in §4 decision (1) stands, restated as *declare on the app registration*.
- **`docs/ENTRA-APP-REGISTRATION.md`** — the declared permission list (§3) is now the *complete
  definition* of what the app can request; §5's "dead-end prompt" symptom shifts from a blocked
  sign-in to a feature-level `403`.

---

## 7. Keyboard walkthrough

Ordinary sign-in is **UI-unchanged** — the user still chooses the backend, enters their address,
and signs in through the embedded window. (In an admin-consent tenant, sign-in is gated on the admin
having consented to the full declared set — that "needs admin approval" screen is the tenant's, not
QuickMail's, and is unchanged by this work.) The only new QuickMail-drawn path is the residual
admin-directed `403` (§5):

1. User opens **Server Rules** and Saves a create/edit. The account's admin has not granted the
   mailbox-settings write permission.
2. The save fails with `403`. QuickMail does **not** close the window or blank the list. It shows
   the admin-directed message and announces (Hint): *"QuickMail can't change server rules because
   your organization hasn't granted it permission. Ask your administrator to grant it, then sign in
   again."*
3. Focus stays on the editor. The user can Escape back to the list (which still displays existing
   rules — reading works under the granted subset).
4. After an admin grants consent tenant-wide, the user signs in again; `.default` now carries the
   write permission and the save succeeds. Announce (Result): *"Rule created."*

---

## 8. Infrastructure changes

- **`OAuthService.GraphMailScopes` / `ImapSmtpScopes`** — values change to per-resource `.default`;
  the overload and `DefaultScopesFor` signatures are unchanged.
- **No CommandRegistry, F6-ring, or `AutomationProperties.Name` changes.**
- **`AccessibilityHelper.Announce`** — the server-rules `403` path announces a **Hint** (was a
  Status "Reauthorized"). No new categories.
- **App registration (Kelly, registration owner):** every delegated permission the app needs must
  be declared for its resource (`docs/ENTRA-APP-REGISTRATION.md` §3), including
  `MailboxSettings.ReadWrite`, **before** GA so the first consent captures the full set. Confirm no
  tenant admin-consent policy blocks any declared scope.

---

## 9. Out of scope

- **Runtime incremental / step-up consent** — deliberately not implemented (§3). No in-app
  "Reauthorize" self-heal for missing Graph permissions.
- **Programmatic admin-consent initiation from inside the app** — QuickMail directs the user to
  their admin; it does not drive the `/adminconsent` flow itself. (A future in-band admin-consent
  action is tracked separately under #202 part b and is not part of this change.)
- **IMAP password accounts** — no OAuth, no consent surface; untouched.
- **Changing the app registration's account-type or redirect configuration** — unchanged (see the
  Entra doc §1–§2).

---

## 10. Open questions

1. **`offline_access` with `.default`** — confirm during live test that refresh tokens are still
   issued for the public-client desktop flow when only `<resource>/.default` is requested. If not,
   append `offline_access` to the request array (MSAL permits `.default` + `offline_access`).
2. **Directory search scope** (`User.ReadBasic.All`) — must be declared on the registration for
   `.default` to include it; verify recipient-name resolution still works post-migration.
