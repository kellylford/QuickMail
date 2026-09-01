# Settings Migration & Sync — PM + Dev Spec

**Status:** Draft for approval (Session 1)
**Issue:** [#507](https://github.com/kellylford/QuickMail/issues/507) — "I would like to have a way
to copy all my settings from one computer to another." P0 (§4.1) is that request, answered
directly: export on one computer, import on another. P1 (§4.2) goes past what was asked and keeps
the two machines in agreement afterwards.
**Drafted:** 2026-09-01
**Template:** `docs/planning/SPEC-TEMPLATE.md`

---

## Section 1: Executive Summary

A QuickMail user who installs the app on a second computer today starts from nothing: every
account re-entered by hand, every rule, saved view, template, flag, keyboard customization,
custom theme and address-book entry rebuilt from memory. Nothing in the app moves settings
between machines, and nothing keeps two machines in agreement.

This spec defines a two-rung ladder. **P0** ships an export/import settings bundle — one small
file that carries everything portable from one profile to another, with account identity
preserved so rules and views still resolve on arrival. **P1** reuses that exact bundle as the
unit of transfer for continuous sync over a pluggable transport, with the user's own mailbox as
the default (no new service, no new credential, no new OAuth scope) and a user-chosen folder
(OneDrive/Dropbox/any sync client) as the alternative.

The measured payload is what makes this tractable: everything worth moving in a real 8-account
profile is **under 100 KB**, roughly 20 KB compressed.

---

## Section 2: User Problem & Opportunity

### 2.1 Current state (verified)

Verified against a live profile at `%APPDATA%\QuickMail` (8 accounts) and against the code that
writes each file.

| Surface | Today | Pain | Who feels it |
|---|---|---|---|
| Second computer | Nothing transfers. Every account added by hand. | 8 accounts x (host, port, TLS, login name, signature) re-typed | Anyone with more than one machine |
| Client rules (`rules.json`) | Rebuilt by hand in the rules manager | Conditions and target folders re-chosen one dialog at a time | Rule users |
| Saved views (`views.json`) | Rebuilt by hand, including their hotkeys | Views are multi-folder; re-picking folders is among the slowest flows in the app | Power users |
| Address book (`contacts.json`, 58 KB live) | Local contacts and groups do not travel | Server-synced contacts re-fetch; **local** ones are simply lost | Everyone who added a local contact |
| Keyboard customizations (`hotkeys.json`) | Rebuilt entry by entry | A keyboard-centric user's whole muscle memory | The core audience |
| Templates, flags, watches, row layout, custom dictionary, custom themes | Rebuilt or lost | Long tail; individually small, collectively a day's work | Long-time users |
| Two machines in daily use | Permanently divergent | A rule added at the desk never exists on the laptop | Multi-machine users |
| Backup | None. A reinstall or a disk failure loses all of the above. | The same loss as a new computer, unplanned | Everyone |

**Full verified inventory** — every file the profile directory holds, with live sizes:

| File | Written by | Contents | Travels? |
|---|---|---|---|
| `accounts.json` (9.8 KB) | `AccountService.cs:20` | Accounts: id, hosts, ports, TLS, `LoginUsername`, **signatures**, provider, backend kind. **No passwords.** | **Yes — core** |
| `config.ini` (12 KB) | `ConfigService.cs:41` | `[global]` prefs, `[windowing]`, per-account `[account:{guid}]` overrides, `[features]` | **Yes, minus machine-local keys (§5.2)** |
| `rules.json` (1 KB) | `RuleService.cs:33` | Client rules; `AccountId` GUID + `TargetFolder` | **Yes — needs GUID remap** |
| `views.json` (0.6 KB) | `ViewService.cs:20` | Saved views; `ViewFolder.AccountId` GUID + folder path | **Yes — needs GUID remap** |
| `contacts.json` (58 KB) | `ContactService.cs:24` | Address book: local rows **and** server-synced slices keyed `(OwnerAccountId, Source)` | **Local rows + prior recipients only** |
| `groups.json` | `ContactService.cs:25` | Contact groups (`MemberContactIds`, int ids) | **Yes — with contacts; ids rewritten together** |
| `templates.json` | `TemplateService.cs:23` | Message templates | **Yes** |
| `hotkeys.json` | `ConfigService.cs:42` | Keyboard customizations | **Yes** |
| `flags.json` | `FlagService.cs:28` | User-defined flags (referenced by GUID from views and `DefaultFlagId`) | **Yes** |
| `watches.json` | `WatchService.cs:33` | Watched conversations (normalized subject — account-agnostic) | **Yes** |
| `rowlayout.json` | `RowLayoutService.cs:31` | Row field layout + speech settings | **Yes** |
| `folderviews.json` | `FolderViewStateService.cs:40` | Per-folder view state | **Yes (low value)** |
| `custom.lex` | `CustomDictionaryService.cs:27` | Custom dictionary, UTF-16 LE + BOM | **Yes** |
| `themes/` | `ThemeStore.cs:35` | User-authored themes | **Yes** |
| `mail.db` (18 MB) | `LocalStoreService.cs:21` | Message cache | **No — rebuilds from the server** |
| `msal.cache` (18 KB) | `OAuthService.cs:222` | **DPAPI-encrypted** MSAL token cache | **No — machine + user bound** |
| Windows Credential Manager | `CredentialService.cs` | Passwords, keyed `QuickMail:{accountId}` | **No — vault bound** |
| `quickmail.log`, `connection.log(.1)` (6.3 MB) | `LogService`, `ConnectionJournal` | Diagnostics | **No** |
| `.immutable-id-rebuilt` | `App.xaml.cs:329` | One-shot migration marker | **No** |

**Portable total from the live profile: ~82 KB.** That single number is what makes a mailbox
message, a cloud object, and a Dropbox file all equally viable transports.

### 2.2 Target personas

- **The two-machine user (primary).** Desktop and laptop, same accounts. Wants the laptop to
  behave exactly like the desktop. Today re-creates everything by hand, and the two drift apart
  within a week.
- **The new-machine migrator.** Bought a replacement PC. Wants a one-shot move and not
  necessarily ongoing sync. Cares that it is *complete* — a missing rule discovered three weeks
  later is worse than an obvious failure on day one.
- **The screen-reader power user (the core audience).** Has customized hotkeys, row speech
  fields, announcement categories and a theme to exact preference. Rebuilding that configuration
  by ear on a second machine is the most expensive re-setup in the app.
- **The cautious user.** Wants a backup before a Windows reinstall or a risky update. Has no
  second machine at all. Export alone solves their entire problem.
- **The IT-constrained user.** Work laptop where no third-party sync client is installable. Any
  design that *requires* a cloud service excludes them; a file they can carry, or their own
  mailbox, does not.

### 2.3 Why now

- The data is stable. The profile layout has settled: fourteen small files, each owned by exactly
  one service, each already round-tripped through JSON or INI.
- The hard part is already solved once. `FolderReferenceRemapper` (issue #529) exists precisely to
  rewrite folder references across an identity change, and to *disable and name* what it cannot
  match rather than guess. Import needs the same discipline and can reuse the same component.
- `ThemeService.ExportTheme` / `ImportTheme` is a shipped precedent for file-based export/import
  with UI, so the interaction pattern does not need inventing.
- Account count is growing (shared mailboxes on by default since 0.8.42), which multiplies the
  manual re-setup cost per machine.

---

## Section 3: Design Principles

1. **Never move a secret.** Passwords and OAuth tokens are machine-bound by design and stay that
   way. A bundle that *cannot* leak a credential can be emailed, copied to a stick, or left in a
   sync folder without a second thought. The cost is a re-authentication on arrival — and the
   design owns that moment explicitly rather than letting it surface as N failed connections.
2. **One bundle format, many transports.** Export-to-file, mailbox sync and folder sync must all
   move the *same* artifact. If P1 invents a second wire format, P0 becomes dead weight.
3. **Remap or disable — never guess.** A reference that cannot be resolved on the target machine
   is turned off and named to the user. Silently retargeting a rule to a similarly-named folder
   files the user's mail in the wrong place. This is `FolderReferenceRemapper`'s rule and it is
   binding here.
4. **Import is additive and reversible.** Import never deletes what it did not bring, and always
   writes a rollback copy of the profile first. A bad import must cost one dialog to undo.
5. **No new privacy surface without a proportionate reason.** A transport needing a new OAuth
   scope, a hosted service, or a copy of the user's data somewhere the project controls must be
   measurably better than one that needs none. Section 5.1 D3 concludes it is not, for v1.

---

## Section 4: Feature Scope & Acceptance Criteria

### 4.1 P0 — in scope (the one-time move)

| Feature | Setting / Shortcut | Default | Notes |
|---|---|---|---|
| Export settings to a file | Settings → Backup tab → **Export settings…**; command `settings.export` | No hotkey | Writes `QuickMail-settings-{yyyy-MM-dd}.qmsettings` (a zip) |
| Category selection on export | Checkbox list | All checked | Accounts, Rules & views, Address book, Templates, Keyboard, Appearance, Other settings |
| Import settings from a file | Settings → Backup tab → **Import settings…**; command `settings.import` | No hotkey | Adopt or merge (§5.1 D2) |
| Import preview | Dialog listing what will be added, updated and skipped | — | Nothing is written until the user confirms |
| Automatic rollback copy | — | Always | Pre-import profile copied to `backup-{timestamp}\` before any write |
| Post-import sign-in list | List of accounts needing authentication, **Sign in** per row | — | The re-auth moment, owned |
| Post-import folder heal | Reuses `FolderReferenceRemapper` after first sync | Always | Unmatched rules disabled and named |
| Import report | Copyable text summary | — | What came in, what was skipped, what was disabled, where the rollback is |

### 4.2 P1 — in scope (continuous sync)

| Feature | Setting | Default | Notes |
|---|---|---|---|
| Settings sync on/off | `SettingsSync` | **off** | Opt-in. Never on by default. |
| Transport choice | `SettingsSyncTransport` = `mailbox` \| `folder` | `mailbox` | §5.1 D3 |
| Mailbox transport | `SettingsSyncAccount` = `{guid}` | First non-POP3 account | Bundle stored as a message in a dedicated mail folder |
| Folder transport | `SettingsSyncFolder` = path | — | User points both machines at one synced folder |
| Sync cadence | — | On start, on settings change (debounced 30 s), every 15 min | Same debounce shape as compose autosave |
| Conflict handling | — | Remote-newer wins; loser preserved | Written to `conflicts\` as an importable `.qmsettings` |
| Sync status | — | Status bar text + `AnnouncementCategory.Status` | Respects `AnnounceStatus` |
| Sync now | Command `settings.syncNow` | No hotkey | Command palette only |

### 4.3 Explicitly out of scope

- **The message cache (`mail.db`) never transfers.** It is 18 MB, it is a cache, and it rebuilds
  from the server. Moving it would trade the entire value of a small bundle for a fragile one.
- **Passwords and OAuth tokens never transfer**, in P0 or P1, by any transport. DPAPI and the
  Credential Manager vault are user- and machine-scoped; a workaround would mean re-implementing
  credential storage in a portable, decryptable form. Not happening.
- **Field-level merge.** Conflict resolution in P1 v1 is whole-bundle, last-writer-wins, with the
  loser preserved. Merging two edits to the same rule is a v2 problem.
- **Real-time sync.** Cadence is minutes, not seconds. Settings are not a collaborative document.
- **Account deletion does not propagate in P1.** Removing an account on machine A leaves it on
  machine B. Deletion is the one operation where a sync bug is unrecoverable.
- **Server-synced contacts do not travel.** Rows whose `Source` is not `Local` re-fetch from the
  provider on the target machine and would only ever arrive stale.
- **A QuickMail-hosted sync service.** Evaluated in §5.1 D3 Option C, deferred to P2 with
  preconditions. Not built in P0 or P1.
- **Cross-platform or mobile.** Windows to Windows only.
- **Automatic discovery of the other machine** (LAN pairing, transfer codes). §5.1 D3 Option F.

---

## Section 5: Architecture & Technical Decisions

### 5.1 Key architectural decisions

---

#### Decision D1: The unit of transfer is a versioned zip bundle (`.qmsettings`), not a folder copy

A `.qmsettings` file is a zip containing `manifest.json` plus the selected profile files verbatim:

```
manifest.json     schemaVersion, appVersion, exportedUtc, deviceName, categories[], accounts[]
accounts.json     (category: accounts)
config.ini        (category: settings — machine-local keys stripped, see §5.2)
rules.json  views.json  flags.json  watches.json  folderviews.json  rowlayout.json
contacts.json     (local rows + prior recipients only)   groups.json
templates.json  hotkeys.json  custom.lex  themes/*.json
```

`manifest.json` carries an `accounts[]` array of `{ id, username, backendKind, displayName }` —
the map import needs for GUID remapping (D2) even when the accounts category itself was not
exported.

**Alternatives:**

1. **Document "copy `%APPDATA%\QuickMail`".** Zero code. But it copies an 18 MB cache and 6 MB of
   logs, copies an `msal.cache` that is useless on the target, copies a `.immutable-id-rebuilt`
   marker that suppresses a migration the new machine may need, has no answer for a target that
   already has accounts, and tells the user nothing about what succeeded. Retained only as a
   documented last resort in the user guide.
2. **A single flat JSON file.** Simpler, but `custom.lex` is UTF-16 LE with BOM and themes are a
   directory; both would need base64 smuggling. Zip carries them as themselves.
3. **Per-category files** (`rules.qmrules`, `views.qmviews`, …). More granular, but multiplies the
   UI by seven and loses the single manifest that makes GUID remapping possible.

**Rationale:** Zip is in the BCL (`System.IO.Compression`), keeps every payload byte-identical to
what the owning service already reads and writes (so no serializer forks), is inspectable with
Explorer, and compresses the measured 82 KB to roughly 20 KB. **Unencrypted by default** — it
contains no credentials, and inspectability is worth more than the false comfort of a password on
a file holding no secrets. The export dialog states plainly that the bundle contains email
addresses, server names, signatures and the local address book, and lets the user uncheck the
address book.

---

#### Decision D2: Import preserves source account GUIDs when it can, and remaps by `(Username, BackendKind)` when it cannot

This is the load-bearing decision. `MailRule.AccountId`, `ViewFolder.AccountId`,
`ContactModel.OwnerAccountId`, `ConfigModel.Accounts[Guid]` and `ConfigModel.StartupFolderAccount`
are all `Guid` references into `accounts.json`. A naive import that lets the target machine mint
fresh GUIDs produces a profile where every rule and every view silently points at nothing — the
worst possible failure, because it looks like success.

Two modes, chosen automatically and shown in the preview:

- **Adopt** — the target profile has no accounts (the headline "new computer" case).
  `accounts.json` is written verbatim, GUIDs and all. Every reference in every other file resolves
  untouched. No rewriting, therefore no rewriting bugs.
- **Merge** — the target already has accounts. Build `sourceGuid → localGuid` by matching
  `Username` case-insensitively **and** `BackendKind` exactly. A source account with no local
  match is added keeping its own GUID (collision-checked first). Then rewrite every GUID reference
  through the map. Anything unmatched is reported, never dropped silently.

**Alternatives:**

1. **Always mint new GUIDs and rewrite.** One code path instead of two, but it forces the primary
   use case through the risky path even when nothing needs rewriting at all.
2. **Match on email address only.** Breaks when the same address exists under two backends —
   exactly the IMAP→Graph conversion state `GraphConversionPending` exists for.
3. **Ask the user to pair accounts manually.** Correct in every case, unusable in the common one.
   Kept as the fallback the preview offers when a match is ambiguous.

**Rationale:** Adopt makes the primary case trivially correct. `(Username, BackendKind)` is the
identity pair `AccountService` already treats as unique. The remapper is a **pure static class**
taking loaded models and returning a report — directly modeled on `FolderReferenceRemapper`, and
testable without a profile, a window, or a network.

---

#### Decision D3: P1 sync transport — the user's own mailbox by default, a user-chosen folder as the alternative. No QuickMail-hosted service in v1.

This is the option space the directive asked to be researched. Six options, judged on setup cost
for the user, infrastructure and privacy cost for the project, who each excludes, and build cost.

| Option | How it works | Setup for user | Cost to project | Excludes | Verdict |
|---|---|---|---|---|---|
| **A. Mailbox folder** | Bundle stored as a message in a dedicated mail folder, on an account QuickMail already authenticates to | **None** — the account is already added | None. No service, no scope, no key, no host. | POP3-only profiles; needs one account added manually first | **Chosen — P1 default** |
| **B. User-chosen sync folder** | Bundle written to a folder the user picks; OneDrive/Dropbox/Drive/iCloud moves it | Pick a folder on each machine | None | Users with no sync client | **Chosen — P1 alternative** |
| **C. Cloudflare Worker + R2/D1** | A QuickMail-hosted endpoint holds the bundle | Sign in or pair | **High — see below** | Nobody | **Deferred to P2** |
| **D. Provider app-folder (OneDrive `approot` / Drive `appDataFolder`)** | Bundle in a hidden per-app cloud folder | None | New OAuth scope per provider + a Google re-verification cycle | **Work/school Microsoft accounts** | **Rejected** |
| **E. Gist or the user's own git repo** | Bundle committed to a repo the user owns | GitHub account + PAT | Low | Almost every non-developer | **Deferred (pluggable)** |
| **F. LAN pairing / transfer code** | Direct machine-to-machine transfer | Both machines on, at once | Medium (NAT, Windows firewall) | Non-simultaneous migrations | **Rejected** |

**Why A wins.** QuickMail is a mail client. On every machine it already holds an authenticated,
encrypted, server-backed, per-user, cross-device store — the mailbox. Storing an 82 KB bundle as a
message in a dedicated folder needs **no new service, no new credential, no new OAuth scope, no
new privacy disclosure beyond "QuickMail keeps a settings message in a folder in your mailbox",
and no new failure mode the app does not already handle.** It works identically over IMAP (`APPEND`
to a folder) and Graph (create a message in a mail folder). Message history in that folder is free
version history: keep the last N and the user can roll back to last Tuesday.

Its costs are real and bounded, and the design absorbs them: the folder is visible to other mail
clients, so it is named plainly (`QuickMail Settings`) rather than hidden and mysterious; a
POP3-only profile cannot host it, so it falls back to B; and there is a chicken-and-egg — the user
must add **one** account on machine 2 before anything can restore, which is exactly the P0 import
flow reduced to a single account. A user rule could in principle move or file the settings message,
so the sync reader locates it by a stored message id *and* by a folder scan, and re-creates the
message if both fail.

**Why B is the necessary companion.** It costs almost nothing on top of P0 — write the bundle to a
path instead of a chosen file, poll the path for changes — and it covers POP3-only users, users
whose mail admin dislikes unfamiliar folders, and users who simply prefer their existing sync
client. It is the pattern KeePass, Obsidian-before-Sync and many others shipped successfully. Its
one real hazard is the sync client reading a half-written file, handled by writing to a temp name
in the same directory and moving into place.

**Why C is deferred, with numbers.** The Cloudflare free tier is real and generous — from
Cloudflare's own pricing page: Workers **100,000 requests/day**; R2 **10 GB-month**, **1M Class A
(write) operations/month**, **10M Class B (read) operations/month**, **free egress**; D1 **5 GB**,
**5M rows read/day**, **100k rows written/day**. Workers KV is the trap: **1,000 writes/day for
the whole account**, so a KV-backed design exhausts the free tier at a few dozen users. The correct
free-tier shape is **R2 for the bundle blob plus D1 for metadata**, supporting on the order of 30k
writes/day. So the *infrastructure* is genuinely free and genuinely sufficient. That is not the
cost.

The cost is that QuickMail would become a service that holds user data. That means an identity
model (who owns this blob, and how is that proven?), an abuse and DoS surface on a public
endpoint, a data-retention and deletion policy, a rewrite of `docs/privacy.html` adding both a new
network destination and a new category of stored data — the page cited by Google OAuth
verification and by the winget manifest's `PrivacyUrl` — and a runtime dependency whose outage
becomes the maintainer's pager. **If it is ever built it must be end-to-end encrypted**: the
bundle encrypted client-side with a key derived from a passphrase the user carries between
machines, so the Worker stores an opaque blob and a routing id and never holds the key. Under that
constraint the server is a dumb relay and the privacy story is defensible. That is a P2
conversation, not a P1 one.

**Why D is rejected outright.** It looks free and invisible and it is neither.
`Files.ReadWrite.AppFolder` is **valid for personal Microsoft accounts only** — Microsoft has
announced no plan to extend it to OneDrive for Business — so it fails precisely the work-account
users who most need settings to follow them between a desk and a laptop. On the Google side,
`drive.appdata` puts a *Drive* scope on a *mail* client's consent screen and requires another
verification cycle with written justification and a demo video (QuickMail is already through
verification for `contacts.readonly`, so this cost is known and real). It must also be confirmed
in the Cloud Console that `drive.appdata` is classified **sensitive** and not **restricted** —
restricted would add an annual third-party CASA security assessment, which ends the discussion.
Two provider-specific implementations and two consent-screen regressions, one of which cannot
serve work accounts, to achieve what Option A achieves with zero new permissions.

**Rationale for the pair:** A and B share one interface —

```csharp
public interface ISettingsSyncTransport
{
    Task<SyncStamp?> GetRemoteStampAsync(CancellationToken ct);
    Task<Stream?>    TryPullAsync(CancellationToken ct);
    Task             PushAsync(Stream bundle, SyncStamp stamp, CancellationToken ct);
}
```

E, C and WebDAV then become additional implementations rather than redesigns.

---

#### Decision D4: Conflict resolution is whole-bundle last-writer-wins, with the loser preserved

Each bundle carries `exportedUtc` and a `deviceId`. On pull, if the remote stamp is newer than the
local `LastSyncedUtc` **and** the local profile has changed since that sync, that is a conflict:
the remote bundle is applied, and the **local** state is first written to
`{ProfileDir}\conflicts\{timestamp}-{device}.qmsettings`, which the user can import to recover.
The user is told via `AnnouncementCategory.Result` and a status line.

**Alternatives:** per-file last-writer-wins (helps only when two machines edited different files —
plausible, but doubles the state to track for a modest win); three-way merge (needs a common
ancestor and a merge rule per file type — v2 at the earliest); prompt on every conflict (a modal
that fires while the user is reading mail).

**Rationale:** Settings edits are rare and bursty; conflicts will be uncommon. What matters is that
a conflict is never silent data loss — hence the preserved loser, which reuses the P0 bundle format
and the P0 import path for recovery. No new recovery machinery is invented.

---

#### Decision D5: `SettingsBundleService` owns the format; the owning services keep owning their files

`SettingsBundleService` reads and writes profile files **as bytes** for everything except the four
that need transformation: `config.ini` (key filtering), `contacts.json` (slice filtering),
`accounts.json` (collision checking), and GUID rewriting across the set. It does not re-serialize
what it does not need to change, so a future field added to `SavedView` needs no bundle change.

**Alternative:** route everything through the owning services (`IViewService.GetAll()` →
serialize). Cleaner in principle, but it makes the bundle format track every model change and
turns a byte copy into a fifteen-service orchestration.

---

#### Decision D6: Import writes a full profile rollback copy first

Before the first byte is written, every file in §2.1's "travels" set is copied to
`{ProfileDir}\backup-{yyyyMMdd-HHmmss}\`. The import report names that directory. This costs 82 KB
and it is what makes Principle 4 true rather than aspirational.

### 5.2 Machine-local vs portable configuration

`config.ini` mixes user preferences with facts about *this installation*. The rule:

> **A key that records a user preference travels. A key that records something about this machine,
> this install, or this session stays.**

| Key | Classification | Why |
|---|---|---|
| `DesktopShortcutPrompted` | **Local** | About this machine's desktop; travelling means machine 2 never offers a shortcut |
| `LastRunVersion`, `NativeArmNoticeVersion` | **Local** | About this install's update and architecture history |
| `EnableLogging`, `ConnectionDiagnostics`, `LogFormat` | **Local** | Troubleshooting state belongs to the machine being troubleshot |
| `LastMoveFolder` | **Local** | An MRU scoped to one account and folder; low value, high staleness |
| `TutorialCompleted`, `TrayHintShown` | **Travels** | Record what the *user* has already learned, not what the machine has shown |
| `AutoUpdate`, `ShowUpdateInstalledAlerts` | **Travels** | User preferences about updates |
| `StartupFolder`, `StartupFolderAccount`, `StartupFolderLabel` | **Travels, remapped** | Account GUID (D2) plus a folder reference (`FolderReferenceRemapper`) |
| `[account:{guid}]` sections | **Travels, remapped** | Per-account overrides; GUID rewritten by D2 |
| `[features]` | **Travels** | Feature-flag opt-ins are user choices |
| Everything else in `[global]` and `[windowing]` | **Travels** | Preferences |

The classification lives in one place — a static set in `SettingsBundleService` — and is asserted
by a test that **fails when a new `ConfigModel` property is added without being classified**. That
test is the mechanism; without it this table rots within two releases.

### 5.3 Runtime mode compatibility

| Mode | `LocalStoreService` available? | Does this feature call it? | Behaviour |
|---|---|---|---|
| Normal | Yes | **No** | The bundle touches no message data |
| `--online` | No | **No** | Fully functional — a genuine advantage of excluding `mail.db` |
| `--profileDir <path>` | Yes | — | Export/import operate on that profile. **This is also the safest way to test import**: import into a scratch profile before touching the real one. |

P1's mailbox transport additionally needs a mail connection; the folder transport needs none and
works entirely offline until the sync client catches up.

### 5.4 Shared component audit (mandatory)

| Component | File | Other consumers | Change needed | Risk |
|---|---|---|---|---|
| `SettingsDialog.xaml` | `Views/SettingsDialog.xaml` | Opened from the main menu only; 6 tabs (General, Advanced, Keyboard Shortcuts, Startup, Windowing, Appearance) | Add a 7th **Backup** tab | Existing tabs untouched. Verify Ctrl+Tab still reaches all 7 and each tab's mnemonic is still unique (`_B` is free). |
| `FolderReferenceRemapper` | `Services/FolderReferenceRemapper.cs` | One caller: the #529 IMAP→Graph conversion path | **Add a second call site** (post-import, after first sync). No signature change. | Already `static`, pure, and idempotent by design (it recognizes an already-matched reference). The new caller must pass *freshly synced* folders, exactly as the existing one does. |
| `ConfigService` | `Services/ConfigService.cs` | Nearly everything | Add `ReloadAsync()` so an import takes effect without a restart | **Highest-risk change in the spec.** Every consumer holding a cached `ConfigModel` reference must re-read. The audit of those captures is a named Phase 3 deliverable, not an assumption. |
| `AccountService` | `Services/AccountService.cs` | `App.xaml.cs` wiring, `MainViewModel`, every mail service | Add bulk `ImportAccountsAsync(list, mode)` | Must raise the same change notification the add-account path raises, or the folder tree stays empty until restart. |
| `ContactService` | `Services/ContactService.cs` | Address book, compose autocomplete | Add local-slice export and import | Follow the existing `(OwnerAccountId, Source)` slice-replacement model. Import must **not** clobber server-synced slices. |
| `CommandRegistry` | `Services/CommandRegistry.cs` | Command palette, hotkeys dialog | Register `settings.export`, `settings.import`, and P1 `settings.syncNow` | Category `Settings`; no default keys, so no collisions. |
| `ThemeStore` | `Services/ThemeStore.cs` | Theme manager | Read/write the `themes/` directory during bundle build | Built-in themes are embedded resources, not files — export must not attempt to write them. |
| `ProfileContext` | `Services/ProfileContext.cs` | Every file-writing service | **None** | Confirmed. `backup-*` and `conflicts\` are created under `ProfileDir`. |
| `ICredentialService` | `Services/CredentialService.cs` | Auth paths | **None** | Confirmed explicitly: nothing in this feature reads or writes credentials. |

**Summary:** this feature modifies `SettingsDialog`, `ConfigService`, `AccountService`,
`ContactService` and `CommandRegistry`, and adds a second caller to `FolderReferenceRemapper`.
`ThemeStore` needs no behavioural change, and `ProfileContext` and `CredentialService` are not
touched at all. The `ConfigService.ReloadAsync` change is backward-compatible **only if** every
cached `ConfigModel` reference is audited, which Phase 3 owns.

---

## Section 6: Keyboard Walkthrough (Mandatory)

### Path 1: Export settings (happy path)

1. User opens Settings, presses Ctrl+Tab to the **Backup** tab. **Expected:** Screen reader
   announces "Backup". Focus lands on the first control, the **Export settings** button.
2. User presses Enter. **Expected:** The Export Settings dialog opens (modeless — see §7). Focus
   lands on the category list. Screen reader announces "What to export, list, Accounts, checked,
   1 of 7."
3. User arrows through the list and presses Space on "Address book". **Expected:** The checkbox
   toggles. The platform reports the new state; **no custom announcement is made** (CLAUDE.md:
   never announce what the platform already reports).
4. User presses Tab. **Expected:** Focus moves to a read-only summary text: "6 categories
   selected. The file will contain email addresses, server names and signatures. It contains no
   passwords."
5. User presses Tab, then Enter on **Export…**. **Expected:** A standard Save dialog opens with
   the filename pre-filled as `QuickMail-settings-2026-09-01.qmsettings`.
6. User confirms the save. **Expected:** The Save dialog closes, the Export dialog closes, focus
   returns to the **Export settings** button in the Backup tab. Screen reader announces (Result):
   "Settings exported. 6 categories, 21 kilobytes."

### Path 2: Import onto a brand-new machine (adopt mode — the P0 headline case)

1. Fresh install, no accounts. User opens Settings → Backup, Tabs to **Import settings**, Enter.
   **Expected:** A standard Open dialog appears, filtered to `*.qmsettings`.
2. User selects the bundle. **Expected:** The Open dialog closes. The Import Preview dialog opens.
   Focus lands on a read-only summary. Screen reader announces: "Import preview. From DESKTOP-KL,
   exported 1 September 2026. This profile has no accounts, so accounts will be added exactly as
   they were: 8 accounts, 4 rules, 3 saved views, 212 contacts, 6 templates, 14 keyboard
   customizations, 2 themes."
3. User presses Tab. **Expected:** Focus moves to a details list, one row per category, each row's
   accessible name being "Accounts, 8 items, will be added."
4. User presses Tab twice to **Import**, Enter. **Expected:** The dialog closes. A progress status
   is announced (Status): "Importing settings." Then (Result): "Settings imported. 8 accounts
   added. A copy of your previous settings is in backup-20260901-143022."
5. **Expected, immediately after:** The Sign In dialog opens with focus on a list of the 8
   accounts, each row named "kelly@example.com, needs sign-in". Screen reader announces (Hint):
   "Your settings arrived without passwords. Choose an account and press Enter to sign in."
6. User presses Enter on the first row. **Expected:** The normal per-account authentication flow
   for that account's `AuthType` runs. On success the row's name becomes "kelly@example.com,
   signed in" and focus stays on the row.
7. User presses Escape. **Expected:** The Sign In dialog closes. Focus returns to the Backup tab's
   **Import settings** button. Accounts not yet signed in remain listed in the Backup tab as
   "3 accounts still need sign-in", with a button to reopen the list.
8. First sync completes. **Expected:** If any rule's target folder could not be matched,
   `FolderReferenceRemapper` disables and names it, and the existing report announcement fires
   (Result): "Turned off rules whose folder could not be matched — set a folder to re-enable: …".

### Path 3: Import onto a machine that already has accounts (merge mode)

1. Steps 1–2 as above. **Expected:** The summary instead announces: "Import preview. This profile
   already has 3 accounts. 2 accounts in the file match accounts you already have and their
   settings will be updated. 6 accounts will be added. Nothing will be deleted."
2. User arrows the details list. **Expected:** Each account row is named by outcome, e.g.
   "kelly@example.com, matched, settings will be updated" / "work@example.com, new, will be added".
3. **Ambiguous match edge case:** where an address matches two local accounts of different backend
   kinds, the row is named "kelly@example.com, could not match, choose an account", and activating
   it opens a small chooser. **Expected:** Until every ambiguous row is resolved or set to "skip",
   the **Import** button is disabled and its accessible name is unchanged (the disabled state is
   what the platform reports).
4. User presses Enter on **Import**. **Expected:** As Path 2 step 4, with the merge counts.

### Path 4: Import fails (error case)

1. User selects a corrupt or truncated file. **Expected:** No preview dialog opens. A message
   dialog states "This file could not be read as a QuickMail settings file." Focus returns to the
   **Import settings** button.
2. User selects a bundle whose `schemaVersion` is newer than this build supports. **Expected:**
   "This file was made by a newer version of QuickMail (0.9.2). Update QuickMail and try again."
   Nothing is written.
3. **Mid-import failure** (disk full, file locked). **Expected:** Import stops, the rollback copy
   is restored automatically, and the report reads "Import failed and your previous settings were
   restored from backup-20260901-143022." Focus returns to the **Import settings** button.

### Path 5: Turn on mailbox sync (P1)

1. In the Backup tab, user Tabs to **Keep settings in sync**, a checkbox, and presses Space.
   **Expected:** The checkbox state is reported by the platform. Two previously-disabled controls
   below become enabled.
2. User Tabs to **Sync using**, a radio group with one tab stop
   (`TabNavigation="Once"`, `DirectionalNavigation="Cycle"`). **Expected:** Screen reader announces
   "Sync using, My mailbox, selected, 1 of 2."
3. User presses Down. **Expected:** "A folder on this computer" is selected — arrowing selects, it
   does not merely move focus (issue #441). The folder path box and **Browse…** button below become
   enabled.
4. User presses Up to return to "My mailbox", then Tab. **Expected:** Focus moves to the **Account**
   combo box, listing non-POP3 accounts. Its items announce their display text via `ToString()`.
5. User Tabs to **Close** and presses Enter. **Expected:** The first sync runs in the background.
   Status text and a Status announcement: "Settings sync on. Settings saved to your mailbox."

### Path 6: A conflict is detected (P1)

1. The user edited a rule on the laptop while the desktop pushed a change. On the laptop's next
   sync. **Expected:** The remote bundle is applied. A Result announcement fires: "Settings updated
   from your other computer. Your unsaved changes from this computer were kept in the conflicts
   folder — open Settings, Backup to restore them."
2. User opens Settings → Backup. **Expected:** A **Recover a conflicting copy…** button is present
   (and absent when the `conflicts\` directory is empty). Activating it opens the standard import
   preview against that file.

---

## Section 7: Accessibility Checklist (Mandatory)

- **`AutomationProperties.Name` values introduced** (short labels only, no roles, no hints):
  "Backup", "Export settings", "Import settings", "What to export", "Import preview",
  "Import details", "Accounts needing sign-in", "Keep settings in sync", "Sync using",
  "Sync account", "Sync folder", "Recover a conflicting copy".
- **Announcements**, all through `AccessibilityHelper.Announce` with an explicit category:
  | Text | Category | Why |
  |---|---|---|
  | "Settings exported. N categories, N kilobytes." | **Result** | Outcome of an explicit user action |
  | "Importing settings." | **Status** | Background progress |
  | "Settings imported. N accounts added. A copy of your previous settings is in …" | **Result** | Outcome |
  | "Import failed and your previous settings were restored from …" | **Result** | Outcome |
  | "Your settings arrived without passwords. Choose an account and press Enter to sign in." | **Hint** | Instructional; fires once on the sign-in list gaining focus |
  | "Settings sync on. Settings saved to your mailbox." | **Status** | Background state |
  | "Settings updated from your other computer. …" | **Result** | Outcome of a background operation the user must know about |
  No announcement is made for a checkbox toggling, a radio selection changing, or a list selection
  moving — the platform reports those already.
- **Screen reader browse mode / WebView2:** none. This feature introduces no WebView2 surface.
- **Focus restoration:** every dialog captures the invoking control and returns focus to it on
  close — Export and Import return to their Backup-tab buttons; the sign-in list returns to
  **Import settings**.
- **F6 ring:** **no change.** All new surfaces are dialogs and a tab inside the existing Settings
  dialog; no new main-window pane is added, so `CycleFocusAsync` and `GetFocusedPaneIndex` in
  `MainWindow.xaml.cs` are untouched.
- **Radio groups:** one — "Sync using" (mailbox / folder). Container gets
  `KeyboardNavigation.TabNavigation="Once"` and `DirectionalNavigation="Cycle"`, shared
  `GroupName`, and arrowing must *select*, not merely move focus (#441).
- **`Selector`-bound item types:** the category list, the import-details list, the sign-in list and
  the sync-account combo all bind item types that **must override `ToString()`**, and each must be
  added to `SelectorItemAccessibilityTests`. This is the failure mode that passes visual review
  every time.
- **Color-only information:** none. Every row state (added / updated / skipped / needs sign-in) is
  in the row's text and therefore in its accessible name.
- **Modal vs modeless:** the Settings dialog does not host a live WebView2, so `ShowDialog()` is
  safe here by the CLAUDE.md rule. The Export and Import dialogs contain editable text (the folder
  path box) — they are opened from Settings, **not** over an open message, so the GrabAddresses
  deadlock condition does not apply. **Constraint for implementation:** the Backup tab's actions
  must not be reachable from a surface that has a live WebView2; if that changes, the dialogs move
  to `Show()` with explicit Escape and Cancel wiring.
- **No event raised into the parent while a dialog is open:** import completion must not fire
  `ViewsChanged` (or anything that rebuilds the main window's menus) from inside the dialog's
  message loop. The dialog sets a flag; the caller refreshes **after** `ShowDialog()` returns.

---

## Section 8: Acceptance Walkthrough (Mandatory)

Run with `--profileDir` scratch profiles so the live profile is never at risk.

### Scenario 1: Round-trip into an empty profile (primary happy path)

**Setup:** Real profile with accounts, rules, views and contacts. A second, empty scratch profile.

1. In the real profile, export all categories. **Verify:** A `.qmsettings` file exists, is under
   100 KB, and opens in Explorer showing `manifest.json` and the expected files.
2. Launch with `--profileDir <scratch>`. Complete no setup. Open Settings → Backup → Import, select
   the file. **Verify:** The preview says "This profile has no accounts", and the counts match the
   source exactly.
3. Confirm the import. **Verify:** The report names a `backup-*` directory that exists.
4. **Verify:** The account list shows every source account with the same display names, and
   `accounts.json` in the scratch profile contains the **same GUIDs** as the source.
5. **Verify:** Every rule appears in the rules manager with its target folder intact, and every
   saved view lists the same folders under the same account names.
6. **Verify:** Keyboard customizations, templates, flags, the custom dictionary and custom themes
   are all present.
7. Sign in to one account. **Verify:** Mail loads; the rules for that account are enabled and their
   target folders resolve.

### Scenario 2: Merge into a populated profile

**Setup:** Scratch profile with 2 of the source accounts already added manually (so with *different*
GUIDs), plus one rule of its own.

1. Import the same bundle. **Verify:** The preview reports 2 matched, N added, 0 deleted.
2. Confirm. **Verify:** The pre-existing local rule still exists and still works — **import is
   additive**.
3. **Verify:** Imported rules that belonged to the 2 matched accounts now carry the **local** GUIDs,
   and they appear under the correct account in the rules manager.
4. **Verify:** No duplicate accounts were created for the 2 matched addresses.

### Scenario 3: Error cases

1. Import a file truncated to 100 bytes. **Verify:** A clear message, nothing written, no `backup-*`
   directory created, focus returned to the Import button.
2. Hand-edit a bundle's `manifest.json` to `schemaVersion: 999` and import. **Verify:** The
   "newer version" message; nothing written.
3. Import a bundle whose accounts category was unchecked at export. **Verify:** Rules and views
   still import, and any whose account cannot be resolved are reported as skipped — not dropped
   silently, and not attached to the wrong account.

### Scenario 4: Shared-component regressions (one per §5.4 consumer)

1. **SettingsDialog:** open Settings, Ctrl+Tab through all 7 tabs. **Verify:** every tab is
   reachable, each announces its name, and every pre-existing setting still saves.
2. **ConfigService.ReloadAsync:** import a bundle whose `AppearanceThemeId` and `PreviewLines`
   differ. **Verify:** the theme changes and the message list preview lines change **without an app
   restart**, and the change persists after restart.
3. **AccountService:** after an import, **verify** the folder tree shows the new accounts without a
   restart.
4. **ContactService:** on a profile with server-synced contacts, import a bundle with local
   contacts. **Verify:** local contacts arrive **and** the synced contacts are still present.
5. **FolderReferenceRemapper:** import into a profile whose Graph account has different folders.
   **Verify:** unmatched rules are disabled and named, never retargeted.
6. **CommandRegistry:** open the command palette. **Verify:** "Export settings" and "Import
   settings" appear under Settings, and both appear in the keyboard customizations dialog.

### Scenario 5: Every new setting toggles live

1. Toggle **Keep settings in sync** on and off. **Verify:** dependent controls enable and disable
   immediately, with no restart.
2. Switch **Sync using** between the two radio options with arrow keys. **Verify:** the selection
   changes on arrow (not just focus), and the folder controls enable and disable to match.

### Scenario 6: `--online` mode

1. Run with `--online` and export, then import into a scratch profile, also with `--online`.
   **Verify:** both complete normally. Nothing in this feature touches `mail.db`.

### Scenario 7: Screen reader pass

1. Tab through every new control in the Backup tab and both dialogs. **Verify:** each announces a
   short label with no role name, no keyboard shortcut and no instruction sentence.
2. **Verify:** the sign-in list, category list, details list and account combo announce their item
   text — not a type name such as `QuickMail.Models.…`. Confirm via the automation peer, not by
   looking at the screen.
3. **Verify:** each announcement in §7 fires exactly once, at the stated moment, and that turning
   off `AnnounceResults` silences the Result ones while leaving the visible status text.

---

## Section 9: Success Metrics

- **Behavioral (P0):** a user with 8 accounts reaches a fully configured second machine in under
  five minutes plus authentication time — one export, one import, N sign-ins.
- **Completeness:** every file in the §2.1 "travels" set round-trips. Asserted by a test that
  enumerates the profile directory and fails on an unclassified file.
- **Correctness:** after an adopt import, zero rules and zero views point at a non-existent
  account. After a merge import, the same.
- **Non-destructive:** no acceptance scenario results in the loss of anything that was in the
  target profile beforehand.
- **Keyboard-centric:** export, import, sign-in and every sync setting are operable keyboard-only,
  end to end.
- **Accessibility:** every new list and combo announces its item text via `ToString()`, proven by
  `SelectorItemAccessibilityTests`, not by inspection.
- **Online mode:** export and import both work under `--online`.
- **P1:** a change made on machine A appears on machine B within one sync interval, and a manufactured
  conflict always leaves a recoverable copy.

---

## Section 10: Implementation Phases

### Phase 1 — Bundle format and the pure remapper (no UI)

**Goal:** A bundle can be built from a profile directory and applied to another, in code.

**Deliverables:** `Models/SettingsBundleManifest.cs`; `Services/SettingsBundleService.cs`
(+ `ISettingsBundleService.cs`); `Services/AccountIdRemapper.cs` (pure static, modeled on
`FolderReferenceRemapper`, returning a report); the machine-local key classification set.

**Tests:** `SettingsBundleServiceTests` (build → apply round-trip in temp dirs; category subsets;
unknown files ignored; newer `schemaVersion` rejected). `AccountIdRemapperTests` (adopt path leaves
GUIDs untouched; merge maps by username+backend; ambiguous match reported not guessed; unmatched
references reported not dropped). `ConfigKeyClassificationTests` (**fails when a new `ConfigModel`
property is unclassified**).

**Risk:** the GUID rewrite missing a reference site — the failure that looks like success.
Mitigation: the remapper takes every referencing model explicitly, so a new referencing model is a
compile error, and a test asserts no residual source GUID remains anywhere in the applied profile.

**Duration:** 5–7 hours.

### Phase 2 — Export UI

**Goal:** A user can produce a bundle from the app.

**Deliverables:** Backup tab in `Views/SettingsDialog.xaml`; `Views/ExportSettingsDialog.xaml(.cs)`;
`ViewModels/ExportSettingsViewModel.cs`; `settings.export` registered in `CommandRegistry`.

**Tests:** `XamlParseTests` for the new dialog; `ExportSettingsViewModelTests` (category selection,
default filename, size estimate); `SelectorItemAccessibilityTests` entry for the category item type;
`CommandRegistryTests` entry.

**Risk:** the new tab breaks Ctrl+Tab order or duplicates a mnemonic. Caught in Scenario 4.1.

**Duration:** 3–4 hours.

### Phase 3 — Import UI, preview, rollback, and live reload

**Goal:** A user can import a bundle and see the result without restarting.

**Deliverables:** `Views/ImportSettingsDialog.xaml(.cs)`; `ViewModels/ImportSettingsViewModel.cs`;
`ConfigService.ReloadAsync()` **plus the audit of every cached `ConfigModel` reference**;
`AccountService.ImportAccountsAsync`; `ContactService` local-slice import; rollback copy and
restore-on-failure; `settings.import` registered.

**Tests:** `ImportSettingsViewModelTests` (adopt vs merge preview text, ambiguous rows block
import, counts); `ConfigServiceReloadTests` (a reloaded value is observed by a bound consumer);
`SettingsImportRollbackTests` (a mid-import failure restores the profile byte-for-byte).

**Risk:** **This is the risky phase.** A stale cached `ConfigModel` makes half the app ignore the
import until restart — and it will look fine in a quick test because the visible settings happen to
re-read. Mitigation: the audit is an explicit deliverable, and Scenario 4.2 tests a setting that is
read at a *different* layer (theme) from the one used for the smoke test (preview lines).

**Duration:** 8–10 hours.

### Phase 4 — Post-import sign-in and folder heal

**Goal:** The arrival experience is owned rather than emergent.

**Deliverables:** `Views/PostImportSignInDialog.xaml(.cs)` + VM, reusing the existing per-`AuthType`
auth flows; second call site for `FolderReferenceRemapper` after the first post-import sync;
persistent "N accounts still need sign-in" affordance in the Backup tab.

**Tests:** `PostImportSignInViewModelTests` (row states, per-account outcome); a regression test
that the remapper's second caller is idempotent when run twice.

**Risk:** re-authentication differs per `AuthType` (password / MSAL / Google) and the shared-mailbox
`ParentAccountId` case must not be offered its own sign-in. Mitigation: the list is built from the
same predicate the account manager uses to decide an account is signable.

**Duration:** 5–6 hours.

**— P0 ships here. Phases 5–6 are P1 and should be a separate spec review. —**

### Phase 5 — Sync engine and the folder transport

**Goal:** Two machines pointed at one folder converge.

**Deliverables:** `ISettingsSyncTransport`; `Services/FolderSyncTransport.cs` (temp-write + move);
`Services/SettingsSyncService.cs` (cadence, debounce, stamps, conflict preservation);
`SettingsSync*` config keys; Backup tab sync controls; `settings.syncNow`.

**Tests:** `SettingsSyncServiceTests` with a fake transport (no-change no-ops; remote-newer applies;
both-changed preserves the loser; debounce coalesces). `FolderSyncTransportTests` (a partially
written file is never read).

**Risk:** a sync loop — applying a remote bundle marks the profile changed, which pushes, which the
other machine applies. Mitigation: applying a remote bundle sets `LastSyncedUtc` to the *remote*
stamp and suppresses the change signal for that write; asserted by a test that runs two fake
machines to a fixed point.

**Duration:** 8–10 hours.

### Phase 6 — Mailbox transport

**Goal:** Sync with zero user setup on an already-added account.

**Deliverables:** `Services/MailboxSyncTransport.cs` over `IMailService` (IMAP `APPEND` / Graph
create); folder creation and discovery; retention of the last N versions; POP3-only fallback to the
folder transport with a clear message.

**Tests:** `MailboxSyncTransportTests` against the existing GreenMail integration harness (from the
#304 testing work) — round-trip, folder auto-create, recovery when the message is moved or deleted.

**Risk:** provider quirks (folder naming, delimiters, message size, a user rule filing the message).
Mitigation: locate by stored message id **and** folder scan, re-create if both fail; the GreenMail
harness covers IMAP, and Graph gets a manual pass in the acceptance walkthrough.

**Duration:** 8–10 hours.

---

## Section 11: Files to Create / Modify

### Files to create

| File | Purpose | Lines (est.) |
|---|---|---|
| `Models/SettingsBundleManifest.cs` | Manifest DTO + schema version | 60–80 |
| `Models/SettingsCategory.cs` | Category enum + display item (`ToString()` override) | 40–60 |
| `Services/ISettingsBundleService.cs` | Interface | 30–40 |
| `Services/SettingsBundleService.cs` | Build / inspect / apply a bundle | 350–450 |
| `Services/AccountIdRemapper.cs` | Pure GUID remap + report | 150–200 |
| `Views/ExportSettingsDialog.xaml(.cs)` | Export UI | 120–160 |
| `ViewModels/ExportSettingsViewModel.cs` | Export VM | 120–160 |
| `Views/ImportSettingsDialog.xaml(.cs)` | Preview + confirm UI | 180–240 |
| `ViewModels/ImportSettingsViewModel.cs` | Import VM | 220–300 |
| `Views/PostImportSignInDialog.xaml(.cs)` | Sign-in list | 120–160 |
| `ViewModels/PostImportSignInViewModel.cs` | Sign-in VM | 120–160 |
| *P1:* `Services/ISettingsSyncTransport.cs` | Transport interface | 30–40 |
| *P1:* `Services/FolderSyncTransport.cs` | Folder transport | 120–160 |
| *P1:* `Services/MailboxSyncTransport.cs` | Mailbox transport | 250–320 |
| *P1:* `Services/SettingsSyncService.cs` | Sync engine | 300–400 |

### Files to modify

| File | Changes | Lines (est.) |
|---|---|---|
| `Views/SettingsDialog.xaml` | Add the Backup tab | +80 |
| `Views/SettingsDialog.xaml.cs` | Wire the four buttons, focus restoration | +60 |
| `Services/ConfigService.cs` | `ReloadAsync()`; P1 sync keys | +60 |
| `Models/ConfigModel.cs` | P1 `SettingsSync*` properties | +25 |
| `Services/AccountService.cs` | `ImportAccountsAsync` + change notification | +70 |
| `Services/ContactService.cs` | Local-slice export/import | +60 |
| `ViewModels/MainViewModel.cs` | Register `settings.export` / `settings.import` / `settings.syncNow` | +25 |
| `App.xaml.cs` | Wire `SettingsBundleService`; P1 `SettingsSyncService` + `Dispose` in `OnExit` | +25 |
| `docs/USER-GUIDE.md` | New "Moving to another computer" section | +100 |
| `docs/privacy.html` | P1 only: the mailbox-transport disclosure + new effective date | +15 |

---

## Section 12: Tests to Add

| Test class | Methods | Coverage |
|---|---|---|
| `SettingsBundleServiceTests` | Round-trip; category subsets; unknown file ignored; newer schema rejected; corrupt zip rejected; `custom.lex` byte-identical (UTF-16 LE + BOM) | Happy path + format edges |
| `AccountIdRemapperTests` | Adopt preserves GUIDs; merge maps by username+backend; case-insensitive username; backend mismatch is not a match; ambiguous reported; unmatched reported; **no residual source GUID anywhere** | The load-bearing logic |
| `ConfigKeyClassificationTests` | Every `ConfigModel` property is classified local or portable | **Fails on any new unclassified property** |
| `ProfileFileCoverageTests` | Every file a service writes under `ProfileDir` is classified travels/excluded | Fails when a new profile file appears |
| `SettingsImportRollbackTests` | Mid-import failure restores byte-for-byte; success leaves a readable backup | Failure path |
| `ExportSettingsViewModelTests` | Category selection, default filename, size estimate, "no passwords" summary text | VM |
| `ImportSettingsViewModelTests` | Adopt vs merge preview; ambiguous rows disable Import; counts match | VM |
| `PostImportSignInViewModelTests` | Row states; shared mailboxes excluded; per-account outcome | VM |
| `ConfigServiceReloadTests` | A reloaded value is observed by a bound consumer without restart | The Phase 3 risk |
| `SelectorItemAccessibilityTests` (existing) | **Add** the category, import-detail, sign-in and sync-account item types | The invisible accessibility bug |
| `CommandRegistryTests` (existing) | **Add** `settings.export`, `settings.import`, `settings.syncNow` | Registration |
| `XamlParseTests` (existing) | **Add** the three new dialogs | XAML loads |
| *P1:* `SettingsSyncServiceTests` | No-change no-op; remote-newer applies; both-changed preserves loser; debounce coalesces; **two fake machines reach a fixed point** | Sync engine |
| *P1:* `FolderSyncTransportTests` | Partial file never read; move-into-place is atomic | Transport |
| *P1:* `MailboxSyncTransportTests` | Round-trip over GreenMail; folder auto-create; recovery when the message is moved or deleted | Transport |

---

## Section 13: Known Risks & Open Questions

### 13.1 Risks

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| A GUID reference site is missed, so rules/views silently point at nothing | Medium | **Blocker** | The remapper takes every referencing model as an explicit parameter (a new one is a compile error); a test asserts no residual source GUID remains in the applied profile |
| A stale cached `ConfigModel` makes half the app ignore an import until restart | **High** | Major | Named audit deliverable in Phase 3; Scenario 4.2 tests a setting read at a different layer than the smoke-test setting |
| **Graph folder ids may not be portable between machines.** Graph accounts store the opaque folder id in `rule.TargetFolder` and `ViewFolder.FolderFullName`. Graph ids are believed mailbox-scoped, therefore identical on both machines — **this must be verified against a real mailbox before Phase 1 is considered done.** | Medium | Major | Verify first. Either way the mitigation exists: run `FolderReferenceRemapper` after the first post-import sync (Phase 4), which remaps by display name or disables and names the rule — it never guesses |
| Import overwrites contacts a user already had | Medium | Major | Import is additive; local rows merge by `(DisplayName, EmailAddress)`; server-synced slices are never touched; Scenario 4.4 |
| The user does not understand why nothing connects after import | **High** | Major | The post-import sign-in list (Phase 4) is the whole answer, plus a persistent Backup-tab affordance |
| A bundle is emailed and contains the address book | Medium | Minor | Address book is a separate, uncheckable-by-the-user category; the export dialog states the contents plainly; no credentials are ever included |
| *P1:* sync loop — apply triggers push triggers apply | Medium | **Blocker** | Applying sets `LastSyncedUtc` to the remote stamp and suppresses the change signal for that write; a two-fake-machine test asserts a fixed point |
| *P1:* a user rule files or archives the settings message | Low | Major | Locate by stored message id **and** folder scan; re-create if both fail |
| *P1:* a settings-sync bug propagates a bad state to every machine | Low | **Blocker** | Retain the last N bundles in the transport; sync is opt-in and off by default; every apply writes a rollback copy exactly as import does |

### 13.2 Questions raised, and their decisions

All design questions are decided. One verification task remains and is called out as such.

1. **P1 transport for v1: mailbox, folder, or both?**
   **DECIDED: both, with mailbox as the default** (2026-09-01). Phase 5 builds the folder
   transport, Phase 6 the mailbox transport, behind the one `ISettingsSyncTransport` interface.
   *Rationale:* the mailbox transport is what makes sync zero-setup, which is the whole reason to
   prefer it over a hosted service; the folder transport is the escape hatch for POP3-only
   profiles, for users whose mail admin dislikes unfamiliar folders, and for anyone who would
   rather use the sync client they already trust. Dropping either one loses a property the other
   cannot supply. Cost of building both: roughly 16–20 hours across the two phases.
2. **Is the address book in the default export selection?**
   **DECIDED: yes, checked by default** (2026-09-01). *Rationale:* a migration that silently drops
   local contacts is not a migration, and the category most likely to be forgotten is the one users
   discover missing weeks later. The PII concern is handled where it belongs — in the export
   dialog, which states in plain text what the bundle contains and lets the user uncheck the
   address book before saving. The bundle still carries no credentials.
3. **Does P0 ship alone, or wait for P1?**
   **DECIDED: P0 ships alone** after Phase 4. *Rationale:* it is independently valuable — it is
   also the backup-and-restore feature the app has never had — and it is the prerequisite for both
   P1 transports, since they move the same bundle. P1 gets its own spec review before Phase 5.
4. **Bundle password protection?**
   **DECIDED: no, for v1.** *Rationale:* the bundle holds no credentials, so a password protects
   PII the user already controls while adding a new zip-encryption dependency and a secret the user
   can lose — locking themselves out of their own backup. Inspectability with Explorer is worth
   more here, both for trust and for diagnosing a failed import. Revisit only if a real transport
   ever puts the bundle somewhere the user does not control.
5. **Graph folder id portability** (see §13.1). **VERIFICATION TASK, not a design decision** —
   must be answered against a real Graph mailbox before Phase 1 is accepted. The mitigation
   (running `FolderReferenceRemapper` after the first post-import sync) is in the plan either way,
   so the answer changes the confidence, not the architecture.

---

## Section 14: Appendix — Command Reference

| Command id | Category | Title | Default key | Surface |
|---|---|---|---|---|
| `settings.export` | Settings | Export settings | none | Settings → Backup, command palette |
| `settings.import` | Settings | Import settings | none | Settings → Backup, command palette |
| `settings.syncNow` *(P1)* | Settings | Sync settings now | none | Command palette |

No default keys are assigned: these are infrequent actions, the keyspace is crowded, and every one
is reachable from the command palette and the keyboard customizations dialog, where a user who
wants a key can assign one.

---

## Section 15: Implementation Guidance for AI

### 15.1 Adjustments you are expected to make

- The spec does not prescribe how `SettingsBundleService` reads the profile directory — a fixed
  list of filenames, or an enumeration filtered by a classification table. Prefer whichever makes
  `ProfileFileCoverageTests` (a new profile file must fail the build) natural to write.
- The exact wording of preview and report text is not normative. Keep it plain, keep it short, and
  keep the counts in it — a screen reader user should learn the outcome from the first sentence.
- The category granularity in §4.1 is a starting point. If two categories can never sensibly be
  separated (flags and views, perhaps), merge them and say so.
- Whether the Backup tab's sync controls live in the same tab as export/import, or in their own
  tab, is yours to decide once you see how tall the tab gets.

### 15.2 When to ask before proceeding

- **If Graph folder ids turn out not to be portable** (§13.2 Q5), stop and report before designing
  around it. It changes what "import succeeded" means for Graph accounts.
- **If the `ConfigModel` cache audit finds more than a handful of capture sites**, stop and report
  the list before rewriting them. It may be a separate refactor and a separate PR.
- **The keyboard walkthrough in §6 is normative.** If a step conflicts with an existing shortcut or
  an existing focus behaviour, ask rather than working around it.
- **Never widen the "travels" set to include a credential, a token, or `mail.db`**, whatever
  convenience argument appears during implementation. That is Principle 1 and it is not a
  trade-off to be re-opened in Session 2.

### 15.3 Acceptance walkthrough preview

After you build this, the user runs §8. The steps most likely to catch bugs in this specific
implementation are:

- **Scenario 2.3** — merge mode rewriting rule GUIDs to the *local* account ids. This is where a
  missed reference site shows up.
- **Scenario 4.2** — a theme change from an import taking effect without a restart. This is where
  the `ConfigModel` cache audit is proven or disproven.
- **Scenario 7.2** — item text announced by `ToString()`. This one passes visual review even when
  it is broken.
