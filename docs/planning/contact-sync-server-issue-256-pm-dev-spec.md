# Server Contact Sync & Prior Recipients — PM + Dev Spec (Issue #256)

> **Status:** Draft for Kelly's review (Session 1).
> **Issue:** [#256 — Add support for syncing contacts from the server to the address book and sending emails to prior recipients](https://github.com/kellylford/QuickMail/issues/256)
> **Scope note:** This spec is about pulling contacts **from the mail provider** (Gmail, Outlook/M365) into QuickMail. The **local address book already exists** — this feature feeds it from the server; it does not build it from scratch.

---

## Section 1: Executive Summary

QuickMail already has a working local address book (`ContactService`, `contacts.json`, the Address Book window, groups, and Grab Addresses) and compose autocomplete already reads from it. What it **cannot** do today is learn about the people you already know from your mail provider: your Gmail/Outlook contacts and the people you've previously emailed never appear until you manually add them. This spec adds **one-way sync (server → local)** for Microsoft Graph and Google accounts, plus surfacing of **prior recipients** (Graph `/me/people`, Google "other contacts"). Synced people flow into the existing Address Book and the existing To/Cc autocomplete with **zero new UI in compose** — the plumbing already reads through `IContactService`. Writing changes **back** to the server and iCloud/CardDAV support are explicitly deferred to v2 (§4.2, §13.2) — this recommendation is the main thing to confirm before implementation.

---

## Section 2: User Problem & Opportunity

### 2.1 Current state (verified against code)

| Surface | Today | Pain | Who feels it |
|---|---|---|---|
| Address Book (`AddressBookWindow`, `ContactService`) | Contacts exist only if the user typed them in, ran Grab Addresses, or picked them in compose (`UpsertContactAsync` call sites) | The user's real contact list — hundreds of people already in Gmail/Outlook — is invisible | Every user with an existing provider account |
| Compose To/Cc autocomplete (`ComposeWindow.xaml.cs:277` — `SearchContactsAsync` + `SearchGroupsAsync`) | Already works, but **only** over locally-known contacts/groups | You can't autocomplete someone you emailed last week unless QuickMail happened to capture them | Everyone composing mail |
| Prior recipients | No concept exists. Graph `/me/people` and Google `otherContacts` are never queried | The single most useful autocomplete source (people you've corresponded with) is absent | Everyone composing mail |
| Cross-device consistency | `contacts.json` is local to the profile | A contact added on another device/mail app never appears here | Multi-device users |

**Verified facts used below:**
- `IContactService` / `ContactService` back a flat `contacts.json`; the in-memory `_contactsCache` is what both the Address Book and compose autocomplete read (`ContactService.cs:60` `SearchContactsAsync`).
- `ContactModel` is `{ Id, DisplayName, EmailAddress, LastUsedTicks }` — **single email per contact**, no source/provenance field (`Models/ContactModel.cs`).
- Microsoft Graph infrastructure exists: `GraphClient` with `GetAllPagesAsync` + `@odata.nextLink` paging and per-account token acquisition (`Services/Graph/GraphClient.cs`).
- Google OAuth infrastructure exists: `IGoogleOAuthService.GetAccessTokenAsync(username)` (`Services/IGoogleOAuthService.cs`). There is **no** Google People API client yet.
- OAuth scopes are declared centrally in `OAuthService` (`ImapSmtpScopes`, `GraphMailScopes`, `GraphMailScopesPersonal`, `DefaultScopesFor`). Adding contact scopes means users **re-consent** (§13.1).
- `SyncService` already runs account sync and raises progress events (`Services/SyncService.cs`) — the natural hook for periodic contact sync.
- iCloud and plain IMAP/SMTP accounts (`BackendKind.ImapSmtp` with `AuthType.Password`) have **no** contact API; iCloud contacts are CardDAV, for which there is no infrastructure.

### 2.2 Target personas

1. **Returning Gmail power user** — has 400 contacts in Google. Wants them all searchable in QuickMail's To field on day one without re-typing. Uses the feature passively: sync runs, names autocomplete.
2. **Outlook/M365 professional** — relies on prior recipients ("I emailed Dana last month, just start typing Dana"). Wants `/me/people` relevance ranking, not just saved contacts.
3. **Screen reader user (Kelly's baseline)** — needs the autocomplete to announce when suggestions appear and how many, and needs a clear, non-chatty sync status. The autocomplete already announces (`ComposeWindow.xaml.cs:294`); this feature must not degrade that.
4. **Privacy-conscious user** — wants sync **off by default** or at least clearly controllable, and does not want prior-recipient harvesting they didn't ask for.
5. **Multi-account user** — has both a Gmail and an Outlook account. Wants both contact sets, deduplicated, without the same person appearing three times in autocomplete.

### 2.3 Why now

- The provider auth stacks (Graph client, Google OAuth) are already in place — this is the first feature that reuses them for *non-mail* data, which validates the investment.
- The local address book and compose autocomplete just matured (groups, edit-mode redesign in `address-book-contacts-pm-dev-spec.md`). Sync is the missing input side.
- Autocomplete already reads through `IContactService`, so server contacts light up existing surfaces with no compose-side work.

---

## Section 3: Design Principles

1. **Feed the existing store; don't fork it.** Synced contacts land in the same `IContactService` the Address Book and autocomplete already read. No parallel "server contacts" panel in v1.
2. **Read-only in v1. The server is the source of truth for synced entries; the user's local additions are never overwritten and never (yet) pushed up.** This eliminates the entire class of two-way-sync conflict/delete-propagation bugs for the first release.
3. **Never clobber user edits or lose local-only contacts.** A re-sync replaces the *server-owned* set for an account; purely-local contacts are untouched.
4. **Off by default, per-account, and visible.** No contact scope is requested and no data is pulled until the user opts in. Opting in triggers a clearly-explained re-consent.
5. **Announcements respect the existing category system.** Sync progress is `Status`; sync results are `Result`; no forced announcements. The autocomplete's existing "N results" announcement is the model.
6. **Degrade silently for accounts that can't sync.** IMAP/password and iCloud accounts simply don't offer the toggle — no errors, no empty panels.

---

## Section 4: Feature Scope & Acceptance Criteria

### 4.1 In scope (v1)

| Feature | Setting / Shortcut | Default | Notes |
|---|---|---|---|
| One-way contact sync (server → local) | Per-account `SyncContacts` bool | **Off** | Enabling triggers scope re-consent |
| Microsoft Graph source | — | — | `/me/contacts` (saved) + `/me/people` (prior recipients / relevance) |
| Google source | — | — | People API `people/me/connections` (saved) + `otherContacts` (prior recipients) |
| Contacts appear in Address Book | (existing window) | — | Synced entries listed alongside local; marked read-only for edit (§5.1) |
| Contacts appear in compose autocomplete | (existing) | — | Free — `SearchContactsAsync` already backs it |
| Manual "Sync Contacts Now" | `CommandRegistry` id `contacts.syncNow`, category **Contacts**, no default key | — | In Command Palette + Address Book |
| Automatic sync | on connect + every N hours via `SyncService` | 12h | Cheap; contacts change rarely |
| Dedup across sources | — | — | By email (case-insensitive); one autocomplete row per address |
| Sync status announcement | `AnnouncementCategory.Status` | respects `AnnounceStatus` | e.g. "Contacts synced, 412 total" |

### 4.2 Explicitly out of scope (v1) — **deferred to v2**

- **Write-back (create/edit/delete → server).** The issue explicitly asks for this. Recommendation: defer. Rationale in §13.2. In v1, the user can still create/edit **local** contacts exactly as today; those simply stay local.
- **iCloud / CardDAV / generic IMAP contact sync.** No infrastructure exists; CardDAV + app-specific-password handling is a separate, large effort.
- **Rich contact fields** — phone numbers, multiple email addresses per person, photos, org/title. `ContactModel` is single-email; expanding it is its own spec.
- **Contact groups/labels from the server** (Google labels, Outlook categories). Local groups are unaffected; server groups are not imported.
- **Conflict resolution UI** — not needed while sync is one-way.
- **Selective/partial sync** (choose which folders/labels). All contacts + all prior recipients, or nothing.

If Kelly wants write-back in v1, that changes the data-model, conflict, and delete-propagation design substantially — flag it now (§13.2), not during implementation.

### 4.3 Acceptance criteria

- A Gmail user who enables sync sees their Google contacts and recently-emailed people in the To autocomplete within one sync cycle, deduplicated.
- A Graph user likewise, sourced from `/me/contacts` + `/me/people`.
- Local-only contacts and Grab Addresses entries are never deleted or renamed by a sync.
- Disabling sync for an account removes that account's server-owned contacts on the next sync/toggle, leaving local contacts intact.
- Screen reader announces suggestion count in compose (unchanged behavior) and a concise sync status when `AnnounceStatus` is on.
- IMAP/password accounts show no sync toggle and are unaffected.

---

## Section 5: Architecture & Technical Decisions

### 5.1 Key architectural decisions

**Decision 1 — Store synced contacts in the existing `contacts.json`, discriminated by provenance fields on `ContactModel`.**

Add to `ContactModel`:
```csharp
public ContactSource Source   { get; set; } = ContactSource.Local; // Local | MicrosoftGraph | Google
public string? SourceId       { get; set; }   // provider resource id (Graph id / People resourceName)
public Guid?   OwnerAccountId { get; set; }   // which account synced it; null for Local
public bool    IsPriorRecipient { get; set; } // from /me/people or otherContacts, not a saved contact
```
- **Alternatives:** (a) separate `synced-contacts-<accountId>.json` files merged at read time; (b) SQLite table. 
- **Rationale:** The single-file approach means the Address Book and autocomplete get server contacts with **zero query changes** — they already read `_contactsCache`. `Source`/`OwnerAccountId` let a sync replace exactly the `(account, source)` slice while leaving `Local` rows alone. `SourceId` is what future write-back needs and what makes re-sync an update-in-place rather than a duplicate. Separate files (a) would require merge logic in every reader; SQLite (b) is over-engineering for a few thousand rows and diverges from the established JSON pattern.
- **Trade-off:** `contacts.json` grows (hundreds–low thousands of rows). Acceptable; it's loaded once into memory. `LocalStoreService` (SQLite) is **not** involved, so `--online` mode is unaffected (§5.2).

**Decision 2 — Read-only-for-edit on synced rows in the Address Book.**

A synced contact (`Source != Local`) is shown but its Add/Edit/Delete affordances behave as: Edit is disabled (or, later, "copy to local"), Delete removes it from the local cache only until the next sync re-adds it. Simplest v1: **synced rows are read-only**; the Edit button is disabled and Delete is hidden for them. This keeps the one-way contract honest and avoids the "I deleted it and it came back" confusion — surfaced instead by disabling the action.

**Decision 3 — New `IContactSyncService`, not more methods on `IContactService`.**

`IContactService` owns local persistence and search; provider I/O is a different concern (HTTP, OAuth, paging, provider DTOs). Add:
```csharp
public interface IContactSyncService
{
    Task<ContactSyncResult> SyncAccountAsync(AccountModel account, CancellationToken ct = default);
    Task<ContactSyncResult> SyncAllAsync(CancellationToken ct = default);
    Task RemoveAccountContactsAsync(Guid accountId, CancellationToken ct = default); // on disable / account removal
}
public record ContactSyncResult(int Fetched, int Added, int Updated, int Removed, string? Error);
```
Implementations: `GraphContactSource` and `GoogleContactSource` (both implement a small internal `IProviderContactSource` returning a normalized `List<ContactModel>` with `Source`/`SourceId` set). `ContactSyncService` diffs the provider set against `_contactsCache` for that `(account, source)` and calls new bulk methods on `IContactService`:
```csharp
// IContactService additions:
Task ReplaceSyncedContactsAsync(Guid accountId, ContactSource source, IReadOnlyList<ContactModel> serverContacts);
Task RemoveSyncedContactsAsync(Guid accountId); // all sources for the account
```
`ReplaceSyncedContactsAsync` does the merge under `_loadLock`: upsert-by-`SourceId`, delete server rows for that `(account, source)` no longer present, **never touch `Source == Local` rows or rows owned by other accounts.**

**Decision 4 — Wire sync into `SyncService`'s existing cadence, gated by config.**

`SyncService` already loops accounts on connect and periodically. Add a contact-sync pass that, for each account with `SyncContacts == true`, calls `IContactSyncService.SyncAccountAsync`. Manual `contacts.syncNow` calls `SyncAllAsync` directly. Contact sync runs **after** mail sync and is best-effort — a contact-sync failure never blocks mail (separate try/catch scope, per the standard fetch pattern in `docs/ARCHITECTURE.md`).

**Decision 5 — OAuth scopes: incremental, opt-in, per provider.**

- **Graph:** add `Contacts.Read` and `People.Read` (read-only). Requested only when the user enables sync, via the existing `GetAccessTokenAsync(account, scopes, ct)` overload (`IOAuthService`). Personal vs work/school scope selection follows the existing `DefaultScopesFor` pattern.
- **Google:** add `https://www.googleapis.com/auth/contacts.readonly` and `https://www.googleapis.com/auth/contacts.other.readonly`. **These are Google "sensitive" scopes** — see §13.1 for the verification/consent-screen implication. `IGoogleOAuthService` currently returns a token for the mail scopes only; it needs an overload to request the contacts scopes (or the contacts scopes added to the consent set, triggering re-consent).

**Decision 6 — Dedup at read time in `SearchContactsAsync` / `LoadAllContactsAsync`, by email.**

When the same email exists from multiple sources (Local + Graph + Google), collapse to one row for autocomplete/list, preferring: `Local` (user-curated name) > saved server contact > prior recipient. `LastUsedTicks` is taken as the max across duplicates so relevance ordering is preserved. This keeps one person = one row in the To field.

### 5.2 Runtime mode compatibility

| Mode | LocalStoreService used? | This feature calls `LocalStore…`? | Effect |
|---|---|---|---|
| Normal | ✓ | **No** — contacts live in `contacts.json` via `ContactService` | Works |
| `--online` | ✗ (SQLite mail cache off) | **No** | Works — contact sync is independent of the mail SQLite cache |
| `--profileDir <path>` | n/a | reads/writes `contacts.json` in that profile | Works (alternate path) |

Contact sync touches only `ContactService` (JSON) and provider HTTP. It is safe in every mode.

### 5.3 Code reuse and duplication risks

- **Paging & token handling:** `GraphClient.GetAllPagesAsync` already handles `@odata.nextLink` + token refresh + retry. `GraphContactSource` must reuse it, not re-implement HTTP. Google has no equivalent client yet — build a minimal `GooglePeopleClient` (paged GET with `pageToken`) mirroring `GraphClient`'s shape so both sources feel the same.
- **DTO mapping:** Graph contact/person → `ContactModel` and Google Person → `ContactModel` are the two normalization points. Keep them in the respective `*ContactSource` classes; do not scatter provider field knowledge into `ContactSyncService`.
- **Announcement text:** reuse the existing `Status`/`Result` announcement pattern from `ComposeWindow`/sync; don't invent a new mechanism.

### 5.4 Shared component audit (mandatory)

| Component | File | Other consumers | Change needed | Risk / mitigation |
|---|---|---|---|---|
| `ContactModel` | `Models/ContactModel.cs` | `ContactService`, `AddressBookViewModel`, `ComposeWindow` autocomplete, `GrabAddressesDialog`, tests | Add `Source`, `SourceId`, `OwnerAccountId`, `IsPriorRecipient` (all defaulted) | New fields default to `Local`/null → existing local flows unchanged. `[JsonIgnore] Display` untouched. Verify `contacts.json` round-trips old files (missing fields deserialize to defaults). |
| `IContactService` / `ContactService` | `Services/IContactService.cs`, `ContactService.cs` | Address Book VM, compose autocomplete, Grab Addresses | Add `ReplaceSyncedContactsAsync`, `RemoveSyncedContactsAsync`; make `SearchContactsAsync`/`LoadAllContactsAsync` dedup by email | Existing `Upsert/Update/Delete/Search` signatures unchanged. Dedup must not drop local-only rows. Covered by new + existing `ContactService` tests. |
| `AddressBookViewModel` | `ViewModels/AddressBookViewModel.cs` | Address Book window only | Disable Edit/Delete for `Source != Local`; add "Sync Contacts Now" command | Local add/edit flow (per `address-book-contacts-pm-dev-spec.md`) must still pass its tests. |
| `SyncService` | `Services/SyncService.cs` | `MainViewModel` background sync | Add best-effort contact-sync pass gated by config | Contact-sync failure must not affect mail sync — separate catch scope. Verify mail sync unaffected when contact sync throws. |
| `OAuthService` | `Services/OAuthService.cs` | mail connect (IMAP/Graph) | Add `Contacts.Read`/`People.Read` to a new scope constant; request only on opt-in | Must **not** add contact scopes to the default mail scope set, or every existing user re-consents on next connect. New scope set requested lazily. |
| `IGoogleOAuthService` / `GoogleOAuthService` | `Services/IGoogleOAuthService.cs`, `GoogleOAuthService.cs` | Gmail mail connect | Add overload / scope set for contacts scopes | Same lazy-consent rule. Google sensitive-scope verification risk (§13.1). |
| `AccountModel` | `Models/AccountModel.cs` | accounts.json, account editor | Add `bool SyncContacts` (persisted, default false) | New defaulted field → old accounts.json round-trips. |
| `StubServices.cs` | `QuickMail.Tests/StubServices.cs` | all VM/service tests | Add `StubContactSyncService` + new `IContactService` method stubs | Tests won't compile without stubs; add no-op implementations. |
| `App.xaml.cs` DI root | `App.xaml.cs` | startup wiring | Register `IContactSyncService` + provider sources; dispose in `OnExit` if `IDisposable` | Follow existing manual-DI ordering (after `ContactService`, needs `IOAuthService`/`IGoogleOAuthService`/`GraphClient`). |

If any component here has no other consumers it's noted as "only" above; every modified shared type is listed.

---

## Section 6: Keyboard Walkthrough (Mandatory)

### Path A: Enable sync for a Google account (opt-in + first sync)

1. User opens the account editor for their Gmail account, Tabs to the new **"Sync contacts from this account"** checkbox. **Expected:** screen reader announces "Sync contacts from this account, checkbox, not checked."
2. User presses Space to check it. **Expected:** checkbox announces "checked." A `Hint` announcement fires once: "Enabling this will ask Google for permission to read your contacts." (respects `AnnounceHints`).
3. User activates **Save**. **Expected:** a browser sign-in/consent window opens (existing Google OAuth flow) requesting the contacts scopes. Focus is in the browser per existing OAuth behavior.
4. User consents; the browser closes. **Expected:** focus returns to the account editor / account list per existing OAuth return behavior. If `AnnounceStatus` on: "Syncing contacts…" then "Contacts synced, 412 total" (`Status`).
5. **Edge — user cancels consent:** **Expected:** checkbox reverts to unchecked, `Result` announcement "Contact sync not enabled," no contacts pulled.

### Path B: Manual sync now

1. In the Address Book (Ctrl+Shift+B) or Command Palette (Ctrl+Shift+P), user activates **"Sync Contacts Now."** **Expected:** `Status` announcement "Syncing contacts…".
2. On completion. **Expected:** `Result` announcement "Contacts synced, 412 total" (or "Contact sync failed: <reason>" on error). Focus unchanged.

### Path C: Autocomplete a prior recipient in compose (the payoff path)

1. User opens compose, focus in To field, types "dan". **Expected:** existing autocomplete behavior — suggestion list appears including synced saved contacts **and** prior recipients; screen reader announces the count exactly as today (`ComposeWindow.xaml.cs:294`, `Result`).
2. User presses Down arrow. **Expected:** moves through suggestions; each announces "DisplayName, email" via existing item automation names. A prior-recipient-only entry (no saved name) announces its email.
3. User presses Enter. **Expected:** the address is inserted into the To field (existing behavior). No duplicate entry for the same person appears (dedup).

### Path D: Browse synced contacts in the Address Book

1. User opens Address Book, arrows to a synced contact. **Expected:** announced as "DisplayName <email>" like any contact.
2. User presses F2 / activates **Edit**. **Expected:** Edit is **disabled** for synced contacts; a `Hint` fires once on focus: "Synced contact — edit it in Google/Outlook." (respects `AnnounceHints`). No edit fields become active.
3. User presses **Delete** on a synced contact. **Expected:** Delete is unavailable for synced rows (button disabled / key ignored); optional `Hint`: "Synced contacts are managed on the server."

### Path E: Disable sync

1. User unchecks the account's "Sync contacts" box, Saves. **Expected:** `Result` announcement "Contact sync disabled." That account's server-owned contacts are removed from the Address Book on save; **local contacts remain.**

### Path F: IMAP/password account (no sync available)

1. User opens the account editor for a plain IMAP account. **Expected:** the "Sync contacts" checkbox is **absent** (not merely disabled) — no contact API exists for this backend. No announcement, no error.

---

## Section 7: Accessibility Checklist (Mandatory)

- **AutomationProperties.Name** — new controls: account-editor checkbox `"Sync contacts from this account"`; Address Book command surfaces reuse existing button/menu naming. No role names, no shortcuts baked in.
- **AnnouncementCategory:**
  - "Syncing contacts…" → **Status** (background progress; respects `AnnounceStatus`).
  - "Contacts synced, N total" / "Contact sync failed: …" → **Result** (action outcome; respects `AnnounceResults`).
  - "Enabling this will ask <provider> for permission…" and "Synced contact — edit it in <provider>" → **Hint** (respects `AnnounceHints`, fired on focus/toggle, not baked into names).
  - Compose suggestion count → **unchanged** (existing `Result` announcement).
- **Screen reader browse mode / WebView2** — none introduced. The consent browser is the existing OAuth window.
- **Focus restoration** — no new dialogs; consent uses the existing OAuth flow's focus return. The account editor's Save/focus behavior is unchanged.
- **F6 ring** — **no new panes.** Synced contacts live in the existing Address Book list and compose autocomplete, both already in their windows' focus models.
- **Checkbox / radio groups** — one new standalone checkbox in the account editor; not a radio group.
- **Color-only information** — synced-vs-local status must not be color-only. Convey "synced" via the disabled Edit/Delete affordance + the on-focus `Hint`, not a color swatch. (If a visual marker is added later, pair it with text.)

**Summary:** One new checkbox, one new command, four new announcements across three categories, no new panes, no new WebView2 surfaces, no F6 changes.

---

## Section 8: Acceptance Walkthrough (Mandatory — run in Session 3)

### Scenario 1: Google happy path
**Setup:** App running, a Gmail account configured, sync currently off, some local-only contacts present.
1. Enable "Sync contacts" on the Gmail account, Save, consent in browser. **Verify:** `Status` "Syncing contacts…" then `Result` "Contacts synced, N total"; N ≈ Google contact count.
2. Open Address Book. **Verify:** Google contacts appear alongside the pre-existing local contacts; local contacts still present and unchanged.
3. Open compose, type a prefix matching a **prior recipient** you never saved. **Verify:** it appears in suggestions; count announced; Enter inserts it.
4. **Edge — offline:** disconnect network, run "Sync Contacts Now." **Verify:** `Result` "Contact sync failed: …"; no crash; existing contacts intact.

### Scenario 2: Graph happy path
**Setup:** M365/Outlook account (`BackendKind.MicrosoftGraph`), sync off.
1. Enable sync, Save, consent. **Verify:** contacts from `/me/contacts` appear; prior recipients from `/me/people` autocomplete in compose.

### Scenario 3: Dedup across two accounts
**Setup:** Both a Gmail and an Outlook account, both synced, a person present in both.
1. Type that person's prefix in compose. **Verify:** exactly **one** suggestion for that email, not two.
2. Open Address Book. **Verify:** one row for that email (or clearly a single entry), local name preferred if one exists.

### Scenario 4: No-regression — local contact flows (shared component: `ContactService`, `AddressBookViewModel`)
**Setup:** Sync on for one account.
1. Add a new **local** contact via the Address Book (existing flow). **Verify:** it saves, appears, autocompletes.
2. Run "Sync Contacts Now." **Verify:** the local contact is **still there**, unchanged, after sync.
3. Edit and delete the local contact. **Verify:** existing edit/delete behavior unchanged; announcements per `address-book-contacts-pm-dev-spec.md`.

### Scenario 5: Synced rows are read-only (shared component: `AddressBookViewModel`)
1. Select a synced contact, press F2. **Verify:** Edit does not open; `Hint` fires (if `AnnounceHints` on). 
2. Press Delete on a synced contact. **Verify:** not deletable via the row; local contacts still deletable.

### Scenario 6: Disable sync
1. Uncheck sync for an account, Save. **Verify:** `Result` "Contact sync disabled"; that account's server contacts gone from Address Book on next load; local contacts remain.

### Scenario 7: IMAP account (shared component: account editor)
1. Open a plain IMAP/password account's editor. **Verify:** no "Sync contacts" checkbox present; no error.

### Scenario 8: `--online` mode
1. Launch with `--online`, run "Sync Contacts Now." **Verify:** works identically (contact sync is independent of the mail SQLite cache).

### Scenario 9: Screen reader pass
1. Tab through the account editor. **Verify:** checkbox name "Sync contacts from this account," state announced.
2. In compose, verify suggestion count still announced (no regression).
3. Toggle `AnnounceStatus` off, run a sync. **Verify:** no "Syncing…" chatter; `Result` still respects `AnnounceResults` setting.

Mark each pass/fail; any fail documented before Session 4.

---

## Section 9: Success Metrics

- **Behavioral:** A synced Gmail/Outlook user gets ≥95% of their provider contacts + prior recipients autocompleting in To/Cc within one sync cycle.
- **No data loss:** Zero local-only contacts removed or renamed by any sync (automated test + Scenario 4).
- **Dedup:** One autocomplete row per email across all sources (Scenario 3).
- **Keyboard-centric:** Enable, sync-now, autocomplete, and browse are all fully keyboard-operable.
- **Accessibility:** Sync status obeys `AnnounceStatus`/`AnnounceResults`; compose count announcement unchanged; synced-vs-local distinguishable without color.
- **No mail regression:** Contact-sync failure never blocks or slows mail sync (separate catch scope).
- **Online mode:** identical behavior under `--online`.

---

## Section 10: Implementation Phases

### Phase 1 — Data model & store merge (no provider I/O)
**Goal:** `ContactModel` carries provenance; `ContactService` can bulk-replace a `(account, source)` slice and dedup on read; nothing hits the network.
**Deliverables:** `ContactSource` enum; `ContactModel` fields; `IContactService.ReplaceSyncedContactsAsync`/`RemoveSyncedContactsAsync`; dedup in `SearchContactsAsync`/`LoadAllContactsAsync`; `AccountModel.SyncContacts`; `StubServices` updates.
**Tests:** replace-preserves-local; replace-removes-stale-server-rows; dedup-by-email-prefers-local; old-`contacts.json`-round-trips.
**Risk:** dedup accidentally dropping local rows → covered by explicit test. **Duration:** 3–4h.

### Phase 2 — Graph contact source
**Goal:** `GraphContactSource` pulls `/me/contacts` + `/me/people`, normalized to `ContactModel`, via `GraphClient`. Read-only scope requested lazily.
**Deliverables:** `IProviderContactSource`, `GraphContactSource`, Graph contact/person DTOs, `Contacts.Read`/`People.Read` scope constant + lazy request path in `OAuthService`.
**Tests:** DTO→`ContactModel` mapping (saved contact, person-only/no-name); paging; scope selection personal vs work.
**Risk:** scope creep into default mail scopes → test asserts default mail connect scopes unchanged. **Duration:** 4–6h.

### Phase 3 — Google contact source
**Goal:** `GooglePeopleClient` + `GoogleContactSource` pull `connections` + `otherContacts`.
**Deliverables:** minimal paged People API client, `GoogleContactSource`, contacts scopes + lazy request in `GoogleOAuthService`.
**Tests:** mapping, paging (`pageToken`), otherContacts→`IsPriorRecipient=true`.
**Risk:** Google sensitive-scope consent/verification (§13.1) — dev/test uses the app's own account; document the verification requirement. **Duration:** 4–6h.

### Phase 4 — Orchestration, config, and cadence
**Goal:** `ContactSyncService` diffs & applies; `SyncService` runs it best-effort; manual `contacts.syncNow` command; account-editor checkbox; disable/removal cleanup.
**Deliverables:** `IContactSyncService`/`ContactSyncService`, DI wiring, `SyncService` hook (separate catch scope), `CommandRegistry` `contacts.syncNow`, account-editor XAML checkbox, `RemoveAccountContactsAsync` on disable/account delete.
**Tests:** diff add/update/remove counts; failure isolation from mail sync; toggle-off removes server rows.
**Risk:** contact-sync exception bubbling into mail sync → explicit isolation test. **Duration:** 4–6h.

### Phase 5 — Address Book read-only affordance & announcements
**Goal:** synced rows read-only in the Address Book with on-focus `Hint`; sync status/result announcements wired through `AccessibilityHelper.Announce` with correct categories.
**Deliverables:** `AddressBookViewModel` `CanEdit/CanDelete` account for `Source`; hint + status + result announcements.
**Tests:** VM `CanEditContact` false for synced rows; announcement category correctness (where testable).
**Risk:** none major. **Duration:** 2–3h.

Each phase is independently committable and reviewable.

---

## Section 11: Files to Create / Modify

### Create
| File | Purpose |
|---|---|
| `Models/ContactSource.cs` | enum `Local / MicrosoftGraph / Google` |
| `Services/IContactSyncService.cs` | sync orchestration interface + `ContactSyncResult` |
| `Services/ContactSyncService.cs` | diff & apply, cadence entry points |
| `Services/Contacts/IProviderContactSource.cs` | per-provider fetch abstraction |
| `Services/Contacts/GraphContactSource.cs` | `/me/contacts` + `/me/people` → `ContactModel` |
| `Services/Contacts/GoogleContactSource.cs` | People `connections` + `otherContacts` → `ContactModel` |
| `Services/Google/GooglePeopleClient.cs` | minimal paged People API client |
| `QuickMail.Tests/ContactSyncServiceTests.cs` | diff/merge/failure-isolation |
| `QuickMail.Tests/ContactSourceMappingTests.cs` | provider DTO → `ContactModel` |

### Modify
| File | Changes |
|---|---|
| `Models/ContactModel.cs` | add `Source`, `SourceId`, `OwnerAccountId`, `IsPriorRecipient` |
| `Models/AccountModel.cs` | add `bool SyncContacts` (default false) |
| `Services/IContactService.cs` / `ContactService.cs` | add bulk replace/remove; dedup on read |
| `Services/OAuthService.cs` | add contact/people read scope set + lazy request |
| `Services/IGoogleOAuthService.cs` / `GoogleOAuthService.cs` | add contacts-scope token path |
| `Services/SyncService.cs` | best-effort contact-sync pass (separate catch) |
| `ViewModels/AddressBookViewModel.cs` | `contacts.syncNow`; read-only for synced rows |
| account editor XAML/VM | "Sync contacts" checkbox (Graph/Google backends only) |
| `App.xaml.cs` | register `IContactSyncService` + sources; dispose |
| `QuickMail.Tests/StubServices.cs` | `StubContactSyncService` + new `IContactService` methods |

---

## Section 12: Tests to Add

| Test Class | Methods | Coverage |
|---|---|---|
| `ContactServiceSyncTests` | ReplacePreservesLocal; ReplaceRemovesStaleServerRows; ReplaceIgnoresOtherAccounts; DedupPrefersLocal; DedupMaxLastUsed; OldJsonRoundTrips | store merge + dedup |
| `ContactSourceMappingTests` | GraphContact→Model; GraphPersonNoName→Model; GooglePerson→Model; OtherContact→PriorRecipientFlag | provider normalization |
| `ContactSyncServiceTests` | DiffCountsAddUpdateRemove; FailureDoesNotThrowToCaller; DisableRemovesAccountRows | orchestration |
| `OAuthScopeTests` | DefaultMailConnectScopesUnchanged; ContactScopesRequestedOnlyOnOptIn | scope-creep guard |
| `AddressBookViewModelSyncTests` | SyncedRowNotEditable; SyncedRowNotDeletable; SyncNowCommandInvokesService | VM affordances |

Every new public method gets a test; scope-creep guard test is the highest-value safety net.

---

## Section 13: Known Risks & Open Questions

### 13.1 Risks

| Risk | Prob. | Impact | Mitigation |
|---|---|---|---|
| Adding contact scopes to the **default** mail scope set forces every existing user to re-consent on next connect | Med | Major | New scope constant requested **only** on opt-in; `OAuthScopeTests.DefaultMailConnectScopesUnchanged` gate. |
| Google `contacts.readonly` / `contacts.other.readonly` are **sensitive scopes** → Google OAuth app needs verification; unverified apps hit the "unverified app" screen and a 100-user cap | Med | Major | Document as a release gate; dev/test with the app owner's own account; may require Google verification submission before GA. **Kelly decision needed (§13.2).** |
| Re-sync creates duplicates instead of updating | Med | Major | Update-by-`SourceId`, not by email; `ReplaceSyncedContactsAsync` test. |
| A sync deletes local-only contacts | Low | Blocker | Merge only ever touches rows with matching `(OwnerAccountId, Source)`; `ReplacePreservesLocal` test + Scenario 4. |
| Contact-sync failure blocks mail sync | Low | Major | Separate try/catch scope in `SyncService`; `FailureDoesNotThrowToCaller` test. |
| `contacts.json` bloat / load time with thousands of prior recipients | Low | Minor | Cap prior recipients (e.g. top ~1000 by relevance) if needed; log the cap per CLAUDE.md "no silent caps." |
| Synced-vs-local conveyed by color only | Low | Major | Non-color affordance (disabled Edit + on-focus Hint), per §7. |

### 13.2 Open questions — **need a decision before Session 2**

1. **Write-back in v1 or v2?** The issue asks for create/edit-syncs-to-server. This spec recommends **v2** (one-way read in v1) to avoid two-way conflict, delete-propagation, and etag-collision complexity on the first release. In v1 users still create/edit *local* contacts. **Confirm: read-only v1 acceptable?**
KF: Yes, no syncing now back to the server, maybe never. The one thing the user can do is resync from the server and we should be able to edit previously synced from that system. For example the user updates an email address or deletes one on the synced account. we need to be able to adapt to that. Realtime syncing isn't critical unless it can easily be done at the same time we are checking mail or something.


2. **Sync default: off (recommended) or on?** Recommend **off** for privacy + to avoid surprise re-consent. **Confirm.**
KF: Off for sure and a notification when turned on that new data will be available in Quickmail.

3. **Google verification:** Are we prepared to submit the QuickMail Google OAuth app for sensitive-scope verification, or restrict Google contact sync to a smaller/test audience initially? (Graph has no equivalent gate.)
KF: I'd think this is the same limitation we have today? Maybe we need to request more permisisons but yes we are prepared to deal with the goolg eissues.


4. **Synced-contact editing model:** v1 recommends **read-only synced rows** (edit in provider). Alternative: "copy to local and edit," which creates a local override. Recommend read-only for v1. **Confirm.**
KF: Yes



5. **Prior-recipient volume cap:** cap at ~1000 by relevance, or pull all? Recommend a logged cap. **Confirm the number.**
You can try 1000 but we'll have to see how the performance is. Also, we already have grab addresses from an open message as one mitigation today.
Every question has a recommended default above; the spec is implementable on those defaults if Kelly approves them.

---

## Section 14: Appendix — Command / Setting Reference

| Item | Type | Default | Notes |
|---|---|---|---|
| `contacts.syncNow` | CommandRegistry command, category **Contacts** | no default key | Palette + Address Book |
| `AccountModel.SyncContacts` | per-account setting | **false** | Persisted in accounts.json |
| Auto-sync interval | internal | 12h + on connect | Via `SyncService` |
| Graph scopes | OAuth | `Contacts.Read`, `People.Read` | Requested on opt-in only |
| Google scopes | OAuth | `contacts.readonly`, `contacts.other.readonly` | Sensitive — verification (§13.1) |

---

## Section 15: Implementation Guidance for AI (Session 2)

### 15.1 Adjustments you're expected to make
- The exact Graph `/me/people` query params (relevance ordering, `$top`, filtering personal-vs-org) are left to you — reuse `GraphClient.GetAllPagesAsync` and pick sensible defaults; log any cap.
- Whether `GooglePeopleClient` shares a base with `GraphClient` or stays standalone is your call — match `GraphClient`'s method shape for consistency.
- The dedup tie-break beyond "local > saved > prior recipient" (e.g. which provider wins between two saved contacts) is your judgment; keep it deterministic and covered by a test.

### 15.2 When to ask for clarification
- The five open questions in §13.2 are **normative gates.** Do not implement write-back, flip the default to on, or change the read-only synced-row model without an explicit answer.
- If Google sensitive-scope verification blocks even test sign-in, stop and report — don't work around it by weakening scopes to something that can't read prior recipients.
- If you discover `contacts.json` merge can't cleanly preserve local rows under the existing `_loadLock` model, stop and raise it — data loss here is a blocker, not an implementation detail.

### 15.3 Highest-risk acceptance steps (from §8)
- Scenario 4 (local contacts survive a sync) — the data-loss guard.
- Scenario 3 (dedup across two accounts) — the one-person-one-row guarantee.
- Scenario 1 step 4 (offline sync fails gracefully) and the mail-sync isolation.
Focus verification effort there.
