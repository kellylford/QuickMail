# QuickMail v0.8.3 Release Notes

## Download

Two options are available for v0.8.3:

| Download | When to use |
|----------|-------------|
| **`QuickMail-0.8.3-win.msi`** — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

This is a **fixes release** on top of v0.8.2. It fixes messages sometimes staying unread in your other email clients after you had read them in QuickMail, and it puts the version number in the downloaded installer's filename. If you installed QuickMail from the MSI, this update is delivered automatically. Everything from v0.8.2, v0.8.1, and v0.8.0 — the Gmail duplicate fix, live folder unread counts, themes and the visual design system, automatic updates, the Tools menu, and in-app bug reporting — is included.

---

## Fixed in 0.8.3

### Messages you read now show as read everywhere

After reading messages in QuickMail, some of them could still appear **unread** when you looked at the same mailbox in another client — Outlook, Apple Mail, or the web — or after you closed and reopened QuickMail. The messages showed as read inside QuickMail, so the problem was invisible until you checked elsewhere. It affected both Gmail and Microsoft accounts and felt random: some messages were marked read on the server and some were not.

The cause was that opening a message only reliably told the server "this is read" when QuickMail had to fetch the message body fresh. QuickMail reads ahead and caches nearby messages so they open instantly — and those pre-cached messages, which are exactly the ones you tend to read next, were being marked read only in QuickMail's own view, never on the server. Opening a message now marks it read on the server directly, whether the body came from the cache or a fresh fetch, in the reading pane, in a tab, and in a standalone message window. (Opening a message in its own window now marks it read on open, the same as the reading pane already did.)

### The installer download now carries its version number

The downloaded Windows installer was always named `QuickMail-win.msi`, so once it was on your disk you could not tell which version it was. The installer is now published as `QuickMail-<version>-win.msi` — for this release, `QuickMail-0.8.3-win.msi`. This is a filename change only; automatic updates for existing MSI installs are unaffected.

---

## Accessibility

- No accessibility behavior changes in this release. The mark-as-read fix is a server-side correction; it does not change what is announced or where focus lands when you open a message.

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

### Not shipped in this build

- Planning specs landed on `main` since v0.8.2 but are not implemented in this release: the Windows mail-client registration spec, the message-selection multi-select spec, and the issue #245 MSI upgrade-UX investigation notes. They are documentation only and have no runtime effect here.

### Version

- Bumped to `0.8.3` (`Version`, `AssemblyVersion`, `FileVersion`).
