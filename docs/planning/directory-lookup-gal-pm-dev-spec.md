# Directory lookup (Global Address List) for work/school Microsoft accounts — PM + Dev spec

**Status:** Draft for review (Tim, Kelly). No code yet.
**Applies to:** Microsoft accounts connected over Graph that belong to a work or school organization
(`BackendKind.MicrosoftGraph`, not personal). Personal Microsoft, IMAP, Google, and iCloud accounts have
no change.

---

## 1. Summary

In a large organization, QuickMail cannot address someone you have never written to. Compose
autocomplete and the address book search only `contacts.json`. That file holds local contacts and,
if contact sync is on, whatever `GraphContactSource` pulled: the account's saved contacts
(`/me/contacts`) and up to 1,000 relevance-ranked prior correspondents (`/me/people`). Nothing searches
the organization's directory. Outlook does, and people moving from Outlook will expect it.

This spec adds a **live directory lookup**: as you type a name in To, Cc, or Bcc, QuickMail asks
Microsoft Graph for matching people in the sending account's organization and adds them to the
suggestion list. It does not bulk-sync the directory. Results are kept in memory and never written to
disk unless you choose to save one.

**Recommendation:** use the People API search that QuickMail already has consent for
(`/me/people?$search=` with the `X-PeopleQuery-QuerySources: Mailbox,Directory` header, scope
`People.Read`). It needs no permission beyond what QuickMail already requests. The directory endpoint
(`/users?$search=`, scope `User.ReadBasic.All`) is the fallback if the verification step (§5) shows
the People API misses directory entries that matter.

**Consent decision (Tim, 2026-10-19):** `User.ReadBasic.All` is included in Tim's org admin-consent
grant **now**, before any code uses it. So on Tim's tenant both options are already consented, and
neither needs a re-consent step. Consent no longer decides between (a) and (b) for Tim's tenant. The
choice rests on result quality (§4 decision table, §5 spike). Whether `AllGraphAdminConsentScopes`, and
therefore the admin-consent URL every other organization uses, should also gain `User.ReadBasic.All` is
a separate open question (K5).

---

## 2. Current state (verified against the code)

| Surface | Today | Gap |
|---|---|---|
| Compose autocomplete (`ComposeWindow.AddressBox_InputTextChanged`) | Searches `ContactService.SearchContactsAsync` + `SearchGroupsAsync` (in-memory `contacts.json` / `groups.json`, substring match, top 10) | No network lookup; someone never emailed cannot be found |
| Ctrl+K Check Addresses (`ComposeWindow.CheckAddresses`) | A bare name with exactly one local match is resolved; otherwise it is marked unrecognized | Cannot resolve a colleague's name you have never emailed |
| Address book search box (`AddressBookWindow`, `SearchBox`) | Filters the local contact list | Same gap |
| `GraphContactSource.FetchAsync` | `/me/contacts` + `/me/people?$top=100` paged, prior recipients capped at 1,000, **no** `X-PeopleQuery-QuerySources` header (so mailbox-only results) | Not a directory search, by design: it is a one-time sync that runs only when contact sync is on |
| Scopes (`OAuthService`) | `GraphContactScopes` = `Contacts.Read`, `People.Read`. `AllGraphAdminConsentScopes` includes them. `ExtraConsentScopesForMicrosoftSignIn` adds the full admin set to **every** work/school Graph sign-in, so `People.Read` is normally granted whether or not contact sync is on | `User.ReadBasic.All` is declared on the app registration but has no call site, and is deliberately left out of the admin-consent URL (comment at `AllGraphAdminConsentScopes`) |
| `GraphClient` | Has `GetAsync(account, path, headers)` and `GetAsync(account, path, scopes, silentOnly)`, but no overload taking **scopes, silentOnly, and headers** together | Directory search needs all three |
| `docs/USER-GUIDE.md` admin section, permission table | Lists `User.Read`, `User.ReadBasic.All` as "Resolve the signed-in user and display recipient names" | **Already inaccurate today.** QuickMail does not request `User.ReadBasic.All`, and this is the page an administrator reads before consenting. Fix it whatever we decide here (see §11) |

---

## 3. Personas

- **New employee at a 5,000-person organization.** Has emailed nobody yet. Types "Pat Rob" in To and
  expects Pat Roberts from Payroll to appear, as in Outlook.
- **Screen reader user moving from Outlook.** Uses Check Names (Ctrl+K in Outlook too) to turn a typed
  name into an address. Expects QuickMail's Ctrl+K to do the same against the directory.
