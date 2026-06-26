# QuickMail v0.7.6 Release Notes

## Download

Two options are available for v0.7.6:

| Download | When to use |
|----------|-------------|
| **`quickmail-v0.7.6-setup.exe`** — Windows installer | Recommended for most users. Installs per-user with no elevation required, checks for the WebView2 Runtime, and registers an uninstaller. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

---

## Important

QuickMail is still under active development and supporting various email providers is an important goal for the product. That said, it is a broad target and you may want to use the BCC field or another option when sending email to ensure it is retained. Reasonable testing is done before releasing a build but as a bug fix here shows, sometimes issues can arise. An always BCC myself feature is also under consideration.

---

## Improvements

### Calendar: upcoming appointments from invitation emails

QuickMail now tracks upcoming appointments from calendar invitation emails in your mailbox. A **Calendar** node appears in the folder list; selecting it shows your upcoming events, drawn from `.ics` attachments in your messages and sorted by date and time.

> **Scope note:** This initial calendar implementation is designed to close a specific gap: when you accept a meeting invitation from within QuickMail, you now have a place to see what you accepted. It is not a general-purpose calendar. A more complete calendar experience is under consideration for a future release.

- **Keyboard navigation:** Arrow keys move through the event list and stay within the calendar pane. F6 cycles to the next pane as expected.
- **Open the source email:** Press **Enter** on any event to open the invitation email it came from. If the email is no longer in your local cache, QuickMail announces this and does nothing instead of showing an error.
- **Invite updates and cancellations:** When you receive an updated or cancelled invitation, the calendar reflects the change automatically — no restart required.
- **Accepting invites:** Accepting a meeting invite updates the calendar without restarting.

### Startup sync is faster, especially with multiple accounts

