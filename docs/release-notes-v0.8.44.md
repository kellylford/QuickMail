# QuickMail v0.8.44 Release Notes

## New: writing mail works without a connection

Until now a dropped connection could trap a message in the compose window. Saving a draft was
always a trip to the server, so on a flaky airport connection it failed; sending failed the same
way; and a window with unsaved changes refused to close after a failed save, leaving you holding a
message you could not keep and could not let go of.

QuickMail now tries your account first and falls back to this computer only when the server does
not answer:

- **Save Draft** (`Ctrl+S`) and auto-save keep the draft on this computer when the server cannot be
  reached. You hear *"Draft saved on this computer. It will upload when you're online."* The draft
  goes to your account's Drafts folder the next time QuickMail connects. Auto-save shows *"Kept on
  this computer 3:42 PM"* in its status line and says so once.
- **Send** (`Alt+S`) queues the message when the server cannot be reached. You hear *"Message
  queued. It will be sent when you're online."*, the window closes, and the message leaves the
  moment a connection returns. A server that answers and refuses the message — a bad address, a
  rejected login — still fails in the window, where you can fix it.
- **Closing** a window with unsaved changes and choosing Save keeps the draft on this computer if
  the server is unreachable, and the window closes. It stays open only when the draft could be
  saved nowhere at all.

Everything waiting lives in a new **Outbox**, the last entry in the All Mail group of the folder
tree. Its count is spoken as *"waiting"*, not *"unread"*. Each row shows the account the message
will leave from, and its subject starts with its state: *"Waiting to send"*, *"Waiting to upload
draft"*, *"Sending…"*, or *"Failed"* followed by the server's reason. Press `Enter` on a row to
reopen it in the compose window exactly as you wrote it — recipients, Bcc, attachments and compose
mode included; saving or sending from there replaces the queued copy. Press `Delete` to remove one;
QuickMail asks first, because there is no Trash to get it back from. **Send Outbox Now** (Message
menu, or the command palette) tries everything right away, including anything marked Failed.

The Outbox drains on its own when QuickMail connects at launch, when the connection returns, and on
each background check for new mail. A drain is announced once as a whole — *"Outbox: 2 messages
sent, 1 draft uploaded."* A message the server refused stays in the Outbox marked Failed with the
reason until you reopen and fix it, or remove it. A message you have reopened is never sent from
under you while its window is open. The Outbox is not available when QuickMail is started with
`--online`, which runs without the local store.
([#637](https://github.com/kellylford/QuickMail/issues/637))

## New: QuickMail knows when it is offline, and says so

QuickMail keeps a copy of your recent mail on this computer, and when the connection drops you keep
reading it. The connection label in the status bar now reads **Offline**, and screen readers hear
*"Offline. Showing cached messages."* once, and *"Back online."* when the connection returns.
QuickMail waits a few seconds before saying Offline, so a momentary blip never interrupts you.

Opening a folder offline shows what is cached with plain wording — *"Offline — showing 12 cached
messages."* or *"Offline — no cached messages in Projects."* — instead of a raw network error, and
it never sits on *"Loading…"*. A message that was never downloaded says *"This message is not
available offline."*; Reply and Forward on it say the same rather than silently doing nothing;
attachments say *"Attachments are not available offline."*

Getting back online needs nothing from you. When Windows reports the network again QuickMail
reconnects every account by itself; when the network is up but nothing answers — a hotel or airport
sign-in page, an outage at the provider — it keeps trying on its own, first every half minute and
then every five, until something does. `F5` forces an attempt at any time. Once something answers,
the folder you are looking at refreshes and anything waiting in the Outbox goes out. A sign-in you
have to do yourself, or a missing password, is reported as exactly that and never as being offline.

Launching with no network no longer spends up to three minutes on *"Connecting…"* before admitting
it; it says Offline at once and shows your cached mail. ([#637](https://github.com/kellylford/QuickMail/issues/637))

## New: keep recent messages for reading offline

By default QuickMail keeps the full text of a message only once you have opened it, or when it
fetched a few ahead of time. A new setting, **Settings → General → Sync → Download messages for
offline reading**, keeps more ready before the connection drops: **Off** (the default), or the last
**7**, **30** or **90** days. With a window set, the sync at launch and each background check finish
by downloading the text of each Inbox message in that window that QuickMail does not have yet,
newest first, and new mail gets its text as it arrives. When a pass completes you hear *"Downloaded
120 messages for offline reading."* once.

Inbox only; never wider than the sync range above it; attachments are not included and still need
a connection; POP3 accounts already keep every message whole. The text lives in `mail.db` in
QuickMail's data folder, which grows accordingly.
([#637](https://github.com/kellylford/QuickMail/issues/637))

## Fixed: two cache fallbacks that skipped the server

Opening a message, and the fetch behind Reply and Forward, both read QuickMail's local copy first
and went to the server only if there was none. A failure reading the local copy — every read fails
in `--online` mode — skipped the server too, so the message did not open at all. Both now fall
through to the server as intended. ([#637](https://github.com/kellylford/QuickMail/issues/637))

## Fixed: the address book reads out contacts again

Moving through the list of contacts in the address book announced every row as
*"QuickMail.Models.ContactModel"* followed by its position — the same thing for all 35 of your
contacts, with no way to tell one from the next except by opening it. The list of a group's
members was silent in exactly the same way. The Groups list happened to escape it, but only by
accident: it had the same underlying fault and was one small edit away from going silent too.

The rows carried the right text all along; it was attached to the wrong part of the row. Screen
readers read a list row's name from the row itself, and the address book had put the name on
something drawn inside the row instead — which looks identical on screen, and is why this went
unnoticed. Every other list in QuickMail already put it in the right place; these three now do
too.

A contact row reads as its name, its address, and where it came from — *"Alice Adams,
alice@example.com, Local address book"* — or with each part labelled, if you have turned on field
labels in the contact list. A contact saved with no name reads as its address and where it came
from. A group reads as its name and how many members it has, and a group's member reads as name
and address.

As a safety net for any list of contacts anywhere in QuickMail, a contact with nothing else to say
for itself now falls back to reading its name and address rather than its internal type name, so
this particular kind of silence cannot come back through some other list.
([#644](https://github.com/kellylford/QuickMail/issues/644))

---

## Reporting Issues

Found a problem or have a suggestion? There are three ways to reach us — pick the one that fits:

1. **Report a Bug → Send** (Help menu, inside QuickMail). Files the report for you anonymously — it includes no email address or other identifying information, so there is no way to follow up with you. **Best when you don't want any follow-up.**
2. **Report a Bug → Copy report and open GitHub** (Help menu). Opens a pre-filled issue that you submit under your own GitHub account, so your GitHub contact information is attached. **Best when you have a GitHub account and want automatic filing plus direct contact.**
3. **Email** [support@theideaplace.net](mailto:support@theideaplace.net). **Best when you don't mind sending email and want a personal follow-up.**

Full details, including exactly what a report contains (and what it never contains), are in the [Reporting Issues section of the User Guide](https://kellylford.github.io/QuickMail/reporting-issues.html).

---

## Download

There are four downloads. Take a regular one unless you know your PC has an ARM processor — to check, open **Settings → System → About** and read **System type**.

| Download | When to use |
|----------|-------------|
| [**QuickMail-0.8.44-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.44/QuickMail-0.8.44-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.44-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.44/QuickMail-0.8.44-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.44/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.44/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 8 runtime — you do not need to install .NET separately.
