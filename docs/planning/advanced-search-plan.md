# Advanced Search — Plan

Status: proposal, not yet a spec. Decisions needed are listed at the end.
Tracking issue: [#717](https://github.com/kellylford/QuickMail/issues/717).

## Where search is today

- `Ctrl+Shift+S` or `/` opens the search box. It filters the messages **already loaded in the list**, as you type (`MainViewModel.MatchesSearch`).
- It checks four fields with a plain "contains": From, To, Subject, and the stored preview (about the first 100 characters of the body).
- Not searched: the full body, Cc, attachment names, dates, flags, or anything outside the current folder or virtual folder.
- No query syntax, no full-text index in `mail.db`, and no server search (IMAP SEARCH, Gmail, Graph `$search` are all unused).
- The filters (Unread, Flagged, With attachments, …) and "find mail from/to a contact" (#370) are separate and do not combine with the search text.
- The user guide says "press Enter"; the box actually filters as you type. Fix with the first phase.

## The engine: three options

### A. Our own full-text index in `mail.db` (recommended)

SQLite's FTS5 full-text index, in the database QuickMail already has.

- One index row per message: subject, from, to, cc, body text (plain body, or text extracted from the HTML body), attachment file names.
- Kept current where the store already writes: summary upsert, detail upsert, delete, move, and the account/folder purges.
- Fast enough to stay search-as-you-type across every account at once. Supports words, phrases, prefix (`budg*`), AND/OR/NOT, and accent-insensitive matching.
- Dates, read state, flags, folder and account stay ordinary SQL conditions next to the text match.
- Works offline, stays on this computer, nothing to register with Windows, same on x64 and ARM64, and testable with the real store as the offline-bodies tests already are.
- Limit: it can only find what is cached. A body is cached once opened, prefetched, or inside **Download messages for offline reading** — which is why the new **1 year** and **All mail** choices matter here. Mail outside the **Sync range** is not in the cache at all.
- To verify first: that the bundled `e_sqlite3` build has FTS5 compiled in (expected, not yet checked).
- Upgrade cost: existing caches are indexed once in the background after the update.

### B. Ask the server

For mail the cache does not have, and the only option in `--online` mode.

- **IMAP:** `SEARCH` with FROM, TO, CC, SUBJECT, BODY, TEXT, SINCE/BEFORE, flags (MailKit `SearchQuery`). One folder per request, so "all folders" is many round trips. Body search speed and quality vary a lot by server.
- **Gmail:** `X-GM-RAW` (MailKit `SearchQuery.GMailRawSearch`) runs Gmail's own search over All Mail in one request. Best of the three.
- **Microsoft 365 / Outlook.com (Graph):** `$search` with `from:`, `subject:`, `body:`, `hasAttachments:`. Has limits on combining with filters and on sorting and paging, to be confirmed while building.
- **POP3:** none; its mail is all local anyway, so option A covers it completely.

Plan: server search is an explicit "Search the server too" action, never on every keystroke.

### C. Windows Search

Three ways it could be involved. None is a good fit as the main engine.

1. **Protocol handler** — how Outlook puts mail into Windows Search. A native COM component that the Windows indexer loads in its own process to crawl QuickMail's store.
   - Mail would show up in Start and File Explorer search, and QuickMail could query the Windows index.
   - Costs: native C++ (Microsoft does not support managed code in the indexer host), separate x64 and ARM64 builds, and machine-wide registration that needs administrator rights, which the per-user Velopack install does not have. Opening a result would also need a `quickmail:` link handler.
   - The biggest job of all the options, and it duplicates option A inside the app.
2. **Write each message as a `.eml` file and have Windows index that folder.** Windows already understands `.eml` (sender, subject, body).
   - Easy to build, but doubles disk use and leaves every message readable as a loose file outside the app.
   - Adding a folder to the indexed locations changes a system setting, results point at files rather than messages, and indexing lags behind sync.
3. **Use only Windows' document filters (IFilter), not the indexer,** to pull text out of attachments (PDF, Word, …) into option A's index.
   - Keeps everything inside QuickMail, and gets attachment content search.
   - Limited to file types that have a filter installed; attachments are fetched on open today, so only downloaded ones would be searchable. A later phase.

Recommendation: A first, B for what the cache lacks, C3 as a later add-on. C1 or C2 only if finding mail from the Windows Start menu is a goal in its own right.

## Phases

### Phase 1 — full-message search in the existing box

- Build the FTS5 index (option A) with background indexing of existing caches.
- The search box queries the index instead of filtering the loaded list, so it finds matches in the body and in messages not currently loaded.
- Scope control beside the box: **This folder** (default), **This account**, **All accounts**.
- Typed field syntax, all optional:
  `from:` `to:` `cc:` `subject:` `body:` `attachment:` `has:attachment` `is:unread` `is:read` `is:flagged` `after:` `before:` `folder:` `account:` and quoted phrases.
- Results go into a **Search Results** virtual folder, built the way #370's contact-mail folder is, so every view (list, conversations, from/to groups), every open mode, and every message command keeps working on them.
- Correct the user guide's "press Enter".

### Phase 2 — Advanced Search window

- A modeless window (it has text fields and opens over the reading pane, so `Show()` not `ShowDialog()`, per the modal dialog rules).
- Fields: Words anywhere; From; To or Cc; Subject; Body; Attachment name; Has attachments; Read state; Flagged; Received after and before (the existing `DateTimeField`); Folder (the tree folder picker); Account; Scope.
- **Search** writes the equivalent typed query into the search box and runs it, so there is one parser and one result path, and the user learns the syntax by seeing it.

### Phase 3 — beyond the cache, and saved searches

- **Search the server too** (option B), results merged into Search Results and de-duplicated against cached ones.
- Save a search as a saved view (`SavedView` gains a query), so it appears in the Views menu with an optional hotkey — a live search folder.

### Phase 4 — optional

- Attachment content via IFilter (C3).
- Offline bodies for folders other than Inbox, so full-message search covers Sent and archive folders without opening each message first.

## Keyboard walkthrough (phases 1–2)

Custom announcements are listed with their category. What the platform itself reports for each control is to be confirmed by listening, not assumed.

1. In the message list, the user presses `/`. Focus moves to the search box.
2. The user types `from:sam budget`. After a short pause the list shows the matches. Announcement (Result): "12 messages."
3. The user presses Down. Focus moves to the first result.
4. The user presses Enter. The message opens in the configured mode. Closing it returns focus to the same result.
5. The user presses Escape in the search box. The search clears and the folder returns. Announcement (Result): the folder's message count, as today.
6. The user runs **Advanced Search** from the menu or command palette. The window opens with focus in Words anywhere.
7. The user tabs through the fields, fills From and Received after, and presses Enter. The window closes; the search box shows `from:… after:…`; focus goes to the first result. Announcement (Result): "N messages."
8. With no results: Announcement (Result): "No messages found." Focus stays in the search box.
9. Phase 3: with results showing, the user runs **Search the server too**. Announcement (Status): "Searching the server…"; then (Result): "N more messages from the server."
10. In the Advanced Search window, F6 cycles fields → buttons; Escape closes it and returns focus to where it was opened from; `Ctrl+Shift+P` opens its command palette.

## Infrastructure changes

- **F6 ring:** unchanged in the main window (the search box is already reachable). The Advanced Search window gets its own F6 cycle.
- **CommandRegistry (category Mail):**
  - `mail.advancedSearch` — Advanced Search. Default key to decide.
  - `mail.searchServer` — Search the Server Too. No default key.
  - `mail.searchScopeFolder` / `mail.searchScopeAccount` / `mail.searchScopeAll` — no default keys.
  - `view.search` (`Ctrl+Shift+S`) stays as is.
- **AutomationProperties.Name:** "Search messages" stays; new: "Search scope", and one short label per Advanced Search field.
- **AccessibilityHelper.Announce:** result counts and no-results (Result); server search progress (Status). The existing search-count announcement is reused, not duplicated.
- **Store:** new FTS5 table in `mail.db`, maintained in `LocalStoreService`; a one-time background index build after upgrade.
- **Mail services:** new `SearchAsync` on `IMailService`, implemented for IMAP, Gmail (X-GM-RAW), and Graph; POP3 returns nothing.
- **VM state:** `IsSearchActive` extended with the scope and a query model; a Search Results virtual folder sentinel.
- **Docs:** user guide Searching section rewritten; keyboard shortcuts doc updated; `docs/privacy.html` needs no change (no new host).

## Out of scope

- Windows Start-menu or File Explorer search of mail (option C1 or C2), unless decided otherwise.
- Searching attachment contents before Phase 4.
- Natural-language or "smart" search, and ranking by relevance — results are newest first.
- Searching calendar events or contacts from the mail search box (the calendar has its own search).
- Search history and suggestions.

## Decisions needed

1. **Default scope** for the search box: this folder (today's behavior), or all accounts?
2. **As-you-type or Enter** for full-message search? Index search is fast enough for as-you-type; server search is on request either way.
3. **Offline bodies beyond Inbox** — needed for full-message search of Sent and archives without opening each message. Yes, and in which phase?
4. **Windows Start-menu search of mail** — a goal or not? It is the only reason to take on option C1 or C2.
5. **Advanced Search default key**, if any.
