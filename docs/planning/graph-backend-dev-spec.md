# Microsoft Graph Backend — Development Specification

**Status:** Proposed
**Version:** 0.2 (incorporates maintainer feedback on feature gating)
**Date:** 2026-05-24
**Based on:** `graph-backend-pm-spec.md` v0.2 (Proposed)
**Target:** AI coding agent or human contributor implementation

**Changelog from v0.1:**
- §2, §3, §4: added `IFeatureGate` + `ConfigFeatureGate` + `FeatureFlag` enum to files-to-create.
- §5: PR 3 implementation order grows to include the feature gate.
- §6: new §6.14 detailing `IFeatureGate` design.
- §7: added gate-state tests to PR 8.
- §8: PR 3 manual smoke checklist explicitly verifies gate-on and gate-off behavior.

---

## Table of Contents

1. [Overview](#1-overview)
2. [Phasing & PR Plan](#2-phasing--pr-plan)
3. [Files to Create](#3-files-to-create)
4. [Files to Modify](#4-files-to-modify)
5. [Implementation Order](#5-implementation-order)
6. [Detailed File Specifications](#6-detailed-file-specifications)
   - [6.1 `BackendKind.cs` — Enum](#61-backendkindcs--enum)
   - [6.2 `IMailService.cs` — Renamed Interface](#62-imailservicecs--renamed-interface)
   - [6.3 `ImapMailService.cs` — Renamed Class](#63-imapmailservicecs--renamed-class)
   - [6.4 `MailServiceRouter.cs` — Per-Account Dispatcher](#64-mailserviceroutercs--per-account-dispatcher)
   - [6.5 `MailMessageSummary.cs` — string MessageId](#65-mailmessagesummarycs--string-messageid)
   - [6.6 `LocalStoreService.cs` — Schema Migration v2](#66-localstoreservicecs--schema-migration-v2)
   - [6.7 `AccountModel.cs` — BackendKind & TenantId](#67-accountmodelcs--backendkind--tenantid)
   - [6.8 `OAuthService.cs` — Per-Call Scopes](#68-oauthservicecs--per-call-scopes)
   - [6.9 `AddAccountViewModel.cs` & Dialog — Account Type Combo](#69-addaccountviewmodelcs--dialog--account-type-combo)
   - [6.10 `GraphMailService.cs` — Read Path](#610-graphmailservicecs--read-path)
   - [6.11 `GraphSmtpService.cs` — Send Path](#611-graphsmtpservicecs--send-path)
   - [6.12 `GraphChangeNotifier.cs` — Delta Polling](#612-graphchangenotifiercs--delta-polling)
   - [6.13 `App.xaml.cs` — DI Wiring](#613-appxamlcs--di-wiring)
7. [Tests](#7-tests)
8. [Per-PR Manual Smoke Checklist](#8-per-pr-manual-smoke-checklist)
9. [Edge Cases & Error Handling](#9-edge-cases--error-handling)
10. [Accessibility Checklist](#10-accessibility-checklist)
11. [Build Verification](#11-build-verification)

---

## 1. Overview

This spec implements the [Microsoft Graph Backend PM spec](graph-backend-pm-spec.md). The implementation is staged across eight PRs over two release cycles (v0.7 refactor, v0.8 Graph backend).

**Key constraints from the codebase (from CLAUDE.md):**

- MVVM strictly enforced — no `MessageBox`, `Window`, or `Dispatcher` in ViewModels
- All keyboard shortcuts registered in `CommandRegistry`
- All screen-reader announcements through `AccessibilityHelper.Announce()` with explicit `AnnouncementCategory`
- Manual DI in `App.xaml.cs` — no container
- Services follow the existing pattern (interface + class, constructor injection)
- Passwords never in JSON; OAuth tokens in DPAPI-encrypted MSAL cache
- New backend must respect the existing IMAP-pool philosophy: foreground operations get priority over background work

**Existing infrastructure that this spec reuses without change:**

- `OAuthService` / MSAL token cache (modified only to accept per-call scopes)
- `MimeMessageBuilder` (Graph `/sendMail` accepts MIME)
- `LocalStoreService` SQLite schema (modified for `unique_id` type change; otherwise same shape)
- `ConversationBuilder`, `SenderGroupBuilder`, `FolderTreeBuilder` (all backend-agnostic)
- `AccessibilityHelper`, `CommandRegistry`, `ViewService`, `RuleService` (zero changes)
- `WebView2` HTML rendering (zero changes — Graph delivers the same HTML)

---

## 2. Phasing & PR Plan

| PR | Title | v target | Approx LOC |
|---|---|---|---|
| 0 | This spec + PM spec, merged as draft | — | — |
| 1 | Rename `IImapService` → `IMailService`, `ImapService` → `ImapMailService` | v0.7 | ~150 |
| 2 | `uint UniqueId` → `string MessageId`; SQLite schema migration v2 | v0.7 | ~800 |
| 3 | `BackendKind` + `MailServiceRouter` + **`IFeatureGate` infrastructure** + Add Account UI combo (Microsoft 365 option visible only when `FeatureFlag.GraphBackend` is on) | v0.7 | ~500 |
| 4 | `GraphMailService` read path + `OAuthService` per-call scopes | v0.8 | ~600 |
| 5 | `GraphSmtpService` send path | v0.8 | ~150 |
| 6 | Graph attachments, folder CRUD, move/copy/delete | v0.8 | ~400 |
| 7 | `IChangeNotifier`, `ImapChangeNotifier` (extracted), `GraphChangeNotifier` (delta polling) | v0.8 | ~300 |
| 8 | Tests: `GraphMailServiceTests`, `MailServiceRouterTests`, `FeatureGateTests`, migration round-trip | v0.8 | ~450 |
| **GA** | One-line PR flipping `FeatureFlag.GraphBackend` default `false` → `true`. Requires explicit maintainer approval. No fixed release. | TBD | <10 |

Total: ~3100 LOC across ~27 files touched.

---

## 3. Files to Create

| # | File | Phase | Purpose |
|---|---|---|---|
| 1 | `QuickMail/Models/BackendKind.cs` | 3 | Enum |
| 2 | `QuickMail/Models/FeatureFlag.cs` | 3 | Enum of feature-gate keys (initially: `GraphBackend`) |
| 3 | `QuickMail/Services/IFeatureGate.cs` | 3 | Feature-gate interface |
| 4 | `QuickMail/Services/ConfigFeatureGate.cs` | 3 | Reads `[features]` from config.ini + CLI `--feature` flags |
| 5 | `QuickMail/Services/IMailService.cs` | 1 | Renamed interface (file move from `IImapService.cs`) |
| 6 | `QuickMail/Services/ImapMailService.cs` | 1 | Renamed class (file move from `ImapService.cs`) |
| 7 | `QuickMail/Services/MailServiceRouter.cs` | 3 | Per-account `IMailService` dispatcher |
| 8 | `QuickMail/Services/GraphMailService.cs` | 4 | Graph backend |
| 9 | `QuickMail/Services/GraphSmtpService.cs` | 5 | Graph send via `/me/sendMail` |
| 10 | `QuickMail/Services/IChangeNotifier.cs` | 7 | Abstraction over IDLE / delta polling |
| 11 | `QuickMail/Services/ImapChangeNotifier.cs` | 7 | Extracted from `ImapMailService.StartIdleWatchers` |
| 12 | `QuickMail/Services/GraphChangeNotifier.cs` | 7 | Delta polling loop |
| 13 | `QuickMail/Services/Graph/GraphClient.cs` | 4 | Thin HttpClient wrapper with auth, throttling, paging |
| 14 | `QuickMail/Services/Graph/GraphMessage.cs` | 4 | DTO for `/me/messages` response |
| 15 | `QuickMail/Services/Graph/GraphMailFolder.cs` | 4 | DTO for `/me/mailFolders` |
| 16 | `QuickMail/Services/Graph/GraphAttachment.cs` | 6 | DTO for `/messages/{id}/attachments` |
| 17 | `QuickMail/Services/Graph/GraphDeltaResponse.cs` | 7 | DTO for delta envelope |
| 18 | `QuickMail.Tests/FeatureGateTests.cs` | 3 | Gate-resolution tests (config + CLI + defaults) |
| 19 | `QuickMail.Tests/GraphMailServiceTests.cs` | 8 | Tests against `HttpMessageHandler` stub |
| 20 | `QuickMail.Tests/MailServiceRouterTests.cs` | 8 | Routing logic tests |
| 21 | `QuickMail.Tests/LocalStoreServiceMigrationTests.cs` | 2 | Schema v1 → v2 round-trip |

---

## 4. Files to Modify

| # | File | Phase | Change |
|---|---|---|---|
| 1 | `QuickMail/Services/IImapService.cs` (delete) | 1 | Replaced by `IMailService.cs` |
| 2 | `QuickMail/Services/ImapService.cs` (delete) | 1 | Replaced by `ImapMailService.cs` |
| 3 | `QuickMail/Models/MailMessageSummary.cs` | 2 | `uint UniqueId` → `string MessageId` |
| 4 | `QuickMail/Models/MailMessageDetail.cs` | 2 | Inherits change (no own field change) |
| 5 | `QuickMail/Models/AttachmentModel.cs` | 6 | Rename `PartSpecifier` → `AttachmentRef` |
| 6 | `QuickMail/Models/AccountModel.cs` | 3 | Add `BackendKind BackendKind`, `string? TenantId` |
| 7 | `QuickMail/Services/ILocalStoreService.cs` | 2 | All `uint uid` → `string messageId` |
| 8 | `QuickMail/Services/LocalStoreService.cs` | 2 | Schema migration v2 (see §6.6) |
| 9 | `QuickMail/Services/OAuthService.cs` | 4 | Accept `string[] scopes` per call (overload retains existing default for callers not ready) |
| 10 | `QuickMail/Services/SyncService.cs` | 2 + 3 | `string` IDs; talks to `IMailService` |
| 11 | `QuickMail/Services/RuleService.cs` | 2 | `string` IDs |
| 12 | `QuickMail/ViewModels/MainViewModel.cs` | 2 + 3 | `string` IDs (44 call sites); injects `IMailService` (was `IImapService`) |
| 13 | `QuickMail/ViewModels/AddAccountViewModel.cs` | 3 | `BackendKind` property; conditional defaults; consults `IFeatureGate` to populate combo options |
| 14 | `QuickMail/ViewModels/AccountEditorViewModel.cs` | 3 | Read-only `BackendKind` (set at construction, never edited) |
| 15 | `QuickMail/Views/AddAccountDialog.xaml` | 3 | New combo at top (single option when gate off; two options when gate on); conditional visibility on IMAP/SMTP groups |
| 16 | `QuickMail/Views/AddAccountDialog.xaml.cs` | 3 | Announce field-set changes on backend type change |
| 17 | `QuickMail/Views/AccountManagerDialog.xaml` | 3 | Display (read-only) account type column |
| 17a | `QuickMail/Models/ConfigModel.cs` | 3 | Add `Dictionary<string, string> Features` for `[features]` section parsing |
| 17b | `QuickMail/Services/ConfigService.cs` | 3 | Parse `[features]` INI section into `ConfigModel.Features` |
| 18 | `QuickMail/App.xaml.cs` | 1, 3, 4 | Each PR updates DI wiring incrementally |
| 19 | `QuickMail.Tests/StubServices.cs` | 1, 3, 4 | Rename stubs in PR 1; add `StubGraphMailService` in PR 4 |
| 20 | `QuickMail/QuickMail.csproj` | 4 | No new packages (raw HttpClient choice per PM spec §9.6) |
| 21 | `CLAUDE.md` | 3 | Document the router pattern in the Architecture section |
| 22 | `USERGUIDE.md` | 4 | Add "Microsoft 365 accounts" section |

---

## 5. Implementation Order

Strict ordering. Each PR is mergeable on its own and leaves the app behaviorally identical to the previous PR (with the exception of the Add Account UI in PR 3, which adds a combo box, and the Graph user-facing functionality in PR 4+).

### PR 1 — Interface rename (1-2 evenings)

1. Copy `IImapService.cs` to `IMailService.cs`; change `interface IImapService` → `interface IMailService`. Delete `IImapService.cs`.
2. Copy `ImapService.cs` to `ImapMailService.cs`; change `class ImapService` → `class ImapMailService : IMailService`. Delete `ImapService.cs`.
3. Update every consumer's constructor parameter: `IImapService` → `IMailService`. Visual Studio "Find All References" + bulk replace.
4. Update `StubServices.cs`: `StubImapService` → `StubImapMailService` implementing `IMailService`.
5. Update `App.xaml.cs`: `var imapService = new ImapService(...)` → `var mailService = new ImapMailService(...)`. Rename local variable everywhere it appears.
6. Build, test suite, manual smoke (any IMAP account).

### PR 2 — UID → string (3-5 evenings)

1. `MailMessageSummary.UniqueId: uint` → `MessageId: string`.
2. `AttachmentModel.PartSpecifier` keeps its type (still `string?`) — no change in PR 2; renamed in PR 6.
3. `IMailService`: every `uint uid` → `string messageId`; every `IList<uint> uids` → `IList<string> messageIds`; `GetMaxUidAsync` → `GetMaxMessageKeyAsync` (returns `string` — IMAP impl returns zero-padded UID; Graph impl returns the delta token).
4. `ILocalStoreService`: same changes.
5. `LocalStoreService`: rewrite schema migration logic. Bump `CurrentSchemaVersion` to 2. Migration SQL per PM spec Appendix C.
6. `LocalStoreService.Initialize`: pre-migration backup of `mail.db` → `mail.db.pre-v2` if `user_version < 2`.
7. `ImapMailService`: every `client.Uid` → `client.Uid.ToString(CultureInfo.InvariantCulture)`. Every internal `uint` becomes `string`. Where `uint.Parse` is needed (e.g. for IMAP UID arithmetic), parse on entry.
8. `SyncService`: `maxUid` (`uint`) → `maxKey` (`string`); IMAP backend interprets, Graph backend ignores.
9. `RuleService`: 12 occurrences of `UniqueId` → `MessageId`.
10. `MainViewModel`: 44 occurrences. The compiler is your friend — `uint` → `string` won't compile until every site is updated.
11. Migration test: `LocalStoreServiceMigrationTests` — seed a v1 schema with 1000 rows, run migration, assert all rows survive with correct text IDs.
12. Full test suite, manual smoke on a Gmail account: open app, fetch mail, read a message, mark unread, move to folder, send a reply, delete. Compare with pre-PR behavior.

### PR 3 — Backend kind + router + feature gate (2-3 evenings)

1. `BackendKind.cs`: enum with `ImapSmtp`, `MicrosoftGraph`.
2. `FeatureFlag.cs`: enum with `GraphBackend` (others added by future PRs).
3. `IFeatureGate.cs`: `bool IsEnabled(FeatureFlag flag);`.
4. `ConfigFeatureGate.cs`: reads `[features]` section from `ConfigModel.Features` and merges CLI `--feature` flags from `App.xaml.cs`. Defaults: every flag is `false` unless explicitly enabled.
5. `ConfigModel.cs`: add `Dictionary<string, string> Features { get; set; } = new();`.
6. `ConfigService.cs`: extend INI parser to read `[features]` section into `ConfigModel.Features`.
7. `App.xaml.cs`: parse `--feature` CLI flags (multiple allowed); construct `ConfigFeatureGate(configService.Load(), cliFlags)`; pass to `AddAccountViewModel`.
8. `AccountModel`: add `BackendKind BackendKind { get; set; } = BackendKind.ImapSmtp;` and `string? TenantId { get; set; }`.
9. `MailServiceRouter.cs`: implements `IMailService`. Holds `Dictionary<Guid, IMailService> _byAccount`. Each method looks up by accountId and delegates. Events are aggregated from all underlying services.
10. `App.xaml.cs`: change `mainVm = new MainViewModel(mailService, ...)` to `mainVm = new MainViewModel(router, ...)` where `router = new MailServiceRouter(...)`. For every account, the router registers `ImapMailService` (Graph backend wired in PR 4).
11. `AccountEditorViewModel`: expose `BackendKind` as a read-only property when editing (set at construction, displayed but not editable).
12. `AddAccountViewModel`: accept `IFeatureGate` in constructor. Expose `BackendKind` as a writable property with bindable default `ImapSmtp`. When set to `MicrosoftGraph`: clear IMAP/SMTP fields, force `AuthType = OAuth2Microsoft`. Expose `IReadOnlyList<BackendKindOption> AvailableBackends` derived from gate state: always includes `ImapSmtp`; includes `MicrosoftGraph` only when `gate.IsEnabled(FeatureFlag.GraphBackend)`.
13. `AddAccountDialog.xaml`: add a `ComboBox` at the top bound to `BackendKind`, items source bound to `AvailableBackends`. When `AvailableBackends.Count == 1`, the dialog renders the combo as a static label ("Standard IMAP/SMTP") to reduce visual clutter for the default user. Add `Visibility` triggers on the IMAP and SMTP group boxes that collapse them when `BackendKind == MicrosoftGraph`.
14. `AddAccountDialog.xaml.cs`: on `BackendKind` change, call `AccessibilityHelper.Announce(...)` with the appropriate `Hint` message.
15. Until PR 4 lands: even with the gate on, selecting "Microsoft 365" produces a dialog warning ("Graph backend not yet implemented") on Save. This keeps PR 3 a pure refactor + UI scaffold + gate infrastructure.
16. Build, test, smoke. **Verify both gate states:**
    - Gate off (default): combo not visible / shows only one option, IMAP account creation works identically to today.
    - Gate on (`config.ini` has `[features]\nGraphBackend=true`, or `--feature GraphBackend`): two options in combo, IMAP path still works identically.

**`TestConnectionAsync` for Graph accounts:** `AccountEditorViewModel.TestConnectionAsync` is currently IMAP-specific — it guards on `ImapHost`, builds an `AccountModel` with IMAP fields, and calls `ConnectAsync`. For a `MicrosoftGraph` account those fields are empty. Make it a `virtual` method on the base `AccountEditorViewModel` that branches on `BackendKind` before the IMAP path: for `MicrosoftGraph`, skip the IMAP host/port validation and let `ConnectAsync` (routed to `GraphMailService` by `MailServiceRouter`) exercise the Graph `GET /me` probe, surfacing success/failure through the same `StatusText`. Because Graph `ConnectAsync` is not implemented until PR 4, a Graph "test connection" in PR 3 reports "not yet implemented" — consistent with step 15, keeping PR 3 a pure refactor + UI scaffold. A future Graph-specific editor can override the virtual cleanly.

### PR 4 — Graph read path (4-6 evenings)

1. `Graph/GraphClient.cs`: thin `HttpClient` wrapper. Holds `IOAuthService`. Methods: `GetAsync<T>`, `PostAsync<T>`, `PatchAsync<T>`, `DeleteAsync`. Honors `Retry-After` on 429. Auto-pages on `@odata.nextLink`. Adds `Authorization: Bearer {token}` from `OAuthService.GetAccessTokenAsync(account, GraphScopes)`.
2. `Graph/GraphMessage.cs`, `Graph/GraphMailFolder.cs`: DTOs matching Graph v1.0 response shape. Use `JsonPropertyName` attributes for camelCase mapping.
3. `OAuthService.cs`: add overload `GetAccessTokenAsync(AccountModel account, string[] scopes, CancellationToken ct = default)`. Existing zero-scope-arg overload calls the new one with IMAP scopes.
4. `GraphMailService.cs`: implements `IMailService`. Reads only — `MarkReadAsync`, `MoveMessagesAsync`, etc. throw `NotImplementedException` (lit up in PRs 5-6). Implements:
   - `ConnectAsync` — `GET /me?$select=id,userPrincipalName`; populates `account.Username` on success.
   - `GetFoldersAsync` — `GET /mailFolders?$top=100`; paginate via `@odata.nextLink`; map to `MailFolderModel` with `FullName = folder.id`, `DisplayName = folder.displayName`. Folder tree built by existing `FolderTreeBuilder` using `parentFolderId`.
   - `GetMessageSummariesAsync` — `GET /mailFolders/{id}/messages?$top={N}&$orderby=receivedDateTime desc&$select=...`. Map to `MailMessageSummary` with `MessageId = msg.id`.
   - `GetMessageDetailAsync` — `GET /messages/{id}?$select=...&$expand=attachments`. Map body, recipients, attachments.
   - `MarkReadAsync` — `PATCH /messages/{id}` with `{ "isRead": true }`.
   - `GetInboxStatusAsync` — `GET /mailFolders/Inbox?$select=totalItemCount,unreadItemCount`.
   - `FindDraftsFolderNameAsync` — return well-known `"Drafts"` (Graph well-known folder name; works as folder ID).
   - All other methods: `throw new NotImplementedException("Wired in PR 5/6")`.
5. `App.xaml.cs`: when an account has `BackendKind == MicrosoftGraph`, register `GraphMailService` in the router instead of `ImapMailService`.
6. `AddAccountDialog.xaml`: enable the "Microsoft 365 / Outlook.com" combo option.
7. `AddAccountViewModel`: on `BackendKind = MicrosoftGraph`, the **Sign in** button (existing OAuth flow) uses Graph scopes via the overload.
8. **Maintainer coordination required**: MSAL app registration needs Graph scopes added in Azure portal before this PR can be tested end-to-end.
9. Smoke test on a real M365 account: create account, fetch inbox, read a message.

### PR 5 — Graph send (1-2 evenings)

1. `GraphSmtpService.cs`: implements `ISmtpService`. `SendAsync` posts the MIME from `MimeMessageBuilder` to `POST /me/sendMail` with `Content-Type: text/plain` and base64-encoded MIME body. Graph auto-saves to Sent — `AppendToSentAsync` becomes a no-op for Graph.
2. `App.xaml.cs`: SMTP service is a router too — `SmtpServiceRouter` (simpler than mail; one method). Or: the existing `SmtpService` checks `account.BackendKind` and delegates. Simpler — go with the second.
3. Modify `SmtpService.SendAsync`: at the top, `if (account.BackendKind == BackendKind.MicrosoftGraph) { await _graphSmtp.SendAsync(...); return; }`.
4. Smoke: compose and send from an M365 account; verify it appears in Sent.

### PR 6 — Graph mutations (3-5 evenings)

1. `GraphMailService`: implement the rest of `IMailService`:
   - `MoveMessagesAsync` — `POST /messages/{id}/move` per message; consider `$batch` for >5 messages.
   - `MoveToTrashBatchAsync` — same, target = Deleted Items well-known folder.
   - `PermanentlyDeleteBatchAsync` — `DELETE /messages/{id}` per message.
   - `CopyMessagesAsync` — `POST /messages/{id}/copy`.
   - `DownloadAttachmentAsync` — `GET /messages/{messageId}/attachments/{attachmentId}/$value`.
   - `CreateFolderAsync` / `DeleteFolderAsync` / `RenameFolderAsync` / `CopyFolderAsync` — `mailFolders` endpoints per appendix A.
   - `AppendDraftAsync` — `POST /messages`; if `replaceMessageId` is provided, `DELETE` it first.
   - `EmptyTrashAsync` — list + bulk delete (preview `permanentlyDelete` may be available; check at impl time).
2. `AttachmentModel.PartSpecifier` rename to `AttachmentRef`. Find-replace across 4 files.
3. `FetchPreviewsAsync` becomes a no-op for Graph (`bodyPreview` already populated in summaries).
4. `NoOpAsync` becomes a no-op for Graph (HTTP is stateless).
5. Smoke: full mutate cycle on M365 — move, copy, delete, attach a file, save draft, edit draft, send.

### PR 7 — Change notifications (3-4 evenings)

1. `IChangeNotifier.cs`: defines `event Action<Guid>? InboxNewMailDetected; void StartWatchers(IReadOnlyList<AccountModel> accounts, CancellationToken ct); void StopWatchers();`.
2. `ImapMailService`: remove `StartIdleWatchers`, `StopIdleWatchers`, `InboxNewMailDetected` from `IMailService`. Move those into `ImapChangeNotifier`, which holds the existing IDLE pool logic.
3. `GraphChangeNotifier`: one polling `Task` per Graph account. Each tick: request the stored `@odata.deltaLink` URL (or start a fresh `…/messages/delta` enumeration on the first run), following `@odata.nextLink` to the end of the page set. If any page contains ≥1 message, raise `InboxNewMailDetected(accountId)`; persist the final page's `@odata.deltaLink` URL to the `DeltaToken` table as the cursor for the next tick. The within-tick `@odata.nextLink`/`$skipToken` paging cursor is never persisted (see §6.12).
4. Poll interval: 60s default, configurable via `config.ini` as `GraphPollSeconds` (clamped 30-600).
5. `App.xaml.cs`: wire a `ChangeNotifierRouter` (or inline both notifiers and start both).
6. `MainViewModel.StartIdleWatchers` call site: rename to `StartChangeWatchers`.
7. Smoke: send a message to the M365 account from another mailbox; verify QuickMail notices within 60s.

### PR 8 — Tests (2-3 evenings)

1. `GraphMailServiceTests`: use `HttpMessageHandler` stub. Test each endpoint mapping. Test 429 backoff. Test pagination via `@odata.nextLink`. Test delta token persistence.
2. `MailServiceRouterTests`: register a fake `IMailService` for two accounts; assert calls route correctly; assert events from both backends are surfaced.
3. `LocalStoreServiceMigrationTests` (already added in PR 2): no change.
4. Update `StubServices.cs` with a `StubGraphMailService` that does nothing — used by VM construction tests.

---

## 6. Detailed File Specifications

### 6.1 `BackendKind.cs` — Enum

**Path:** `QuickMail/Models/BackendKind.cs`

```csharp
namespace QuickMail.Models;

/// <summary>
/// Which protocol stack this account uses. Selected when the account is added
/// and fixed for its lifetime. To switch, delete and re-add the account.
/// </summary>
public enum BackendKind
{
    /// <summary>Standard IMAP for receive + SMTP for send (default for all existing accounts).</summary>
    ImapSmtp,

    /// <summary>Microsoft Graph for receive + send. Used for M365 / Outlook.com.</summary>
    MicrosoftGraph,
}
```

### 6.2 `IMailService.cs` — Renamed Interface

**Path:** `QuickMail/Services/IMailService.cs`

**Phase 1: byte-identical to `IImapService.cs` except:**
- Filename
- Interface name (`IMailService`)

**Phase 2 (within PR 2):** every `uint uid` → `string messageId`, every `IList<uint>` → `IList<string>`, `GetMaxUidAsync` → `GetMaxMessageKeyAsync`.

**Phase 7 (PR 7):** remove `event Action<Guid>? InboxNewMailDetected; StartIdleWatchers; StopIdleWatchers;` — moved to `IChangeNotifier`.

No other changes.

### 6.3 `ImapMailService.cs` — Renamed Class

**Path:** `QuickMail/Services/ImapMailService.cs`

Identical contents to `ImapService.cs` after rename. Class name `ImapMailService`. The class continues to use MailKit and the existing per-account connection pool. The only logical changes happen in later PRs:

- PR 2: `client.Uid` → `client.Uid.ToString(CultureInfo.InvariantCulture)`; internal `uint`s become `string`. UID parsing happens at the IMAP boundary only.
- PR 7: IDLE logic moves to `ImapChangeNotifier`; `ImapMailService.StartIdleWatchers` becomes a thin wrapper that delegates (kept for compile compatibility, then removed).

### 6.4 `MailServiceRouter.cs` — Per-Account Dispatcher

**Path:** `QuickMail/Services/MailServiceRouter.cs`

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// IMailService implementation that holds one backend per account and dispatches
/// every call to the right backend based on accountId. Consumers see a single
/// IMailService surface.
/// </summary>
public class MailServiceRouter : IMailService
{
    private readonly ConcurrentDictionary<Guid, IMailService> _byAccount = new();
    private readonly IReadOnlyList<IMailService> _allBackends; // ordered, for event aggregation

    public MailServiceRouter(IEnumerable<IMailService> backends)
    {
        _allBackends = backends.ToList();
        // Subscribe to each backend's events and re-raise on this router.
        foreach (var b in _allBackends)
            b.InboxNewMailDetected += OnInnerInboxNewMail;
    }

    /// <summary>
    /// Bind an account to a specific backend. Called once at account-load time
    /// and once per Add Account.
    /// </summary>
    public void RegisterAccount(Guid accountId, IMailService backend)
        => _byAccount[accountId] = backend;

    public void UnregisterAccount(Guid accountId)
        => _byAccount.TryRemove(accountId, out _);

    private IMailService For(Guid accountId)
        => _byAccount.TryGetValue(accountId, out var b)
           ? b
           : throw new InvalidOperationException($"No backend registered for account {accountId}");

    private IMailService For(AccountModel account)
    {
        if (!_byAccount.TryGetValue(account.Id, out var b))
        {
            // Lazy registration on first Connect — useful for first-time account adds.
            // The host (App.xaml.cs) is responsible for choosing the right backend.
            throw new InvalidOperationException(
                $"No backend registered for account {account.Id} ({account.AccountLabel}). " +
                "Call RegisterAccount before any IMailService method.");
        }
        return b;
    }

    private void OnInnerInboxNewMail(Guid accountId) => InboxNewMailDetected?.Invoke(accountId);
    public event Action<Guid>? InboxNewMailDetected;

    // ── Every method delegates ───────────────────────────────────────────────

    public Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default)
        => For(account).ConnectAsync(account, password, ct);

    public Task DisconnectAsync(Guid accountId, CancellationToken ct = default)
        => For(accountId).DisconnectAsync(accountId, ct);

    public Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default)
        => For(accountId).GetFoldersAsync(accountId, ct);

    // ... and so on for every method on IMailService ...

    public void Dispose()
    {
        foreach (var b in _allBackends)
        {
            b.InboxNewMailDetected -= OnInnerInboxNewMail;
            b.Dispose();
        }
    }
}
```

**Notes:**
- Router itself implements `IMailService` so `MainViewModel`, `SyncService`, `RuleService` are unchanged — they always saw `IMailService`.
- Backends are constructed once in `App.xaml.cs` and registered per-account at account load.
- `RegisterAccount` is idempotent — re-registration replaces the binding.

### 6.5 `MailMessageSummary.cs` — string MessageId

**Path:** `QuickMail/Models/MailMessageSummary.cs`

Change:

```csharp
// before:
public uint UniqueId { get; set; }

// after:
public string MessageId { get; set; } = string.Empty;
```

Everything else unchanged. `MailMessageDetail : MailMessageSummary` inherits.

**Sort behavior note:** when sorting messages by `MessageId` for backend-internal purposes (e.g. IMAP `GetMaxMessageKeyAsync`), the IMAP impl returns the zero-padded UID (`uid.ToString("D10")`) so string comparison preserves numeric order. Graph never compares IDs — it uses the delta token instead.

**`GetMessagesSinceAsync` backend contract (resolves the PR 2 semantic gap):** after PR 2 the signature becomes `GetMessagesSinceAsync(Guid accountId, string folderName, string messageId, int initialCount, …)`. The `messageId` argument is meaningful **only** to the IMAP backend, which parses it back to a `uint` UID and requests `UID > messageId`. **`GraphMailService` ignores `messageId` entirely** and instead reads the folder's stored `@odata.deltaLink` cursor (see §6.12) to perform an incremental delta enumeration; an empty/`"0"` argument requests a first/full sync on both backends. This difference is invisible at the call site by design — `SyncService` passes the value it has and lets each backend interpret it. PR 4 implementers: do **not** attempt to honor `messageId` in `GraphMailService`; treating it as a UID range there is a bug.

### 6.6 `LocalStoreService.cs` — Schema Migration v2

**Path:** `QuickMail/Services/LocalStoreService.cs`

Migration logic added at top of `Initialize`:

```csharp
public void Initialize()
{
    // Pre-migration backup: if we're upgrading from v1, copy mail.db to mail.db.pre-v2.
    var dbPath = Path.Combine(_profileDir, "mail.db");
    if (File.Exists(dbPath))
    {
        using (var probe = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;"))
        {
            probe.Open();
            using var pragma = probe.CreateCommand();
            pragma.CommandText = "PRAGMA user_version;";
            var ver = Convert.ToInt32(pragma.ExecuteScalar());
            if (ver < 2)
            {
                var backupPath = dbPath + ".pre-v2";
                File.Copy(dbPath, backupPath, overwrite: true);
                LogService.Log($"LocalStoreService: backed up mail.db to {backupPath} before v2 migration");
            }
        }
    }

    using var conn = Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        PRAGMA journal_mode=WAL;
        -- (CREATE TABLE statements with TEXT unique_id from PM spec Appendix C)
        """;
    cmd.ExecuteNonQuery();

    RunMigration(conn, /* existing v1 ALTER TABLEs */);
    RunDataMigrations(conn);
}

private const int CurrentSchemaVersion = 2;

private static void RunDataMigrations(SqliteConnection conn)
{
    var version = GetUserVersion(conn);
    if (version >= CurrentSchemaVersion) return;

    if (version < 1)
    {
        // existing v1 backfill (unchanged)
    }

    if (version < 2)
    {
        // PM spec Appendix C — change unique_id from INTEGER to TEXT, add DeltaToken table.
        using var migrateCmd = conn.CreateCommand();
        migrateCmd.CommandText = @"
            CREATE TABLE MessageSummary_v2 (
                unique_id    TEXT    NOT NULL,
                /* ... */
            );
            INSERT INTO MessageSummary_v2
            SELECT CAST(unique_id AS TEXT), /* ... */ FROM MessageSummary;
            DROP TABLE MessageSummary;
            ALTER TABLE MessageSummary_v2 RENAME TO MessageSummary;
            /* ... same for MessageDetail ... */
            CREATE TABLE DeltaToken (
                account_id   TEXT NOT NULL,
                folder_id    TEXT NOT NULL,
                delta_token  TEXT NOT NULL,
                updated_utc  INTEGER NOT NULL,
                PRIMARY KEY (account_id, folder_id)
            );
        ";
        migrateCmd.ExecuteNonQuery();
    }

    SetUserVersion(conn, CurrentSchemaVersion);
}
```

All other method signatures in `ILocalStoreService` and `LocalStoreService` change `uint uniqueId` → `string messageId`. SQL parameter bindings change from `AddWithValue("@unique_id", uid)` to `AddWithValue("@unique_id", messageId)`.

### 6.7 `AccountModel.cs` — BackendKind & TenantId

**Path:** `QuickMail/Models/AccountModel.cs`

Add two fields after the existing persistent fields:

```csharp
public AuthType AuthType { get; set; } = AuthType.Password;

// NEW:
/// <summary>Which protocol stack this account uses. Fixed at account creation.</summary>
public BackendKind BackendKind { get; set; } = BackendKind.ImapSmtp;

/// <summary>Optional Azure AD tenant ID for Graph accounts. Null = "common" authority.</summary>
public string? TenantId { get; set; }
```

No code changes elsewhere in `AccountModel`. The new fields serialize/deserialize via System.Text.Json defaults; existing `accounts.json` files lack these fields and get the defaults (`ImapSmtp`, `null`) on load.

### 6.8 `OAuthService.cs` — Per-Call Scopes

**Path:** `QuickMail/Services/OAuthService.cs`

Add a new overload that accepts scopes; keep existing overload for backward compatibility during refactor:

```csharp
// Existing scope constants (unchanged):
public static readonly string[] ImapSmtpScopes =
[
    "https://outlook.office.com/IMAP.AccessAsUser.All",
    "https://outlook.office.com/SMTP.Send",
    "offline_access",
];

// NEW:
public static readonly string[] GraphMailScopes =
[
    "https://graph.microsoft.com/Mail.ReadWrite",
    "https://graph.microsoft.com/Mail.Send",
    "https://graph.microsoft.com/MailboxSettings.Read",
    "offline_access",
];

// Existing methods become wrappers around new scope-aware overloads:
public Task<string> GetAccessTokenAsync(AccountModel account, CancellationToken ct = default)
    => GetAccessTokenAsync(account, DefaultScopesFor(account), ct);

// NEW:
public async Task<string> GetAccessTokenAsync(AccountModel account, string[] scopes, CancellationToken ct = default)
{
    // existing silent-then-interactive logic, with `scopes` parameter passed to MSAL
    // instead of the hardcoded `Scopes` static field
}

private static string[] DefaultScopesFor(AccountModel account)
    => account.BackendKind == BackendKind.MicrosoftGraph
        ? GraphMailScopes
        : ImapSmtpScopes;
```

`ImapMailService.ConnectAsync` continues to call `_oauth.GetAccessTokenAsync(account, ct)` — the default-scope path picks `ImapSmtpScopes`. `GraphMailService.ConnectAsync` calls `_oauth.GetAccessTokenAsync(account, GraphMailScopes, ct)` explicitly. After all consumers are updated, the no-scopes-arg overload can stay as a convenience.

### 6.9 `AddAccountViewModel.cs` & Dialog — Account Type Combo

**`AddAccountViewModel.cs` additions:**

```csharp
public partial class AddAccountViewModel : AccountEditorViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImapFieldsVisible))]
    private BackendKind _backendKind = BackendKind.ImapSmtp;

    public bool IsImapFieldsVisible => BackendKind == BackendKind.ImapSmtp;

    partial void OnBackendKindChanged(BackendKind value)
    {
        if (value == BackendKind.MicrosoftGraph)
        {
            // Force OAuth — no password option for Graph
            AuthType = AuthType.OAuth2Microsoft;
            // Clear IMAP/SMTP fields (they'll be hidden)
            ImapHost = SmtpHost = string.Empty;
            ImapPort = 993; SmtpPort = 587;
            ImapUseSsl = true; SmtpUseSsl = false;
        }
        else
        {
            // Restore IMAP defaults (existing logic in OnAuthTypeChangedInternal)
            OnAuthTypeChangedInternal(AuthType);
        }
    }

    public AccountModel ToAccountModel() => new()
    {
        AccountName = AccountName,
        DisplayName = DisplayName,
        Username = Username,
        AuthType = AuthType,
        BackendKind = BackendKind,        // NEW
        TenantId = null,                  // NEW — v1 always common
        ImapHost = ImapHost,
        // ... rest unchanged ...
    };
}
```

**`AddAccountDialog.xaml` additions:**

Add at the top of the form, before existing fields:

```xml
<StackPanel Orientation="Vertical" Margin="0,0,0,12">
    <TextBlock Text="Account type:" />
    <ComboBox x:Name="BackendKindCombo"
              SelectedIndex="0"
              SelectedItem="{Binding BackendKind, Mode=TwoWay, Converter={StaticResource BackendKindStringConverter}}"
              AutomationProperties.Name="Account type">
        <ComboBoxItem Content="Standard IMAP/SMTP" Tag="ImapSmtp" />
        <ComboBoxItem Content="Microsoft 365 / Outlook.com" Tag="MicrosoftGraph" />
    </ComboBox>
</StackPanel>
```

Wrap the IMAP and SMTP groups with `Visibility="{Binding IsImapFieldsVisible, Converter={StaticResource BoolToVisibilityConverter}}"`.

**`AddAccountDialog.xaml.cs` additions:**

```csharp
public AddAccountDialog(AddAccountViewModel vm)
{
    InitializeComponent();
    DataContext = vm;
    vm.PropertyChanged += OnVmPropertyChanged;
}

private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
{
    if (e.PropertyName == nameof(AddAccountViewModel.BackendKind))
    {
        var vm = (AddAccountViewModel)DataContext;
        if (vm.BackendKind == BackendKind.MicrosoftGraph)
            AccessibilityHelper.Announce(
                "Switched to Microsoft 365. IMAP and SMTP fields are not needed for this account type.",
                AnnouncementCategory.Hint);
        else
            AccessibilityHelper.Announce(
                "Switched to standard IMAP and SMTP. Enter your server settings below.",
                AnnouncementCategory.Hint);
    }
}
```

### 6.10 `GraphMailService.cs` — Read Path

**Path:** `QuickMail/Services/GraphMailService.cs`

Skeleton (full method bodies for the read path; mutations and notifications come in PR 6 and 7):

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services.Graph;

namespace QuickMail.Services;

public class GraphMailService : IMailService
{
    private readonly GraphClient _client;
    private readonly IConfigService? _config;

    public GraphMailService(IOAuthService oauth, IConfigService? config = null)
    {
        _client = new GraphClient(oauth);
        _config = config;
    }

    public event Action<Guid>? InboxNewMailDetected; // wired by GraphChangeNotifier in PR 7

    public async Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default)
    {
        // Validate the token by calling /me. No persistent connection.
        var me = await _client.GetAsync<GraphMe>(account, "/me?$select=id,userPrincipalName", ct);
        if (string.IsNullOrEmpty(me?.UserPrincipalName))
            throw new InvalidOperationException("Graph /me returned no userPrincipalName");
        if (!string.Equals(account.Username, me.UserPrincipalName, StringComparison.OrdinalIgnoreCase))
        {
            LogService.Log($"GraphMailService: token UPN {me.UserPrincipalName} differs from account.Username {account.Username}; updating");
            account.Username = me.UserPrincipalName;
        }
        LogService.Log($"GraphMailService: connected for {account.AccountLabel} ({account.Username})");
    }

    public Task DisconnectAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;

    public async Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default)
    {
        // Graph paginates; GraphClient.GetAllPagesAsync handles @odata.nextLink.
        var folders = await _client.GetAllPagesAsync<GraphMailFolder>(
            accountId, "/me/mailFolders?$top=100&$select=id,displayName,parentFolderId,totalItemCount,unreadItemCount", ct);

        return folders.Select(f => new MailFolderModel
        {
            AccountId = accountId,
            FullName = f.Id,                       // Graph uses opaque IDs as "folder names"
            DisplayName = f.DisplayName,
            UnreadCount = f.UnreadItemCount,
            MessageCount = f.TotalItemCount,
            Kind = MapWellKnownFolder(f.DisplayName),
            ExcludeFromAllMail = ShouldExcludeFromAll(f.DisplayName),
        }).ToList();
    }

    public async Task<List<MailMessageSummary>> GetMessageSummariesAsync(
        Guid accountId, string folderName, int maxMessages, CancellationToken ct = default)
    {
        var path = $"/me/mailFolders/{folderName}/messages" +
                   $"?$top={Math.Min(maxMessages, 999)}" +
                   "&$orderby=receivedDateTime desc" +
                   "&$select=id,subject,from,toRecipients,receivedDateTime,isRead,bodyPreview,hasAttachments";
        var msgs = await _client.GetAllPagesAsync<GraphMessage>(accountId, path, ct);
        return msgs.Select(m => MapToSummary(m, accountId, folderName)).ToList();
    }

    public async Task<MailMessageDetail> GetMessageDetailAsync(
        Guid accountId, string folderName, string messageId, CancellationToken ct = default)
    {
        var path = $"/me/messages/{messageId}" +
                   "?$select=id,subject,body,from,toRecipients,ccRecipients,internetMessageId,receivedDateTime,isRead,hasAttachments" +
                   "&$expand=attachments($select=id,name,contentType,size,isInline)";
        var msg = await _client.GetAsync<GraphMessage>(accountId, path, ct);
        return MapToDetail(msg!, accountId, folderName);
    }

    public async Task MarkReadAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
    {
        await _client.PatchAsync(accountId, $"/me/messages/{messageId}", new { isRead = true }, ct);
    }

    // PR 5 hook
    public Task<(int Total, int Unread)> GetInboxStatusAsync(Guid accountId, CancellationToken ct = default)
        => _client.GetAsync<GraphMailFolder>(accountId, "/me/mailFolders/Inbox?$select=totalItemCount,unreadItemCount", ct)
            .ContinueWith(t => (t.Result!.TotalItemCount, t.Result.UnreadItemCount), ct);

    public Task<string?> FindDraftsFolderNameAsync(Guid accountId, CancellationToken ct = default)
        => Task.FromResult<string?>("Drafts"); // Graph well-known name

    public Task NoOpAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyDictionary<string, string>> FetchPreviewsAsync(
        Guid accountId, string folderName, IList<string> messageIds, int maxLines, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, string>>(
            new Dictionary<string, string>()); // No-op: Graph fills bodyPreview natively

    // ── PR 6 stubs ──
    public Task MoveToTrashAsync(...) => throw new NotImplementedException("PR 6");
    public Task MoveToTrashBatchAsync(...) => throw new NotImplementedException("PR 6");
    public Task PermanentlyDeleteBatchAsync(...) => throw new NotImplementedException("PR 6");
    // ... etc ...

    // ── PR 7 stubs ──
    public void StartIdleWatchers(IReadOnlyList<AccountModel> accounts, CancellationToken ct = default)
        => throw new InvalidOperationException("StartIdleWatchers removed in PR 7; use IChangeNotifier instead");

    // Helpers
    private static MailMessageSummary MapToSummary(GraphMessage m, Guid accountId, string folderName) => new()
    {
        MessageId = m.Id,
        AccountId = accountId,
        FolderName = folderName,
        From = m.From?.EmailAddress?.AsHeaderString() ?? string.Empty,
        To = string.Join(", ", m.ToRecipients?.Select(r => r.EmailAddress.AsHeaderString()) ?? []),
        Subject = m.Subject ?? string.Empty,
        Date = m.ReceivedDateTime,
        IsRead = m.IsRead,
        Preview = m.BodyPreview ?? string.Empty,
        HasAttachments = m.HasAttachments,
    };

    // (MapToDetail similar; populates body, cc, attachments)

    public void Dispose() => _client.Dispose();
}
```

### 6.11 `GraphSmtpService.cs` — Send Path

**Path:** `QuickMail/Services/GraphSmtpService.cs`

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services.Graph;

namespace QuickMail.Services;

public class GraphSmtpService : ISmtpService
{
    private readonly GraphClient _client;
    private static readonly string UserAgent =
        "QuickMail/" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0");

    public GraphSmtpService(IOAuthService oauth) => _client = new GraphClient(oauth);

    public async Task SendAsync(ComposeModel compose, AccountModel account, string? password, CancellationToken ct = default)
    {
        // Graph /sendMail accepts MIME directly when Content-Type is text/plain and the body is the raw MIME.
        var mime = MimeMessageBuilder.Build(compose, account, UserAgent);
        using var ms = new MemoryStream();
        await mime.WriteToAsync(ms, ct);
        var mimeBytes = ms.ToArray();

        using var content = new ByteArrayContent(mimeBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        LogService.Log($"GraphSmtpService: sending {mimeBytes.Length} bytes via /me/sendMail");
        await _client.PostRawAsync(account.Id, "/me/sendMail", content, ct);
        LogService.Log("GraphSmtpService: send complete");
    }
}
```

`SmtpService.SendAsync` dispatches by `BackendKind`:

```csharp
public async Task SendAsync(ComposeModel compose, AccountModel account, string? password, CancellationToken ct = default)
{
    if (account.BackendKind == BackendKind.MicrosoftGraph)
    {
        await _graphSmtp.SendAsync(compose, account, password, ct);
        return;
    }
    // existing MailKit SmtpClient logic
}
```

### 6.12 `GraphChangeNotifier.cs` — Delta Polling

**Path:** `QuickMail/Services/GraphChangeNotifier.cs`

```csharp
public class GraphChangeNotifier : IChangeNotifier
{
    private readonly GraphClient _client;
    private readonly ILocalStoreService _store; // for delta-token persistence
    private readonly IConfigService? _config;
    private readonly Dictionary<Guid, Task> _watchers = new();
    private CancellationTokenSource? _cts;

    public event Action<Guid>? InboxNewMailDetected;

    public GraphChangeNotifier(GraphClient client, ILocalStoreService store, IConfigService? config = null)
    {
        _client = client; _store = store; _config = config;
    }

    public void StartWatchers(IReadOnlyList<AccountModel> accounts, CancellationToken ct)
    {
        StopWatchers();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        foreach (var account in accounts.Where(a => a.BackendKind == BackendKind.MicrosoftGraph))
        {
            _watchers[account.Id] = Task.Run(() => PollLoopAsync(account, _cts.Token), _cts.Token);
        }
    }

    private async Task PollLoopAsync(AccountModel account, CancellationToken ct)
    {
        var intervalSec = _config?.Load().GraphPollSeconds ?? 60;
        intervalSec = Math.Clamp(intervalSec, 30, 600);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // The stored cursor is a full @odata.deltaLink URL captured at the end of
                // the previous tick (null on the very first poll → start a fresh delta
                // enumeration). IMPORTANT: @odata.deltaLink (persisted; drives the NEXT
                // tick) and @odata.nextLink/$skipToken (paging WITHIN this tick) are two
                // different cursors. Only the deltaLink is ever persisted.
                var deltaLink = await _store.GetDeltaTokenAsync(account.Id, "Inbox");
                var url = string.IsNullOrEmpty(deltaLink)
                    ? "/me/mailFolders/Inbox/messages/delta?$select=id"
                    : deltaLink; // request the stored deltaLink URL verbatim

                var sawMessages = false;
                string? nextDeltaLink = null;

                // Drain this tick's pages: follow @odata.nextLink to exhaustion, then keep
                // the final page's @odata.deltaLink as the cursor for the next tick.
                while (!string.IsNullOrEmpty(url))
                {
                    var resp = await _client.GetAsync<GraphDeltaResponse>(account.Id, url, ct);
                    if (resp?.Value?.Length > 0)
                        sawMessages = true;

                    nextDeltaLink = resp?.DeltaLink ?? nextDeltaLink; // set only on the final page
                    url = resp?.NextLink;                             // null on the final page
                }

                if (sawMessages)
                    InboxNewMailDetected?.Invoke(account.Id);

                if (!string.IsNullOrEmpty(nextDeltaLink))
                    await _store.SetDeltaTokenAsync(account.Id, "Inbox", nextDeltaLink);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { LogService.Log($"GraphChangeNotifier {account.AccountLabel}", ex); }

            try { await Task.Delay(TimeSpan.FromSeconds(intervalSec), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void StopWatchers()
    {
        _cts?.Cancel();
        _watchers.Clear();
        _cts = null;
    }
}
```

`GraphDeltaResponse` exposes both cursors so the loop above can tell them apart:
```csharp
public class GraphDeltaResponse
{
    [JsonPropertyName("value")]            public GraphMessage[]? Value { get; set; }
    [JsonPropertyName("@odata.nextLink")]  public string? NextLink { get; set; }  // within-tick paging; transient
    [JsonPropertyName("@odata.deltaLink")] public string? DeltaLink { get; set; } // next-tick cursor; persist this
}
```

The value persisted via `SetDeltaTokenAsync` is the **full `@odata.deltaLink` URL**, requested verbatim on the next tick — there is no `ExtractSkipToken` step. (The `folderId`/`deltaToken` parameter names below are historical; the stored string is a URL, not a bare token.)

`ILocalStoreService` grows two methods:
```csharp
Task<string?> GetDeltaTokenAsync(Guid accountId, string folderId);
Task SetDeltaTokenAsync(Guid accountId, string folderId, string deltaToken);
```

### 6.13 `App.xaml.cs` — DI Wiring

Per-PR changes to the composition root. The final shape (after PR 7):

```csharp
var oauthService = new OAuthService(profile);
var configService = new ConfigService(profile);

// Both backends constructed once:
var imapBackend = new ImapMailService(oauthService, configService);
var graphBackend = new GraphMailService(oauthService, configService);

var mailRouter = new MailServiceRouter(new IMailService[] { imapBackend, graphBackend });

// Register every account to its backend:
foreach (var account in accountService.LoadAccounts())
{
    mailRouter.RegisterAccount(account.Id,
        account.BackendKind == BackendKind.MicrosoftGraph ? graphBackend : imapBackend);
}

// SMTP — single service that delegates internally:
var graphSmtpService = new GraphSmtpService(oauthService);
var smtpService = new SmtpService(oauthService, graphSmtpService);

// Change notifier — composite over both backends:
var imapNotifier = new ImapChangeNotifier(imapBackend);
var graphNotifier = new GraphChangeNotifier(graphBackend.Client, localStore, configService);
var changeNotifier = new ChangeNotifierRouter(new IChangeNotifier[] { imapNotifier, graphNotifier });

// Rest of wiring uses mailRouter wherever IMailService is needed:
var ruleService = new RuleService(mailRouter, localStore, profile.ProfileDir);
var syncService = new SyncService(mailRouter, localStore, configService, ruleService);

var mainVm = new MainViewModel(
    mailRouter, accountService, credentialService, localStore, oauthService,
    syncService, configService, commandRegistry, viewService, ruleService,
    changeNotifier,
    onlineMode: onlineMode);
```

When a new account is added at runtime, the caller (`MainViewModel.AddAccountAsync`) must call `mailRouter.RegisterAccount(account.Id, backendForKind(account.BackendKind))` before the first IMailService method runs against that account.

### 6.14 `IFeatureGate.cs` and `ConfigFeatureGate.cs` — Feature Gate

**Path:** `QuickMail/Services/IFeatureGate.cs`

```csharp
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Runtime feature gate. Code paths and UI surfaces check this before exposing
/// experimental functionality. Defaults are baked into ConfigFeatureGate and
/// overridable via config.ini [features] section or --feature CLI flags.
/// </summary>
public interface IFeatureGate
{
    bool IsEnabled(FeatureFlag flag);
}
```

**Path:** `QuickMail/Models/FeatureFlag.cs`

```csharp
namespace QuickMail.Models;

/// <summary>
/// Feature-gate keys. Adding a new gate is one enum value here plus an entry
/// in ConfigFeatureGate.Defaults.
/// </summary>
public enum FeatureFlag
{
    /// <summary>
    /// Enables Microsoft Graph as a mail-backend option in the Add Account dialog.
    /// Default: false. Flip default to true via a future joint-decision PR.
    /// </summary>
    GraphBackend,
}
```

**Path:** `QuickMail/Services/ConfigFeatureGate.cs`

```csharp
using System;
using System.Collections.Generic;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Resolves feature flags from (in order of precedence, highest first):
///   1. CLI --feature flags passed at startup
///   2. config.ini [features] section
///   3. Built-in defaults
/// </summary>
public class ConfigFeatureGate : IFeatureGate
{
    /// <summary>Built-in defaults. Every flag MUST appear here.</summary>
    private static readonly IReadOnlyDictionary<FeatureFlag, bool> Defaults = new Dictionary<FeatureFlag, bool>
    {
        [FeatureFlag.GraphBackend] = false,
    };

    private readonly IReadOnlyDictionary<string, string> _configFlags;
    private readonly IReadOnlySet<string> _cliFlags;

    public ConfigFeatureGate(ConfigModel config, IEnumerable<string> cliFlags)
    {
        _configFlags = config.Features ?? new Dictionary<string, string>();
        _cliFlags = new HashSet<string>(cliFlags ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    public bool IsEnabled(FeatureFlag flag)
    {
        var name = flag.ToString();

        // 1. CLI override
        if (_cliFlags.Contains(name)) return true;

        // 2. config.ini
        if (_configFlags.TryGetValue(name, out var raw)
            && bool.TryParse(raw, out var configValue))
            return configValue;

        // 3. Default
        return Defaults[flag];
    }
}
```

**Parsing `--feature` in `App.xaml.cs`:**

```csharp
// Multiple --feature flags are supported. Each takes the next arg as its value.
//   QuickMail.exe --feature GraphBackend --feature SomethingElse
static IEnumerable<string> ParseFeatureFlags(string[] args)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals("--feature", StringComparison.OrdinalIgnoreCase))
            yield return args[i + 1];
    }
}

// In OnStartup, after configService is loaded:
var cliFlags = ParseFeatureFlags(e.Args).ToList();
var featureGate = new ConfigFeatureGate(configService.Load(), cliFlags);
```

**`ConfigService.cs` extension:**

The existing INI parser already iterates sections. Add a case for `[features]` that copies all key/value pairs into `ConfigModel.Features`. No new file, ~10 lines added.

**Consumer pattern in `AddAccountViewModel`:**

```csharp
public AddAccountViewModel(IFeatureGate gate, IMailService mailService, IOAuthService oauth) : base(mailService, oauth)
{
    var backends = new List<BackendKindOption>
    {
        new(BackendKind.ImapSmtp, "Standard IMAP/SMTP"),
    };
    if (gate.IsEnabled(FeatureFlag.GraphBackend))
        backends.Add(new(BackendKind.MicrosoftGraph, "Microsoft 365 / Outlook.com"));

    AvailableBackends = backends;
}

public IReadOnlyList<BackendKindOption> AvailableBackends { get; }
```

**`StubFeatureGate` for tests** (added to `QuickMail.Tests/StubServices.cs`):

```csharp
public class StubFeatureGate : IFeatureGate
{
    private readonly Dictionary<FeatureFlag, bool> _flags = new();
    public bool this[FeatureFlag flag] { set => _flags[flag] = value; }
    public bool IsEnabled(FeatureFlag flag) => _flags.TryGetValue(flag, out var v) && v;
}
```

Usage in tests:

```csharp
[Fact]
public void AddAccountViewModel_GateOff_OnlyShowsImap()
{
    var gate = new StubFeatureGate();
    var vm = new AddAccountViewModel(gate, stubImap, stubOauth);
    Assert.Single(vm.AvailableBackends);
    Assert.Equal(BackendKind.ImapSmtp, vm.AvailableBackends[0].Kind);
}

[Fact]
public void AddAccountViewModel_GateOn_ShowsGraphOption()
{
    var gate = new StubFeatureGate { [FeatureFlag.GraphBackend] = true };
    var vm = new AddAccountViewModel(gate, stubImap, stubOauth);
    Assert.Equal(2, vm.AvailableBackends.Count);
    Assert.Contains(vm.AvailableBackends, b => b.Kind == BackendKind.MicrosoftGraph);
}
```

---

## 7. Tests

### 7.1 Per-PR test additions

| PR | Tests added |
|---|---|
| 1 | None (rename only); existing tests prove no regression. |
| 2 | `LocalStoreServiceMigrationTests.cs`: seed v1 schema with 1000 rows; run init; assert 1000 rows survive with text IDs matching original UIDs. |
| 3 | `FeatureGateTests.cs` (CLI overrides config; config overrides default; defaults are honored when neither overrides); `MailServiceRouterTests.cs` (router dispatch — fake backends); `AddAccountViewModelTests.cs` (BackendKind change clears IMAP fields; **gate state controls which backends appear in `AvailableBackends`**). |
| 4 | `GraphMailServiceTests.cs` for read endpoints; `OAuthServiceScopesTests.cs` (per-call scope selection). |
| 5 | Extend `GraphMailServiceTests` with send tests. |
| 6 | Extend `GraphMailServiceTests` with mutation endpoint tests. |
| 7 | `GraphChangeNotifierTests.cs` (delta token persistence; new-mail event raising). |
| 8 | Coverage round-up: any missing integration test; verify CI runs both gate-on and gate-off variants of `AddAccountViewModelTests`. |

### 7.2 GraphMailServiceTests pattern

Use `HttpMessageHandler` stub to inject canned Graph responses:

```csharp
public class GraphMailServiceTests
{
    private readonly HttpMessageHandler _handler;
    private readonly GraphMailService _service;

    public GraphMailServiceTests()
    {
        _handler = new StubHandler();
        var oauth = new StubOAuthService("fake-token");
        _service = new GraphMailService(oauth, config: null);
        // Inject _handler into GraphClient via test seam
    }

    [Fact]
    public async Task GetMessageSummariesAsync_ParsesGraphResponse()
    {
        StubHandler.WhenGet("/me/mailFolders/Inbox/messages")
            .ReturnsJson(new { value = new[] { new { id = "ABC=", subject = "Hi", /* ... */ } } });

        var summaries = await _service.GetMessageSummariesAsync(Guid.NewGuid(), "Inbox", 50);

        Assert.Single(summaries);
        Assert.Equal("ABC=", summaries[0].MessageId);
        Assert.Equal("Hi", summaries[0].Subject);
    }

    [Fact]
    public async Task GetAllPagesAsync_FollowsODataNextLink()
    {
        // First page returns 100 items + @odata.nextLink
        // Second page returns 50 items, no nextLink
        // Assert: total 150 items returned
    }

    [Fact]
    public async Task GetAsync_Respects429RetryAfter()
    {
        // First response: 429 with Retry-After: 1
        // Second response: 200 with the data
        // Assert: total ~1s wall time, returns data
    }
}
```

### 7.3 Integration test (manual, per PR)

The retro from the mail-rules feature called for explicit manual checklists. See §8.

---

## 8. Per-PR Manual Smoke Checklist

After every PR lands, before merge, the contributor runs this checklist on the dev branch.

### After PR 1 (rename)

- [ ] Build succeeds: `dotnet build QuickMail.sln -nologo`
- [ ] All tests pass: `dotnet test QuickMail.Tests/QuickMail.Tests.csproj -c Release`
- [ ] App launches; existing IMAP account connects, folders load, messages display
- [ ] Send a test message; verify it arrives in another inbox

### After PR 2 (UID → string)

- [ ] Backup mail.db.pre-v2 is created on first launch (verify file exists)
- [ ] App launches; existing IMAP account: folders load, messages display, no missing messages
- [ ] Pre-PR Sent folder still contains all previous Sent messages (verifies migration)
- [ ] Mark message read/unread persists across restart
- [ ] Delete a message; verify it goes to Trash and is no longer in Inbox
- [ ] Reply to a message; verify it sends and appears in Sent
- [ ] Stop and re-launch; verify Sent message persisted via SQLite

### After PR 3 (router + UI + feature gate)

**Gate OFF (default — no config.ini override, no CLI flag):**
- [ ] Add Account dialog: "Microsoft 365 / Outlook.com" option is **not** present in the account-type combo (or the combo is rendered as a static label showing only "Standard IMAP/SMTP")
- [ ] Existing IMAP account still connects and works identically
- [ ] No visible difference from pre-PR-3 behavior for end users
- [ ] `--feature SomeOtherFlag` (a flag that doesn't exist) doesn't crash; logs a warning

**Gate ON (set `[features]\nGraphBackend=true` in config.ini, OR pass `--feature GraphBackend`):**
- [ ] Account-type combo appears with two options, accessible by Tab
- [ ] Selecting "Microsoft 365" disables IMAP/SMTP fields (visually + tab-stops)
- [ ] Selecting "Standard IMAP/SMTP" re-enables them
- [ ] Screen reader announces field-set changes (manually verified with one screen reader)
- [ ] Attempting to save a "Microsoft 365" account produces the documented "Graph backend not yet implemented" message (until PR 4 lands)
- [ ] IMAP account creation works identically to gate-off mode

**Both modes:**
- [ ] Build succeeds: `dotnet build QuickMail.sln -nologo`
- [ ] All tests pass: `dotnet test QuickMail.Tests/QuickMail.Tests.csproj -c Release`
- [ ] `FeatureGateTests` verifies CLI > config > default precedence

### After PR 4 (Graph read)

- [ ] **Maintainer has added Graph scopes to MSAL app registration in Azure portal**
- [ ] Add Account → Microsoft 365: OAuth flow opens, completes, account is saved
- [ ] Folder tree populates from Graph
- [ ] Inbox messages display with correct subject, sender, date, preview
- [ ] Open a message: body renders (HTML if present, plain text otherwise)
- [ ] Mark message read; verify it persists on next refresh
- [ ] IMAP account still works (no regression)

### After PR 5 (Graph send)

- [ ] Compose new message from M365 account; send to a Gmail address; verify arrival
- [ ] Reply to an M365 message; verify it sends and appears in Sent (auto-saved by Graph)
- [ ] Forward an M365 message with an attachment; verify the attachment is included

### After PR 6 (Graph mutations)

- [ ] Move a message to another folder; verify it appears in the destination
- [ ] Copy a message; verify a copy exists in both folders
- [ ] Delete a message; verify it appears in Deleted Items
- [ ] Empty trash; verify Deleted Items is empty
- [ ] Download an attachment; verify the file opens correctly
- [ ] Save a draft; verify it appears in Drafts; edit it; verify changes persist
- [ ] Create, rename, delete a folder; verify each in the folder tree

### After PR 7 (notifications)

- [ ] M365 account: send a message to it from another mailbox; verify QuickMail notices within 60s
- [ ] IMAP account: same test using IDLE; verify no regression

### After PR 8 (tests)

- [ ] `dotnet test` reports >90% pass on Graph-related tests
- [ ] Integration test recipe documented in `CLAUDE.md`

---

## 9. Edge Cases & Error Handling

| Scenario | Handling |
|---|---|
| Tenant denies Graph scopes during sign-in | MSAL throws `MsalServiceException` with `AADSTS65001` or similar; surface user-facing message: "Your organization's Microsoft 365 settings do not permit this app to read mail. Contact your administrator." |
| Conditional access requires a managed device | MSAL throws with `AADSTS50158`; user-facing message about device compliance |
| Token expired between calls | MSAL silent refresh transparent to caller |
| Network drops mid-Graph-request | `HttpClient` throws `HttpRequestException`; backend retries once, then surfaces to UI via existing exception path |
| Graph returns 429 with `Retry-After: N` | `GraphClient` waits N seconds, retries once; if still 429, surfaces error |
| Graph returns 5xx | Retry with exponential backoff up to 3 times; surface on persistent failure |
| `accounts.json` from old version has no `backendKind` | System.Text.Json defaults to `ImapSmtp` — backward compatible |
| User adds Graph account but tenant has no Exchange Online mailbox | `/me` returns 200 but `GET /mailFolders` returns 404; surface: "No Exchange mailbox is associated with this account." |
| Schema migration fails partway | Wrapped in transaction; rollback leaves v1 intact; backup file is the safety net |
| Stored deltaLink expires (rare; Graph occasionally invalidates) | requesting the stored `@odata.deltaLink` returns 410 Gone; clear the stored cursor and re-poll from a fresh `…/messages/delta` enumeration (full resync of changes) |
| Long-running attachment download cancelled by user | Existing `CancellationToken` path applies; cancel propagates to `HttpClient` |

---

## 10. Accessibility Checklist

Performed manually after PR 3 lands and again after PR 4 lands.

### Add Account dialog (new combo)

- [ ] `AutomationProperties.Name` on combo: "Account type"
- [ ] Combo announces selected option when changed
- [ ] Field-set change announcement plays via `AccessibilityHelper.Announce(category: Hint)`
- [ ] Hidden IMAP/SMTP fields are removed from tab order when M365 selected
- [ ] Focus moves to the next visible field, not lost
- [ ] No layout shift causes focus reset on field hide
- [ ] Contrast on combo dropdown ≥ 4.5:1

### Account list (no change)

- [ ] Existing account list unchanged; no regression test required beyond visual inspection

### Connect / sign-in flow

- [ ] "Opening Microsoft sign-in in your browser" announced (Status)
- [ ] On success: "Signed in as {Username}. Microsoft 365 account ready." announced (Result)
- [ ] On tenant denial: user-facing message announced (Result), not silent

### Error states

- [ ] Throttling notice in status bar uses appropriate category (Status, silenced by default)
- [ ] Permanent errors (tenant denial, mailbox missing) use Result category and are not silenced

---

## 11. Build Verification

### Per-PR build

```bat
build.bat clean
build.bat
dotnet test QuickMail.Tests/QuickMail.Tests.csproj -c Release
build.bat smoke
```

### Pre-merge publish check

```bat
build.bat publish
```

Verify `publish/QuickMail.exe` runs end-to-end against a real M365 account (after PR 4+).

### CI

The existing `.github/workflows/quickmail.yml` GitHub Actions workflow handles per-PR build and artifact upload. No CI changes are required for this work — Graph integration tests are gated to `[Trait("Category", "RequiresTenant")]` and skipped by default.

---

## Appendix: Open code decisions (deferred to implementation)

These are choices that don't change the spec but need to be made by the implementer:

1. **Graph client retry policy:** how many retries on 5xx? **Recommendation: 3, exponential backoff starting at 1s.**
2. **Batch via `$batch`?** For mutations that touch >5 messages, Graph supports a JSON-batch endpoint. **Recommendation: defer to PR 6; ship single-call first, batch as a follow-up if perf matters.**
3. **Body content-type negotiation:** Graph can return body as HTML or text via `Prefer: outlook.body-content-type="text"` header. **Recommendation: request HTML (current default); fall back to text only if body is empty.**
4. **Folder display-name vs Graph well-known names:** Graph supports `/me/mailFolders/Inbox` (well-known), `/me/mailFolders/{id}` (specific ID). **Recommendation: use the ID for everything except the very first connection (use well-known to find Inbox/Drafts/SentItems/DeletedItems).**
5. **Delta token storage:** in `mail.db` (proposed) or separate file? **Recommendation: in mail.db for transactional consistency with summaries.**
