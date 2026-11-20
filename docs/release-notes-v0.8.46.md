# QuickMail v0.8.46 Release Notes

This release is mostly about search. Search now reads the whole message, there is an Advanced Search form, and one search can cover every folder of every account and ask the mail server too. The command palette filters as you type, client-side rules can copy and do more than one thing, and background syncs no longer move focus.

## Added

### Search reads the whole message

The search box (**Ctrl+Shift+S**, or `/` in the message list) used to look only at what the message list shows. It now searches the whole message: its text, its Cc recipients and the names of its attachments. It works in any folder, including **All Inboxes** and **All Mail**, and still narrows the list as you type.

- Every word must be there, in any order, and a word also finds longer words that begin with it.
- Quotes keep a phrase together, and a minus sign leaves out messages with a word: `"quarterly report" -draft`.
- `from:`, `to:`, `cc:`, `subject:`, `body:` and `attachment:` look in one place.
- `has:attachment`, `is:unread`, `is:read`, `is:flagged`, `is:unflagged`, `after:`, `before:`, `folder:` and `account:` narrow the results.

Search uses the mail kept on this computer, so a message's text is found once it has been downloaded. The first time you start this version, QuickMail indexes the mail it already has in the background, newest first. Until that finishes, older messages are found by sender, recipients, subject and preview, as before.

The user guide's Searching section lists everything search understands.

