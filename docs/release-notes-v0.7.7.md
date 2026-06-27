# QuickMail v0.7.7 Release Notes

## Download

Two options are available for v0.7.7:

| Download | When to use |
|----------|-------------|
| **`quickmail-v0.7.7-setup.exe`** — Windows installer | Recommended for most users. Installs per-user with no elevation required, checks for the WebView2 Runtime, and registers an uninstaller. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

---

## Bug Fixes

### IMAP accounts no longer disconnect repeatedly on shared hosting

Accounts were frequently dropping their connections with a "Server shutting down" response from the server, then reconnecting, then dropping again — repeating every minute or two. This affected all accounts simultaneously, including accounts on different servers.

The root cause was the startup sync introduced in v0.7.6, which parallelized syncing across all accounts at once. When two or more accounts share the same IMAP server — common when multiple addresses are hosted on the same provider — the parallel sync opened simultaneous connections to that server and exceeded its per-IP connection limit. The server responded by terminating connections, which also took down the IDLE watchers responsible for new-mail notification.

Accounts on the same IMAP server now sync sequentially, so only one sync connection at a time is active per server. Accounts on different servers continue to sync in parallel, preserving the startup speed improvement from v0.7.6.

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

When an IDLE watcher loses its connection, the log now includes the full exception chain — type, message, and (for socket-level failures) the `SocketError` code and native error code. Previously only `ex.Message` was logged, which collapsed the chain and omitted the socket error code needed to distinguish a server-initiated disconnect from a network timeout. The account username now appears in error lines instead of the internal account GUID. Session elapsed time is also recorded in debug mode, making it straightforward to correlate how long a connection was alive before it failed.
