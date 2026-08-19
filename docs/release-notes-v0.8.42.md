# QuickMail v0.8.42 Release Notes

## New: expanding and collapsing folders

Until now the only way to open or close a folder was Right and Left arrow on the folder itself, one
level at a time. An account with folders nested several levels deep could only be folded away one
item at a time, and there was no way at all to close the whole tree — which is what prompted this:
*"I was in the folder tree and wanted to collapse all folders. There was no option to do so."*

Four new actions:

- **Expand Folder** opens the selected folder and everything inside it, all the way down.
- **Collapse Folder** closes it and everything inside it, so a deeply nested branch folds back to a
  single line.
- **Expand All Folders** opens every folder in the tree.
- **Collapse All Folders** closes everything, account headers included, leaving the tree as a short
  list of accounts.

The two single-folder actions deliberately act on the whole branch rather than one level, because
one level is what Right and Left arrow already do — and those keep working exactly as before.

Each is reachable three ways: from the new **Folder** menu items on the menu bar, from the folder
tree's context menu (**Shift+F10**), and from the Command Palette (**Ctrl+Shift+P**). None of the
four has a shortcut key to begin with; if you use one often, assign your own in **File → Settings →
Keyboard Shortcuts**. On a calendar the first two read **Expand Calendar** and **Collapse Calendar**
and do the same thing.

Three details worth knowing:

- **A collapse stays collapsed.** It survives QuickMail refreshing its folder list, and coming back
  to the tree with **F6** or **Ctrl+2** no longer re-opens the branch holding the folder you are
  reading. Go to a different folder and the tree opens up to show it again, as it always has.
- **The selection never disappears into a closed branch.** If collapsing hides the folder you were
  on, the selection moves up to the nearest folder still showing.
- **The two "all folders" actions say what they did** — "All folders collapsed" — because you can
  start them from the menu bar with focus anywhere. The single-folder ones stay quiet when you use
  the context menu, where the folder keeps focus and your screen reader reports its own expanded or
  collapsed state; started from the menu bar with focus elsewhere, where nothing would report it,
  they say what they did instead.

Expansion is not remembered between runs — QuickMail still starts with each account open.
([#590](https://github.com/kellylford/QuickMail/issues/590))

## Fixed: Microsoft 365 unread counts went stale until you refreshed

On a Microsoft 365 account, the unread count beside a folder only changed when the whole folder list
was fetched from the server again. Reading, deleting, or moving mail — including doing it inside
QuickMail — left the number where it was for the rest of the session.

That number is part of what a folder is called, so a stale count was not merely displayed: arrowing
past the folder said "Newsletters, 3 unread" long after you had read all three. A manual **Refresh**
was the only thing that put it right.

The count now updates as the mail does, on Microsoft 365 accounts as it already did on IMAP ones.
([#491](https://github.com/kellylford/QuickMail/issues/491))

## Changed: Sign in is now the last thing you tab to

In both **Add Account** and **Manage Accounts**, the **Sign in with Microsoft…** / **Sign in with
Google…** button sat in the middle of the form — before **Advanced settings**. Signing in is the
last thing you do when setting up or re-authenticating an account, so tabbing to it meant passing it
on the way through the fields and coming back.

The button is now the final tab stop in both dialogs, after Advanced settings and everything inside
it. ([#584](https://github.com/kellylford/QuickMail/issues/584))

## Changed: one permission screen instead of three, for a personal Microsoft account on Graph

Adding a personal Outlook.com, Hotmail, or Live.com account with **Sync contacts** and **Sync
calendar** checked asked for permission three separate times — mail, then contacts, then calendar.
Cancelling any one of them left the account in a state that needed the checkbox switched off and
back on to recover.

The contact and calendar permissions are now folded into the mail sign-in, so one screen lists all
three together. This applies when the account is set to connect over **Microsoft 365 (Graph)** —
which you choose under **Advanced settings → Connection method**; a personal account left on the
standard IMAP connection is unaffected. Work and school accounts are deliberately left as they were:
on a tenant that restricts consent, asking for everything at once would end the whole sign-in rather
than just the extra part, and the account would never be added at all.

A related bug went with it: an account with only **Sync calendar** checked used to fall through to a
plain mail sign-in and a separate calendar prompt. Either box now triggers the fold.
([#544](https://github.com/kellylford/QuickMail/issues/544))

---

## Reporting Issues

Found a problem or have a suggestion? There are three ways to reach us — pick the one that fits:

1. **Report a Bug → Send** (Help menu, inside QuickMail). Files the report for you anonymously — it includes no email address or other identifying information, so there is no way to follow up with you. **Best when you don't want any follow-up.**
2. **Report a Bug → Copy report and open GitHub** (Help menu). Opens a pre-filled issue that you submit under your own GitHub account, so your GitHub contact information is attached. **Best when you have a GitHub account and want automatic filing plus direct contact.**
3. **Email** [quickmailissues@theideaplace.net](mailto:quickmailissues@theideaplace.net). **Best when you don't mind sending email and want a personal follow-up.**

Full details, including exactly what a report contains (and what it never contains), are in the [Reporting Issues section of the User Guide](https://kellylford.github.io/QuickMail/reporting-issues.html).

---

## Download

There are four downloads. Take a regular one unless you know your PC has an ARM processor — to check, open **Settings → System → About** and read **System type**.

| Download | When to use |
|----------|-------------|
| [**QuickMail-0.8.42-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.42/QuickMail-0.8.42-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.42-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.42/QuickMail-0.8.42-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.42/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.42/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 8 runtime — you do not need to install .NET separately.
