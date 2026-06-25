# POP3 Backend — PM & Dev Specification

**Status:** Draft — awaiting approval  
**Date:** 2026-06-24  
**Target:** v0.8.x (after Graph backend ships)  
**Scope:** Large (new protocol backend, new service, model changes, UI additions)

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [User Problem & Opportunity](#2-user-problem--opportunity)
3. [Design Principles](#3-design-principles)
4. [Feature Scope & Acceptance Criteria](#4-feature-scope--acceptance-criteria)
5. [Architecture & Technical Decisions](#5-architecture--technical-decisions)
6. [Keyboard Walkthrough](#6-keyboard-walkthrough)
7. [Accessibility Checklist](#7-accessibility-checklist)
8. [Acceptance Walkthrough](#8-acceptance-walkthrough)
9. [Success Metrics](#9-success-metrics)
10. [Implementation Phases](#10-implementation-phases)
11. [Files to Create / Modify](#11-files-to-create--modify)
12. [Tests to Add](#12-tests-to-add)
13. [Known Risks & Open Questions](#13-known-risks--open-questions)
14. [Appendix — Keyboard Reference](#14-appendix--keyboard-reference)
15. [Implementation Guidance for AI](#15-implementation-guidance-for-ai)

---

## 1. Executive Summary

QuickMail currently supports IMAP/SMTP and Microsoft Graph as mail backends. A meaningful segment of users — particularly those with older hosting providers, shared hosting environments, ISP-provided accounts, and legacy business accounts — have POP3-only access. This spec adds POP3 as a third backend option (`BackendKind.Pop3Smtp`), following exactly the pattern established by the Graph backend addition. A new `Pop3MailService` implements `IMailService` with POP3-appropriate semantics: full message download on sync, UIDL-based deduplication, a single synthetic "Inbox" folder, and local-only semantics for all state mutations (read, flag, move). The rest of the application — `SyncService`, `MainViewModel`, `MailServiceRouter`, rules, views, compose — is unaware of the new backend.

---

## 2. User Problem & Opportunity

### 2.1 Current state (verified)

| Surface | Today | Pain | Who feels it |
|---|---|---|---|
| Add Account dialog | Offers IMAP/SMTP and (with feature gate) Microsoft 365. No POP3 option. | Users with POP3-only providers cannot add their account. | Legacy account holders, shared hosting users, ISP account users |
| `BackendKind` enum | `ImapSmtp`, `MicrosoftGraph`. No `Pop3Smtp`. | New backend type has no representation. | Implementation |
| `AccountModel` | Has `ImapHost/Port/Ssl` fields. No POP3 fields. | POP3 server settings cannot be stored. | Implementation |
| `IMailService` interface | Designed with IMAP semantics (UIDs, folder hierarchy, server-side flags). | Must adapt IMAP-shaped methods to POP3's flat, download-only model. | Implementation |

Verified: `BackendKind.cs` line 1–14; `AccountModel.cs` lines 23–33; `AddAccountViewModel.cs` lines 17–22.

### 2.2 Target personas

| Persona | Need | Why it matters | How they use this |
|---|---|---|---|
| **Legacy ISP user (Phyllis)** | Checks email through her ISP (e.g. Comcast, AT&T) which provides POP3 but not IMAP. | IMAP accounts from these providers require calling support to enable; POP3 "just works." | Adds POP3 account, reads and archives mail locally. |
| **Shared hosting owner (Mark)** | Runs a small business domain on cPanel/Plesk hosting. POP3 is available by default; IMAP requires additional configuration or upgrading the plan. | POP3 is the path of least resistance. | Downloads business email into QuickMail alongside their Gmail IMAP account. |
| **Privacy-focused user (Dana)** | Prefers that mail not persist indefinitely on the server. | POP3's "delete after download" option removes mail from the server once retrieved. | Adds POP3 account with `Delete from server after download` enabled. |
| **Corporate user with old groupware (Ray)** | Corporate mail server is running an old GroupWise or Notes setup that exports POP3 but not IMAP. | No choice: POP3 or nothing. | Reads work mail in QuickMail alongside personal IMAP accounts. |
| **Keyboard / screen reader user** | Needs all of the above access to work keyboard-only with screen reader announcements. | Accessibility is not optional. | Uses the same keyboard-centric Add Account flow as IMAP, with accessible announcements throughout. |

### 2.3 Why now

- The Graph backend PR established the `BackendKind` + `MailServiceRouter` infrastructure needed to add any new backend cleanly. POP3 is the first consumer of that infrastructure beyond Graph.
- MailKit (already a dependency) includes a full `Pop3Client` implementation — no new dependencies are required.
- The `IMailService` interface shape, defined during the Graph refactor, is protocol-agnostic enough that POP3 can implement it with minor adaptation.

---

## 3. Design Principles

1. **Zero disruption to existing accounts.** Existing IMAP and Graph accounts are completely unaffected. `BackendKind.Pop3Smtp` is additive; no migrations run for existing accounts.
2. **The rest of the app is protocol-unaware.** `SyncService`, `MainViewModel`, `RuleService`, `ViewService`, and `ComposeWindow` call `IMailService`. They must not need to know they are talking to a POP3 backend.
3. **Local store is authoritative for POP3.** Because POP3 has no server-side folder state, flags, or multi-folder hierarchy, the SQLite local store is the single source of truth for all state beyond "what new messages are available."
4. **Full message download on sync.** Unlike IMAP (headers first, body on demand), POP3 downloads full messages immediately. This is baked into the sync path and is not optional.
5. **Feature-gated.** POP3 is introduced behind `FeatureFlag.Pop3Backend` (default off), exactly like `FeatureFlag.GraphBackend`. The option does not appear in the Add Account dialog until the flag is on.
6. **Keyboard parity.** Adding a POP3 account, editing it, and using mail from it is fully keyboard-navigable. No new mouse-only workflows.

---

## 4. Feature Scope & Acceptance Criteria

### 4.1 In scope (v1)

| Feature | Setting / Location | Default | Notes |
|---|---|---|---|
| POP3 account type | `BackendKind.Pop3Smtp` enum value | — | Selected in Add Account dialog |
| POP3 server settings | `Pop3Host`, `Pop3Port`, `Pop3UseSsl`, `Pop3AcceptInvalidCert` on `AccountModel` | port 995, SSL on | Stored in accounts.json |
| Leave mail on server | `Pop3LeaveMailOnServer` on `AccountModel` | `true` | When false, messages are deleted from server after download |
| Feature gate | `FeatureFlag.Pop3Backend` in `config.ini [features]` | `false` | Enables POP3 option in Add Account |
| Add Account UI | POP3 server fields shown when `Pop3Smtp` backend selected | — | Replaces IMAP fields; SMTP fields unchanged |
| Edit Account UI | Same fields available in account editor | — | For updating host/port/SSL |
| Sync / download | `Pop3MailService.GetMessagesSinceAsync` downloads new messages via UIDL | — | UIDL tracked in local store to avoid re-downloading |
| Polling | `Pop3MailService.PollAsync` checks for new messages | — | No IDLE support; SyncService polls on the same schedule |
| Message detail | Always served from local store (downloaded at sync time) | — | No on-demand POP3 fetch needed |
| Mark read | Local-only; no server-side flag | — | State stored in SQLite |
| Flag/unflag | Local-only | — | State stored in SQLite |
| Delete | Local delete + optionally delete from server | — | Controlled by `Pop3LeaveMailOnServer` |
| Drafts | Local-only (SQLite); no server append | — | SmtpService still sends; no server draft folder |
| Sent mail | Local copy saved to SQLite "Sent" synthetic folder | — | AppendToSentAsync writes to local store only |
| Synthetic folders | "Inbox" and "Sent" synthetic folders exposed to the rest of the app | — | No server folder tree |

### 4.2 Explicitly out of scope (v1)

- **POP3 over OAuth** — password authentication only for v1. OAuth POP3 is uncommon and complex.
- **Local folder hierarchy** — users cannot create sub-folders for POP3 accounts in v1. Folder CRUD calls (`CreateFolderAsync`, `DeleteFolderAsync`, `RenameFolderAsync`, `CopyFolderAsync`) throw `NotSupportedException` for POP3 accounts.
- **Move between local folders** — `MoveMessagesAsync` between POP3 synthetic folders is deferred to v2.
- **POP3 `TOP` command for header-only preview** — all messages are downloaded in full on sync. The IMAP-style "download headers, body on demand" pattern is not implemented.
- **Recurring background sync beyond SyncService** — POP3 uses the same `SyncService` polling cadence as IMAP. No separate POP3-specific polling timer.
- **`--online` mode for POP3** — POP3 in `--online` mode is unsupported in v1. The local store is required for all POP3 operations beyond the initial download. Running `--online` with a POP3 account will log a warning and skip that account.
- **Message count limits** — POP3 accounts download all available messages not previously seen (no `SyncDays` / `InitialSyncCount` date-filtering). A future PR can add a "download last N messages" limit.
- **Folder operations across backends** — Moving or copying messages from a POP3 account to an IMAP account is out of scope.

---

## 5. Architecture & Technical Decisions

### 5.1 Key architectural decisions

---

**Decision:** `Pop3MailService` implements `IMailService` directly, with the same shape as `ImapMailService` and `GraphMailService`. The router dispatches POP3 accounts to it.

**Alternatives:**
1. Create a slimmer interface for POP3 and have the router special-case it. Con: violates the "rest of app is protocol-unaware" principle; every caller needs to know if the backend is limited.
2. Subclass `ImapMailService`. Con: POP3 and IMAP share almost no implementation; inheritance creates a misleading hierarchy.

**Rationale:** The router pattern already works for Graph — a second non-IMAP backend validates the abstraction. Unsupported operations throw `NotSupportedException` with a clear message.

---

**Decision:** UIDL strings are used as `messageId` for POP3 messages.

**Alternatives:**
1. Use the RFC 2822 `Message-ID` header. Con: not guaranteed to be present or unique; many servers produce malformed or missing headers.
2. Assign a local UUID at download time. Con: breaks the assumption that `messageId` is stable across sessions and can identify the message on the server for deletion.

**Rationale:** UIDL is the POP3 standard for persistent message identity across sessions. It is stable and server-assigned, and MailKit exposes it directly. When `Pop3LeaveMailOnServer = false`, the UIDL is used to delete the specific message after download.

---

**Decision:** POP3 messages are fully downloaded at sync time. `GetMessageDetailAsync` serves from the local store only.

**Alternatives:**
1. Use POP3 `TOP` to fetch headers during sync, then `RETR` on open. Con: requires re-connecting on every open; POP3 sessions are short-lived and re-connect overhead is noticeable.
2. Download summary only at sync, full body only on open (IMAP pattern). Con: POP3 doesn't support partial fetch in the same way; TOP gives headers but no body, and the message is not cached for offline use.

**Rationale:** POP3 is designed for download-and-disconnect. Downloading everything immediately matches the protocol's intent and means QuickMail works fully offline after the sync completes. The main trade-off is larger local store size, which is acceptable for the expected message volumes.

---

**Decision:** Folder structure is two synthetic folders: "Inbox" (server messages) and "Sent" (locally saved sent mail). Neither maps to a real server folder.

**Alternatives:**
1. Single "Inbox" only, with sent mail not appearing for POP3 accounts. Con: users expect sent mail to be visible.
2. Allow local folder creation for POP3 (arbitrary folder tree in SQLite). Con: significant additional work, out of scope for v1.

**Rationale:** "Inbox" + "Sent" matches the minimum viable mail workflow. These map cleanly to the existing virtual-folder sentinels when aggregated across accounts (e.g. `\x00AllInboxes` will include POP3 inboxes).

---

**Decision:** Synthetic POP3 folders use `FullName` values `"POP3/Inbox"` and `"POP3/Sent"`, not `\x00` sentinels.

**Alternatives:**
1. Use `\x00` sentinels as virtual folders. Con: `\x00` prefix is reserved for cross-account virtual folders; using it for per-account synthetics would confuse the virtual folder logic in `MainViewModel`.
2. Use `"INBOX"` for the inbox. Con: IMAP uses `"INBOX"` — this creates ambiguity in code that handles folder names from multiple backends.

**Rationale:** A distinct prefix prevents collisions with both real IMAP folder names and `\x00` virtual sentinels. `"POP3/Inbox"` and `"POP3/Sent"` are stable, not present in IMAP accounts, and unambiguous.

---

**Decision:** `Pop3MailService` uses a single `Pop3Client` connection (no pool). Connections are created per-operation and disposed immediately (open, execute, close).

**Alternatives:**
1. Connection pool like `ImapMailService`. Con: POP3 has only one mailbox and most servers allow only one concurrent session. A pool offers no benefit.
2. Long-lived persistent connection. Con: POP3 sessions are not designed to stay open; many servers close idle sessions after 10 minutes.

**Rationale:** Open/execute/close is the standard POP3 client pattern. MailKit's `Pop3Client` is lightweight enough that connection overhead is not a concern for a polling-based workflow.

---

**Decision:** Server deletion (when `Pop3LeaveMailOnServer = false`) happens immediately after a successful full-message download, not deferred to a separate pass.

**Alternatives:**
1. Batch delete at end of sync session. Pro: fewer round-trips. Con: if the app crashes mid-sync, some messages are in the local store but not deleted; they are re-downloaded next sync and duplicated.
2. Delete only after local store confirms the write. Con: still requires a second connection in the same session.

**Rationale:** Delete-after-confirmed-download is the safest approach for the "no duplicates" requirement. POP3 clients have used this pattern since RFC 1939. The implementation downloads message N, writes to SQLite, verifies the write succeeded, then marks it deleted on the server.

---

**Decision:** `--online` mode silently skips POP3 accounts and logs a warning.

**Rationale:** POP3 in `--online` mode is semantically undefined — POP3 requires the local store for virtually all operations. Failing loudly would be disruptive for users who launch `--online` for a different account that happens to share the profile. Warning + skip is the least-surprise behavior.

---

### 5.2 Runtime mode compatibility

| Mode | Local store available? | Pop3MailService behavior |
|---|---|---|
| Normal | ✓ | Full operation: sync downloads, local state, all UI |
| `--online` | ✗ | Account is skipped at startup with a log warning; user sees account as disconnected |
| `--profileDir <path>` | ✓ | Full operation using alternate profile |

### 5.3 Code reuse and duplication risks

- **`MimeMessageBuilder`** — already protocol-agnostic; used by `SmtpService` unchanged. No duplication.
- **`LocalStoreService`** — POP3 messages are stored using the same `MailMessageSummary` / `MailMessageDetail` schema as IMAP. No schema changes required for v1; the `folder_name` column will hold `"POP3/Inbox"` or `"POP3/Sent"`.
- **`SyncService`** — used unchanged. It calls `IMailService.GetMessagesSinceAsync` and `NoOpAsync`. Both are implemented in `Pop3MailService`. The only behavioral difference is that `GetMessagesSinceAsync` for POP3 downloads full bodies, not just summaries.
- **`FolderTreeBuilder`** — builds from `MailFolderModel` lists. POP3 returns two `MailFolderModel` objects (`POP3/Inbox`, `POP3/Sent`). The builder handles them without changes.
- **UIDL tracking** — new. POP3 needs to know which UIDLs have been downloaded to avoid re-downloading. This is stored in the local store as `messageId` strings per account. No new table is needed; the existing message summary table already stores this.

### 5.4 Shared component audit

| Component | File | Other consumers | Change needed | Risk |
|---|---|---|---|---|
| `BackendKind` enum | `Models/BackendKind.cs` | `AccountModel`, `AddAccountViewModel`, `MailServiceRouter`, `App.xaml.cs` | Add `Pop3Smtp` value | Switch expressions on `BackendKind` in callers must handle the new value. Audit all switch sites. |
| `AccountModel` | `Models/AccountModel.cs` | `AccountService`, `AddAccountViewModel`, `AccountEditorViewModel`, `ImapMailService`, `SmtpService`, settings dialogs | Add `Pop3Host/Port/Ssl/AcceptInvalidCert/LeaveMailOnServer` fields | New fields are nullable/default; existing serialized accounts.json files will default them correctly via JSON deserialization. |
| `FeatureFlag` enum | `Models/FeatureFlag.cs` | `ConfigFeatureGate`, `AddAccountViewModel`, `IFeatureGate` | Add `Pop3Backend` value | `ConfigFeatureGate.Defaults` must include it (default off). All existing consumers are unaffected. |
| `AddAccountViewModel` | `ViewModels/AddAccountViewModel.cs` | `SettingsDialog.xaml.cs` | Add POP3 to `AvailableBackends` when gate is on; add POP3-specific server field visibility | Must verify IMAP and Graph flows unchanged when `Pop3Smtp` branch is not taken. |
| `AccountEditorViewModel` (base) | `ViewModels/AccountEditorViewModel.cs` | `AddAccountViewModel`, `EditAccountViewModel` | Add POP3 property bindings | Shared base — changes must not break IMAP or Graph edit flows. |
| `MailServiceRouter` | `Services/MailServiceRouter.cs` | `App.xaml.cs`, `SyncService`, `MainViewModel` | Register POP3 accounts at startup | `RegisterAccount` is already defined; call it for `Pop3Smtp` accounts at startup and at add-account time. |
| `App.xaml.cs` | `App.xaml.cs` | — | Instantiate `Pop3MailService`, pass to router, call `RegisterAccount` for each POP3 account | Manual DI root — new backend must be wired here. Dispose in `OnExit`. |
| `ConfigFeatureGate` | `Services/ConfigFeatureGate.cs` | `IFeatureGate` consumers | Add `Pop3Backend` default (false) | Straightforward addition. |
| `SyncService` | `Services/SyncService.cs` | `App.xaml.cs`, `MainViewModel` | No changes required | POP3 uses the same `IMailService` interface. Verify `NoOpAsync` and `GetMessagesSinceAsync` are called correctly. |
| `LocalStoreService` | `Services/LocalStoreService.cs` | Every account's message storage | No schema changes for v1 | POP3 writes to same tables with `folder_name = "POP3/Inbox"`. Verify existing queries filter by folder name correctly so POP3 and IMAP messages don't cross-contaminate. |

---

## 6. Keyboard Walkthrough

### Path: Add a POP3 account

**Prerequisites:** Feature gate `Pop3Backend = true` in `config.ini [features]`.

1. User opens Settings (Ctrl+Comma). **Expected:** Settings dialog opens, focus on General tab. Screen reader announces "Settings, General tab."
2. User presses Tab to navigate to the Accounts section and activates "Add Account." **Expected:** Add Account dialog opens, focus on the Account type combo box (or Username field if only one backend is available).
3. User opens the backend combo box (Alt+Down) and navigates to "POP3/SMTP." **Expected:** Screen reader announces each option as the user arrows down. "POP3/SMTP" is announced.
4. User presses Enter to select "POP3/SMTP." **Expected:** The dialog switches from IMAP fields to POP3 fields. IMAP host/port fields are hidden; POP3 host/port/SSL fields appear. Screen reader announces the backend selection.
5. User tabs to the Username field and types an email address. **Expected:** Focus is on Username text box. Screen reader announces "Username, edit."
6. User tabs to Password and types a password. **Expected:** Focus is on the masked password field.
7. User tabs to "POP3 server" and enters the hostname. **Expected:** Focus on POP3 Host text box.
8. User tabs through Port (default 995), SSL checkbox (default checked), and Accept invalid certificate checkbox (default unchecked). **Expected:** Screen reader announces each field with its current value.
9. User tabs through SMTP fields (host, port, SSL) — these are the same as IMAP. **Expected:** No change to SMTP field behavior.
10. User tabs to the "Leave mail on server" checkbox (default checked). **Expected:** Screen reader announces "Leave mail on server, checkbox, checked."
11. User activates the Test Connection button. **Expected:** Status text updates to "Testing…", then "Connected." Screen reader announces the result (Status category announcement).
12. User activates Save. **Expected:** Account appears in the accounts list. Dialog closes. Focus returns to the account list.

### Path: Sync and read POP3 mail

1. App starts. POP3 account connects. **Expected:** Status bar announces "Syncing mail…" (same as IMAP). Account label shows "Connected."
2. SyncService calls `GetMessagesSinceAsync`. Pop3MailService downloads full messages. **Expected:** Messages appear in the Inbox folder list as they are downloaded. Screen reader does not announce each individual message (batch suppression via `BatchObservableCollection`).
3. Sync completes. **Expected:** Status bar shows "N messages loaded." Screen reader announces the count (Status category).
4. User navigates to the POP3 account's Inbox in the folder tree. **Expected:** Messages appear in the message list, sorted by date descending.
5. User presses Enter on a message. **Expected:** Message body appears in the reading pane (or new tab/window per windowing setting). Screen reader announces the subject/sender as normal. Body is already local — no loading delay.
6. User presses K to flag the message. **Expected:** Flag indicator appears. Flagging is local-only — no server round-trip. Screen reader announces flag status.

### Path: Delete a POP3 message (leave on server = true)

1. User selects a message and presses Delete. **Expected:** Message is removed from the local list. It remains on the server. No server round-trip occurs (deletion is local-only). Screen reader announces "Message deleted" or equivalent.

### Path: Delete a POP3 message (leave on server = false)

1. User selects a message and presses Delete. **Expected:** Message is removed from the local list. On the next sync cycle, Pop3MailService marks the message for deletion on the server and calls `DELE` + `QUIT`. Screen reader announces "Message deleted."
2. On the next sync, the message is absent from the server UIDL list. **Expected:** No re-download occurs.

### Path: Send mail from a POP3 account

1. User composes a new message with the POP3 account selected as sender. **Expected:** Compose window opens normally.
2. User sends. **Expected:** `SmtpService` sends via the SMTP settings on the POP3 account. `Pop3MailService.AppendToSentAsync` saves a local copy to the "POP3/Sent" synthetic folder. Screen reader announces "Message sent."
3. User navigates to the Sent folder in the POP3 account. **Expected:** The sent message appears.

### Path: Keyboard shortcut and command palette — no new commands

No new keyboard shortcuts are added for POP3. All existing commands (Reply, Forward, Delete, Move, etc.) work identically through `IMailService`. Folder operations that are not supported (Create Folder, Delete Folder, Rename Folder) remain available in the UI but are disabled or produce a toast notification explaining they are not supported for POP3 accounts.

---

## 7. Accessibility Checklist

- **AutomationProperties.Name** — New fields introduced in Add Account dialog for POP3:
  - "POP3 server" (text box)
  - "POP3 port" (text box, default 995)
  - "POP3 SSL" (checkbox)
  - "Accept invalid certificate" (already exists for IMAP; reused)
  - "Leave mail on server" (checkbox)
  All names are short identifying labels only. No role names, no instructions.
- **AnnouncementCategory** — New announcements:
  - "Connected" / "Connection failed: [reason]" when Test Connection resolves: `AnnouncementCategory.Status` (connection state).
  - Message download count during sync: `AnnouncementCategory.Status` (same pattern as IMAP sync).
  - "Folder operations are not supported for POP3 accounts" when user tries to create/rename/delete a folder: `AnnouncementCategory.Result`.
  No `Hint` announcements are added for the Add Account dialog (the IMAP flow has none).
- **Screen reader browse mode** — No new WebView2 usage. Not applicable.
- **Focus restoration** — Add Account dialog follows the existing pattern: closes and returns focus to the account list row. No change.
- **F6 ring** — No new panes added to the main window. The POP3 Inbox and Sent folders appear in the existing folder tree (same pane). Not applicable.
- **Checkbox / radio button groups** — "Leave mail on server" is a standalone checkbox; no grouping needed.
- **Color-only information** — No new color-only states. POP3 account status uses the same `StatusLabel` / `AccessibleName` pattern as IMAP.
- **Account status** — `AccountModel.AccessibleName` already handles "connected" / "disconnected" / "N unread" dynamically. No changes needed.

---

## 8. Acceptance Walkthrough

### Scenario: Add a POP3 account (happy path)

**Setup:** App running with at least one existing IMAP account. `Pop3Backend = true` in `config.ini [features]`. No existing POP3 account.

1. Open Settings → Accounts → Add Account. **Verify:** Dialog opens. Backend picker shows at least "Standard IMAP/SMTP" and "POP3/SMTP." 
2. Select "POP3/SMTP" from backend picker. **Verify:** IMAP fields disappear; POP3 host/port/SSL fields appear. "Leave mail on server" checkbox appears. SMTP fields remain visible and unchanged.
3. Fill in username, password, POP3 host (pop.example.com), port (995), SSL on. Fill in SMTP host, port, SSL.
4. Press Test Connection. **Verify:** Status changes from blank to "Testing…" to either "Connected" or a specific error. If connected: button text changes back to "Test Connection" or is re-enabled. Screen reader announces the result.
5. Press Save. **Verify:** Dialog closes. New account appears in the account list. Screen reader announces focus returns to the account list.
6. Wait for initial sync. **Verify:** Messages appear in the POP3 account's Inbox. Each message has a subject, sender, date.
7. Select a message and press Enter. **Verify:** Message body opens immediately (no loading state — it was downloaded during sync). Body is rendered correctly.

### Scenario: Feature gate off — POP3 option is hidden

**Setup:** App running. `Pop3Backend` absent from `[features]` section (defaults to false).

1. Open Settings → Add Account. **Verify:** Only "Standard IMAP/SMTP" appears in the backend picker (if Graph is also off, the picker may be hidden and only a static label shown). "POP3/SMTP" is NOT present.

### Scenario: Delete behavior — leave on server (default)

**Setup:** POP3 account with messages. `Pop3LeaveMailOnServer = true`.

1. Note the message count.
2. Select a message. Press Delete. **Verify:** Message disappears from the local list immediately. No error.
3. Trigger a manual sync (or wait for the next automatic sync). **Verify:** Deleted message does NOT reappear (its UIDL is tracked as "seen"). Message count on server is unchanged (verified by logging in via webmail).

### Scenario: Delete behavior — delete from server

**Setup:** POP3 account with messages. `Pop3LeaveMailOnServer = false`.

1. Note the message count in webmail (open separately in a browser).
2. Select a message in QuickMail. Press Delete. **Verify:** Message disappears from local list.
3. Trigger sync. **Verify:** Server message count in webmail decreases by one after sync completes.

### Scenario: Unsupported folder operations

**Setup:** POP3 account selected in folder tree.

1. Attempt to create a new folder (via context menu or keyboard shortcut). **Verify:** Either the option is disabled/absent, or if activated, a toast or status announcement reads "Folder operations are not supported for POP3 accounts." No crash.

### Scenario: Existing IMAP account unaffected

**Setup:** At least one IMAP account and one POP3 account both present.

1. Navigate to the IMAP account's inbox. **Verify:** Messages load as normal. No visual or behavioral change.
2. Send a message from the IMAP account. **Verify:** Sent mail appears in the IMAP Sent folder (server). No contamination from POP3 sent folder.
3. Navigate to the virtual All Inboxes folder. **Verify:** Messages from both accounts appear merged. POP3 messages are visually indistinguishable from IMAP messages in the list.

### Scenario: `--online` mode with a POP3 account

**Setup:** Launch with `--online` flag. Profile has one IMAP and one POP3 account.

1. App starts. **Verify:** IMAP account connects and operates normally. POP3 account shows as "Disconnected" in the account list. A log entry (in `quickmail.log`) records "POP3 account skipped in --online mode: [account name]." No crash.

### Scenario: Screen reader — Add Account dialog, POP3 path

**Setup:** Screen reader active.

1. Open Add Account dialog, select POP3 backend. **Verify:** Screen reader announces each POP3 field label when tabbing through them. "Leave mail on server, checkbox, checked" is announced on reaching that checkbox.
2. Toggle "Leave mail on server" off. **Verify:** Screen reader announces "unchecked." Toggle back on. Announces "checked."
3. Tab to Save and activate. **Verify:** Dialog closes and screen reader announces focus returning to the account list (account name + status).

---

## 9. Success Metrics

- User can add a POP3 account via Add Account with keyboard only (no mouse required).
- Messages appear in the POP3 account's Inbox after the first sync.
- Message detail opens without a loading delay (body is already in the local store).
- Delete with `LeaveMailOnServer = true` does not re-download the message on subsequent syncs.
- Delete with `LeaveMailOnServer = false` removes the message from the server on next sync.
- Send from a POP3 account saves a copy to the local Sent folder.
- All existing IMAP and Graph accounts are unaffected.
- The POP3 backend option is invisible when the feature gate is off.
- Keyboard-only walkthrough of add account → sync → read → delete → send completes without mouse.
- No crashes in `--online` mode when a POP3 account is present.

---

## 10. Implementation Phases

### Phase 1: Model & Feature Gate

**Goal:** `BackendKind.Pop3Smtp` exists, `AccountModel` has POP3 fields, `FeatureFlag.Pop3Backend` is defined and defaults to off. No functional code yet.

**Deliverables:**
- `Models/BackendKind.cs` — add `Pop3Smtp` value
- `Models/AccountModel.cs` — add `Pop3Host`, `Pop3Port` (default 995), `Pop3UseSsl` (default true), `Pop3AcceptInvalidCert` (default false), `Pop3LeaveMailOnServer` (default true)
- `Models/FeatureFlag.cs` — add `Pop3Backend` value
- `Services/ConfigFeatureGate.cs` — add `Pop3Backend = false` to `Defaults`

**Tests:**
- Verify `BackendKind.Pop3Smtp` round-trips in JSON via existing `AccountModel` serialization tests
- `FeatureFlagTests` — `Pop3Backend` defaults to false; can be overridden via config

**Risk:** Switch expressions on `BackendKind` in existing code will now hit an unhandled case at compile time (if exhaustive). Low risk — these fail at compile, not runtime. Fix them all in this phase.

**Duration:** 1–2 hours

---

### Phase 2: `Pop3MailService` — Core (Connect + Download)

**Goal:** `Pop3MailService` can connect to a POP3 server, authenticate, download new messages using UIDL deduplication, and disconnect. `SyncService` can call `GetMessagesSinceAsync` and get real messages.

**Deliverables:**
- `Services/Pop3MailService.cs` — new class, implements `IMailService`:
  - `ConnectAsync` — authenticate (password only, v1), probe connection, store credentials
  - `DisconnectAsync` — close any open connection
  - `GetFoldersAsync` — return two `MailFolderModel` objects: `"POP3/Inbox"` and `"POP3/Sent"`
  - `GetMessagesSinceAsync` — use MailKit `Pop3Client`, call `GetMessageUidsAsync` (UIDL), filter against already-seen UIDs (passed as `sinceMessageId` or via local store query), download new messages with `GetMessageAsync`, build `MailMessageSummary` + `MailMessageDetail` pairs, write both to local store, optionally delete from server
  - `GetMessageSummariesAsync` — query local store only (no server call)
  - `GetMessagesSinceDateAsync` — not natively supported; filter local store results by date
  - `GetMessageDetailAsync` — local store only
  - `PrefetchMessageDetailAsync` — no-op (messages already downloaded; return same as GetMessageDetailAsync)
  - `PollAsync` — delegates to `GetMessagesSinceAsync` for the inbox
  - `NoOpAsync` — no-op (nothing to keep alive between syncs)
  - `GetInboxStatusAsync` — query local store for count/unread
  - All other `IMailService` methods — see Phase 3 and Phase 4
- `Services/MailServiceRouter.cs` — register `Pop3MailService` as a backend
- `App.xaml.cs` — instantiate `Pop3MailService`, register POP3 accounts in the router, dispose on exit

**Tests:**
- `Pop3MailServiceTests` (new) — uses a stub/mock `Pop3Client` or a real local MailKit in-memory server if available:
  - `ConnectAsync` succeeds with valid credentials
  - `GetFoldersAsync` returns exactly `[POP3/Inbox, POP3/Sent]`
  - `GetMessagesSinceAsync` with no prior downloads: downloads all messages, writes to store
  - `GetMessagesSinceAsync` with prior downloads: skips already-seen UIDLs, downloads only new ones
  - `GetMessageDetailAsync` returns from local store (verifies round-trip)

**Risk:** MailKit `Pop3Client` requires a real server to test fully. Use a MailKit-in-process fake or integration test with a local POP3 server (Papercut SMTP has a POP3 mode, or use Greenmail). If integration test is impractical, test with a stub that implements the UIDL/download contract. Mitigation: ensure the stub covers the deduplication logic, which is the most error-prone part.

**Duration:** 6–8 hours

---

### Phase 3: `Pop3MailService` — State Mutations

**Goal:** Mark read, flag, move to trash, batch delete all work correctly against the local store. The server deletion path (when `LeaveMailOnServer = false`) is implemented.

**Deliverables:**
- `Pop3MailService` additions:
  - `MarkReadAsync` / `MarkReadBatchAsync` — update local store only (no server call)
  - `SetMessageFlaggedAsync` — update local store only
  - `MoveToTrashAsync` / `MoveToTrashBatchAsync` — move in local store; if `LeaveMailOnServer = false`, queue for server deletion on next sync
  - `PermanentlyDeleteBatchAsync` — remove from local store; if `LeaveMailOnServer = false`, delete from server immediately (open connection, DELE, QUIT)
  - `EmptyTrashAsync` — 0 (no server trash; local trash messages are already in local store; permanent delete handles them)
  - `CountTrashMessagesAsync` — query local store for messages in a synthetic `POP3/Trash` folder (if we decide to implement a local trash; otherwise 0)
  - `AppendToSentAsync` — write to local store under `"POP3/Sent"` folder
  - `AppendDraftAsync` — write to local store under `"POP3/Drafts"` synthetic folder; return a synthetic message ID
  - `FindDraftsFolderNameAsync` — return `"POP3/Drafts"` (local synthetic)
  - `GetFolderMessageIdsAsync` — return list of message IDs from local store for the given folder
  - `FetchPreviewsAsync` — return from local store (already downloaded)
  - `DownloadAttachmentAsync` — extract from the full message stored in local store (MailKit can re-parse a stored MIME byte array)
  - Folder CRUD (`CreateFolderAsync`, `DeleteFolderAsync`, `RenameFolderAsync`, `CopyFolderAsync`) — throw `NotSupportedException("Folder operations are not supported for POP3 accounts.")`
  - `CopyMessagesAsync` / `MoveMessagesAsync` — local store copy/move for same-account ops; throw `NotSupportedException` for cross-account ops

**Tests:**
- `MarkReadAsync` updates `IsRead` in local store; no exception thrown
- `MoveToTrashAsync` moves message to `POP3/Trash` folder in local store
- `PermanentlyDeleteBatchAsync` with `LeaveMailOnServer = false` opens a POP3 connection and issues DELE
- `AppendToSentAsync` writes to `POP3/Sent` in local store
- `CreateFolderAsync` throws `NotSupportedException`

**Risk:** `DownloadAttachmentAsync` requires re-parsing the full MIME message stored in `LocalStoreService`. Verify that `LocalStoreService` stores the raw MIME bytes (or confirm it stores enough metadata to reconstruct attachment content). If it stores only parsed text bodies, attachment download from the local store will need a schema addition (storing raw MIME bytes for POP3 messages). Investigate `MailMessageDetail` schema before starting this phase.

**Duration:** 4–6 hours

---

### Phase 4: Add Account & Edit Account UI

**Goal:** The Add Account dialog shows a POP3 backend option (behind feature gate), with all required fields. The account editor supports editing POP3 settings.

**Deliverables:**
- `ViewModels/AddAccountViewModel.cs` — add `Pop3Smtp` to `AvailableBackends` when gate is on; add VM properties `Pop3Host`, `Pop3Port`, `Pop3UseSsl`, `Pop3AcceptInvalidCert`, `Pop3LeaveMailOnServer` with visibility flags; wire `OnSelectedBackendChanged` for POP3
- `ViewModels/AccountEditorViewModel.cs` (base) — add POP3 properties with `[ObservableProperty]`
- `Views/AddAccountDialog.xaml` — add POP3 fields section (visible when `ShowPop3Fields`); add "Leave mail on server" checkbox; hide IMAP fields when POP3 is selected
- `Views/EditAccountDialog.xaml` (if separate from Add) — same POP3 fields
- `App.xaml.cs` — when a new POP3 account is saved, call `_router.RegisterAccount(account.Id, _pop3Service)` and `_pop3Service.ConnectAsync(account, password)`

**Tests:**
- `XamlParseTests` — `AddAccountDialog.xaml` loads without error
- `AddAccountViewModelTests` — selecting `Pop3Smtp` sets `ShowPop3Fields = true`, `ShowImapFields = false`
- `AddAccountViewModelTests` — POP3 backend option absent when gate is off
- `AddAccountViewModelTests` — saving a POP3 account writes correct `BackendKind` and POP3 fields to `AccountModel`

**Risk:** `AccountEditorViewModel` is a shared base class for both Add and Edit. Any new property must have `[ObservableProperty]` and a corresponding save/load path. Verify the Edit dialog serializes the new fields back to `AccountModel.Pop3*` properties correctly.

**Duration:** 3–4 hours

---

### Phase 5: Integration, `--online` Mode Guard, and Polish

**Goal:** POP3 accounts work end-to-end in a running app. `--online` mode guard is in place. Status bar and folder tree show POP3 accounts correctly. Unsupported operations are gracefully disabled.

**Deliverables:**
- `App.xaml.cs` — skip POP3 accounts in `--online` mode with a `LogService.Log` warning
- `MainViewModel.cs` — ensure `GetFoldersAsync` for POP3 accounts produces the two synthetic folders in the folder tree; ensure `SyncAllAccountsAsync` is not blocked by `NotSupportedException` from unsupported folder operations
- Folder context menu or folder action commands — disable/hide "Create folder," "Delete folder," "Rename folder" for POP3 accounts (check `BackendKind` on `SelectedAccount`)
- Status bar text and accessibility announcements — no POP3-specific changes needed; the existing IMAP announcements apply

**Tests:**
- Integration test: App startup with POP3 account in `--online` mode logs warning and does not throw
- Integration test: `SyncService.SyncAllAccountsAsync` with a POP3-backed `IMailService` stub completes without error
- Regression: All existing IMAP tests pass unchanged

**Duration:** 2–3 hours

---

## 11. Files to Create / Modify

### Files to Create

| File | Purpose | Est. lines |
|---|---|---|
| `QuickMail/Services/Pop3MailService.cs` | `IMailService` implementation for POP3 | 400–550 |

### Files to Modify

| File | Changes | Est. lines changed |
|---|---|---|
| `QuickMail/Models/BackendKind.cs` | Add `Pop3Smtp` value | +4 |
| `QuickMail/Models/AccountModel.cs` | Add POP3 fields (`Pop3Host`, `Pop3Port`, `Pop3UseSsl`, `Pop3AcceptInvalidCert`, `Pop3LeaveMailOnServer`) | +15 |
| `QuickMail/Models/FeatureFlag.cs` | Add `Pop3Backend` | +5 |
| `QuickMail/Services/ConfigFeatureGate.cs` | Add `Pop3Backend = false` default | +3 |
| `QuickMail/Services/MailServiceRouter.cs` | No API changes; verify `RegisterAccount` covers POP3 | 0 (doc only, or +0) |
| `QuickMail/ViewModels/AccountEditorViewModel.cs` | Add POP3 `[ObservableProperty]` fields + visibility flags | +40 |
| `QuickMail/ViewModels/AddAccountViewModel.cs` | Add `Pop3Smtp` backend option behind gate; wire `OnSelectedBackendChanged` for POP3 | +30 |
| `QuickMail/Views/AddAccountDialog.xaml` | Add POP3 server field section; "Leave mail on server" checkbox; field visibility bindings | +40 |
| `QuickMail/App.xaml.cs` | Instantiate `Pop3MailService`; register POP3 accounts; `--online` guard; `Dispose` in `OnExit` | +25 |
| `QuickMail/ViewModels/MainViewModel.cs` | Disable folder CRUD commands when selected account is POP3 | +10 |
| `docs/ARCHITECTURE.md` | Add `Pop3MailService` description to service layer section | +8 |

---

## 12. Tests to Add

| Test Class | Test Methods | Coverage |
|---|---|---|
| `Pop3MailServiceTests` (new) | `ConnectAsync_SucceedsWithValidCredentials`, `GetFoldersAsync_ReturnsTwoSyntheticFolders`, `GetMessagesSinceAsync_FirstSync_DownloadsAll`, `GetMessagesSinceAsync_IncrementalSync_SkipsSeenUidls`, `GetMessagesSinceAsync_WithLeaveOnServerFalse_DeletesAfterDownload`, `GetMessageDetailAsync_ReturnsFromLocalStore`, `MarkReadAsync_UpdatesLocalStore_NoServerCall`, `MoveToTrashAsync_MovesInLocalStore`, `AppendToSentAsync_WritesToLocalPop3SentFolder`, `CreateFolderAsync_ThrowsNotSupportedException`, `PollAsync_DelegatesCorrectly` | Happy path + POP3-specific edge cases |
| `AddAccountViewModelTests` (extend) | `Pop3Backend_AbsentWhenGateOff`, `Pop3Backend_PresentWhenGateOn`, `SelectingPop3Backend_ShowsPop3Fields_HidesImapFields`, `SelectingImapBackend_ShowsImapFields_HidesPop3Fields` | Feature gate + field visibility |
| `AccountModelTests` (extend or new) | `Pop3Fields_RoundTripThroughJsonSerialization`, `Pop3LeaveMailOnServer_DefaultsToTrue` | Model serialization |
| `FeatureFlagTests` (extend) | `Pop3Backend_DefaultsToFalse`, `Pop3Backend_EnabledByConfigSection` | Feature gate defaults |
| `XamlParseTests` (extend) | `AddAccountDialog_LoadsWithoutError` (if not already present) | XAML parse |

---

## 13. Known Risks & Open Questions

### 13.1 Risks

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| `LocalStoreService` does not store raw MIME bytes for POP3 messages, breaking attachment downloads | Medium | Major | Investigate `MailMessageDetail` schema before Phase 3. If raw MIME is not stored, add a `MimeBytes` BLOB column (schema migration v3 or v4). Confirm in Phase 2 planning before starting Phase 3. |
| Server enforces single-session; two concurrent POP3 connections fail | Medium | Major | `Pop3MailService` uses one connection at a time. Ensure `SyncService` does not call `PollAsync` while `GetMessagesSinceAsync` is still in progress for the same account. Use a `SemaphoreSlim(1,1)` per account inside `Pop3MailService`. |
| `SyncService.SyncAllAccountsAsync` calls `NoOpAsync` before syncing — POP3 no-op is fine, but verify no timeout | Low | Minor | `NoOpAsync` for POP3 is a no-op (returns immediately). No server call is made. Risk: SyncService assumes `NoOpAsync` proves the connection is alive; for POP3 this is false, but POP3 reconnects per-operation anyway. Acceptable. |
| UIDL deduplication state lost if local store is deleted | Low | Minor | On store loss, all messages re-download. This is correct behavior (same as IMAP first-sync). No special handling needed. |
| Switch expressions on `BackendKind` in callers miss `Pop3Smtp` | High (first sync risk) | Blocker | Audit all `switch(account.BackendKind)` expressions in Phase 1 before writing any POP3 logic. The compiler enforces exhaustiveness if `default` is absent — treat each compiler error as a design question. |
| `FolderTreeBuilder` does not handle `POP3/` prefix in folder names | Low | Minor | `FolderTreeBuilder` splits on `/` as a separator. `"POP3/Inbox"` will be rendered as a sub-item "Inbox" under a parent "POP3." Verify this is the desired rendering or use a flat `FullName = "Inbox"` with a `BackendKind` context instead. Decide in Phase 2. |

### 13.2 Open questions

All the following questions must be answered before implementation starts.

**Q1: Folder name rendering** — Should POP3 folders appear as a sub-tree "POP3 → Inbox, Sent" or as flat "Inbox, Sent" under the account (same as IMAP)? The `FolderTreeBuilder` will render `"POP3/Inbox"` as a child of a synthetic "POP3" parent. If flat is preferred, use `"Inbox"` and `"Sent"` as `FullName` values (same as IMAP). Cross-contamination between POP3 "Inbox" and IMAP "INBOX" in the local store is avoided because they are keyed by `(accountId, folderName)`.

> **Recommended:** Use `"Inbox"` and `"Sent"` as the `FullName` values (consistent with IMAP naming). The `(accountId, folderName)` compound key in the local store prevents collision.

**Q2: Local Trash folder** — Should POP3 accounts have a local "Trash" synthetic folder, or should deleted messages be permanently removed from the local store immediately? If we implement a local Trash, `CountTrashMessagesAsync` and `EmptyTrashAsync` return non-zero values for POP3 accounts.

> **Recommended:** Implement a `"Trash"` synthetic folder (consistent with IMAP UX). Users familiar with IMAP expect two-step delete. `PermanentlyDeleteBatchAsync` performs the final local removal + optional server DELE.

**Q3: Attachment storage** — Does `LocalStoreService` store raw MIME bytes today? If not, what format stores attachments such that `DownloadAttachmentAsync` can extract them from the local store without a network call? This must be resolved before Phase 3.

> **Investigate:** Read `MailMessageDetail` schema in `LocalStoreService.cs`. The answer determines whether Phase 3 needs a schema migration.

**Q4: Draft support** — When a user composes and saves a draft from a POP3 account, should it appear in a synthetic `"Drafts"` folder in QuickMail, or be stored transparently in the same draft mechanism used by IMAP (which may expect a server folder)?

> **Recommended:** Store POP3 drafts in a synthetic `"POP3/Drafts"` or simply `"Drafts"` folder in the local store. `FindDraftsFolderNameAsync` returns `"Drafts"` (or a POP3-specific value). The existing draft-save and draft-open code in `ComposeViewModel` calls `IMailService.AppendDraftAsync` — `Pop3MailService` handles this locally.

---

## 14. Appendix — Keyboard Reference

No new keyboard shortcuts are added. All existing shortcuts work unchanged with POP3 accounts.

| Action | Key | Notes for POP3 |
|---|---|---|
| Open message | Enter | Body served from local store (no loading delay) |
| Delete message | Delete | Local-only; server deletion deferred to next sync if `LeaveMailOnServer = false` |
| Reply / Forward | R / F | Uses SMTP (unchanged) |
| Flag | K | Local-only |
| Mark read | M | Local-only |
| Move to folder | Shift+M | Local-only for same-account; cross-account not supported in v1 |
| Create folder | — | Disabled/throws for POP3 accounts |
| Open Settings / Add Account | Ctrl+, | Unchanged |

---

## 15. Implementation Guidance for AI

### 15.1 Adjustments you're expected to make

- The spec names `Pop3MailService` but leaves the internal structure to the implementer. Use the same code organization as `ImapMailService` (regions for connect/disconnect, message fetch, mutations, folder ops) for consistency.
- The spec says `GetMessagesSinceAsync` downloads full messages. You'll decide whether to use MailKit's `Pop3Client.GetMessageAsync(int index)` or `Pop3Client.GetMessageAsync(string uid)`. Use the UIDL-based overload (`GetMessageUidAsync` / `GetMessageAsync(string uid)`) so the UIDL is the `messageId` throughout.
- For the `sinceMessageId` parameter: when `sinceMessageId == "0"` (first sync), download all messages on the server. Otherwise, treat it as a set of already-seen UIDLs. The cleanest approach: ignore `sinceMessageId` for POP3 and instead query the local store for all known UIDLs for this account — the set difference between server UIDLs and local UIDLs is the download set.
- The spec uses `"Inbox"` and `"Sent"` as `FullName` values (resolved in Q1). If Q1 is answered differently before you start, follow the user's decision.
- `FetchPreviewsAsync` for POP3 should query the local store's `Preview` column (set during download from the message text body). It must not open a POP3 connection.

### 15.2 When to ask for clarification

- If the raw MIME bytes are NOT stored in `LocalStoreService` for existing IMAP messages, stop Phase 3 and ask the user how to handle attachment storage for POP3.
- If any `switch(BackendKind)` expression in the existing code has no `default` case and the compiler error is unclear, ask before introducing a new case.
- The open questions in §13.2 should be resolved before Phase 2 begins. If the user has not answered them, ask before writing `Pop3MailService`.

### 15.3 After implementation: acceptance walkthrough

After building this, the user will run the Acceptance Walkthrough in §8. The steps most likely to catch bugs in this implementation are:

1. **§8 Scenario "Delete behavior — delete from server"** — the interaction between local delete, UIDL tracking, and the POP3 `DELE`/`QUIT` sequence is the highest-risk codepath. Verify with a real or mock POP3 server.
2. **§8 Scenario "Add a POP3 account, happy path"** — the feature gate and field visibility logic in `AddAccountViewModel` has a history of subtle ordering bugs (see `OnSelectedBackendChanged` in `AddAccountViewModel.cs`). Test all three permutations: IMAP→IMAP, IMAP→POP3, POP3→IMAP.
3. **§8 Scenario "Existing IMAP account unaffected"** — the shared `MailServiceRouter` dispatch table must route IMAP accounts to `ImapMailService` and POP3 accounts to `Pop3MailService` without mixing. Test with both account types active simultaneously.

If any of these fail, document the failure before handing to code review.

---

## Approval Checklist

- [ ] Scope is bounded. (New backend only; no folder hierarchy, no POP3 OAuth, no partial download.)
- [ ] Architecture is decided. (Router pattern; single connection; UIDL deduplication; local store for all state.)
- [ ] Shared component audit complete (§5.4). All modified classes named with their other consumers.
- [ ] Keyboard walkthrough complete (§6). All paths covered including happy path, delete variants, send, and unsupported ops.
- [ ] Acceptance walkthrough written (§8). Happy path, feature gate, delete variants, regression, `--online`, screen reader.
- [ ] Accessibility explicit (§7). All new controls named; announcement categories specified.
- [ ] Implementation phases testable. Each phase produces independently testable code.
- [ ] Risk assessment documented (§13.1). `NotSupportedException` audit, single-session enforcement, MIME storage, folder name rendering.
- [ ] Open questions listed (§13.2). Three questions requiring pre-implementation decisions: folder naming, local Trash, attachment storage.
- [ ] Files and tests are listed (§11, §12). Full implementation checklist.
- [ ] Runtime modes considered. `--online` guard defined; POP3 skipped with log warning.

**Status:** Draft — resolve §13.2 open questions before approving.
