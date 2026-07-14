# QuickMail v0.8.31 Release Notes

## Download

Two options are available for v0.8.31:

| Download | When to use |
|----------|-------------|
| **`QuickMail-0.8.31-win.msi`** — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

This release fixes **launching QuickMail while it is already running in the background**: opening the app again now brings back your existing window instead of starting another copy. It includes everything from v0.8.3 — **plain-text message reading**, **Windows notifications for new mail**, the option to **run in the background when you close the window**, and the fix for messages sometimes staying unread in your other email clients after you had read them in QuickMail. If you installed QuickMail from the MSI, this update is delivered automatically.

---

## Fixed in 0.8.31

### Launching QuickMail while it is running no longer opens extra copies

With **Keep running in the notification area when I close the window** turned on, QuickMail keeps running after you close its window — and the natural way to get it back is to launch QuickMail again from the Start menu or desktop. Doing that started a whole new copy of the app each time: launch it ten times and you had ten copies running, ten tray icons, and duplicated notifications for every new message.

QuickMail now runs as a single instance. Launching it while it is already running simply restores the existing window — whether it was hidden in the notification area or just behind other windows — exactly as opening it from the tray icon would. This also protects your local mail cache, which was never designed to be shared by several running copies at once.

If you use a custom `--profileDir` for a separate profile, each profile still gets its own instance: one QuickMail per profile, as many profiles as you like. Thank you to the user who reported this and pinpointed exactly how the extra copies accumulated.

---

## New in 0.8.3

### Plain Text View

Read messages as plain text instead of HTML. Open **Settings → General** and check **Read messages as plain text**, or toggle it with **Ctrl+Shift+H** or the **View → Plain Text View** menu item. The preference is sticky: it persists across restarts and applies to the reading pane, message tabs, and standalone message windows. When plain-text view is on, QuickMail renders each message from its original plain-text part, falling back to text extracted from the HTML only when the sender included no plain-text part. Use it for a cleaner, low-noise read or to inspect a message's raw text for security checking.

### Windows Notifications for New Mail

Get a native Windows notification when new mail arrives in an inbox. Open **Settings → General → Notifications** and check **Show a notification when new mail arrives**. Notifications are announced by screen readers and appear in the Windows notification center. Activate a notification to bring QuickMail to the front and open that message. Requires Windows 10 1809 or later.

### Run in the Background When You Close the Window

Keep QuickMail running in the notification area when you close the main window, so new-mail notifications keep arriving. Open **Settings → General → Notifications** and check **Keep running in the notification area when I close the window**. The first time you close to the tray, a notification explains that QuickMail is still running. Restore the window from the tray icon, by launching QuickMail again, or by activating a notification. Use **File → Exit** or the tray icon's **Exit QuickMail** to quit.

---

## Fixed in 0.8.3

### Messages you read now show as read everywhere

After reading messages in QuickMail, some of them could still appear **unread** when you looked at the same mailbox in another client — Outlook, Apple Mail, or the web — or after you closed and reopened QuickMail. The messages showed as read inside QuickMail, so the problem was invisible until you checked elsewhere. It affected both Gmail and Microsoft accounts and felt random: some messages were marked read on the server and some were not.

The cause was that opening a message only reliably told the server "this is read" when QuickMail had to fetch the message body fresh. QuickMail reads ahead and caches nearby messages so they open instantly — and those pre-cached messages, which are exactly the ones you tend to read next, were being marked read only in QuickMail's own view, never on the server. Opening a message now marks it read on the server directly, whether the body came from the cache or a fresh fetch, in the reading pane, in a tab, and in a standalone message window. (Opening a message in its own window now marks it read on open, the same as the reading pane already did.)

### The installer download now carries its version number

The downloaded Windows installer was always named `QuickMail-win.msi`, so once it was on your disk you could not tell which version it was. The installer is now published as `QuickMail-<version>-win.msi` — for this release, `QuickMail-0.8.31-win.msi`. This is a filename change only; automatic updates for existing MSI installs are unaffected.

---

## Accessibility

- **Single instance** means launching QuickMail from the Start menu while it is running in the background is now a reliable, predictable way to return to your mail — the same result every time, with no duplicate tray icons to distinguish and no doubled notification announcements.
- **Plain Text View** provides a lower-noise reading option for users who prefer a simpler layout or want to inspect raw message text for security purposes.
- **New-mail notifications** are delivered as native Windows notifications, which screen readers announce automatically. Notifications appear in the Windows notification center (open it with **Win+N** on Windows 11, or **Win+A** on Windows 10). No custom in-app announcements are layered on top — the platform's native delivery is the accessibility mechanism.
- The mark-as-read fix is a server-side correction; it does not change what is announced or where focus lands when you open a message.

---

## Thank You to Contributors

Thank you, as always, to everyone who contributes to QuickMail through code, bug reports, feature suggestions, and other feedback — including the clear report of extra copies accumulating in the notification area that led directly to this release, the report that surfaced the mark-as-read problem, and the independent confirmation that it also affected Gmail accounts.

---

## Internal

### Single instance per profile (issue #240, PR #251)

- New `Services/SingleInstanceService`: `App.Main` claims a named mutex (`Local\QuickMail-<sha256(profileDir)>`) after the Velopack hook runs and before WPF initializes. A second launch for the same profile signals a named AutoReset event and exits; the first instance listens via `ThreadPool.RegisterWaitForSingleObject` and marshals to the UI thread to call `MainWindow.RestoreFromTray()` (now public). Distinct `--profileDir` values hash to distinct object names, so multi-profile use still runs side by side; `--help` bypasses the gate. A signal that arrives before the listener registers is not lost (the event stays set and fires on registration). Velopack update restarts are unaffected: Update.exe waits for the old process to fully exit, which destroys the mutex.
- Covered by `SingleInstanceServiceTests` (key stability across case/trailing separator/relative segments, per-profile mutex exclusivity, activation signaling) plus a manual two-process smoke test. An independent review of the change confirmed mutex thread affinity, startup/shutdown race handling, toast COM-activation interplay, and disposal ordering; its two out-of-scope findings are tracked as issues #252 and #253.

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

- Bumped to `0.8.31` (`Version`, `AssemblyVersion`, `FileVersion`).
