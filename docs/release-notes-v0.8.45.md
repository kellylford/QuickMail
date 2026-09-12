# QuickMail v0.8.45 Release Notes

## Security: a crafted message could run script in the reading pane

**Please update.** A specially written HTML email could run its own JavaScript inside the component
that displays message bodies, on nothing more than opening the message. Script that ran this way
could read the message you were reading and send it elsewhere, and it could suppress the spoken
confirmations QuickMail delivers from inside a message — the invitation RSVP result, and the link
menu's report when a copy fails — so that an action appeared to have succeeded when it had not.

Message bodies are displayed under a Content Security Policy that forbids script, and a stripping
pass removes script blocks before they ever reach the display. Two flaws had to line up for either
to be got past, and both are fixed: the policy is now always written into the part of the document
where a browser reads it, and the stripping pass now recognises the ways of closing an element that
it previously did not.

It affects QuickMail 0.7.0 and later. It was found by review rather than by anything going wrong,
and was reported privately rather than published.

Found and reported privately by Timothy Spaulding, with a working proof of concept and a diagnosis
that named both flaws exactly.

---

## Fixed: every rule condition now has a checkbox

**Create Rule from Message** (`Ctrl+Shift+T`) filled in the sender *and* the subject of the message
you were reading, and the rule editor had no way to say which of them the rule was supposed to use.
Both were conditions, both had to match, so "Rule for someone@example.com" quietly became a rule
that matched that sender only when the subject was the exact line it was made from — in practice,
the one conversation you started from.

Each text condition in the editor now has its own checkbox in front of it: **From addresses**,
**Subject contains**, and the four under **Advanced conditions & actions** (**Sender contains**,
**Sent to addresses**, **Subject or body contains**, **Body contains**). A message has to satisfy
every condition you checked, so leaving one unchecked is how you say "don't care". Clearing a
checkbox keeps the text in its box — read-only and skipped by Tab, but one keystroke from being
part of the rule again, rather than something you have to retype.

