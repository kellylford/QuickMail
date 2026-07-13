# QuickMail v0.8.3 Release Notes

## Download

Two options are available for v0.8.3:

| Download | When to use |
|----------|-------------|
| **`QuickMail-0.8.3-win.msi`** — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

This release adds **plain-text message reading** and **Windows notifications for new mail**, plus the option to **run in the background when you close the window**. It also fixes messages sometimes staying unread in your other email clients after you had read them in QuickMail, and puts the version number in the downloaded installer's filename. If you installed QuickMail from the MSI, this update is delivered automatically. Everything from v0.8.2, v0.8.1, and v0.8.0 — the Gmail duplicate fix, live folder unread counts, themes and the visual design system, automatic updates, the Tools menu, and in-app bug reporting — is included.

---

## New in 0.8.3

### Plain Text View

Read messages as plain text instead of HTML. Open **Settings → General** and check **Read messages as plain text**, or toggle it with **Ctrl+Shift+H** or the **View → Plain Text View** menu item. The preference is sticky: it persists across restarts and applies to the reading pane, message tabs, and standalone message windows. When plain-text view is on, QuickMail renders each message from its original plain-text part, falling back to text extracted from the HTML only when the sender included no plain-text part. Use it for a cleaner, low-noise read or to inspect a message's raw text for security checking.

### Windows Notifications for New Mail

Get a native Windows notification when new mail arrives in an inbox. Open **Settings → General → Notifications** and check **Show a notification when new mail arrives**. Notifications are announced by screen readers and appear in the Windows notification center. Activate a notification to bring QuickMail to the front and open that message. Requires Windows 10 1809 or later.

### Run in the Background When You Close the Window

Keep QuickMail running in the notification area when you close the main window, so new-mail notifications keep arriving. Open **Settings → General → Notifications** and check **Keep running in the notification area when I close the window**. The first time you close to the tray, a notification explains that QuickMail is still running. Restore the window from the tray icon, by double-activating the icon, or by activating a notification. Use **File → Exit** or the tray icon's **Exit QuickMail** to quit.

---

## Fixed in 0.8.3

### Messages you read now show as read everywhere

After reading messages in QuickMail, some of them could still appear **unread** when you looked at the same mailbox in another client — Outlook, Apple Mail, or the web — or after you closed and reopened QuickMail. The messages showed as read inside QuickMail, so the problem was invisible until you checked elsewhere. It affected both Gmail and Microsoft accounts and felt random: some messages were marked read on the server and some were not.

The cause was that opening a message only reliably told the server "this is read" when QuickMail had to fetch the message body fresh. QuickMail reads ahead and caches nearby messages so they open instantly — and those pre-cached messages, which are exactly the ones you tend to read next, were being marked read only in QuickMail's own view, never on the server. Opening a message now marks it read on the server directly, whether the body came from the cache or a fresh fetch, in the reading pane, in a tab, and in a standalone message window. (Opening a message in its own window now marks it read on open, the same as the reading pane already did.)

### The installer download now carries its version number

The downloaded Windows installer was always named `QuickMail-win.msi`, so once it was on your disk you could not tell which version it was. The installer is now published as `QuickMail-<version>-win.msi` — for this release, `QuickMail-0.8.3-win.msi`. This is a filename change only; automatic updates for existing MSI installs are unaffected.

---

## Accessibility

- **Plain Text View** provides a lower-noise reading option for users who prefer a simpler layout or want to inspect raw message text for security purposes.
- **New-mail notifications** are delivered as native Windows notifications, which screen readers announce automatically. Notifications appear in the Windows notification center (Win+A) and can be navigated with Win+Shift+V. No custom in-app announcements are layered on top — the platform's native delivery is the accessibility mechanism.
- The mark-as-read fix is a server-side correction; it does not change what is announced or where focus lands when you open a message.

---

## Thank You to Contributors

Thank you, as always, to everyone who contributes to QuickMail through code, bug reports, feature suggestions, and other feedback — including the report that surfaced the mark-as-read problem and the independent confirmation that it also affected Gmail accounts.

