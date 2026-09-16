# QuickMail v0.8.46 Release Notes

<!-- What is new in 0.8.46 goes here, above the footers: Changed/Fixed/Added sections, each ending with its issue link and a --- divider. -->

## Added

### The command palette filters as you type

The command palette (**Ctrl+Shift+P**) opens with a filter box, and typing narrows the list to what matches instead of only jumping to a name that starts with what you typed.

You do not have to know how a command's name begins. Typing `arch` finds **Move to Archive**; `gtf` finds **Go to Folder** from its initials; `mail` lists the Mail commands by category; and `arch mail` finds the Mail category's Archive command with the words in either order. The best match is always at the top and is what **Enter** runs.

Focus stays in the filter box the whole time, so you can keep typing to narrow further without moving anywhere first. The command at the top is reported as it changes, and so is each command you reach with **Up** and **Down** — by Windows itself, from the list, rather than by QuickMail announcing over the top of it, so no announcement setting can silence it. When a keystroke narrows the list without changing the command at the top, you hear the new count on its own — "3 commands" — since nothing else would tell you the list had moved under you.

**Escape** clears the filter if you have typed something and closes the palette if the box is already empty. **Page Up** and **Page Down** move ten at a time. When nothing matches you hear "No matching commands", and Enter says so rather than doing nothing silently.

This applies to every palette in the app, not just the main window's — the compose window, an open message, the address book, the rules window and the rest all get it.

A search box shipped here once before and was taken out again, because it moved focus onto the list on every keystroke and announced a new top match on every character. This one does neither: focus never leaves the box, and nothing is announced that Windows already reports.

---

### Client-side rules can copy, and can do more than one thing

A client-side rule used to do exactly one thing — move, delete, mark as read, or mark as unread — and **Save** refused one that used **Copy to folder**. Now a client-side rule can copy, and can combine its actions: mark a mailing list read and file it, say, or keep a copy in one folder and move the message to another. It always does them in the same order: marking read or unread first, then copying, then moving or deleting, since after that the message is no longer in the Inbox.

Some combinations are refused, with Save saying why: marking a message both read and unread; in a client-side rule, both moving and deleting it; and **Mark as unread** together with moving or deleting, because marking unread changes only this computer's copy of the message, which the move then throws away.

A rule that copies now needs at least one condition, as moving and deleting already did. Without one it would copy every message that arrives, and copy your whole Inbox again on every **Run on Existing Mail**. This covers server-side copy rules as well, so an existing one with no conditions has to be given one before it can be saved again.

A client-side rule also cannot copy into the Inbox. It runs on the Inbox, so each copy would land where the rule is looking and be copied again on the next check.

If a copy cannot be made — the folder is gone, say — the rule stops there, leaving the message in the Inbox rather than filing it with no copy kept anywhere. Whatever the rule did before the copy, such as marking the message read, has already happened, and the reason the copy failed is in the log.

An earlier version of QuickMail reading a rule with more than one action does the one action it understands best: the move, or the marking. A rule that only copies — or that copies and then deletes — does nothing there, rather than deleting the message without keeping the copy. If that earlier version then saves your rules, the extra actions are dropped from them.

