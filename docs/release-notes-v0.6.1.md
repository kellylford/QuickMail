# QuickMail v0.6.1 Release Notes

## New Features

### IMAP IDLE — real-time new mail notification

QuickMail now uses IMAP IDLE to detect new mail as soon as it arrives, without polling. After connecting, a dedicated background connection per account watches the INBOX. When the server signals that a new message has arrived, QuickMail runs a targeted sync for that inbox and the message appears in your list automatically — no manual refresh needed.

IDLE works in both normal and `--online` modes. Both of your mail servers support IDLE, so this is active by default with no configuration required.

### Online mode (`--online`)

A new `--online` command-line flag runs QuickMail in fully live mode: every folder selection fetches messages directly from IMAP, and nothing is read from or written to the local SQLite cache. Useful for a clean look at your mail without any cached state.

```
QuickMail.exe --online
```

### 12-hour clock and updated date format

Message dates in the list now use a 12-hour clock with an A or P suffix for today's messages (e.g., `9:30A`, `3:45P`), and `M/d/yyyy` for older messages (e.g., `5/22/2026`).

---

## Bug Fixes

- **IDLE push not working in `--online` mode** — The IDLE watcher was never started when `--online` was active, so new mail required a manual refresh. IDLE connections now start regardless of caching mode, and new arrivals are fetched live from IMAP and merged into the message list without touching the cache.
- **Preview text showing when Preview Lines is set to 0** — When `PreviewLines = 0` in Settings, the IMAP PREVIEW extension on the server was still populating preview text in the message list. QuickMail now clears preview text when loading or inserting messages whenever the preview display is disabled.

---

## Internal

- Added `SyncOneFolderOnlineAsync` to `ISyncService` / `SyncService` for IDLE-triggered inbox refreshes that bypass the local store.
- Added `SessionFeaturesTests.cs` with 12 new tests covering date display format, preview suppression, the `OnlineMode` constructor flag, and IDLE-triggered sync dispatch in both normal and online modes.
- Total test count: 223 (v0.6) → 235 (v0.6.1).
