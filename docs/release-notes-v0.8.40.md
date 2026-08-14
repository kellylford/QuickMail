# QuickMail v0.8.40 Release Notes

## Download

There are four downloads. Take a regular one unless you know your PC has an ARM processor — to check, open **Settings → System → About** and read **System type**.

| Download | When to use |
|----------|-------------|
| **`QuickMail-0.8.40-win.msi`** — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| **`QuickMail-0.8.40-win-arm64.msi`** — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |
| **`QuickMail-arm64.exe`** — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 8 runtime — you do not need to install .NET separately.

---

## New: POP3, for mail services that offer no IMAP

Some mail services still hand out POP3 and nothing else, and until now that put QuickMail out of
reach entirely. It can now collect mail over POP3, and send over SMTP exactly as it always has.

**It is off until you turn it on.** POP3 is new here, and it works differently enough from IMAP to
be worth choosing deliberately rather than stumbling into. In **File → Settings** (Ctrl+comma),
**Advanced**, under **Account Types**, tick **Offer POP3 when adding an account**, then restart
QuickMail. Nothing else about QuickMail changes while it is off — and an account already using POP3
keeps working whether the setting is on or off, so turning it back off hides the option without
stranding your mail.

Then add the account as usual: **Accounts → Add Account**, open **Advanced settings**, and choose
**POP3/SMTP** under **Connection method**. Gmail, Outlook.com and Yahoo fill in their POP3 server for
you; for anything else you type the server, as you would for IMAP. Most services need POP3 switched
on in their own web settings first, and Gmail and Yahoo want an app password rather than your account
password.

### What POP3 means day to day

POP3 has no folders on the server, no shared read or flag state, and no way to fetch part of a
message. So a POP3 account in QuickMail works like this:

- **Whole messages are downloaded and kept on this computer.** Reading a message, opening its
  attachments and searching all work with no network at all.
- **Four folders — Inbox, Sent, Drafts and Trash — and they are QuickMail's own.** There is nothing
  on a POP3 server to make folders out of, so creating, renaming or moving folders is not offered;
  ask for it and QuickMail says why rather than failing at the server.
- **Read and unread, flags, and moves stay on this computer.** POP3 gives them nowhere to go, so
  another mail program looking at the same mailbox will not see them.
- **New mail arrives on the sync interval**, not the instant it lands. POP3 has no equivalent of the
  live connection QuickMail holds open for IMAP.
- **Deleting is two steps, as everywhere else in QuickMail**: to Trash, then permanently.

### Keep mail on the server

The one setting worth reading twice. **Keep mail on the server after downloading** is on by default,
and while it is on QuickMail never deletes anything from your mail service — collect mail here and it
is still there for your phone, or webmail, or whatever else you use.

Clear it and QuickMail removes each message from the server once it has been safely stored here,
which is the classic POP3 behaviour. That makes this computer the only copy, so keep backups in mind
before you choose it. Either way, a message you delete only ever leaves the server on a permanent
delete, never on the way to Trash — and QuickMail checks it is deleting the right message before it
does, so a message another program has already collected is left alone rather than deleted by
position.

Thanks to the people who have been asking for this since QuickMail's early days.
([#128](https://github.com/kellylford/QuickMail/issues/128))

## New: the folder picker opens where you last filed

Filing is repetitive. You move a message to **Projects/2026**, then the next one, then the one after that — and until now the picker opened each time on the folder the messages were *in*, so you walked the tree to the same destination over and over.

**Move to Folder…** now opens on the folder you last chose in **Move to Folder…**. Filing a run of messages to one place is Enter, Enter, Enter after the first. It is remembered between sessions, so tomorrow morning starts where last night left off. (Archiving and deleting do not change it — only the folder you picked yourself.)

Some details worth knowing:

- **Move and copy are remembered separately.** Copying something to a reference folder does not change where **Move to Folder…** opens.
- **Each account keeps its own.** A folder on one account is not a destination for another, so your work account and your personal account do not tread on each other.
- **The old behaviour is still the fallback.** Before you have moved anything, or if the remembered folder has since been deleted or renamed, the picker opens on the folder the messages are in — exactly as before. Nothing to clear up, and no error.

Thanks to the person who asked for this, and for pointing at Outlook Classic as the thing to match. ([#515](https://github.com/kellylford/QuickMail/issues/515))

## Fixed: the arrow keys skipped half the Settings tabs

In **File → Settings** (Ctrl+comma), Left and Right on the tab headers only ever reached three of the six tabs — pressing Right from General went to Advanced, then Keyboard Shortcuts, then straight back to General. Startup, Windowing and Appearance could not be reached with the arrow keys at all.

The cause was that the six headers do not fit on one line, so they are laid out on two, and the arrow keys were finding the next tab by *where it sits on screen* rather than by its place in the list. That meant they never crossed from one line to the other.

Left and Right now move through the tabs in order, whatever the layout, and wrap around at both ends. **Home** goes to the first tab and **End** to the last. Arrowing to a tab shows it, as it does in any tabbed window on Windows, and **Ctrl+Tab** and **Ctrl+Shift+Tab** work as before. The Address Book's tabs behave the same way. Your open message tabs are unchanged — that strip has always had its own arrow handling, which steps onto each tab's close button and stops at the ends rather than wrapping. ([#528](https://github.com/kellylford/QuickMail/issues/528))

---

## Reporting Issues

Found a problem or have a suggestion? There are three ways to reach us — pick the one that fits:

1. **Report a Bug → Send** (Help menu, inside QuickMail). Files the report for you anonymously — it includes no email address or other identifying information, so there is no way to follow up with you. **Best when you don't want any follow-up.**
2. **Report a Bug → Copy report and open GitHub** (Help menu). Opens a pre-filled issue that you submit under your own GitHub account, so your GitHub contact information is attached. **Best when you have a GitHub account and want automatic filing plus direct contact.**
3. **Email** [quickmailissues@theideaplace.net](mailto:quickmailissues@theideaplace.net). **Best when you don't mind sending email and want a personal follow-up.**

Full details, including exactly what a report contains (and what it never contains), are in the [Reporting Issues section of the User Guide](https://kellylford.github.io/QuickMail/reporting-issues.html).
