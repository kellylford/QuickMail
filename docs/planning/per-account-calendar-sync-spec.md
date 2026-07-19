# Per-Account Calendar Sync — Spec (#282)

## Goal

A user adds an email account. If that account's provider has a calendar QuickMail can read, the user can **opt in per account** with a checkbox — exactly the address-book (contact) sync model (#256). One-stop management in Add Account and Manage Accounts. No global calendar setting.

This replaces the removed global Settings → Internet Calendar (CalDAV) source. Calendar sync becomes opt-in for **Microsoft, Google, and iCloud** accounts.

## The model (mirrors contact sync #256)

- `AccountModel.SyncCalendar` (bool, default false), sibling to `SyncContacts`. Auto-persists to `accounts.json` via `AccountService`.
- Add Account dialog: a "Sync _calendar from this account" checkbox, bound to `AccountEditorViewModel.SyncCalendar`, visible per `ShowCalendarSyncOption`.
- Manage Accounts dialog: the same checkbox, self-applying (immediate), via `AccountManagerViewModel.SetCalendarSyncAsync(bool)` + a `Click` handler (never fires on programmatic re-selection), gated by `CanSyncCalendar`.
- Sync is gated on the flag everywhere: the background pull loop, the save-target picker, and the per-account calendar tree nodes.

## Provider coverage & consent

| Provider | Detected by | Calendar API | Consent at opt-in |
|----------|-------------|--------------|-------------------|
| **Microsoft** | `BackendKind.MicrosoftGraph` or `AuthType.OAuth2Microsoft` | Graph `/me/calendarView` + `/me/events` (existing) | **Explicit** — new `RequestCalendarConsentAsync` acquires `GraphCalendarScopes` (interactive if not yet granted), mirroring `RequestContactsConsentAsync`. |
| **Google** | `AuthType.OAuth2Google` | Google Calendar API (existing) | **None needed** — `GoogleOAuthService` already requests `CalendarScopes` in its base mail sign-in (`MailAndCalendarScopes`). Just gate on the flag. |
| **iCloud** | `ImapHost == "imap.mail.me.com"` (the existing `IsICloudAccount` predicate) | **CalDAV** at `https://caldav.icloud.com`, per-account | **None needed** — reuses the account's stored app-specific password (`CredentialService.GetPassword(account.Id)`), the same one iMAP uses. |

`ShowCalendarSyncOption => IsOAuth2 || IsGoogleOAuth || IsICloudAccount` (a superset of contacts, which is OAuth-only).

Generic CalDAV (Fastmail/Nextcloud/arbitrary servers) is **out of scope** — it needs a server-URL field that doesn't fit the one-checkbox model. iCloud's server is known, so it fits.

## Backend

`GraphCalendarSyncService` (name predates multi-provider):

1. Account loop gate: `if ((graphEligible || googleEligible) && account.SyncCalendar)` — sync Microsoft via Graph, Google via Google API (existing paths, unchanged except the gate).
2. iCloud: in the same loop, for accounts where `IsICloudCalendar(account) && account.SyncCalendar`, run CalDAV per-account: discover + fetch via `CalDavCalendarClient`, map ICS → rows with `account.Id` as the row `AccountId`, replace-slice by `account.Id`. Best-effort (never throws), like the other providers.
3. `CalDavCalendarClient` is adapted from the (still-present on main) global version: keep the manual-redirect `HttpClient`, discovery (PROPFIND chain), fetch (REPORT), and `SyntheticUid`. Drop the global-only helpers `AccountIdFor` (use the real `account.Id`) and `SecretKeyFor` (use `GetPassword(account.Id)`). Discovery cache becomes per-account (keyed by account id).
4. Push (create/edit/delete) is Microsoft + Google single events only, unchanged. The save-target picker (`IsCalendarPushAccount`) additionally requires `SyncCalendar`. iCloud CalDAV is read-only (no push) — consistent with the current v1.

## Infrastructure changes

- **AccountModel**: `+ bool SyncCalendar`.
- **OAuthService / IOAuthService / OAuthRouter**: `+ RequestCalendarConsentAsync` (Microsoft real; Google/Router no-op or route). Google already has scope; Router routes Microsoft only.
- **CommandRegistry**: `calendar.syncNow` "Sync Calendars Now" (Calendar category, no default key) — manual sync, mirroring `contacts.syncNow`.
- **Calendar tree** (`BuildFolderTree`): per-account child nodes only for `SyncCalendar` accounts.
- **AutomationProperties.Name** introduced: "Sync calendar from this account" on both checkboxes (short label only).
- **Announcements**: opt-in/opt-out result announced (`AnnouncementCategory.Result`) via the existing account-dialog announce paths, mirroring contacts.
- **Removed**: the global Settings → Internet Calendar (CalDAV) group + `CalDav*` config keys + `SettingsViewModel` CalDav members (superseded by per-account).

## Keyboard walkthrough

**Add Account (iCloud example)**

1. User opens Add Account, types their iCloud address. Screen reader announces the fields; server settings auto-fill.
2. User tabs to **Sync calendar from this account** and checks it. (Checkbox only shows because it's an iCloud/OAuth account.)
3. User enters the app-specific password and finishes adding. The account is saved with `SyncCalendar = true`; calendar sync starts in the background using that password.
4. A **Calendar → [account]** node appears in the folder tree; the account's events populate on the next sync.

**Manage Accounts (toggle later)**

1. User opens Manage Accounts, selects the account. Screen reader announces the account.
2. The **Sync calendar from this account** checkbox reflects the current state (set programmatically — no side effect).
3. User checks it. For Microsoft, a consent prompt may appear; for Google/iCloud it applies silently. Screen reader announces "Calendar sync on" (Result).
4. Unchecking removes that account's synced calendar rows and the tree node; announces "Calendar sync off".

## Out of scope

- Generic (non-iCloud) CalDAV servers.
- Two-way / write-back for iCloud (read-down only, matching current v1 for CalDAV).
- Editing repeating server events (already read-only).
- Multiple named calendars per account, colors, per-calendar visibility.

## Tests

- `AccountModel` round-trips `SyncCalendar`.
- `GraphCalendarSyncService`: only syncs accounts with `SyncCalendar` true; iCloud account syncs via CalDAV using `GetPassword(account.Id)`; non-opted accounts skipped. (Mirror `ContactSyncServiceTests.SyncAll_OnlySyncsEnabledAndSupportedAccounts`.)
- `AccountEditorViewModel.ShowCalendarSyncOption` for MS/Google/iCloud/other.
- `AccountManagerViewModel.SetCalendarSyncAsync` enable/disable path (fills the gap the contact side left untested).
- CalDAV mapping tests restored/adapted per-account (from the deleted `CalDavCalendarSyncTests`).