---

## Internal

### Mark messages read on the server when opened from cache (issue #225, PR #248)

- Root cause: opening a message set `IsRead` locally and updated the local store, but the IMAP `\Seen` flag was only set as a **side effect** of `ImapMailService.GetMessageDetailAsync` (which opens the folder `ReadWrite` and calls `AddFlags(\Seen)`). In cached (default, non-online) mode, `SelectMessageAsync` serves the detail from the local store on a cache hit and only falls back to that fetch on a miss. The prefetcher (`PrefetchMessageDetailAsync`, `markRead: false`) caches the top of the folder and the neighbors of every opened message without setting `\Seen`, so those messages were served from cache on open and never flagged on the server — read in QuickMail, unread everywhere else. Provider-independent; the timing dependence (whether a message had been prefetched) is why the symptom looked random.
- Fix: decouple the server mark-read from the body fetch. `SelectMessageAsync` now calls `_imap.MarkReadAsync(...)` explicitly (fire-and-forget via `.LogFaults`) when opening an unread message in cached mode — covering the reading-pane and tab paths, which both route through it. `AddFlags(\Seen)` is idempotent, so the cache-miss path is unaffected, and online mode already flags during its fetch, so it is skipped there. Standalone **window** mode (`MessageWindow.LoadSelectedMessageAsync`) previously auto-marked read on open through no path at all (only explicit Ctrl+Q); it now marks read after the body loads via the existing `MarkReadCommand` → `MarkReadAction` → `MainViewModel.MarkMessagesReadAsync` chain, which is a no-op when already read and runs after the render so a load failure never marks an unread message.
- Tests: `MarkReadOnOpenTests` covers the cache-hit path (fails without the fix) and the already-read no-op case; verified the cache-hit test fails on a stashed tree and passes with the fix. An independent review approved the change (no double-flag in online mode, cache-miss path unaffected, null-safe window wiring).

### Versioned installer filename (issue #244)

- `vpk pack` always emits the MSI as the version-less `QuickMail-win.msi`. The release workflow now renames the packed MSI to `QuickMail-<version>-win.msi` after packing and uploads that (glob still covered by `fail_on_unmatched_files`). Filename only; the MSI's internal `ProductVersion` is unchanged. Workflow-only change, no application code impact.

### Plain Text View (issue #34)

- Added a sticky "Plain Text View" preference, configurable via the View menu, a **Ctrl+Shift+H** hotkey, and a Settings checkbox. When on, messages render from their original `text/plain` MIME part (falling back to text extracted from HTML only when the sender provided no plain-text part). The setting persists across restarts and applies to all three reading surfaces (reading pane, tab, and standalone window). Default is off. Plain-text rendering reuses the existing `BuildPlainTextHtmlDocument` builder; a new optional `forcePlainText` parameter routes callers through the plain-text branch.

### Windows Notifications for New Mail (issue #240)

- Two new features enable Windows native notifications for new mail and background-running when the window is closed. Phase 1 adds `INotificationService` / `WindowsToastNotificationService` (requires Windows 10 1809+) and a `NotifyOnNewMail` config flag (opt-in, off by default). The service hooks into the existing `InboxNewMailDetected` event; a dedupe filter (by session start time, read status, and message key) prevents duplicate toasts. Activating a single-message toast opens that message; multi-message toasts bring the app forward. Phase 2 adds `CloseToTray` and `TrayHintShown` flags; when `CloseToTray` is on, closing the window hides it to the notification area (system tray) instead of exiting, so the IDLE/delta watchers keep running. Exit can be triggered explicitly via File → Exit or the tray icon's Exit menu item. Both features require explicit opt-in in **Settings → General → Notifications**.

### Not shipped in this build

- Planning specs landed on `main` since v0.8.2 but are not implemented in this release: the Windows mail-client registration spec, the message-selection multi-select spec, and the issue #245 MSI upgrade-UX investigation notes. They are documentation only and have no runtime effect here.

### Version

- Bumped to `0.8.3` (`Version`, `AssemblyVersion`, `FileVersion`).