[#717](https://github.com/kellylford/QuickMail/issues/717)

---

### Advanced Search, across every account

**Ctrl+/** (or **View → Advanced Search…**) opens a form, so you don't need the search syntax. It has a field for words anywhere, sender, To, Cc, subject, body and attachment name. You can also choose whether the message has attachments, is read or flagged, and when it arrived.

The form can search the folder you are in, or every folder of the accounts you choose. Searching the current folder fills in the search box, so you can see how the search is written. Searching accounts opens a **Search results** folder, which works like any other folder. A bar above the list says what was found, with **Change Search** (or **Ctrl+/**) and **Close** (or **Escape**).

- **Also search the mail server** asks each account's server for the same search, and adds what it finds. This reaches mail older than the sync range, or whose text was never downloaded. **Search the Server** on the results bar does the same after a search. Messages found this way are shown but not kept.
- **Save View…** on the results keeps the search as a view, with its own name and optional shortcut. Choosing the view runs the search again.

If nothing is found, the form stays open with focus in the first field.

[#717](https://github.com/kellylford/QuickMail/issues/717)

---

### More mail downloaded for offline reading

**Settings → General → Sync → Download messages for offline reading** used to stop at 90 days. It now also offers **6 months**, **1 year** and **All mail**. It never reaches past the **Sync range**.

A new option, **Include other folders, not just the Inbox**, also downloads Sent, Archive and your other folders, except Trash, Junk and Drafts. This is what lets search find words in sent and archived mail. It is off by default, because it can download a lot more.

A large download fills in gradually over many background checks, and the data file grows to match. The status bar shows progress, for example "Messages: 1,240 of 2,000 downloaded". It is between the rules status and the sync progress (**Ctrl+9**, then **Right**), and is not announced.

[#715](https://github.com/kellylford/QuickMail/issues/715), [#717](https://github.com/kellylford/QuickMail/issues/717)

---

### The command palette filters as you type

The command palette (**Ctrl+Shift+P**) now has a filter box. Typing narrows the list to matching commands, and the best match is at the top, ready for **Enter**.

A match can come from anywhere in the name. `arch` finds **Move to Archive**, `gtf` finds **Go to Folder** by its initials, `mail` lists the Mail commands, and `arch mail` works with the words in either order.

Focus stays in the filter box. **Up** and **Down** move through the list, and **Page Up** and **Page Down** move ten at a time. If the top match stays the same as you type, the new count is announced instead, such as "3 commands". **Escape** clears the filter, or closes the palette if the filter is empty.

This works in every command palette in the app, not only the main window's.

[PR #705](https://github.com/kellylford/QuickMail/pull/705)

---

### Client-side rules can copy, and can do more than one thing

A client-side rule used to do one thing: move, delete, mark as read or mark as unread. It can now also copy to a folder, and combine actions — for example, mark a mailing list read and file it. Actions always run in this order: mark read or unread, then copy, then move or delete.

**Save** refuses a combination that can't work, and says why:

- Marking a message both read and unread.
- Both moving and deleting.
- **Mark as unread** with a move or delete, since the move discards the local change.
- A client-side rule copying into the Inbox, which would copy the same message again and again.

A copy rule now needs at least one condition, as move and delete rules already do. This includes server-side copy rules, so an existing one with no conditions needs one before it can be saved again.

If a copy fails, the rule stops, and the message stays in the Inbox. The reason is in the log.

If you go back to an earlier version, a rule with several actions does at most one of them, and saving rules there drops the rest.

[#682](https://github.com/kellylford/QuickMail/issues/682)

---

## Fixed

### Background syncs no longer move focus

Two things could move focus with no key pressed:

- In the folder tree, focus could jump to **All Mail** when a sync started or an account connected.
- When messages were removed on the server, focus could jump from the folder tree into the message list.

Focus now stays where you put it. Opening a folder still moves focus to the message list, as before.

[#719](https://github.com/kellylford/QuickMail/issues/719)

---

### Client-side rules no longer miss mail that arrives while the Inbox is open

A rule could skip a new message if QuickMail picked it up another way first — usually by opening the Inbox, All Inboxes, All Mail or a saved view just as it arrived. The message was never checked by rules, and nothing was logged.

Rules now run on every message that arrives in your Inbox, however QuickMail first saw it. If the Inbox is open, a matching message may show briefly before the rule files it.

Rules still leave older mail alone, including mail that appears because you widened the **Sync range**. Mail already in your mailbox when you update counts as already handled.

Also, Microsoft 365 and POP3 messages now show whether they have attachments without being opened first, so filtering for attachments includes them.

[#712](https://github.com/kellylford/QuickMail/issues/712)

---

### A rules file QuickMail can't read is no longer replaced

If `rules.json`, the file that holds client-side rules, couldn't be read, QuickMail treated it as empty. The next rule you saved replaced the file, and every other client-side rule was lost.

QuickMail now never overwrites a rules file it couldn't read:

- The Rules Manager says "Couldn't load client-side rules", and why. A damaged file is named, with its folder.
- Saving a client-side rule is refused with the same reason, and the editor keeps what you typed.
- The status bar says "Client-side rules can't be read".
- Mail still arrives, but no client-side rule runs until the file can be read. After that, use **Run on Existing Mail** for anything that arrived in the meantime.

A file that was only locked for a moment recovers by itself. And any other failure to save a rule change, such as a full disk, is now reported instead of silent.

[#700](https://github.com/kellylford/QuickMail/issues/700)

---

### The rule editor's folder buttons say which folder is chosen

The move and copy folder buttons were always read as "Choose move-to folder", even when a folder was chosen. They now say "Move to folder: Digests" or "Copy to folder: Kept", and only ask you to choose when no folder is set.

[#713](https://github.com/kellylford/QuickMail/issues/713)

---

### Rule editor messages are said once

When the rule editor refused to save, its message was spoken twice: once by the editor and once by the Rules Manager. A missing-permission message was also spoken twice. Each is now spoken once.

[#701](https://github.com/kellylford/QuickMail/issues/701)

---

### Every item in the View, Sort and Help menus has its own access key

Three pairs of menu items shared an access key, so pressing it moved between them instead of choosing one.

- **View**: **Sync Range** is now Y. **Search Folders** keeps S.
- **View → Sort**: **Fewest Messages** is now W. **Newest First** keeps F.
- **Help**: **Get the ARM Version** is now V. **About QuickMail** keeps A.

[#695](https://github.com/kellylford/QuickMail/issues/695)

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
| [**QuickMail-0.8.46-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.46/QuickMail-0.8.46-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.46-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.46/QuickMail-0.8.46-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.46/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.46/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 8 runtime — you do not need to install .NET separately.