- **IT administrator approving QuickMail.** Wants the smallest permission set and does not want to be
  asked to approve again later.

---

## 4. Options evaluated

### (a) People API search: `GET /me/people?$search="<text>"` *(recommended)*

```
GET https://graph.microsoft.com/v1.0/me/people?$search="pat rob"&$top=10
    &$select=displayName,scoredEmailAddresses,personType,userPrincipalName
X-PeopleQuery-QuerySources: Mailbox,Directory
```

- **What it returns:** By default only mailbox results, meaning saved contacts and people the user
  interacts with. With the `X-PeopleQuery-QuerySources: Mailbox,Directory` header it also returns
  directory results from the organization (Microsoft Learn, "Use the people API", section "Types of
  results included"). Matching is fuzzy on display name and email address ("tiler" finds Tyler), and
  results are ranked by relevance to the user. Each result carries
  `personType { class, subclass }`. `OrganizationUser` is a directory person; `PersonalContact` and
  `ImplicitContact` are mailbox people. Default page is 10 results, `$top` raises it.
- **Consent:** `People.Read`, delegated, user-consentable. It is already in `GraphContactScopes`,
  already in `AllGraphAdminConsentScopes` (so in the admin-consent URL), and already added to every
  work/school sign-in. **No new scope, no change to the admin-consent URL, no re-consent.**
- **Cost:** One small request per settled keystroke. Relevance ranking puts the people you work with
  first, which suits autocomplete.
- **Unknowns (verified in §5 before PR 1):** whether directory results cover everyone in the GAL,
  including mailboxes you have no relationship with; whether mailboxes hidden from address lists are
  left out (they should be); whether distribution lists and mail-enabled groups come back, and as which
  `personType`; and how fast it is on a real tenant. Microsoft's documentation for this API is old
  (2019) but the endpoint is v1.0 and current.

### (b) Directory search: `GET /users?$search=` *(fallback)*

```
GET https://graph.microsoft.com/v1.0/users?$search="displayName:pat rob" OR "mail:pat"&$top=10
    &$select=id,displayName,mail,userPrincipalName,userType
ConsistencyLevel: eventual
```

- **What it returns:** Entra ID user objects. `$search` on directory objects only works as an advanced
  query, so it needs the `ConsistencyLevel: eventual` header. `displayName` is tokenized (any word in any
  order, prefix per token); `mail` is a starts-with match. No relevance ranking and no fuzzy matching.
  A `$filter=startswith(displayName,'…') or startswith(mail,'…')` form works without the header but is
  starts-with only on the whole string.
- **Consent:** `User.ReadBasic.All`, delegated. Microsoft does not require admin consent for it by
  default, but many tenants (Tim's included) turn off user consent, so in practice an admin grants it.
  **On Tim's tenant it is already granted:** Tim's admin includes it in the org grant now (decision,
  §1), so no re-consent step is planned for it there. **For other organizations** it is not in
  today's code-built admin-consent URL (`AllGraphAdminConsentScopes`). If (b) ships, either that list
  gains it (`BuildAdminConsentUrl` and `AdminConsentUrlTests` change, organizations that consented
  without it must grant it once, and `ExtraConsentScopesForMicrosoftSignIn` would add it to every
  work/school sign-in, the onboarding risk the `GraphMailScopesWorkSchool` comment warns about), or
  their admins grant it some other way. Which one is open question K5. With no grant, the call fails
  silent-only and §6.7 applies: no directory rows, local suggestions unaffected.
- **Data-quality problems:** `/users` is the Entra directory, not the Exchange address list. It
  includes accounts the GAL hides (disabled accounts, service accounts, mailboxes hidden from address
  lists; `showInAddressList` is not reliably readable). It includes users without a mailbox, whose
  `mail` is null and must be dropped. It includes guests (`userType eq 'Guest'`). It returns no
  distribution lists or mail-enabled groups. Those need `/groups` and a further permission.
- **Advantage:** Coverage is predictable. Every user object in the tenant is searchable regardless of
  relationship.

### (c) Bulk-sync the GAL into `contacts.json` *(rejected)*

Rejected. It does not scale to large organizations, it copies the whole directory to disk (a privacy
regression, and against the "kept only on your computer, only what you use" posture in
`docs/privacy.html`), and it goes stale. The 1,000 cap in `GraphContactSource` exists for the same
reason.

### Decision table

| | (a) People search | (b) `/users` search |
|---|---|---|
| Scope | `People.Read` (already requested) | `User.ReadBasic.All` (no call site yet) |
| Consented on Tim's tenant | Yes | Yes (in the org grant now, by decision) |
| Code admin-consent URL change | No | Open question (K5) |
| Ranking | Relevance to the user | None (alphabetical / arbitrary) |
| Fuzzy matching | Yes | No |
| Honors GAL hiding | To verify (expected yes) | No |
| Distribution lists | To verify | No (needs `/groups`) |
| Guests, no-mailbox accounts | Expected excluded | Must filter |
| Extra header | `X-PeopleQuery-QuerySources` | `ConsistencyLevel: eventual` |

---

## 5. Verification step before PR 1 (Tim's tenant)

A short manual spike, no QuickMail code: Graph Explorer, or a throwaway `/debug` command, signed in to
Tim's work account. It answers the unknowns in (a):

1. Search for a colleague Tim has **never** emailed: with the header, without it, and in a mailbox not
   yet indexed. Expected: found only with the header.
2. Search for a mailbox known to be hidden from the GAL. Expected: not returned.
3. Search for a distribution list and a Microsoft 365 group by name. Record whether each comes back and
   its `personType`.
4. Search for a room or equipment mailbox. Record what comes back.
5. Measure latency across about 20 queries.
6. Repeat test 1 with `/users?$search=` so the coverage comparison is evidence rather than
   assumption.

If test 1 fails (the People API does not reach people with no relationship to the user), switch the
recommendation to (b). Tim's tenant already has the grant, so the spike, and a (b) build, can run
there with no consent step. Only K5 (other organizations) remains. Test 6 can therefore run as a real
comparison on Tim's tenant.

---

## 6. Design (option a)

### 6.1 Which account's directory

The directory searched is that of the compose window's **From account**
(`ComposeViewModel.SenderAccount`), when that account is eligible:

- Work/school Graph account (`BackendKind.MicrosoftGraph` and not `ResolveIsPersonalMicrosoftAccount`):
  eligible.
- Shared mailbox (`IsShared`): search with the **parent** account's token (`ParentAccountId`), since
  the shared account has no `/me`. It is the same organization.
- Anything else: no directory lookup. Local suggestions only, exactly as today.

Changing From does not re-run a finished search. The next keystroke uses the new account. The address
book (§6.5) searches the account selected in its account filter if that one is eligible, otherwise the
default account if eligible, otherwise none.

### 6.2 The request

- `GraphDirectorySearchService.SearchAsync(account, text, ct)` sends the request in §4(a). It uses
  `OAuthService.GraphContactScopes` with **`silentOnly: true`**, because a keystroke must never open a
  sign-in window, and a sign-in inside the modal address book can deadlock (see the
  `GraphContactSource` comment).
- `$search` is the trimmed input in double quotes, with `"` and `\` escaped, URL-encoded.
- Results are mapped to `ContactModel` with `Source = ContactSource.Directory` (new enum value, never
  persisted), `OwnerAccountId = account.Id`, `DisplayName`, and the first `scoredEmailAddresses`
  address. Entries with no address are dropped.
- **`personType` filter:** v1 keeps `OrganizationUser`, `PersonalContact`, and `ImplicitContact`. The
  rest depends on §5 test 3 (open question Q5).

### 6.3 When it runs

- Minimum **2 characters** of input (Q3). The local search keeps its current one-character minimum.
- **Debounce 300 ms** after the last keystroke, then one request. Every new keystroke cancels the
  pending debounce and any in-flight request, using the existing `_autocompleteCts` pattern.
- **Timeout 4 s.** A slower answer is dropped and logged.
- A per-window in-memory cache (query text to results, 5-minute lifetime, at most 50 entries) makes
  backspacing and retyping free. It is discarded when the window closes. Nothing is written to disk.
- Not run while QuickMail is offline (`ConnectivityService`).

### 6.4 Compose autocomplete merge

1. Local contacts and groups are searched and shown immediately, as today. The keystroke path is not
   slowed down.
2. When directory results arrive, they are de-duplicated against the suggestions already shown
   (case-insensitive email), and the rest are **appended after** the local rows, at most 10.
3. If nothing local matched, the popup stays closed until the directory answers, then opens with
   directory rows only. If the directory has nothing either, it stays closed, as today.
4. **The list must not change under the user.** If focus is still in the address box, the list is
   replaced with the combined result. If the user has already arrowed into the suggestion list, the
   new rows are added to the end of the existing collection without reordering, removing, or
   re-selecting anything. `SuggestionList.ItemsSource` becomes an `ObservableCollection` so that an
   append does not reset the selection. If the input text has since changed, the result is discarded
   (it was cancelled anyway).
5. **Row text.** On screen: the primary line is the display name, and the secondary line is
   "`{email}` · Directory". The accessible name (`AddressSuggestion.Display`, set on the container as
   today) is "`{Name} {email}`" for local rows, unchanged. Whether directory rows say where they came
   from, and how, is Tim's call (Q1).
6. **Picking a row** behaves like a local contact: one chip, display name plus address. A directory
   pick is **not** saved to `contacts.json`. The existing "add this chip to the address book" action
   still works on it.

### 6.5 Address book

The address book's local list filters as you type and must stay instant, so directory search there is
**explicit**:

- With an eligible account and 2 or more characters in Search, pressing **Enter** in the search box (or
  activating a new **Search directory** button next to it, also in the address book's command palette)
  runs the directory query.
- Results appear in the Contacts list **after** the local matches, as rows whose source is "Directory".
  The row's `AccessibleName` follows the existing contact-row format with the source label "Directory"
  (the same mechanism that labels synced rows with their source today).
- Add to To, Add to Cc, and Add to Bcc work on directory rows. Edit and Delete are disabled (it is not
  your contact). A new **Save to address book** action copies the row to a local contact.
- Changing the search text clears the directory rows until the next explicit search.

### 6.6 Ctrl+K Check Addresses

For a chip that is a bare name (no `@`) with **zero** local matches, the directory is searched as well
(same service, 4 s timeout per chip, overall 30 s budget unchanged). Exactly one directory match
resolves the chip. More than one leaves it unrecognized; a disambiguation picker is out of scope (§10).
The summary gains a directory count, for example "3 addresses checked. 1 resolved from contacts, 1 from
the directory." This is a Result message through `SetStatusOutcome`, as today.

### 6.7 Failures

Directory lookup is supplementary. Local suggestions are always shown, so a failure never leaves the
popup blank where it used to have content. Still, a silent failure would look like "that person does not
exist":

- `InteractiveSignInRequiredException` (no `People.Read` grant, for example on a tenant where the user
  declined), 403, 429 after the timeout, network errors: logged once per compose window
  (`LogService.Log`, no query text), and the window stops querying the directory until it is reopened.
- **Ctrl+K and the address book's explicit search report it**, because the user explicitly asked:
  "Directory search is unavailable for {account}." as a Result. Autocomplete does not announce it,
  since it would interrupt typing (Q2 covers this).
- 401/403 caused by a missing grant could point to the existing "Request contacts permission" flow
  (`RequestContactsConsentAsync`). Q6 covers whether to offer it.

### 6.8 Privacy and logging

- What is sent: the typed search text, to Microsoft Graph, under the user's own token, for their own
  organization. What comes back stays in memory for at most 5 minutes per open window.
- Never logged: query text and result names/addresses. `/debug` logs count and latency only.
- `docs/privacy.html`: the `People.Read` row changes (the "When requested" and "Why" columns now include
  live directory search when addressing mail on a work or school account, not stored). New effective
  date. No new scope row under (a).

### 6.9 Setting

A per-account switch in the account editor, **"Look up people in my organization's directory"**,
default on, shown only for eligible accounts. Stored as `AccountModel.SearchDirectory` (JSON, default
`true` through the property initializer, so no migration). Q4 asks whether this should exist at all,
and whether it should be global instead.

---

## 7. Keyboard walkthroughs

How the screen reader speaks each step depends on the user's own software and settings. The lines
below say what QuickMail exposes (focus, names, announcements), not how it will sound. Tim confirms the
spoken result during manual testing.

### 7.1 Compose: finding someone never emailed

1. User is in To on a message from their work account and types "pa". Local search runs at once and
   finds two prior recipients. The popup opens. QuickMail announces "2 suggestions" (Result, as today).
2. 300 ms after the last keystroke, the directory request goes out. User keeps typing "t r". Each
   keystroke cancels and restarts. Local results update as today.
3. The directory answers "pat r" with Pat Roberts and Pat Rogers. Neither is local. Focus is still in To,
   so the list is replaced with the local rows followed by the two directory rows. Announcement: per
   Q2 (proposal: none, because the user is typing).
4. User presses Down. Focus moves to the first suggestion. The rows are reachable with Up and Down;
   each directory row exposes its name and address (plus a source label per Q1).
5. User presses Enter on Pat Roberts. A chip is added. The popup closes and focus returns to To, as
   today.

### 7.2 Compose: directory answers after the user is in the list

1. User types "jo" and presses Down at once, onto the first local suggestion.
2. The directory then answers. Its new rows are appended to the end. The selected row and focus do not
   move. If the user arrows to the end, the new rows are there.

### 7.3 Compose: From is a personal or IMAP account

1. Same typing as 7.1. No directory request is made. Behavior is exactly as today.

### 7.4 Ctrl+K resolves a typed name

1. User types "Pat Roberts" in To and presses Tab. An unresolved chip is created, as today.
2. User presses Ctrl+K. No local match, so one directory match: the chip becomes "Pat Roberts
   <pat.roberts@org>". Status says "1 address checked. 1 resolved from the directory." (Result)
3. If the directory is unavailable: "1 address checked. 1 unrecognized. Directory search is unavailable
   for {account}." (Result)

### 7.5 Address book directory search

1. User opens the address book (Ctrl+Shift+B from compose, as today) and types "rob" in Search. The
   local list filters as today.
2. User presses Enter. The directory query runs. Directory rows are added after the local matches. A
   Result announcement says how many were found, "4 found in the directory" (the user explicitly asked,
   so the outcome is reported).
3. User tabs to the list, arrows to a directory row, and activates Add to To. A chip is added in
   compose, as today.
4. User presses Escape. The address book closes and focus returns as today.

---

## 8. Infrastructure changes

- **New service:** `IDirectorySearchService` / `GraphDirectorySearchService` in
  `QuickMail/Services/Contacts/`. Wired in `App.xaml.cs` after `ContactService`, passed to
  `ComposeWindow` and `AddressBookViewModel`. Stub in `QuickMail.Tests/StubServices.cs`.
- **`GraphClient`:** a new `GetAsync<T>(account, path, scopes, silentOnly, headers, ct)` overload. The
  existing overloads delegate to it.
- **`GraphPerson` DTO:** gains `personType` (`class`, `subclass`) and `userPrincipalName`.
- **`ContactSource`:** gains `Directory = 4`. It is never written to `contacts.json`; a guard in
  `ContactService` rejects persisting it.
- **`AccountModel`:** `SearchDirectory` (bool, default true), if Q4 keeps the setting.
- **`OAuthService`:** no scope change under (a). Update the `GraphContactScopes` comment: `People.Read`
  now also covers live directory search, and that use is **not** gated on contact sync. Under (b):
  a new `GraphDirectoryScopes` array that the directory call requests silent-only. Whether it is also
  added to `AllGraphAdminConsentScopes` is K5. If it is, `AdminConsentUrlTests` and
  `OAuthServiceScopeSelectionTests` change too.
- **F6 ring:** no change. The suggestion popup and the address book list are already reachable.
- **CommandRegistry:** no main-window command. The address book's local command palette gains
  "Search directory" (no default key; Enter in the search box is in-control behavior, like the arrows).
- **`AutomationProperties.Name`:** no new control names in compose. The address book's new button is
  named "Search directory". Directory rows reuse the existing container-level `AccessibleName` /
  `Display` setters; per CLAUDE.md, the name goes on the row container, not inside the template.
- **Announcements:**
  - Compose autocomplete: the existing "N suggestions" (Result) is unchanged. Any announcement when
    directory rows arrive: Q2, proposed **none**.
  - Ctrl+K summary: extended text, same `SetStatusOutcome` Result path.
  - Address book explicit search: "N found in the directory" / "No one found in the directory" /
    "Directory search is unavailable for {account}." (Result).
- **VM state:** none beyond the address book VM's directory-row collection and a busy flag for the
  button.
- **Tests:**
  - `GraphDirectorySearchServiceTests` (stub `HttpMessageHandler`): URL, `$search` quoting and escaping,
    the `X-PeopleQuery-QuerySources` header, `silentOnly`, `personType` filtering, rows with no address
    dropped.
  - A merge/de-dup test for the compose append logic, extracted as a pure function so it can be tested
    without a window.
  - `AddressSuggestion.Display` for directory rows.
  - `SelectorItemAccessibilityTests` / `AddressBookRowSpeechTests` cover directory rows (container
    name, not `ToString()`).
  - Ctrl+K resolution path (local miss, directory hit).
  - `ContactService` refuses to persist `ContactSource.Directory`.
  - `ViewModelConstructionTests` with the new service stub.

---

## 9. Documentation changes (same PRs as the code)

- `docs/privacy.html`: `People.Read` row (§6.8), new effective date.
- `docs/USER-GUIDE.md`: a compose section on directory suggestions and Ctrl+K. The admin permission
  table: `People.Read` "why" now includes directory lookup.
- `docs/ENTRA-APP-REGISTRATION.md` code-audit section: `People.Read` is used for live search too.
  `User.ReadBasic.All` stays a forward declaration under (a).
- Release notes for the version that ships it.

---

## 10. Out of scope

- Bulk download or offline copy of the GAL (option c).
- A disambiguation picker for Ctrl+K when a name matches several people (Outlook's Check Names
  dialog). v1 leaves the chip unrecognized.
- Expanding distribution lists into members. A list, if returned at all (Q5), is one address.
- Contact details beyond name and address: title, department, phone, photo, presence, org chart.
- Directory lookup for personal Microsoft, IMAP-connected Microsoft, Google Workspace, or iCloud
  accounts. Google Workspace directory search is a possible follow-up through the Google People API
  `directory.readonly` scope.
- Searching more than one organization at once when the user has several work accounts. Only the From
  account's directory is searched.
- Writing anything back to the directory or to the user's Outlook contacts.
- Changing how `GraphContactSource` sync works or its 1,000 cap.

---

## 11. Rollout and PR plan

0. **Spike (§5)** on Tim's tenant. The result decides (a) vs (b). No PR.
1. **PR 1:** `GraphClient` overload, `GraphPerson` fields, `ContactSource.Directory` plus its persistence
   guard, `GraphDirectorySearchService` and tests. No UI.
2. **PR 2:** compose autocomplete merge, Ctrl+K, the account setting (if kept), `privacy.html`, User
   Guide compose section.
3. **PR 3:** address book explicit directory search, its palette entry, User Guide address book
   section.

**Separate, independent fix, worth doing now regardless:** `docs/USER-GUIDE.md`'s admin permission
table says QuickMail uses `User.ReadBasic.All` to "display recipient names". It does not request that
scope at all. The same table describes `MailboxSettings.ReadWrite` as "for a planned server-side rules
feature", which has shipped. This is the page an administrator reads before consenting, so it should be
accurate before Tim's admin reads it.

---

## 12. Open questions

**For Tim:**

- **Q0 (timing). Resolved 2026-10-19:** your admin consents now, and the grant includes
  `User.ReadBasic.All` ahead of any code, so neither option needs a re-consent on your tenant.
- **Q1 (row wording).** Should a directory suggestion's accessible name say where it came from (for
  example "Pat Roberts pat.roberts@org, directory"), or read exactly like a local row?
- **Q2 (arrival announcement).** When directory rows arrive while you are typing, should QuickMail say
  anything ("2 more from the directory"), or stay silent and let you find them by arrowing? Proposal:
  silent while typing; the address book's explicit search does report its count.
- **Q3 (threshold).** Is a 2-character minimum and a 300 ms pause right?
- **Q4 (setting).** Keep a per-account on/off switch, make it one global setting, or have no setting?
- **Q5 (distribution lists).** If §5 shows distribution lists come back, offer them as single-address
  suggestions in v1?
- **Q6 (missing grant).** When `People.Read` was never granted, should Ctrl+K and the address book offer
  the existing "request contacts permission" sign-in, or only say the directory is unavailable?

**For Kelly:**

- **K1.** Is (a) acceptable without the (b) fallback, if the spike shows good coverage?
- **K2.** `ContactSource.Directory` on `ContactModel` (recommended: reuses the address book row and
  suggestion plumbing) versus a separate `DirectoryEntry` type?
- **K3.** OK with the address book making network calls while modal, given that the calls are
  silent-only and cancellable?
- **K4.** Take the User Guide permission-table correction (§11) as its own small PR now?
- **K5 (for Tim and Kelly).** Should `OAuthService.AllGraphAdminConsentScopes`, and so the admin-consent
  URL `BuildAdminConsentUrl` gives every organization, gain `User.ReadBasic.All` now, to match what
  Tim's tenant is granting? Doing so changes `AdminConsentUrlTests`, the `privacy.html` scope table,
  and the `AllGraphAdminConsentScopes` comment (which today calls it a forward declaration with
  nothing to consent). It also adds the scope to every work/school sign-in through
  `ExtraConsentScopesForMicrosoftSignIn`. Not doing so means only organizations whose admins grant it
  by hand get (b). No code changes are made by this spec either way.