[#682](https://github.com/kellylford/QuickMail/issues/682)

---

### Download a year, or all of your Inbox, for offline reading

**Settings → General → Sync → Download messages for offline reading** used to stop at the last 90 days. It now also offers the last **6 months**, the last **year**, and **All mail**, matching the longer choices of the **Sync range** above it.

As before, it only downloads the text of Inbox messages, and never reaches further back than the Sync range, so **All mail** under a one-year Sync range keeps a year. A large Inbox fills in gradually: each pass downloads up to 500 messages per Inbox, newest first, so a year or all mail can take many background checks to finish, and the data file grows to match — hundreds of megabytes or more for a busy Inbox.

In `config.ini`, **All mail** is saved as `OfflineBodyDays = -1`, since `0` already means off.

[#715](https://github.com/kellylford/QuickMail/issues/715)

---

### Search reads the whole message

The search box (**Ctrl+Shift+S**, or `/` in the message list) used to look only at what the message list shows: the sender, the recipients, the subject and the first line or so of the preview. It now searches the whole message — its text, its Cc recipients and the names of its attachments — for every message in the folder you are in, including combined folders such as **All Inboxes** and **All Mail**. It still narrows the list as you type.

A search can also say where to look and which messages to keep:

- Words must all be there, in any order, and a word finds the words that begin with it. Quotes keep a phrase together, and a minus sign leaves out messages containing a word: `"quarterly report" -draft`.
- `from:`, `to:`, `cc:`, `subject:`, `body:` and `attachment:` look for a word in one place.
- `has:attachment`, `is:unread`, `is:read`, `is:flagged`, `is:unflagged`, `after:`, `before:`, `folder:` and `account:` narrow the results.

Search looks at the copy of your mail kept on this computer, so a message's text is found once it has been downloaded — opened, fetched ahead of time, or inside **Download messages for offline reading**. The first time you start this version, QuickMail indexes the mail it already has in the background, newest first; until it finishes, older messages are found by their sender, recipients, subject and preview, as before.

The user guide's Searching section lists everything search understands.

[#717](https://github.com/kellylford/QuickMail/issues/717)

---

## Fixed

### Client-side rules no longer miss mail that arrives while the Inbox is open

A client-side rule could silently skip a message. If the message reached QuickMail first by another route — most often opening or returning to the Inbox just as it arrived, but also All Inboxes, All Mail or a saved view — the next background check found it already stored, took it for mail it had already handled, and no rule ever ran on it. Nothing was logged, and the message was never looked at again: **Run on Existing Mail** was the only way to apply your rules to it.

QuickMail now keeps track of which messages your rules have run on separately from which messages it has stored, so a rule runs on each message that arrives in your Inbox, however QuickMail first picked it up. If the Inbox is open when a matching message lands, the message can appear in the list for a moment before the rule files it.

Rules still leave older mail alone. That includes mail QuickMail shows you for the first time because you widened the **Sync range**, and, on a Microsoft 365 account, a message moved into the Inbox from another program: it keeps the time it first arrived, so rules leave it alone unless that was within about an hour of your newest mail. Mail already in your mailbox when you update counts as already handled.

Microsoft 365 and POP3 messages QuickMail lists from its cache now show whether they have attachments, and are included when you filter for messages with attachments, without having to be opened first.

[#712](https://github.com/kellylford/QuickMail/issues/712)

---

### The rule editor's folder buttons say which folder is chosen

A rule that moves or copies to a folder shows that folder on the button beside the action — but the button was still reported as "Choose move-to folder" whichever folder was chosen, so a rule that already had one sounded as though it had none. That happened both right after choosing a folder and when reopening a saved rule; activating the button did land on the right folder.

The button is now reported as "Move to folder: Digests" (and "Copy to folder: Kept"), and says "Choose move-to folder" only while no folder is chosen.

[#713](https://github.com/kellylford/QuickMail/issues/713)

---

### A rules file QuickMail can't read is no longer replaced

QuickMail keeps your client-side rules in a file called `rules.json`. When that file couldn't be read — damaged, or held open by another program at the wrong moment — QuickMail treated it as holding no rules, and the next client-side rule you saved went into a new file with only that rule in it. Every other client-side rule was gone.

Now QuickMail never writes over a rules file it couldn't read, and says what happened:

- The Rules Manager's status line says "Couldn't load client-side rules" and why, instead of reading as though the account had none. A damaged file is named along with the folder it is in, so you can repair or remove it.
- Saving a client-side rule is refused with the same reason, and the editor stays open with what you typed.
- The status bar says "Client-side rules can't be read", instead of "No active client-side rules". It checks again when you change folder or close the Rules Manager.
- Mail keeps arriving and showing as normal. No client-side rule runs on it until the file can be read, so once it can, use **Run on Existing Mail** on each account for anything that arrived meanwhile.

A file that was only held open for a moment is read again the next time QuickMail needs it, so it recovers by itself.

A client-side rule change that can't be saved for any other reason — a full disk, say — now says so, where it used to fail without a word. That covers turning a rule on or off and deleting one, as well as saving from the editor.

[#700](https://github.com/kellylford/QuickMail/issues/700)

---

### Messages in the rule editor are said once

When the rule editor refused to save — a rule with no name, say, or one using something a client-side rule can't do — its message was announced twice: once by the editor and again by the Rules Manager behind it. Now it is said once: by the editor while it is open, or by the Rules Manager if you have already closed the editor when saving to your work or school Microsoft 365 account fails.

A rule change refused because QuickMail doesn't have a permission it needs was also said twice, as a hint and as a result, even from the Rules Manager alone. It is now said once, as a result, and still shown on the Rules Manager's status line.

[#701](https://github.com/kellylford/QuickMail/issues/701)

---

### Each item in the View, Sort and Help menus has its own access key

Three more pairs of menu items shared an access key, so pressing it moved between the two instead of choosing either — the same fault fixed in the message menus in 0.8.45.

- **View** menu: **Sync Range** and **Search Folders** were both on S. **Sync Range** now uses Y; **Search Folders** keeps S.
- **View → Sort**: **Newest First** and **Fewest Messages** were both on F. **Fewest Messages** now uses W; **Newest First** keeps F.
- **Help** menu: **Get the ARM Version** and **About QuickMail** were both on A. **Get the ARM Version** now uses V; **About QuickMail** keeps A.

Nothing about what the menus say has changed — only which letter each item answers to.

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
