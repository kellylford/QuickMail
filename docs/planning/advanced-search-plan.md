# Advanced Search — Plan

Status: decided 2026-09-16 (see [Decisions](#decisions)); phases 1–3 built in [#722](https://github.com/kellylford/QuickMail/pull/722), [#723](https://github.com/kellylford/QuickMail/pull/723) and [#724](https://github.com/kellylford/QuickMail/pull/724), for 0.8.46. Phase 4 is not scheduled.
Tracking issue: [#717](https://github.com/kellylford/QuickMail/issues/717).

## Where search is today

- `Ctrl+Shift+S` or `/` opens the search box. It filters the messages **already loaded in the list**, as you type (`MainViewModel.MatchesSearch`).
- It checks four fields with a plain "contains": From, To, Subject, and the stored preview (about the first 100 characters of the body).
- Not searched: the full body, Cc, attachment names, dates, flags, or anything outside the current folder or virtual folder.
- No query syntax, no full-text index in `mail.db`, and no server search (IMAP SEARCH, Gmail, Graph `$search` are all unused).
- The filters (Unread, Flagged, With attachments, …) and "find mail from/to a contact" (#370) are separate and do not combine with the search text.
- The user guide says "press Enter"; the box actually filters as you type. Fix with the first phase.

## The engine: three options

### A. Our own full-text index in `mail.db` (chosen)

SQLite's FTS5 full-text index, in the database QuickMail already has.

- One index row per message: subject, from, to, cc, body text (plain body, or text extracted from the HTML body), attachment file names.
- Kept current where the store already writes: summary upsert, detail upsert, delete, move, and the account/folder purges.
- Fast enough to stay search-as-you-type across every account at once. Supports words, phrases, prefix (`budg*`), AND/OR/NOT, and accent-insensitive matching.
- Dates, read state, flags, folder and account stay ordinary SQL conditions next to the text match.
- Works offline, stays on this computer, nothing to register with Windows, same on x64 and ARM64, and testable with the real store as the offline-bodies tests already are.
- Limit: it can only find what is cached. A body is cached once opened, prefetched, or inside **Download messages for offline reading**. Mail outside the **Sync range** is not in the cache at all.
- To verify first: that the bundled `e_sqlite3` build has FTS5 compiled in.
- Upgrade cost: existing caches are indexed once in the background after the update.

### B. Ask the server (chosen, on request)

For mail the cache does not have.

- **IMAP:** `SEARCH` with FROM, TO, CC, SUBJECT, BODY, TEXT, SINCE/BEFORE, flags (MailKit `SearchQuery`). One folder per request, so "all folders" is many round trips. Body search speed and quality vary a lot by server.
- **Gmail:** `X-GM-RAW` (MailKit `SearchQuery.GMailRawSearch`) runs Gmail's own search over All Mail in one request.
- **Microsoft 365 / Outlook.com (Graph):** `$search` with `from:`, `subject:`, `body:`, `hasAttachments:`. Has limits on combining with filters and on sorting and paging, to be confirmed while building.
- **POP3:** none; its mail is all local anyway, so option A covers it completely.

Server search is an explicit "Search the server too" action, never on every keystroke.

### C. Windows Search

**Not a goal** (decision 4). Kept for the record of why. Three ways it could be involved; none is a good fit as the main engine.

1. **Protocol handler** — how Outlook puts mail into Windows Search. A native COM component that the Windows indexer loads in its own process to crawl QuickMail's store.
   - Mail would show up in Start and File Explorer search, and QuickMail could query the Windows index.
   - Costs: native C++ (Microsoft does not support managed code in the indexer host), separate x64 and ARM64 builds, and machine-wide registration that needs administrator rights, which the per-user Velopack install does not have. Opening a result would also need a `quickmail:` link handler.
   - The biggest job of all the options, and it duplicates option A inside the app.
2. **Write each message as a `.eml` file and have Windows index that folder.** Windows already understands `.eml` (sender, subject, body).
   - Easy to build, but doubles disk use and leaves every message readable as a loose file outside the app.
   - Adding a folder to the indexed locations changes a system setting, results point at files rather than messages, and indexing lags behind sync.
3. **Use only Windows' document filters (IFilter), not the indexer,** to pull text out of attachments (PDF, Word, …) into option A's index.
   - Keeps everything inside QuickMail, and gets attachment content search.
   - Limited to file types that have a filter installed; attachments are fetched on open today, so only downloaded ones would be searchable.

Decided: A first, B for what the cache lacks, C3 at most as a later add-on. C1 and C2 are not being pursued.

## Phases

### Phase 1 — full-message search in the existing box

- Build the FTS5 index (option A) with background indexing of existing caches.
- The search box stays scoped to **the folder you are in** — a real folder or a virtual one such as All Mail or All Inboxes (decision 1). No scope control beside the box; choosing accounts is Advanced Search's job.
- Within that folder it searches the whole cached message, bodies included, **as you type** (decision 2). If that proves slow on large caches, scale back (a longer pause before searching, or body search on Enter) rather than dropping body search.
- In `--online` mode, with no cache, the box keeps matching what the list holds (sender, recipients, subject, preview), and the typed fields that can be answered from that.
- Typed field syntax, all optional:
  `from:` `to:` `cc:` `subject:` `body:` `attachment:` `has:attachment` `is:unread` `is:read` `is:flagged` `after:` `before:` `folder:` `account:` and quoted phrases.
- Correct the user guide's "press Enter".

### Phase 2 — Advanced Search window

- Opened with **Ctrl+/** (decision 5), registered in `CommandRegistry` so it can be reassigned like any other shortcut, and listed in the Mail menu and the command palette.
- A modeless window (it has text fields and opens over the reading pane, so `Show()` not `ShowDialog()`, per the modal dialog rules).
- Fields: Words anywhere; From; To or Cc; Subject; Body; Attachment name; Has attachments; Read state; Flagged; Received after and before.
- Where to look: **the current folder**, or **all folders of the accounts you choose** — every account by default, narrowed with one check box per account. This is where searching across accounts lives (decision 1).
- **Search** builds the same typed query the box understands, so there is one parser. Results go into a **Search Results** virtual folder, built the way #370's contact-mail folder is, so every view (list, conversations, from/to groups), every open mode, and every message command keeps working on them. A results bar shows the query; **Ctrl+/** from there reopens the window with it filled in.

### Phase 3 — beyond the cache, saved searches, and offline bodies for every folder

- **Search the server too** (option B), results merged into Search Results and de-duplicated against cached ones.
- Save a search as a saved view (`SavedView` gains a query), so it appears in the Views menu with an optional hotkey — a live search folder.
- Offline bodies for folders other than Inbox (decision 3), so full-message search covers Sent and archive folders without opening each message first.

### Phase 4 — optional, not scheduled

- Attachment content via IFilter (C3).

## Keyboard walkthrough

Custom announcements are listed with their category. What the platform itself reports for each control is to be confirmed by listening, not assumed.

1. In the message list, the user presses `/`. Focus moves to the search box.
2. The user types `from:sam budget`. After a short pause the list shows the matches in this folder, including messages where "budget" is only in the body. Announcement (Result): "12 messages."
3. The user presses Down. Focus moves to the first result.
4. The user presses Enter. The message opens in the configured mode. Closing it returns focus to the same result.
5. The user presses Escape in the search box. The search clears and the folder returns. Announcement (Result): the folder's message count, as today.
6. The user presses **Ctrl+/** (or runs **Advanced Search** from the Mail menu or command palette). The window opens with focus in Words anywhere.
7. The user tabs through the fields, fills From and Received after, leaves every account checked, and presses Enter. The window closes; the Search Results folder opens with the results bar showing `from:… after:…`; focus goes to the first result. Announcement (Result): "N messages."
8. With no results: the window stays open with focus in Words anywhere. Announcement (Result): "No messages found."
9. Phase 3: with results showing, the user runs **Search the server too**. Announcement (Status): "Searching the server…"; then (Result): "N more messages from the server."
10. In the Advanced Search window, F6 cycles fields → accounts → buttons; Escape closes it and returns focus to where it was opened from; `Ctrl+Shift+P` opens its command palette.

## Infrastructure changes

- **F6 ring:** unchanged in the main window (the search box is already reachable). The Advanced Search window gets its own F6 cycle.
- **CommandRegistry (category Mail):**
  - `mail.advancedSearch` — Advanced Search. Default **Ctrl+/** (`GestureHelper` gains a `/` key name so the shortcut displays and can be reassigned).
  - `mail.searchServer` — Search the Server Too. No default key.
  - `mail.saveSearch` — Save Search as View. No default key.
  - `view.search` (`Ctrl+Shift+S`) stays as is.
- **AutomationProperties.Name:** "Search messages" stays; new: one short label per Advanced Search field, and one per account check box (the account name).
- **AccessibilityHelper.Announce:** result counts and no-results (Result); server search progress (Status). The existing search-count announcement is reused, not duplicated.
- **Store:** new FTS5 table in `mail.db`, maintained in `LocalStoreService`; a one-time background index build after upgrade.
- **Mail services:** new `SearchAsync` on `IMailService`, implemented for IMAP, Gmail (X-GM-RAW), and Graph; POP3 returns nothing.
- **VM state:** a parsed query model behind `SearchText`; a Search Results virtual folder sentinel holding the query and chosen accounts.
- **Docs:** user guide Searching section rewritten; keyboard shortcuts doc updated; `docs/privacy.html` needs no change (no new host).

## Out of scope

- Windows Start-menu or File Explorer search of mail (option C1 or C2) — decided against.
- A scope control on the search box — searching across accounts is Advanced Search's.
- Searching attachment contents (Phase 4, unscheduled).
- Natural-language or "smart" search, and ranking by relevance — results are newest first.
- Searching calendar events or contacts from the mail search box (the calendar has its own search).
- Search history and suggestions.

## Decisions

Made 2026-09-16.

1. **Scope of the search box:** this folder, which already covers virtual folders. Searching across accounts belongs to Advanced Search, where the user chooses the accounts.
2. **As you type**, searching full messages. Scale back only if it proves slow.
3. **Offline bodies beyond Inbox:** Phase 3.
4. **Windows Search:** not a goal.
5. **Advanced Search key:** Ctrl+/, customizable like every registered command.
