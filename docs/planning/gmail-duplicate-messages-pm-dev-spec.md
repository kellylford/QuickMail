# Gmail Duplicate Messages — PM/Dev Spec

**Issue:** [#220](https://github.com/kellylford/QuickMail/issues/220) — "Duplicate messages are being displayed in the list."
**Reported by:** @diamondStar35 (Gmail account, Google OAuth).
**Status:** Draft for implementation.

---

## 1. Problem

After sync, the default **All Mail** view shows the same message repeated many times. The copies
are not conversations and not related messages — they are the *same* message appearing repeatedly.

### Root cause (verified in code)

A message's identity in QuickMail is its **per-folder IMAP UID**
(`ImapMailService.SummaryToModel`, `MailMessageSummary.MessageId = s.UniqueId`). The RFC 5322
`Message-ID` is fetched in the envelope but **discarded** — it is never stored on the summary.

Gmail exposes **every label as an IMAP folder**, and the *same physical message* appears in many of
them at once — `INBOX`, `[Gmail]/All Mail`, `[Gmail]/Important`, `[Gmail]/Starred`, and every user
label — each with a **different UID**. `[Gmail]/All Mail` alone is a superset containing a copy of
essentially every message.

The pipeline then:

1. **Syncs every non-excluded folder** (`SyncService.SyncPassAsync`). "Excluded" only covers
   Trash/Junk/Sent/Drafts (`ImapMailService.IsExcludedFromAllMail`). Gmail's `\All`, `\Important`,
   `\Flagged` special folders are **not recognized** (`GetSpecialFolderKind` never checks
   `FolderAttributes.All/Important/Flagged`) and are synced as ordinary folders.
2. **Stores one row per (UID, account, folder)** (`MessageSummary` PK), so each Gmail copy is a
   distinct row.
3. **Unions every stored row into the All Mail view with no dedup** —
   `LocalStoreService.LoadAllSummariesAsync` is a plain `SELECT … FROM MessageSummary ORDER BY
   date_ticks DESC`. The in-memory pool `_rawMessages` and the incremental `OnFolderSynced` path
   also dedup only on the **per-folder key** `(MessageId, AccountId, FolderName)`, which never
   collapses two folders' copies of one message.

Result: one inbox message = INBOX + All Mail + Important + (Starred) + each label ⇒ "a lot of
times." Because identity is the per-folder UID and the `Message-ID` is thrown away, **the code
currently has no way to even know two rows are the same message.**

This reproduces for essentially **every Gmail user**, on both Google OAuth and app-password IMAP
(it is an IMAP-layer issue, independent of auth). It is a single-real-folder-clean, aggregate-only
defect: opening `INBOX` alone is fine; the duplicates appear in cross-folder/virtual views.

---

## 2. Goals

1. Each unique message appears **once** in every aggregate/virtual view (All Mail, per-account All
   Mail, All Flagged, saved views spanning folders), and in the conversation and sender-group trees
   built from those views.
2. **Nothing is lost.** Archived Gmail mail (which lives only in `[Gmail]/All Mail`) still appears
   exactly once. No message silently disappears.
3. **Provider-agnostic.** The fix is correct for any IMAP server that exposes a message in multiple
   folders, not a Gmail-only special case. Non-Gmail providers see no behavior change.
4. **Acting on a deduped message is unambiguous** — open/read/flag/delete target a single,
   predictable representative copy.

## 3. Non-goals (this pass)

- Reworking Gmail into a labels-as-tags model (sync All Mail only, present labels as virtual
  folders). This is the "most native" Gmail design and eliminates storage duplication entirely, but
  it is a large, Gmail-specific rewrite of the sync/folder model. **Deferred** — see §9.
- Using `X-GM-MSGID` (`X-GM-EXT-1`) as the identity. `Message-ID` is provider-agnostic, already
  fetched, and sufficient. `X-GM-MSGID` is noted as optional future hardening (§9).
- Reducing storage: we continue to store one row per folder copy (single-folder views need it). We
  dedup at the **view** layer, not the storage layer.
- Sent-mail leakage: `[Gmail]/All Mail` contains Sent copies, so some sent mail may surface in the
  All Mail aggregate. This is pre-existing and only fully fixed by the labels-as-tags model. **Out
  of scope** — see §9.

---

## 4. Design

### 4.1 Global message identity

Add a stored, normalized `Message-ID` to the summary so the same message is recognizable across
folders.

- **Model:** add `string InternetMessageId` to `MailMessageSummary` (mirrors the existing field on
  `MailMessageDetail`).
- **Capture:** in `ImapMailService.SummaryToModel`, set it from `s.Envelope?.MessageId`
  (already fetched — just stop discarding it). In `GraphMailService`, set it from the DTO's
  `internetMessageId` (already available).
- **Normalization** (`MessageIdentity.Normalize`): trim, strip a single pair of surrounding angle
  brackets, lowercase with `InvariantCulture`. Empty/whitespace ⇒ empty (treated as "no identity").

### 4.2 Collapse key and representative selection

Define one helper, `MessageDeduplicator` (new, in `Services/`), used at every aggregate choke point.

- **Collapse key:** `(AccountId, NormalizedInternetMessageId)`.
  - Never collapse across accounts (the same message to two of your accounts is two real items).
  - **Never collapse empty identities** — a message with no `Message-ID` falls back to its
    per-folder key `(AccountId, FolderName, MessageId)` and is always kept as distinct. This is the
    critical safety rule: collapsing on empty would wrongly merge unrelated messages.
- **Representative selection** (when multiple copies share a key): choose the copy whose source
  folder has the **best "home" priority**, so the user opens/acts on the most intuitive copy and
  read/flag state comes from a real mailbox:

  `Inbox (0) → ordinary user folder / label (1) → Sent/Drafts (2) → Gmail All Mail / Important /
  Starred / Junk / Trash (3)`.

  Ties broken by newest `Date`, then by `FolderName` ordinal for determinism. Requires knowing a
  folder's kind — see §4.4.

### 4.3 Choke points (where dedup is applied)

`_rawMessages` (in `MainViewModel`) is the single source pool: the flat list (`Messages` via
`ApplyFiltersAndSearch`), the conversation tree (`ConversationBuilder.Build`), and the sender-group
trees (`SenderGroupBuilder.Build/BuildByTo`) all derive from it. Deduping the aggregate's
`_rawMessages` fixes all three at once. Concretely:

1. **Batch aggregate loads** call `MessageDeduplicator.CollapseForAggregate(list)` before
   `SetMessages`:
   - `FetchAllMailAsync` (Phase 1 cache load *and* Phase 2 IMAP results, incl. recipient-repair
     branch)
   - `FetchAccountAllMailAsync` (per-account All Mail)
   - `FetchVirtualFolderAsync` (All Inboxes/Drafts/Sent/Trash — harmless, mostly single-kind)
   - saved-view virtual folders that combine multiple folders (`newMessages.AddRange` path)
2. **Incremental adds** — `OnFolderSynced`: when `SelectedFolder` is an aggregate/virtual view, use
   the **global collapse key** for the `seen`/`_rawMessages` dedup instead of the per-folder key, so
   an incoming All Mail copy of an already-visible Inbox message is recognized as a duplicate and
   skipped. Single-real-folder views keep the per-folder key (no behavior change). Because Pass 1
   syncs Inbox before other folders, the Inbox copy is normally the representative already; a
   later-arriving higher-priority copy replacing a lower one is a **v1.1 refinement** (noted), not
   required for correctness.
3. **Single-real-folder views** (`FetchFolderAsync` / `LoadFolderSummariesAsync`): **no dedup** — a
   real folder shows its own contents as-is.

`CollapseForAggregate` is idempotent and O(n) (hash on the collapse key), safe to run on the
thousands-of-messages All Mail pool.

### 4.4 Gmail special-folder recognition

Extend `SpecialFolderKind` and classification so representative selection can deprioritize Gmail's
virtual folders, and so future sync-trimming (§9) has the data it needs.

- Add `SpecialFolderKind` values: `AllMail`, `Important`, `Starred`.
- `ImapMailService.GetSpecialFolderKind`: map `FolderAttributes.All → AllMail`,
  `FolderAttributes.Important → Important`, `FolderAttributes.Flagged → Starred` (checked after the
  existing Trash/Junk/Sent/Drafts cases).
- **Do not** add these to `IsExcludedFromAllMail` — excluding `[Gmail]/All Mail` from sync would
  drop archived mail (Goal 2). They stay synced; dedup handles the duplication. They are only
  *deprioritized* as representatives.

### 4.5 Persistence and migration

- **Schema:** add `internet_message_id TEXT NOT NULL DEFAULT ''` to `MessageSummary` via the
  existing `RunMigration(ALTER TABLE …)` pattern; include it in the `CREATE TABLE` for fresh DBs,
  in `UpsertSummariesAsync` (insert + `ON CONFLICT DO UPDATE`), and in every
  `SELECT`/`ReadSummariesAsync` projection.
- **Index:** `CREATE INDEX idx_summary_msgid ON MessageSummary(account_id, internet_message_id);`
- **Backfill:** existing rows have no stored `Message-ID` and it cannot be reconstructed from cached
  data. Bump `CurrentSchemaVersion` to 5 and, in `RunDataMigrations` `if (version < 5)`, **clear
  `MessageSummary`** so the next sync repopulates it with identities. The cache rebuilds
  automatically on the next launch's sync; offline users briefly see fewer cached messages until the
  first sync completes (acceptable, small user base). `MessageDetail` is untouched (keyed the same;
  bodies remain cached).
- Dedup tolerates legacy empty-identity rows safely (they never collapse), so even if a user runs a
  build before re-sync completes, they see correct-or-legacy behavior, never wrong merges.

---

## 5. Keyboard walkthrough

The change is mostly invisible — the same view, with duplicates removed. No new controls, no focus
changes. Explicitly:

1. User launches the app. Screen reader announces the cached All Mail count (now the **deduped**
   count). Previously: inflated by duplicates.
2. User arrows down the message list. Each unique message is encountered **once**. Focus order and
   announcements are unchanged except that repeats are gone.
3. User presses Enter on a message. It opens from its **representative folder** (Inbox copy
   preferred). Read state is set on that copy; Gmail propagates `\Seen` across the message's other
   folders server-side, so the message reads as read everywhere on the next sync.
4. User presses Delete on a deduped message. The delete targets the representative copy's folder/UID
   (existing delete path, unchanged). Focus returns to the next message in the list (existing
   behavior).
5. User switches to Conversations or By Sender/By Recipient view. Groups are built from the deduped
   pool — a message contributes to its conversation/sender group **once**, not N times.
6. User opens a single real folder (e.g., `INBOX` or a specific label) from the folder tree. Its
   contents display **as-is, un-deduped** — this is that folder's actual mailbox.

## 6. Infrastructure changes

- **F6 ring:** no change (no new panes).
- **Commands:** none added/removed.
- **`AutomationProperties.Name`:** none changed.
- **`AccessibilityHelper.Announce`:** none added. Existing status/count announcements now report
  deduped counts (no category change).
- **VM state:** no new observable properties. `_rawMessages` semantics change (holds the deduped set
  for aggregate views); `OnFolderSynced` dedup key becomes view-dependent.
- **Data model:** `MailMessageSummary.InternetMessageId` added; `SpecialFolderKind` gains `AllMail`,
  `Important`, `Starred`.
- **Storage:** `MessageSummary.internet_message_id` column + index; schema v5 migration clears
  `MessageSummary` for backfill.
- **New type:** `Services/MessageDeduplicator.cs` (+ a small `MessageIdentity.Normalize` helper).
- **Services touched:** `ImapMailService` (capture Message-ID, Gmail folder kinds),
  `GraphMailService` (capture InternetMessageId), `LocalStoreService` (schema/upsert/select/
  migration), `MainViewModel` (choke points, `OnFolderSynced`).

## 7. Out of scope

- Labels-as-tags Gmail model; `X-GM-MSGID` identity; storage-level dedup / sync trimming; sent-mail
  leakage from `[Gmail]/All Mail`. All in §9.
- Any Outlook/Graph behavior change (Graph message ids are already unique per message; the helper is
  a harmless no-op there once `InternetMessageId` is populated).

---

## 8. Testing

- **`MessageDeduplicatorTests`** (new):
  - Two folders' copies of one message (same normalized `Message-ID`, same account) collapse to one;
    representative is the Inbox copy.
  - Representative priority: Inbox > label > All Mail/Important/Starred; tie-break by Date.
  - **Empty `Message-ID` rows never merge** (two distinct empty-id messages stay two).
  - Different accounts with the same `Message-ID` do **not** collapse.
  - Normalization: angle brackets / case / whitespace variants of one id collapse.
  - Idempotent: running twice equals running once.
- **`ConversationBuilderTests` / `SenderGroupBuilderTests`:** add a Gmail-duplicate fixture; assert a
  message counts once per conversation/sender after dedup.
- **`LocalStoreServiceTests`:** round-trip the new column; assert v5 migration adds the column and
  clears summaries; assert `internet_message_id` survives upsert `ON CONFLICT`.
- **Non-Gmail regression:** a single-folder load is unchanged; distinct messages with distinct ids
  are all retained.

## 9. Proactive notes / follow-ups (things worth knowing)

1. **Google verification ceiling (the "expensive verification" you recalled).** Gmail's
   `https://mail.google.com/` is a **restricted scope**. Going to *verified production* requires an
   annual third-party **CASA security assessment** (~$15k–$75k/yr). Until then the OAuth app stays
   in **testing** status: capped at **100 users**, each of whom must be added as a **test user** and
   will see Google's "unverified app" warning. **Implication:** for a small user base you are fine in
   testing mode, but Google OAuth **cannot scale** past ~100 users without the paid assessment, so
   **app-password IMAP remains the scalable Gmail path**. This dedup fix helps **both** paths equally
   (it is IMAP-layer), so it is the right investment regardless of the OAuth ceiling. Track the
   assessment decision separately if the user count ever approaches the cap.
2. **The reported issue (#220) is still authored under the maintainer's account** — same token
   attribution problem tracked in #222; the human reporter is @diamondStar35.
3. **Labels-as-tags (future).** The fully-native Gmail model: sync `[Gmail]/All Mail` only, read
   `X-GM-LABELS` per message, present labels/inbox as virtual folders/filters. Eliminates storage
   duplication and the sent-in-All-Mail leak by construction, and gives correct Gmail threading via
   `X-GM-THRID`. Larger change; revisit if Gmail becomes the dominant backend.
4. **Storage duplication remains** after this fix (we still store one row per folder copy). If cache
   size becomes a concern, sync-trimming Gmail's `\Important`/`\Starred` (pure duplicate views that
   are never a message's sole home) is a safe, small optimization on top of this work.

### Known limitations (accepted, from independent review)

- **Reused Message-ID collapses distinct messages.** Dedup keys on `(account, normalized
  Message-ID)`. If a misbehaving sender reuses one Message-ID for two genuinely different messages,
  they collapse to one row in aggregate views (the hidden one still exists in its real folder — no
  data loss). Accepted: RFC 5322 requires unique Message-IDs and Gmail's own X-GM-MSGID dedup is
  equivalent, so this mirrors the server; keying additionally on Date/Subject was rejected because
  those can differ between copies of the *same* message and would reintroduce duplicates. Documented
  on `MessageDeduplicator.CollapseKeyFor`.
- **First cached load uses neutral representative ranking.** `InitialLoadAsync` runs `SetMessages`
  before `_cachedFolders` is populated, so the Inbox-preferred representative ranking is neutral for
  that one render (collapse is still correct); it settles on the first real fetch. Commented at the
  `SetMessages` dedup call.
- Both `RemoveVanishedMessages` and all incremental merges are now Message-ID-aware, so archiving a
  Gmail message out of INBOX no longer transiently drops its representative from the aggregate.
