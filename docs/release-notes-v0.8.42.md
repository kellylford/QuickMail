# QuickMail v0.8.42 Release Notes

## New: a key that goes straight to the next unread message

A user wrote in asking for one, mentioning that Space did this in the mail program they came from.
Until now the only way to find unread mail in a long folder was to arrow through everything you had
already read.

**Alt+Down** goes to the nearest unread message below where you are. **Alt+Up** goes to the nearest
one above. Both are also in the Command Palette (**Ctrl+Shift+P**) as **Next Unread Message** and
**Previous Unread Message**, and either can be given a key of your own in **File → Settings →
Keyboard Shortcuts**.

They work in every view. In **Messages** they move down or up the list, skipping what you have read.
In **Conversations**, **From**, and **To** they run through every message the tree holds, in group
order, so they cross from one conversation or sender into the next rather than stopping at the end of
the one you are in.

Three details worth knowing:

- **A closed group is searched, and opened.** An unread message inside a conversation you have not
  opened is still found, and the conversation opens so that message can take focus. It stays open
  afterwards, the same as one you opened yourself.
- **Neither wraps around.** Reaching the end says "No unread messages below" — QuickMail does not
  quietly start again from the top, and nothing moving is never left unexplained.
- **They stay out of the way of everything else.** The keys act while you are in the message list or
  a group tree — the reading pane can be open beside it — so Alt+Down still opens a drop-down list
  when that is what you are on. They do nothing while focus is inside the reading pane itself, and an
  open message window has its own keys: Alt+Left and Alt+Right already move between messages there.
  Started from the palette where there is no list to move through, they say why instead of going
  quiet.

