# QuickMail v0.8.2 Release Notes

## Download

Two options are available for v0.8.2:

| Download | When to use |
|----------|-------------|
| **`QuickMail-win.msi`** — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

This is a **fixes release** on top of v0.8.1. It resolves a long-standing Gmail problem where the same message appeared many times, makes folder unread counts update as you read, keeps spell check inside your own text when replying and forwarding, makes Google and Microsoft account reconnection more reliable, and fixes a crash when pasting certain text into an address field. If you installed QuickMail from the MSI, this update is delivered automatically. Everything from v0.8.1 and v0.8.0 — themes and the visual design system, automatic updates, the Tools menu, and in-app bug reporting — is included.

---

## Fixed in 0.8.2

### Gmail: the same message no longer appears many times

On Google accounts, the main **All Mail** view could show the same message repeated over and over. This happened because Gmail presents one message in several folders at once — Inbox, All Mail, and every label it carries — and QuickMail was listing each of those copies separately.

QuickMail now recognizes when copies in different folders are the same message and shows each message **once** in All Mail, the per-account All Mail, conversations, and the by-sender and by-recipient groupings. Opening a message, marking it read, flagging, or deleting acts on the most natural copy — the one in your Inbox when the message is there. Individual folders (Inbox, a specific label) are unchanged: they show exactly their own contents.

The first time you start this version, QuickMail rebuilds its message cache so the change takes effect, so you may see a brief "syncing" pass on launch. Your accounts, settings, and message bodies are untouched.

### Folder unread counts now stay up to date

The unread count shown next to each folder was a snapshot taken when QuickMail connected, and it did not change afterward — reading, deleting, or moving messages, new mail arriving, or a sync would not update it until you reconnected. Now the counts refresh shortly after you read a message (including simply opening it), delete or move messages, new mail arrives, or a sync completes. On Gmail, where reading one message can change several folders' counts at once, all of the affected counts update together. The counts update in place, so your position in the folder list is not disturbed, and the count is spoken as part of each folder's name.

**Gmail folders that don't mean "new mail."** Gmail's **All Mail** folder holds every message you have ever received — including old, archived mail — so its "unread" count is the total of everything unread across your whole account, not new mail to read. That is why it could show a large number (for example, 23 unread) even when your inbox was empty. QuickMail no longer shows an unread count on Gmail's virtual folders — **All Mail**, **Important**, and **Starred** — and no longer counts them toward your account's unread total, so the total reflects real new mail. Your Inbox, Spam, Trash, and label folders show their counts exactly as before. (One consequence: an unread message that lives *only* in one of those hidden folders — for example a message you starred and then archived — is not counted; it is still visible when you open that folder.)

### Spell check stays in your own text on reply and forward

Starting a spell check (F7) while replying or forwarding used to continue past the end of your own writing and into the quoted original message, prompting you about words other people wrote. Spell check now stops at the start of the quoted original — the "On … wrote:" line on a reply or the "Forwarded message" header on a forward — in plain text, Markdown, and HTML messages. The subject line is still checked in full, and a brand-new message (with nothing quoted) is checked exactly as before.

One current limit: if you have a signature configured, it is added below the quoted message, so it is not part of this spell check on a reply or forward. This is noted for a future update.

### Connecting Google and Microsoft accounts is more reliable

Several rough edges in reconnecting Google (OAuth) and Microsoft accounts are fixed:

- A reconnecting account could wrongly report "No password stored" even though these accounts do not use a stored password.
- A routine background reconnect could briefly pop up a sign-in prompt and then cancel it; background reconnects are now silent, and a sign-in window only appears when one is genuinely needed.
- Google sign-in now requests the standard per-service permission scope, which is the more compatible choice.

### Crash fixed when pasting certain text into an address field

Pasting some comma-containing text into a To, Cc, or Bcc field could crash QuickMail. That path is fixed.

---

## Accessibility

- Folder unread counts are part of each folder's spoken name and update in place — no rebuilding of the folder list, so focus and your place in the tree are preserved when a count changes.
- Deduplicating Gmail messages does not change focus behavior; the message list, conversation view, and sender/recipient groupings simply show each message once.
- Spell check on a reply or forward no longer walks you through the quoted original's words.

---

## Thank You to Contributors

Thank you, as always, to everyone who contributes to QuickMail through code, bug reports, feature suggestions, and other feedback — including the reports that surfaced the Gmail duplicates, the folder-count behavior, and the reply/forward spell-check issue in this release.

---

## Internal

### Gmail duplicate collapse (issue #220, PR #224)

