# QuickMail v0.7.7 Release Notes

## Download

Two options are available for v0.7.7:

| Download | When to use |
|----------|-------------|
| **`quickmail-v0.7.7-setup.exe`** — Windows installer | Recommended for most users. Installs per-user with no elevation required, checks for the WebView2 Runtime, and registers an uninstaller. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

---

## Improvements

### Automatic update notifications

QuickMail now checks for a newer release on startup. When an update is available, you are notified in two ways:

- A spoken announcement a few seconds after launch: "QuickMail update available: version X.Y.Z. Check the Help menu." This respects your action-result announcement preference.
- A new item at the top of the **Help** menu showing the available version — activate it to open the release page in your default browser.

The check runs quietly in the background. If there is no network connection, or you are already on the latest version, nothing appears and startup is not delayed. This is what will let you know when fixes like the connection improvement below are available, without having to check manually.

---

## Bug Fixes

### IMAP accounts no longer disconnect repeatedly on shared hosting

Accounts were frequently dropping their connections with a "Server shutting down" response from the server, then reconnecting, then dropping again — repeating every minute or two. This affected all accounts simultaneously, including accounts on different servers.

The root cause was the startup sync introduced in v0.7.6, which parallelized syncing across all accounts at once. When two or more accounts share the same IMAP server — common when multiple addresses are hosted on the same provider — the parallel sync opened simultaneous connections to that server and exceeded its per-IP connection limit. The server responded by terminating connections, which also took down the IDLE watchers responsible for new-mail notification.

Accounts on the same IMAP server now sync sequentially, so only one sync connection at a time is active per server. Accounts on different servers continue to sync in parallel, preserving the startup speed improvement from v0.7.6.

This release also adds several resilience measures around the same area, so a brief server hiccup no longer turns into a repeating disconnect cycle:

- Account connections at startup are also grouped by server — accounts sharing a host connect one at a time, while different hosts still connect in parallel.
- When connections do drop, the automatic reconnect attempts are now spread out with randomized timing instead of all retrying at the same instant, so they no longer arrive in lockstep and re-trigger the server's limit.

---

## Known Issues

### First Shift+F10 press shows the system menu instead of the QuickMail context menu

On the very first Shift+F10 keypress after launching QuickMail, the Windows system menu (Restore, Move, Size, Minimize, Maximize, Close) may appear instead of the QuickMail context menu. All subsequent presses in the same session work correctly.

**Workaround:** Press Shift+F10 a second time. After the first press, the behaviour is correct for the remainder of the session.

The root cause is a startup-timing issue: WebView2 initialization can claim Win32 focus before WPF's first focus-restoration pass completes. When Shift+F10 arrives while `Keyboard.FocusedElement` is null, WPF has no element to route `ContextMenuOpening` from and falls through to `DefWindowProc`, which shows the system menu. ([#148](https://github.com/kellylford/QuickMail/issues/148))

### iCloud: two copies of sent messages appear in Sent Messages

When sending from an iCloud account, two identical copies of each sent message may appear in the Sent Messages folder. iCloud's SMTP server saves a copy automatically when relaying the message; QuickMail also appends a copy after a successful send.

**Impact:** Cosmetic only. Both copies are identical and no mail is lost.

**Planned fix:** Before appending, search the Sent folder for a matching `Message-ID` and skip the append if a copy already exists. ([#150](https://github.com/kellylford/QuickMail/issues/150))

---

## Thank You to Contributors

Thank you to everyone who has contributed to QuickMail through code, bug reports, feature suggestions, and other feedback. Your contributions make the project better for everyone.

---

## Internal

### Improved IMAP disconnect logging

When an IDLE watcher loses its connection, the log now includes the full exception chain — type, message, and (for socket-level failures) the `SocketError` code and native error code. Previously only `ex.Message` was logged, which collapsed the chain and omitted the socket error code needed to distinguish a server-initiated disconnect from a network timeout. The account username now appears in error lines instead of the internal account GUID. Session elapsed time is also recorded in debug mode, making it straightforward to correlate how long a connection was alive before it failed. (`LogService.FormatException`, `ImapMailService.RunIdleWatcherAsync`)

### Connection hardening from a connection-code review

A review of the connection code following the disconnect fix produced several hardening changes beyond the sync-ordering fix:

- **Randomized reconnect jitter.** Both the IDLE watcher retry backoff (`ImapMailService.RunIdleWatcherAsync`) and the startup connection retry backoff (`MainViewModel.ConnectOneAccountAsync`) apply ±30% jitter. When a server terminates every connection at once, retries previously fired on the identical schedule and reconnected in lockstep, which could re-trip the per-IP connection limit that disconnected them.
- **Per-host grouping at connect time.** `MainViewModel.ConnectAllAccountsAsync` now groups accounts by IMAP host so same-host accounts connect sequentially, matching the grouping `SyncService.SyncAllAccountsAsync` applies during sync.
- **Thread-safe Google token cache.** `GoogleOAuthService`'s in-memory credential cache is now a `ConcurrentDictionary`; it is read and written concurrently by IDLE watcher threads and pooled IMAP/SMTP operations, where the previous plain `Dictionary` could race under read-during-write.
- **Cleanups.** Removed a dead `delaySeconds` declaration in `ConnectOneAccountAsync`; corrected the periodic-NOOP heartbeat comment to describe its actual behavior.

A larger structural follow-up — a per-host connection cap that also accounts for IDLE watcher connections — is tracked in [#152](https://github.com/kellylford/QuickMail/issues/152) for a future release.

### Update check service

The startup update notification is implemented by `UpdateCheckService` (`IUpdateCheckService`), which queries the GitHub Releases API with `HttpClient` + `System.Text.Json` (no new dependencies, 10-second timeout, debug-level logging on failure). It holds an internal `CancellationTokenSource` that is cancelled before disposal so an in-flight request on app exit is cancelled cleanly rather than relying on the HTTP timeout or throwing `ObjectDisposedException`. The reflected assembly version is cached in a static field. ([#151](https://github.com/kellylford/QuickMail/pull/151))