([#617](https://github.com/kellylford/QuickMail/issues/617))

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

## Fixed: the mouse did nothing in the folder tree

Selecting a folder with the mouse left the folder highlighted and nothing else: the message list
went on showing the folder you came from, and moving back to the tree with **F6** put the highlight
back on the folder that was really open, as though the mouse had never been used. Reported by a
sighted user as *"clicking on folders does not select the folders"*, which is exactly what it
looked like.

The cause was that QuickMail is built keyboard-first, and every one of these lists was wired to
**Enter** only. They all respond to the mouse now:

- **Folders** open on a single click, the same as Enter — with the difference that Enter also moves
  you on to the message list, and a click leaves you looking at the tree you just used.
- **Accounts** connect on a single click.
- **Messages in the Conversations, From and To views** open on a single click, which they already
  did in the plain message list. A conversation holding a single message opens that message, the
  same as pressing Enter on it.
- **Attachments** open on a double click, in both the reading pane and an open message window.

Three smaller faults in the message list went with them, all of them about a click that meant
something other than "open this". Selecting several messages with **Ctrl** or **Shift** held used to
open each one as you added it — in Window mode, a window per message on the way to deleting them.
Dragging across several messages to select them used to open the last one, which dropped the
selection back to that message, so the Delete that followed deleted one message instead of the five
you had selected. And a click on the empty space below the last message re-opened whichever message
was selected. All three now leave the selection alone.

Nothing about the keyboard changed: no shortcut, no announcement, and no reading order is different
from 0.8.41. ([#601](https://github.com/kellylford/QuickMail/pull/601))

## Fixed: typing a date or a time in the appointment editor did nothing at first

Pressing **N** for a new appointment and typing a date into the **Starts** field left the field
showing the date it had opened on. The same happened in the time field. As the report put it:
*"The date field didn't change. The same thing happened in the time field. You have to move the
field with an arrow key or select it all before it takes any input."*

These fields open holding a full, spelled-out value — "Thursday, July 16, 2026" — with the cursor
parked at the end of it and nothing selected. Typing `8/3` therefore made
"Thursday, July 16, 20268/3", which means nothing as a date, so QuickMail quietly put the old value
back. Nothing was said about it either: the value it restored was the one already there, so as far
as the field was concerned nothing had changed.

The value is now selected when you arrive at the field, so the first thing you type replaces it —
the same as what already happened when a refused save sent you back to a field to correct it.
Typing `8/3`, `tomorrow`, or `+7` works from the first keystroke. The arrow keys are unchanged: Up
and Down still step the value by a day or a quarter hour and leave it unselected with the cursor at
the end, so what you hear afterwards is the new value and not a selection.

Leaving a field part-way through typing and coming back to it — opening the Command Palette over
the editor, or switching to another window and back — keeps what you had typed, and keeps the
cursor where you left it.

This applies everywhere these fields are used — the **Starts** and **Ends** dates and times, the
repeat interval and **until** date, and **Go to Date** (**Ctrl+G**) in the calendar.
([#570](https://github.com/kellylford/QuickMail/issues/570))

## New: choose which calendar an appointment goes on

Until now an appointment saved to a Microsoft or Google account always landed on that account's
default calendar. There was no way to file one on **Team**, or on **Family**, short of moving it
afterwards in Outlook or Gmail. iCloud accounts have offered a choice for a while; the other two
now do too.

The **Calendar** picker in the appointment editor lists your **Local Calendar** first, then every
calendar you can write to on each connected account, named account-first: **Work: Team**,
**Apple: Family**. Microsoft and Google accounts also get an entry for the account itself —
**Work (default calendar)** — for when you do not mind which calendar it lands on and would rather
it follow whatever you have set on the provider's own site. iCloud has no such entry, because an
iCloud appointment always names the calendar it goes on.

The picker still starts on your default calendar if you have set one, whether you set that to a
whole account or to a single calendar under it.

Two things worth knowing:

- **Calendars you only subscribe to are left out.** A holidays feed, or someone else's calendar
  shared with you to read, cannot have appointments added to it, so offering it could only lead to a
  save that was refused. They keep their place in the folder tree, which is where reading them
  happens.
- **A calendar you have not put anything in yet is offered like any other.** QuickMail now keeps
  each account's real list of calendars rather than working out which calendars exist from the
  appointments already in them — so an empty calendar is somewhere you can file the first one.
  Those calendars also appear in the folder tree now, where before an empty one was missing
  entirely.

([#569](https://github.com/kellylford/QuickMail/issues/569))

## Fixed: a new appointment was missing from the calendar it was filed on

If you had one of your calendars selected in the folder tree — not **Calendar** at the top, but a
particular calendar underneath it — an appointment you created went in, and then was not in the
list. Pressing **F5** brought it into view. Reported twice, once as *"I added a new event in my
default Gmail calendar. I looked for it and it didn't show up. I pressed F5 to refresh and then it
showed up."*

The appointment was saved correctly the whole time; it just was not labelled with the calendar it
had gone onto, so the list you were looking at filtered it out. The next sync relabelled it and it
appeared. QuickMail now labels a new appointment with its calendar as soon as it saves it, so it is
in the list straight away — and the **Calendar** column reads correctly instead of coming up blank.

The same fault had a second form on Microsoft 365 accounts: *editing* an appointment stripped the
label off one that already had it, so an appointment you had just edited dropped out of the list
until the next sync. Edits keep the label now, as they already did on Google accounts.

This is one half of a complaint that came in twice. Both halves looked identical from the outside —
an appointment missing until F5 — but they had different causes. This one is about an appointment
you created in QuickMail; the other, **"the calendar did not ask the server when you opened it"**
below, is about one you added somewhere else.
([#569](https://github.com/kellylford/QuickMail/issues/569),
[#519](https://github.com/kellylford/QuickMail/issues/519))

## Fixed: the calendar did not ask the server when you opened it

The other half of the complaint described under **"a new appointment was missing from the calendar
it was filed on"** above: the same symptom, a different cause. An appointment
added somewhere other than QuickMail — in Gmail or Outlook on the web, or on a phone — was not in
the agenda when you opened the calendar. It turned up on its own eventually, because
QuickMail checks connected calendars every fifteen minutes, but until then **F5** was the only way
to see it. The report was precise about it: *"I added an appointment to my Gmail calendar and it
didn't show in the agenda view... Press F5 to refresh the list. Now it will show up."*

Opening the calendar now asks your accounts as well. The list still appears immediately from what
QuickMail already had — you are never left waiting on the network to see your own calendar — and
anything new folds in when the answer arrives.

Three details worth knowing:

- **It speaks only if something changed.** Opening the calendar already tells you which view you
  are in and how many events it holds, so repeating that number a moment later would be chatter —
  and in the ordinary case, where your accounts had nothing new, you hear nothing at all. When the
  check does bring an appointment down, the count you were just given is out of date, so QuickMail
  says the new one. The fifteen-minute background check announces itself as it always did.
- **You keep your place.** When a check lands while you are reading, the appointment you were on
  stays selected — and in Month view, so does the day you had moved to. Until now a check arriving
  mid-read could drop you back to the top of the list, or back onto today in the month grid,
  because the events had been rebuilt underneath you.
- **Opening it again does not ask again.** Going out to your mail and straight back reuses what was
  just fetched; there is a half-minute gap before another check.

([#519](https://github.com/kellylford/QuickMail/issues/519))

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