- Root cause: messages were identified by their per-folder IMAP UID and the RFC 5322 `Message-ID` was fetched but discarded, so the "All Mail" aggregate unioned every folder's copy with no cross-folder dedup. Gmail exposes one message in INBOX + `[Gmail]/All Mail` + Important + Starred + every label, each with a distinct UID.
- Fix: persist the normalized `Message-ID` on `MailMessageSummary` (schema v5, one-time cache clear so it backfills on next sync) and collapse copies in aggregate views by `(account, Message-ID)` via a new `MessageDeduplicator`. Empty Message-IDs and cross-account are never merged; the representative is chosen by folder priority (Inbox > user label > Gmail virtual folders). Applied at every aggregate choke point — All Mail, per-account All Mail, saved views (batch and incremental), and the live sync path — so the flat list, conversations, and sender/recipient groups all dedup. Single real-folder views are untouched. Provider-agnostic; Outlook/IMAP with unique Message-IDs see no change.
- An independent review confirmed correctness (no cross-account/empty-id merges; migration safe for fresh/v1/v2–v4 databases) and one behavioral edge was fixed: `RemoveVanishedMessages` now keys on global identity so archiving a Gmail message out of Inbox doesn't transiently drop its representative. Full suite green.

### Folder unread-count refresh (issue #227, PRs #229 and #232)

- Folder `UnreadCount` was set only at folder-list load and never updated afterward. A debounced, server-authoritative refresh now re-queries IMAP `STATUS` after the read (including open/select), delete, move, new-mail, and sync paths (coalescing bursts, with a per-account minimum interval between sweeps) and writes counts onto the existing folder models, updating the tree nodes **in place** — no tree rebuild, so folder-tree keyboard focus is preserved. Server-authoritative (not optimistic decrement) so Gmail's cross-label `\Seen` propagation is reflected across every affected folder.
- Accessibility: the count is carried in the folder's `AutomationProperties.Name`, not only `ItemStatus` — a screen-reader user confirmed that a count in `ItemStatus` alone is not announced. The visible label stays count-free (the count shows as a badge), and `AutomationName` is a computed value refreshed in place, so the count is both announced and kept current without doubling.
- Gmail virtual folders: `MailFolderModel.SuppressUnreadCount` (Kind `AllMail`/`Important`/`Starred`, from the SPECIAL-USE `\All`/`\Important`/`\Flagged` attributes) hides the unread count in the tree and excludes it from `account.TotalUnread`, since those folders' `UNSEEN` counts overlap the Inbox/labels and include archived mail (`[Gmail]/All Mail` is the whole-account superset). Accepted tradeoff: an unread message present only in a suppressed folder is not counted.
- Two independent reviews (one per PR). The first caught the accessibility issue above (count must be in the Name). The second caught that the delete-path refresh was scheduled before the awaited move-to-trash — the debounced sweep could read a pre-delete count and clobber the optimistic decrement — now scheduled after the server mutation and gated on an unread message actually leaving a folder; the move path got the same trigger; and steady reading is throttled to one `STATUS` sweep per account per interval.

### Spell-check scope on reply/forward (issue #228, PR #230)

- The Check Spelling body scan is bounded to the user's own text, stopping at the quoted/forwarded original. `SpellScan.QuoteBoundaryIndex` (plain/markdown) and `QuoteBoundaryPointer` (HTML) detect the boundary fresh each session from QuickMail's own seeded markers; `TextBoxSpellSource` / `RichTextBoxSpellSource` take an exclusive upper bound; the subject scan is unbounded; a no-marker compose scans the whole body unchanged.
- Independent review found no Critical/High. Two Medium items were addressed: boundary detection was tightened against false positives (the plain reply attribution must be followed by a `>`-quoted line; a user's own blockquote is no longer treated as the boundary) so real user text is never silently skipped, and the boundary index no longer drifts after a length-changing replacement. Known limitation, deferred and documented: an auto-appended signature is seeded below the quote and is therefore not checked on a reply/forward.

### Account reconnect reliability (issues #205, #206, #207, #208)

- OAuth/Graph accounts no longer report "No password stored" on reconnect; background connect no longer opens and then cancels an interactive sign-in window; sign-in is requested only when genuinely required; Google requests the per-resource `.default` scope rather than explicit permission lists.

### Address-field paste crash (issue #216)

- Fixed a stack-overflow crash when pasting comma-containing text into an address (To/Cc/Bcc) field.

### Testing / process

- WPF STA tests are serialized into a single collection to remove intermittent test-host teardown flakiness (issues #211, #213).
- Documented a dedicated bot-account setup for the in-app bug-report token so submitted reports are attributed to an anonymous machine account rather than a maintainer's personal account (issue #222, `docs/BUG-REPORT-BOT-ACCOUNT.md`) — a provisioning/process change, no code impact.

### Version

- Bumped to `0.8.2` (`Version`, `AssemblyVersion`, `FileVersion`).