Creating a rule from a message uses that: the sender arrives checked, and the subject arrives
**unchecked** with its text ready to switch on. So the rule you get by default is the one the name
says — everything from that sender.
([#665](https://github.com/kellylford/QuickMail/issues/665))

---

## Fixed: View Mode counts its choices correctly

**View → View Mode** offers four choices — Messages, Conversations, From, To — but arrowing
through them announced *"1 of 8"*, *"2 of 8"*, and so on. The menu actually held eight items: the
four above plus the four calendar views (Agenda, Day, Week, Month), which were hidden rather than
removed while you were reading mail. Hiding a menu item takes it off the screen but leaves it in
the menu, so it still counted.

The menu now holds only the choices that apply: the four mail views while you are in a mail
folder, and the four calendar views while the calendar is open. The count you hear matches what
is there.

The **View mode** button on the toolbar (`Ctrl+Shift+V`) drops the same list, so it is fixed the
same way — it was counting seven — and it gains **Month**, which it had been missing while the
calendar is open. Two View menu entries are renamed to match what the toolbar button, the folder
tree and the user guide have always called them: **By Sender** and **By Recipient** are now **From**
and **To**. **Settings → General → View → Display mode**, which sets the same four modes, follows
suit and reads **From (grouped by sender)** and **To (grouped by recipient)**.
([#663](https://github.com/kellylford/QuickMail/issues/663))

---

## Changed: one Rules window, whatever kind of account you have

QuickMail had two Rules windows behind the scenes, and which one you got depended on how the account
connected rather than on what it could actually do. An account connected through Microsoft 365 opened
a fuller window — the one that can also show rules that run on the server; an account connected the
ordinary IMAP way opened a simpler one.

A personal Outlook.com account connected through Microsoft 365 is the case that went wrong. It
connects the same way a work or school account does, so it was handed the fuller window — but a
personal account cannot have server-side rules, so in that window the Move Up and Move Down buttons
never came to life, and creating a rule popped up a box telling you it had been saved as a
client-side rule instead. Neither made sense for an account that was only ever going to run its
rules inside QuickMail.

There is now a single Rules window for every account. It shows what the selected account supports —
server-side rules for a work or school account, client-side rules for everyone else — and the window's
status line says which kinds the account can have, so an empty list is never a mystery. **F6** cycles
the panes — account list, rules, details, status line — so it reaches that status line and reads it
back at any time.

That replaces a spoken message on every account you landed on, which meant arrowing down the account
list said a sentence about each account you passed through on the way to the one you wanted. The
pop-up box after saving a rule is gone as well; on an account that has no server rules there was
never anything surprising to report.

One place still speaks up: on a work or school account, a rule that uses something only QuickMail can
do — marking a message unread, say — is saved as a client-side rule rather than a server-side one, and
because that account *does* also do server rules, QuickMail announces that when it happens. The rule's
own row reads **on client** regardless, so where it went is there to read with announcements off.

The single window also brings back a safeguard the old one had. A **Move to folder** or **Delete** rule
must now have at least one condition before it can be saved: with none, a rule matches every message,
and rules run on mail as it arrives and through **Run on Existing Mail**, so it would empty the Inbox.
This covers server-side rules too, since Exchange treats a rule with no conditions the same way. A
server-side rule made in Outlook with no conditions still runs, and can still be turned on or off from
QuickMail, but QuickMail asks for a condition before it will save a change to it.

Which account a new rule belongs to is now decided by where you are. A new rule belongs to the account
the Rules Manager is showing, and it opens on the account of the folder or view you are in; from a view
that spans accounts, such as **All Inboxes**, it opens on your default account. Before, a profile with
no Microsoft 365 account got a window that gave every new rule your default account, and a profile with
one got a window that opened on whichever account you had last visited or read a message from.

In the rules list, **Space** on a rule reliably turns it on or off. And **Test**, which checks a rule
against the messages in the message list, now says so instead of calling them your selected messages.
([#550](https://github.com/kellylford/QuickMail/issues/550))

---

## Changed: a shared mailbox's rules are left to Outlook

QuickMail cannot reach a shared mailbox's server-side rules: signing in as yourself does not give access
to another mailbox's rules, and no permission QuickMail can ask for changes that. Opening the Rules Manager
on a shared mailbox used to ask you to get an administrator to grant a permission that was already
granted, and that would not have helped if granted again.

A shared mailbox is no longer in the Rules Manager's **Account** list. If you open the Rules Manager from
one, the window shows your default account instead, names it in the title, and says on its status line
that the shared mailbox's rules are managed in Outlook. **Create Rule from Message** is not in the context
menu for a shared mailbox's messages, and **Ctrl+Shift+T** and the command palette say why instead of
making a rule. In the mailbox's folders the rule summary on the status bar says its rules are managed in Outlook instead of
counting rules. Everywhere else the summary now says it counts client-side rules, because server-side
rules were never in its count. And a client-side rule saved on a shared mailbox by an earlier version is kept
but no longer runs: it acted, from one person's computer only, on mail everyone with access to the mailbox reads.
([#678](https://github.com/kellylford/QuickMail/issues/678))

---

## Fixed: deleting a message no longer talks over the next one

Pressing Delete on a message could produce a spoken **"unavailable"** before the next message was
read, and the confirmation that followed could cut that reading off. Two separate faults, both now
fixed.

The **"unavailable"** came from the order things happened in. The deleted row was taken out of the
list while it still held keyboard focus, which leaves focus on a row that no longer exists —
Windows then describes that row as a disabled control, and a screen reader says so. Focus now moves
to the message you are about to land on *before* the deleted one leaves the list, so there is never
a moment where the focused row is a row that has gone. Delete and Archive both do this.

The confirmation was the second half. Deleting one message announced **"1 message deleted"** a
moment after the next message started being read — telling you something you had just been told,
by interrupting the sentence that told you. **A single delete or archive now says nothing.** The
row is gone and the next one is read: that is the confirmation.

What still speaks is anything you could not otherwise know: a count when you acted on several at
once ("3 messages deleted"), "Folder is now empty" when the last one goes, and every failure. All
of it still appears in the status bar, and `Ctrl+9` reads the status bar on demand.

This is not the announcement setting doing its job — **Settings → Accessibility → Announce delete
and archive actions** is still on by default and still controls the announcements that remain.
Nothing to turn off, and nothing to turn back on.
([#667](https://github.com/kellylford/QuickMail/issues/667))

---

## Fixed: Shift+F10 while reading no longer throws you back to the message list

Pressing **Shift+F10** with focus in the message body moved focus out of the message and back to
the message list. Nothing had closed, but there was no way to tell that from the outside: you were
reading, and then you were on the list.

Windows reports "no element has focus" in two unrelated situations — at startup, before any pane
has been focused, and whenever focus is inside the message body, which sits in a separate window of
its own underneath. QuickMail had a piece of startup repair that read the second as the first, and
moved focus to the message list to correct a problem that was not happening.

Reading a message is now told apart from having nothing focused, so focus stays where you are
reading. On ordinary body text the key now does nothing, rather than doing the wrong thing; on a
link it opens a menu — see **a context menu on links in a message**, below.

Shift+F10 and the Applications key on the message list, the folder tree, and the attachment list
are unaffected and open the same menus as before, including on the first press after launch.
([#672](https://github.com/kellylford/QuickMail/issues/672))

---

## Added: a context menu on links in a message

Pressing **Shift+F10** or the Applications key on a link in a message did nothing. The link could
be opened with Enter, but there was no way to copy where it went — or to find out where it went
without going there.

Links in a message now have a context menu, reached with **Shift+F10**, the Applications key, or a
right-click:

- **Open** — the same as pressing Enter. A web link opens in your default browser; an email address
  link goes to whichever mail program Windows has registered, which is what **New Message
  to This Address**, below, avoids.
- **Copy Address** — puts the destination on the clipboard.
- **Copy Text** — puts the link's own wording on the clipboard. Offered only when that wording is
  not simply the address again, so an auto-linked address does not present the same item twice.
- **New Message to This Address** — last, and only on an email address link. Opens a new message in
  QuickMail rather than handing the address to whichever mail client Windows has registered.

A copy that works says nothing — it did what you asked. A copy that fails writes a line at the end of
the message, starting **QuickMail:**, and in the reading pane also puts it in the status bar where
**Ctrl+9** reads it back. That line is not usually spoken as it appears, because it is written just as
the menu closes and your screen reader is already announcing its way back into the message; it is
there to be found rather than to interrupt.

Copying the address is how you check where a link goes before following it, and comparing it with
the link's text is how you spot a link that does not go where it says it does.

Escape closes the menu and leaves you exactly where you were — on the link you opened it on, not at
the top of the message and not back on the message list. The menu works the same way in the reading
pane, in a message tab, and in a message window. It is offered for ordinary web and email links,
and not for the **Accept** / **Tentative** / **Decline** buttons on a meeting invitation, which are
internal to QuickMail; on ordinary body text the key does nothing, as before.
([#671](https://github.com/kellylford/QuickMail/issues/671))

---

## Fixed: F6 out of an open message goes to the next pane

Pressing **F6** while reading a message skipped ahead: instead of moving to the pane after the
reading pane, it carried on from the toolbar and landed on the account list. Coming back the other
way, focus was not returned to the message after changing the view mode.

Windows reports no focused element while the message body has focus — the message is drawn by a
separate component with its own window, so the focus QuickMail can see has moved outside its own
controls. Two pieces of code asked the question in a way that could not be true at the time they
asked it, so the reading pane was never recognised as the pane you were in.

Both now use the same test, and it is one that works while you are reading.
([#673](https://github.com/kellylford/QuickMail/issues/673))

---

## Fixed: rules are saved, edited and run as you set them up

- **A new rule belongs to the account its editor opened on.** The Rules Manager's **Account** list
  stays usable while a rule's editor is open. If you changed it before saving, the rule was saved to
  the first account but made server-side or client-side according to the second, and the folder list
  offered the second account's folders. The editor now uses the account it opened on throughout, and when you
  save, the list goes back to that account and selects the rule, and the status line says where it was
  saved.
  ([#683](https://github.com/kellylford/QuickMail/issues/683))
- **Editing a server-side rule no longer drops Mark as unread without a word.** A server-side rule
  can't mark mail unread, so ticking it and saving closed the editor as if it had worked and saved the
  rule without it. **Mark as unread** is now unavailable while you edit a server-side rule.
  ([#684](https://github.com/kellylford/QuickMail/issues/684))
- **Run on Existing Mail acts on each message once.** When two rules matched the same message, a
  later rule could act again on a message an earlier rule had already moved or deleted, and the
  message was counted twice. A moved or deleted message is now out of the running for the rules after
  it, as it already was for mail as it arrives.
  ([#685](https://github.com/kellylford/QuickMail/issues/685))

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
| [**QuickMail-0.8.45-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.45/QuickMail-0.8.45-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.45-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.45/QuickMail-0.8.45-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.45/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.45/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 8 runtime — you do not need to install .NET separately.