QuickMail now syncs your **Inbox folders first** across all accounts before syncing sent mail, drafts, trash, and other folders. Accounts also sync in parallel rather than one at a time. Together these changes mean new mail in your inboxes typically appears within a few seconds of launch rather than after the full sync cycle completes. No configuration is required — the improvement applies automatically. ([#144](https://github.com/kellylford/QuickMail/issues/144))

### Move and copy now use a folder tree instead of a flat list

When you choose to move or copy an email message or conversation, the folder picker now shows the same hierarchical tree view used in the main folder panel — folders nested under their parents, with account names as collapsible group headers when multiple accounts are present. Previously the picker showed a flat alphabetical list with full folder paths written out for every entry. ([#145](https://github.com/kellylford/QuickMail/issues/145))

### Move to Folder and Copy to Folder now appear in the command palette

**Move to Folder** and **Copy to Folder** are now available in the command palette (**Ctrl+Shift+P**), in addition to the existing context menu entries. (#146)

### Logging control in Advanced settings

**Settings → Advanced** now has a **QuickMail Logging** section with two options:

- **Enable logging** checkbox — turn logging on or off. Logging is **off by default**. When enabled, QuickMail writes activity to `quickmail.log` in your profile directory (usually `%APPDATA%\QuickMail`, or the path supplied via `--profileDir`). Changes take effect as soon as you select **Save** — no restart required.
- **Delete QuickMail log** button — deletes the current log file after a Yes/No confirmation. If logging is still enabled, a fresh log file is created the next time an activity is logged.

> **Note:** If QuickMail is launched with the `/debug` flag, logging always runs regardless of the **Enable logging** setting. The `/debug` flag is intended for diagnosing problems and overrides this preference so that no diagnostic detail is lost.

### Spelling suggestions are now numbered

When a misspelling is announced, each suggestion is now preceded by its number — for example: "Misspelling: teh. 1: the, 2: then, 3: them." This makes the `Alt+1` / `Alt+2` / `Alt+3` correction shortcuts immediately obvious without having to count suggestions mentally.

A new **Spelling Suggestions Verbosity** setting in **Settings → Screen Reader Announcements** lets you choose between **Numbers with suggestions** (the new default) and **Just suggestions** if you prefer the previous style.

---

## Bug Fixes

### Newsletter emails no longer announce meaningless characters at the top

Opening a newsletter from MailChimp, Mailmojo, or similar senders no longer causes screen readers to read a long sequence of invisible Unicode characters before reaching the message body. These characters (U+034F Combining Grapheme Joiner and U+200C Zero-Width Non-Joiner) are padding injected by newsletter tools to control the preview snippet shown in email client lists — they are meant to be invisible. QuickMail's HTML sanitizer was stripping the `display:none` style that kept them hidden, making the whole block visible to screen readers. They are now removed before sanitization runs. (#142)

### Spell-check corrections no longer jump focus to the menu bar

Pressing **Alt+1**, **Alt+2**, or **Alt+3** to apply a spell-check suggestion in the compose window no longer causes focus to jump to the menu bar (File menu). The earlier fix for this class of bug (#130) handled window-level shortcuts (Alt+S, Alt+U, Alt+M, Alt+Y) but missed the spell-check correction paths. The suppression flag is now armed in both the plain-text and rich-text correction handlers. (#140)

### Shift+Tab from the message body returns focus to header fields

In both plain-text and HTML compose modes, pressing **Shift+Tab** from the message body now moves focus back to the Subject field and through the other header fields in reverse order. Previously, `AcceptsTab` on the editor blocked WPF's reverse-tab navigation, leaving focus stranded in the body. List indentation (Tab to indent, Shift+Tab to dedent) continues to work correctly inside list items in Markdown and HTML modes. (#131)

### Alt+key shortcuts in compose no longer activate the menu bar

Pressing **Alt+S** to send, **Alt+U** for subject, **Alt+M** for From, or **Alt+Y** for body no longer causes the menu bar to receive focus after the shortcut runs. The previous fix intercepted the Win32 `SC_KEYMENU` system command but missed two additional paths: `DefWindowProc` generating `SC_KEYMENU` from the Alt key-release, and WPF's `AccessKeyManager` responding to the Alt key-up event independently of Win32. The fix now intercepts `WM_KEYUP(VK_MENU)` in the WndProc hook before either path fires. (#130)

### Calendar invitation links no longer error when the source email has been purged

Pressing **Enter** on a calendar event whose original invitation email had been removed from the local message cache (due to the sync window advancing or a remote deletion) previously failed with a "Message UID not found" error. The local store now clears the source-message link when the referenced message is purged, and a background cleanup pass at the end of each sync removes any orphaned links that slipped through earlier. If no source email is available, pressing Enter announces "The original invitation email is no longer in your local message cache" and does nothing, instead of attempting a failing network fetch.

### "All Flagged Mail" virtual folder renamed to "All Flagged"

The cross-account virtual folder that collects all flagged messages is now named **All Flagged** to match the shorter, consistent naming used by other virtual folders. (#135)

### Adding a new account no longer disconnects existing accounts

Opening the Account Manager and adding a new account no longer causes all existing accounts to appear disconnected. The bug was a race condition in the `AccountReachabilityChanged` event handler: when the Account Manager refreshed the accounts collection, the event handler remained bound to the old account objects. New accounts would never receive their connection status updates and would show as disconnected indefinitely. The handler is now properly unsubscribed and re-subscribed when the accounts collection is refreshed. (#126)

### Virtual folder refresh now reconciles read and flag state

Pressing **F5** in **All Mail**, **All Inboxes**, **Drafts**, **Sent**, **Trash**, or a per-account All Mail view now correctly updates messages that were already in the list. Previously, if you marked a message read or flagged it in another mail client and then pressed F5, the existing row kept its stale state — only newly arrived messages were added. The refresh now updates `IsRead`, reply/forward state, attachments, and flag information on any message that has changed on the server, without disturbing selection or screen reader focus. (#111)

### Sent folder now recognized on servers that name it "Sent Messages"

On IMAP servers that use "Sent Messages" as the Sent folder name — including Apple Mail and some Dovecot configurations — sent mail now appears correctly in the Sent view. Previously, QuickMail's special-folder detection did not recognize this name variant, so sent messages were not mapped and the Sent view appeared empty for those accounts. (#149)

### Shift+F10 context menu regression in conversation tree fixed

Pressing **Shift+F10** on a message in the conversation tree now opens the QuickMail context menu correctly. A previous fix to conversation-tree context menu handling had inadvertently suppressed Shift+F10 for messages shown as tree items. The fix also corrects the menu's placement target so that Move to Folder and Copy to Folder work correctly when opened from the conversation tree. (#146)

---

## Known Issues

### First Shift+F10 press shows the system menu instead of the QuickMail context menu

On the very first Shift+F10 keypress after launching QuickMail, the Windows system menu (Restore, Move, Size, Minimize, Maximize, Close) may appear instead of the QuickMail context menu. All subsequent presses in the same session work correctly.

**Workaround:** Press Shift+F10 a second time. After the first press, the behaviour is correct for the remainder of the session.

The root cause is a startup-timing issue: WebView2 initialization can claim Win32 focus before WPF's first focus-restoration pass completes. When Shift+F10 arrives while `Keyboard.FocusedElement` is null, WPF has no element to route `ContextMenuOpening` from and falls through to `DefWindowProc`, which shows the system menu. ([#148](https://github.com/kellylford/QuickMail/issues/148))

### iCloud: two copies of sent messages appear in Sent Messages

When sending from an iCloud account, two identical copies of each sent message may appear in the Sent Messages folder. iCloud's SMTP server saves a copy automatically when relaying the message; QuickMail also appends a copy after a successful send. Before fix #149 added the "Sent Messages" folder alias, QuickMail's explicit append was silently failing — so only the auto-saved copy existed. The fix makes both copies land correctly.

**Impact:** Cosmetic only. Both copies are identical and no mail is lost.

**Planned fix:** Before appending, search the Sent folder for a matching `Message-ID` and skip the append if a copy already exists. ([#150](https://github.com/kellylford/QuickMail/issues/150))

---

## Thank You to Contributors

Thank you to everyone who has contributed to QuickMail through code, bug reports, feature suggestions, and other feedback. Your contributions make the project better for everyone.

---

## Internal

### InvokeOnUi dispatcher guard

`InvokeOnUi` (the helper that marshals change-notifier events onto the UI thread) now detects background STA threads whose dispatcher is not pumping a message loop and runs the action inline instead of queuing it. This prevents a class of silent hang where an event fires on a background STA thread that has no WPF message loop — the queued work would never execute and the caller would time out.

### Virtual folder refresh reconciliation

- `MainViewModel`: replaced the add-only `existingKeys` `HashSet` in all virtual-folder merge paths (All Mail, per-account All Mail, All Inboxes, Drafts, Sent, Trash) with a `Dictionary` keyed on `(MessageId, AccountId, FolderName)`. On collision, a new `ReconcileMessageState` helper copies server-fresh mutable fields (`IsRead`, `IsReplied`, `IsForwarded`, `HasAttachments`, `FlagId`, `FlagName`, `FlagColorHex`, `IsServerFlagged`) onto the existing `ObservableObject` in place, triggering `PropertyChanged` so rows update without disturbing selection or screen reader focus. Genuinely new messages continue to be inserted sorted. The local-store upsert at the end of each path already covered all fetched messages, so cache correctness was unaffected.
