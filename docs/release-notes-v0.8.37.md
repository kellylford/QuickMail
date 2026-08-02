# QuickMail v0.8.37 Release Notes

This is a large release — the most change in a single version since the calendar arrived in v0.8.33. Adding an account has been rebuilt around three questions instead of a page of server settings. You can now decide what each row of a message list says out loud, and in what order. Microsoft 365 accounts get a run of fixes, mail rules change in when they run and which folders they act on, and there are new tools for working out what is happening when a connection misbehaves.

The last public release was **v0.8.36**, so if that is what you have been running, everything below is new to you.

## Contents

- [Download](#download)
- [Accounts and signing in](#accounts-and-signing-in)
  - [New: adding an account now asks three questions, not ten](#new-adding-an-account-now-asks-three-questions-not-ten)
  - [Changed: Google sign-in for Gmail is now something you turn on](#changed-google-sign-in-for-gmail-is-now-something-you-turn-on)
- [Microsoft 365 accounts](#microsoft-365-accounts)
  - [Fixed: deleted and moved mail no longer comes back](#fixed-deleted-and-moved-mail-no-longer-comes-back)
  - [Fixed: the same message no longer appears twice in combined views](#fixed-the-same-message-no-longer-appears-twice-in-combined-views)
  - [Fixed: a folder no longer stops loading because of one message](#fixed-a-folder-no-longer-stops-loading-because-of-one-message)
  - [Fixed: adding a work or school account now always asks your permission](#fixed-adding-a-work-or-school-account-now-always-asks-your-permission)
- [Reading and organizing mail](#reading-and-organizing-mail)
  - [New: choose what a message list row says, and in what order](#new-choose-what-a-message-list-row-says-and-in-what-order)
  - [New: combined views say which folder a message is in](#new-combined-views-say-which-folder-a-message-is-in)
  - [New: All Archive](#new-all-archive)
  - [Fixed: attachments were unreachable when a message opens in its own window](#fixed-attachments-were-unreachable-when-a-message-opens-in-its-own-window)
- [Mail rules](#mail-rules)
  - [Changed: rules are now per-account](#changed-rules-are-now-per-account)
  - [Changed: rules run on mail that arrives while QuickMail is open](#changed-rules-run-on-mail-that-arrives-while-quickmail-is-open)
  - [Changed: rules act on the Inbox, and only on new arrivals](#changed-rules-act-on-the-inbox-and-only-on-new-arrivals)
  - [Fixed: the rules editor no longer offers actions with nothing to act on](#fixed-the-rules-editor-no-longer-offers-actions-with-nothing-to-act-on)
- [Writing and sending mail](#writing-and-sending-mail)
  - [Fixed: choosing a sending account could send the message](#fixed-choosing-a-sending-account-could-send-the-message)
  - [Fixed: sending mail gave no feedback](#fixed-sending-mail-gave-no-feedback)
  - [Fixed: the compose window used only part of its width](#fixed-the-compose-window-used-only-part-of-its-width)
- [Address book](#address-book)
  - [New: find a contact's mail from the address book](#new-find-a-contacts-mail-from-the-address-book)
  - [New: filter the contact list by account](#new-filter-the-contact-list-by-account)
  - [Fixed: typing a letter jumps to a contact](#fixed-typing-a-letter-jumps-to-a-contact)
- [Calendar](#calendar)
  - [New: typing and stepping dates and times in the appointment editor](#new-typing-and-stepping-dates-and-times-in-the-appointment-editor)
  - [Fixed: creating an appointment late in the evening](#fixed-creating-an-appointment-late-in-the-evening)
- [Keyboard and navigation](#keyboard-and-navigation)
  - [Fixed: typing a folder name in the folder picker now works](#fixed-typing-a-folder-name-in-the-folder-picker-now-works)
  - [Fixed: arrowing through settings options now chooses them](#fixed-arrowing-through-settings-options-now-chooses-them)
- [Appearance](#appearance)
  - [New: message list density](#new-message-list-density)
  - [Changed: themes](#changed-themes)
  - [Changed: Manage Themes moved to the View menu](#changed-manage-themes-moved-to-the-view-menu)
  - [Fixed: the Theme Manager's Import button at large text sizes](#fixed-the-theme-managers-import-button-at-large-text-sizes)
- [Windows on ARM](#windows-on-arm)
  - [New: a version built for ARM PCs](#new-a-version-built-for-arm-pcs)
- [Diagnostics and troubleshooting](#diagnostics-and-troubleshooting)
  - [Fixed: adding an account no longer shows every other account as disconnected](#fixed-adding-an-account-no-longer-shows-every-other-account-as-disconnected)
  - [New: Connection Diagnostics, for when something looks wrong](#new-connection-diagnostics-for-when-something-looks-wrong)
  - [Changed: Delete QuickMail logs removes every diagnostic file](#changed-delete-quickmail-logs-removes-every-diagnostic-file)
- [Thank You to Contributors](#thank-you-to-contributors)
- [Internal](#internal) — developer detail, not needed to use QuickMail
- [Reporting Issues](#reporting-issues)

---

## Download

v0.8.37 is the first release built for ARM PCs as well as regular ones, so there are four downloads. Take a regular one unless you know your PC has an ARM processor — to check, open **Settings → System → About** and read **System type**.

| Download | When to use |
|----------|-------------|
| **`QuickMail-win.msi`** — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| **`QuickMail-win-arm64.msi`** — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |
| **`QuickMail-arm64.exe`** — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 8 runtime — you do not need to install .NET separately.

---

## Accounts and signing in

Setting up a mail account was the hardest thing QuickMail asked anyone to do. That is what changed most in this release.

### New: adding an account now asks three questions, not ten

Adding an account used to mean supplying an IMAP host, a port, an SSL setting, and a certificate rule — and then the same four things over again for SMTP. Ten or so fields, every one of them a chance to get something subtly wrong, for values QuickMail already knew perfectly well.

Adding an account now asks three questions:

1. **Which provider?** — Gmail, Outlook.com / Microsoft 365, Yahoo Mail, iCloud Mail, or **Other (enter settings manually)**.
2. **What is your email address?**
3. **What is your password?**

QuickMail works out the rest. Choosing a provider fills in every server setting and tells you what it filled in, and typing an address at a provider it recognizes chooses that provider for you — so most people never touch the list at all. The hosts, ports, and SSL settings have not gone anywhere: they moved behind an **Advanced settings** expander that stays closed unless you need it, and Tab reaches that expander's heading before its contents, so a single keystroke moves past the whole thing.

The Provider list opens on **Other (enter settings manually)** rather than ending with it, so Down arrow reaches the rest of the list from wherever the dialog puts you.

**The account name is now optional.** Leave it blank and the account is labelled with its email address.

**An address QuickMail does not recognize is looked up for you** when you leave the Email field, so a work address, or one at a domain of your own, usually takes no more typing than a Gmail one. QuickMail checks its own built-in list of providers first — that step is entirely offline, and nothing leaves your computer — and then tries three public sources of mail settings, ending with the records that say where your domain's mail is actually delivered. Only your **domain** is sent to two of those; the third sends your address to your own provider's server, exactly as Outlook does. That step is what makes ordinary business mail work, because a work mailbox often has no IMAP host to type in the first place — the way in is a sign-in. If nothing is found anywhere, Advanced settings opens with focus in the **IMAP host** box, and a message names the sign-in route to try for a work or school account. To use the offline built-in list only, set `AutoDiscoverOnline = off` in `config.ini`.

**Gmail, Yahoo Mail, and iCloud Mail need an app password** — a password you generate on the provider's own website for use in a mail program, rather than the one you sign in with. Each of those providers now says so above the password box, and Gmail links straight to the page where you create one. Gmail defaults to this route because Google is not currently granting QuickMail new sign-in authorizations, so an app password is the path that works today. (If your Gmail account already signs in with Google, see the next entry — it keeps working.) (#369)

**Work or school Microsoft 365 accounts now connect through Microsoft 365 directly.** An address on your organization's own domain moves onto that connection method as you type it, instead of being left on IMAP — where sign-in ended at "your administrator needs to make a change" for a mailbox that signs in perfectly well the other way. Personal Outlook.com, Hotmail, and Live.com accounts are unaffected and stay on IMAP.

**Test Connection checks both halves of your mail.** Incoming and outgoing are probed separately and reported separately — "IMAP: OK. SMTP: OK." A working inbox with a misconfigured send server used to pass this test and then fail on your first message. Microsoft 365 accounts can be tested now as well.

**Manage Accounts was simplified in the same way.** It has the same **Advanced settings** expander, so reaching the settings people actually change no longer means moving past hosts and ports on an account that is working perfectly. Each account now shows which provider it belongs to, and **Test Connection** is on this window too — which is where you want it when an account has stopped working, rather than only when you are setting one up.

The [Accounts section of the User Guide](https://kellylford.github.io/QuickMail/accounts.html) covers the whole lifecycle — choosing a provider, adding an account, entering settings by hand, testing, editing, and removing.

### Changed: Google sign-in for Gmail is now something you turn on

Google stopped granting QuickMail new authorizations, so a new Gmail account signs in with an app password. But a small number of accounts were authorized before that happened and still work perfectly well over Google sign-in, and this release makes sure they keep their route in.

**If your Gmail account already uses Google sign-in, nothing changes and you need do nothing.** It keeps signing in, keeps syncing mail, contacts, and calendar, and Manage Accounts still shows it as a Google account. Only the *offer* of Google sign-in to a new account is affected.

**If you want to add another Gmail account over Google sign-in**, turn the option on:

1. **Tools → Settings → Advanced**, check **Sign in with Google for Gmail accounts**, and select **Save**.
2. Restart QuickMail — the setting is read at startup.

The **Provider** list then has a **Gmail (sign in with Google)** entry directly below plain **Gmail**. Choose it and there is no password box at all: Gmail's servers fill in as usual and a **Sign in with Google** button stands where the password would be. Contact and calendar sync are offered too, granted as part of the same sign-in. The Google choice also returns to **Advanced settings → Authentication**. (The same switch is available as `GoogleAuth = true` under `[features]` in `config.ini`, or `--feature GoogleAuth` at launch.)

**Why it is off by default.** It used to be on for everyone, which meant the one path Google refuses was the one on offer — and a sign-in that ends in "This app has been blocked" tells you nothing about what to do instead. Off by default, a new Gmail account gets the app-password route that works, and the people the sign-in still works for have a supported way to ask for it.

If you turn the setting on and sign-in still ends in **"This app has been blocked,"** your account is not one of the ones authorized earlier, and no QuickMail setting can change that. Use a Gmail app password — the User Guide's [Accounts section](https://kellylford.github.io/QuickMail/accounts.html) has the steps.

---

## Microsoft 365 accounts

Four fixes for accounts that connect through Microsoft 365 — work and school accounts, and Outlook.com accounts added with a Microsoft sign-in. Accounts that connect over IMAP are untouched by everything in this section.

### Fixed: deleted and moved mail no longer comes back

Deleting or moving a message on a Microsoft 365 account removed it from the list, and then the next sync brought it back about a minute later, often with a "Delete may not have completed — refreshing." message. The cause was that Microsoft 365 changes a message's identifier whenever the message moves between folders — which happens on any move, from QuickMail, from Outlook, or from a server-side rule. QuickMail's stored identifier then pointed at nothing, so the delete or move was refused, and the message came back.

QuickMail now asks Microsoft 365 for identifiers that do not change, so a stored identifier stays valid for the life of the message. Delete, move, mark as read, and flag all act on the message you meant. As a second line of defence, an action against a message the server says is already gone is treated as success rather than as a failure to report. (#416, #419)

**This brings a one-time re-sync on your first launch after updating.** The identifiers already in QuickMail's local cache are the old, changeable kind, so they are cleared for Microsoft 365 accounts and re-fetched from the server. What that means for you:

- **You need do nothing.** It happens by itself during the normal startup sync. QuickMail announces "Microsoft 365 mail is doing a one-time re-sync — this may take a few minutes."
- Your Microsoft 365 message list starts empty and refills over the next few minutes, going back as far as your **View → Sync Range** setting (Last 30 Days by default). Larger mailboxes take longer.
- **Nothing is deleted from the server**, and nothing else on your computer is touched — accounts, settings, rules, flags, drafts, contacts, and calendar events all survive.
- **Your rules will not re-run** over the mail that comes back, so nothing gets moved or deleted a second time.
- If you happen to be offline for that first launch, Microsoft 365 mail is unavailable until you reconnect. It resumes by itself.
- Accounts that are not Microsoft 365 are not touched at all. If you have no Microsoft 365 accounts, nothing happens.

It runs once. Later launches start normally.

### Fixed: the same message no longer appears twice in combined views

A Microsoft 365 message was fetched without the identifier that the rest of QuickMail uses to recognize two copies of one message, so nothing could merge them. In **All Mail** and the other combined views, one message could show up twice. Microsoft 365 mail now carries that identifier and collapses the way IMAP and Gmail mail always has. (#429)

### Fixed: a folder no longer stops loading because of one message

Microsoft 365 reports some messages — certain drafts and system-generated mail — with no read state at all. A single one of those failed the entire folder's fetch, so none of that folder's new mail arrived and nothing said why. One real session hit it 49 times. A message with no read state is now treated as unread. (#395)

### Fixed: adding a work or school account now always asks your permission

On some organizations, sign-in finished without ever showing a permission screen — and then every attempt to read mail failed. The account was added, looked connected, and could not reach a single message. It happened where the organization had already approved QuickMail for something else, such as contacts or calendar: Microsoft treats an existing approval as covering the whole request and signs you in silently, so the mail permissions were never asked for and never granted.

Adding an account now always shows the permission screen, so the full set is approved once, up front. You will see it when you add an account, and when you use **Sign in** in Manage Accounts — which is the button you reach for when an account has stopped working, so asking again there is deliberate. An account whose sign-in has merely expired renews as before, without asking. This also applies to Outlook.com accounts added with a Microsoft sign-in over IMAP. (#391)

---

## Reading and organizing mail

The headline here is that the line a message list reads out is no longer fixed: you choose which pieces it says, and in what order.

### New: choose what a message list row says, and in what order

Every row in a message list is read as one line — sender, subject, date, and so on — and until now that line was fixed. **View → Message List Fields…** lets you decide which pieces are spoken and where each one falls.

Every field is a check box. Check one to include it, uncheck it to leave it out, and use **Alt+Up** and **Alt+Down** to move it. The Up and Down arrows move through the fields and stop at the first and last one, the way they do in any list. **Home** and **End** go straight to those ends, and typing a letter jumps to the next field starting with it. Moving a field that is switched off says so, since that changes nothing you can hear.

A **Spoken preview** box shows the message you had selected when you opened the window, read exactly as the list would read it, updating as you go. There is no OK or Cancel — changes take effect as you make them, and the window is modeless, so you can leave it open, arrow through the list behind it, and hear the result.

Each kind of row keeps its own arrangement: individual **messages**, **conversation groups**, and **sender and recipient groups**.

**Status is no longer one lump.** It used to be a single word — "replied", "forwarded", "unread", or "read" — that you could take or leave. **Unread**, **Replied**, and **Forwarded** are now separate fields you can place independently, and each offers **Speak only when true** or **Always speak**. So "tell me about unread but never say read" is: turn **Status (combined)** off, turn **Unread** on, leave it on *Speak only when true*. The same applies to **Attachments**, which can now sit anywhere in the line rather than in the middle of it.

Because **Status (combined)** and **Unread** both produce the word "unread", turning one on while the other is already on says it twice. Selecting either field explains what the other is doing, so the two do not quietly fight.

Other fields you can now add: **To**, **Mailing list**, and **Source folder**.

**Speak field labels** prefixes text fields with their names ("From: Chris Lee. Subject: Budget review."). States and counts are never labelled, since "unread" and "3 messages" already say what they are.

**If you never open the window, nothing changes** — the default arrangement is exactly what QuickMail has always said, with one deliberate exception: empty fields are now skipped rather than leaving a gap, so a message with no preview or no subject reads without a pause where it would have been.

Two small consequences. The **Announce flag status** checkbox has left Settings, because Flag is now one field with its own checkbox; if you had it turned off, Flag starts out unchecked for you. And a conversation or sender group used to keep saying "Has unread" after you had read its last unread message — it now updates. (#457)

**Show message status column** has also left Settings. It only ever governed the visible Status column — the one showing New, Replied, Fwd, or a flag name — and never what a row said out loud, which is what people reasonably expected of a setting with that name; turning it off changed nothing you could hear. Speech is now chosen field by field in the window above. The column itself is always shown, so if you are among the few who turned it off to reclaim the space on screen, it comes back and there is currently no way to hide it — say so and it can be made resizable. The `ShowMessageStatus` line in `config.ini` is no longer read and can be deleted; it will disappear on its own the next time QuickMail rewrites the file. ([#19](https://github.com/kellylford/QuickMail/issues/19))

### New: combined views say which folder a message is in

In a view that gathers mail from more than one place, a row never told you where the message actually lives, so an inbox message and one a rule had filed into a custom folder read identically. Each row now ends with its folder — "… 12:24 PM. Inbox." — and when the view spans more than one account the folder is named with its account, "Work -- Inbox".

This applies to All Mail, All Inboxes, All Drafts, All Sent, All Archive, All Trash, All Flagged, an account's own All Mail, every saved view, and the contact-mail results that are also new in this release (see [find a contact's mail from the address book](#new-find-a-contacts-mail-from-the-address-book)). An ordinary single-folder view says nothing extra, because there the folder is already obvious. **Source folder** is one of the fields in **Message List Fields…**, so you can move it or switch it off. (#423)

### New: All Archive

The **All Mail** group in the folder tree gathers each kind of folder across every account — All Inboxes, All Drafts, All Sent, All Trash, All Flagged — and archived mail was the one thing missing. **All Archive** is now there too, listed between All Sent and All Trash, and it is available in the folder picker and as a saved view like the others.

It follows each account's own archive setting rather than guessing. If you pointed an account at a particular folder with **Set as Archive Folder**, that is the folder All Archive reads, so the list is exactly the mail **Move to Archive** put there. Accounts with no Archive folder contribute nothing rather than causing an error.

One thing to know if you use Gmail: the guide now recommends creating a Gmail label named **Archive** and pointing the account at it with **Set as Archive Folder**, rather than at **[Gmail]/All Mail**. Both archive correctly, but an account pointed at All Mail contributes its entire mailbox to All Archive, because for that account All Mail *is* the archive. A label gives you a folder holding only what you archived. (#452)

### Fixed: attachments were unreachable when a message opens in its own window

v0.8.36 added **Alt+A** for jumping to the attachment list, and fixed the Shift+Tab path (#350). One part was still missing: with **Message open mode** set to **Window**, a message with attachments had no attachment list at all. Alt+A answered "No attachments" and Shift+Tab from the message body skipped straight past it. Reading pane and Tab modes were unaffected, which is what made it look like an Alt+A problem — the list existed, it was just never shown. Both now work in every mode. ([#439](https://github.com/kellylford/QuickMail/issues/439))

**Alt+A now also works while composing.** It moves focus to the attachment list of the message you are writing, matching what it does in an open message, and lands on the first file rather than the empty list. **Ctrl+Shift+A** is still the way to add files. The new command is in the compose window's Command Palette (**Ctrl+Shift+P**) as **Focus Attachment List**; compose shortcuts are fixed and are not among the ones Settings → Keyboard can rebind.

---

## Mail rules

Mail rules — the ones you set up in **Tools → Rules…** to file, flag, or delete mail automatically — changed in three ways this release: which account a rule belongs to, when rules run, and which folders they act on. If you use rules, all three are worth reading, because together they change behaviour you may be relying on.

### Changed: rules are now per-account

The "All accounts" rule option has been replaced by scoping each rule to a specific account. This happens automatically the first time rules load after updating, once QuickMail can see your account list:

- Existing "All accounts" rules are **copied to each of your standard (IMAP/SMTP) accounts**, so they keep working exactly as before.
- **If you use *only* Microsoft 365 accounts**, any old "All accounts" rules are **removed**, because the migration has no standard account to attach them to. If you had such rules, recreate them and scope them to the account you want — a rule scoped to a Microsoft 365 account does run. (This affects very few people; every removal is recorded in the log.)
- New rules now ask which account they apply to, starting on your default account, instead of offering "All accounts". (#333)

### Changed: rules run on mail that arrives while QuickMail is open

Rules used to be applied only during a full sync. Mail that arrived afterwards — the mail QuickMail picks up while you are sitting there working — was never looked at, so a rule that should have moved or flagged it simply did not fire until the next full sync. Rules now run on new mail as it arrives, on every sync path. Each message is considered once, so nothing is acted on twice. (#411)

### Changed: rules act on the Inbox, and only on new arrivals

Two things used to happen that they should not have. Rules ran against **every folder** QuickMail synced, so a message that a server-side rule — or you — had already filed into Sent, Archive, or a folder of your own could be picked up and acted on again when that folder synced. And **closing the Rules Manager silently reprocessed your whole cached mailbox**, so simply looking at your rules could move mail.

Rules now behave the classic way: they run on the **Inbox**, on **new arrivals**, and never retroactively unless you ask. Other folders are still fetched and cached as before — they are just not rule-processed. Closing the Rules Manager no longer runs anything. ([#336](https://github.com/kellylford/QuickMail/issues/336))

**Run on Existing Mail** is how you ask. It is the button beside New and Delete in the Rules Manager, and it has not moved — but its reach is now the Inbox of each account rather than every cached folder, so it can no longer move or delete mail you deliberately filed somewhere else. It reports how many messages were moved or deleted. An account whose Inbox cannot be identified is skipped rather than guessed at, and that is recorded in the log. (#346)

### Fixed: the rules editor no longer offers actions with nothing to act on

Three things in the Rules Manager:

- A new rule now starts on a **real account** — your default account, or your only account — instead of leaving the Account list blank.
- With **no rule selected**, the rule's fields are disabled and drop out of the Tab order, so you cannot land on a live but meaningless Account list. The buttons stay reachable, so **Close** always works.
- **Delete**, **Save**, and **Test** are disabled until you select a rule, and **Run on Existing Mail** is disabled while you have no rules — including the moment you delete the last one.

**"Show field labels in the rules list" now survives a restart.** The setting saved and applied for the session, but was never written to `config.ini`, so it reverted to off every time QuickMail started.

---

## Writing and sending mail

### Fixed: choosing a sending account could send the message

Pressing Enter after arrowing to an account in the compose window's **From** list sent the message ([#201](https://github.com/kellylford/QuickMail/issues/201)). Not chose the account — sent the mail, half-written, to whoever was already in the To field. Choosing a mode from the compose-mode list, or pressing Enter in the **Subject** box or the attachment list, did the same thing.

The Send button was marked as the window's default button, which in Windows means Enter activates it from anywhere in the window that does not use Enter for something of its own. A closed list is exactly such a place: arrowing through it already changes the selection, so Enter had nothing to do there and went to Send instead. That default is now removed. Enter no longer sends from anywhere in the compose window.

Send is still **Alt+S**, **Ctrl+Enter**, or Enter or Space with the Send button focused.

**Enter on the From list now confirms the account.** Since the keystroke no longer sends, it says which account you landed on — "IdeaPlace used as From address". You also hear it when you pick an account from the expanded list, and when you leave the From field having changed it. Arrowing past accounts stays quiet, because your screen reader is already reading each one. This uses the **Announce action results** setting, so turning that off turns this off with it.

### Fixed: sending mail gave no feedback

A report of "sending an email gives no feedback, and does not close the compose window" ([#396](https://github.com/kellylford/QuickMail/issues/396)) turned out to be four separate problems stacked on top of each other. All four are fixed.

**A send that fails now says so out loud.** The failure message was classed as background progress, so if you had turned **Announce background progress** off — a reasonable thing to do, since that is the setting that stops every folder announcing itself during a sync — pressing Send produced the button greying out, coming back, and nothing else. Send failures, refusals, and confirmations are now announced as results, which is the category for the outcome of something you just did, and they interrupt rather than queue. The same fix applies to a refused save in Add Account and Manage Accounts.

**A message that was accepted is no longer reported as failed.** QuickMail closed the connection inside the same step that sent the message, so a server that hangs up the instant it takes your mail — or any hiccup during the sign-off — produced "Send failed" for a message that was already on its way. This is why the reporter saw messages sometimes arrive anyway. The sign-off is now separate and its own failure is ignored, because by then the server has your message.

**Your login and your email address can now be different things.** There was one box serving as both, and for some accounts they are not the same string: an iCloud mailbox on your own domain logs in under the Apple ID, and some hosted servers want a bare user name. Whichever one you entered, the other use of it was wrong — a login name in the box became the From address on your mail, which servers reject. **Advanced settings** in both account dialogs now has a **Login username** box, empty for almost everyone, filled in only when your server logs in under something other than your email address. The **Email address** box is now only that, and saving an account refuses an entry that is not a full address, pointing at the new box instead.

If you already had an account set up with a login name in the address box, QuickMail copies it into **Login username** for you the first time it starts. That matters: correcting the address is what you are now asked to do, and without the copy you would be deleting the very thing your account signs in with. You enter the address; the login carries on working. This also covers contact and calendar sync on iCloud, which sign in with the same name.

**A wrong encryption setting on a known server is corrected at startup.** An account set up by hand before QuickMail knew these providers could end up with **Implicit SSL on connect** checked while using port 587, which is a STARTTLS port. That combination fails every single send, about a second after you press the button, with an error that names a certificate rather than a checkbox. At startup QuickMail now corrects the encryption setting when — and only when — the account is one you have never saved yourself, the server is one it ships settings for, *and* the port is the exact port it publishes for that server. Anything else — one of those servers on a different port, or any account you have saved in Manage Accounts — is left exactly as you set it. A corrected connection also requires encryption from then on, rather than falling back to plain text if the server offers none.

### Fixed: the compose window used only part of its width

The address fields, the subject line, and the body editor were squeezed into a narrow column against the left edge, leaving roughly two thirds of the window blank no matter how large you made it. It had been that way since the compose window was built. They now stretch across the full width and grow when you resize. Nothing else about the window changed — the same fields, the same order, the same keyboard behaviour. ([#435](https://github.com/kellylford/QuickMail/issues/435))

---

## Address book

### New: find a contact's mail from the address book

Select someone in the address book, press **Shift+F10**, and choose **Find mail from this contact** or **Find mail to this contact**. The address book closes and the message list fills with the matches, newest first, drawn from every account and folder QuickMail has cached — not just the folder you were in. Focus lands on the message list and the count is announced ("12 messages from Bob Baker."), and the window title reads **Mail from Bob Baker** so the results are easy to tell apart from a folder.

Press **Escape** in the message list to close the results and return to the folder you started from — the destination and its message count are announced ("Search closed. Inbox, 42 messages."). There is also a **Close** button above the results next to the count, and **Close Contact Mail Results** in the Command Palette, which you can give a keyboard shortcut in Settings → Keyboard. Escape keeps everything else it already did: an open reading pane, the calendar, and tab mode claim it first, and in the search box it still clears your search text.

Both actions are also in the address book's Command Palette (**Ctrl+Shift+P**) as **Find Mail From Contact** and **Find Mail To Contact**.

Two things to know about the results: mail older than your sync range is not stored locally, so it is not searched, and **Find mail to this contact** matches the To line — a message where the person was only in Cc does not appear. (#370)

### New: filter the contact list by account

A **Filter** button sits to the right of the address book's search box and carries the current filter in its own label, so it reads **Filter: All accounts**, **Filter: Work**, and so on. **Alt+F** reaches it from anywhere on the Contacts tab. The menu offers **All accounts**, then **Local address book**, then each of your accounts; the active one is checked and is where the menu opens, and applying one announces the result ("Work, 12 contacts").

The filter works alongside the search box, survives a contact sync or an edit, and falls back to All accounts if the account it named is removed. A contact filtered out of view is deselected, so Edit and Delete cannot act on a row you can no longer see. It is also in the address book's Command Palette as **Filter Addresses by Account**, with no default key, so you can assign one. Where the address book opens with focus is unchanged. (#399)

### Fixed: typing a letter jumps to a contact

With focus on the address book's contact list, typing a letter did nothing. Now it jumps to the first contact starting with that letter, the same way lists behave elsewhere in Windows. Type several letters quickly to match a longer beginning ("br" goes to Brenda rather than Bob), or press the same letter again to move to the next contact starting with it. Contacts saved without a name are matched on their address. The **Groups** and **Group members** lists work the same way. (#371)

---

## Calendar

### New: typing and stepping dates and times in the appointment editor

The appointment editor's dates went through a calendar popup built for a mouse, and its times were plain text boxes. Nothing responded to the arrow keys, so changing anything meant retyping the whole value.

**Start date**, **Start time**, **End date**, **End time**, **Repeat interval**, and **Repeat until** are now ordinary edit fields that step with the arrow keys. The same field is used for the date in the **Go to date** window.

| Keys | Date field | Time field | Number field |
|------|------------|------------|--------------|
| Up / Down | 1 day | 15 minutes, snapped to the quarter hour | 1 |
| Ctrl+Up / Ctrl+Down | 1 day | 1 minute | 1 |
| Shift+Up / Shift+Down | 1 week | 1 hour | 5 |
| Page Up / Page Down | 1 month | 1 hour | 10 |
| Ctrl+Page Up / Ctrl+Page Down | 1 year | 1 day | 10 |

Typing still works, and takes far more than it used to. Dates accept "8/3", "August 3", "2026-08-03", "today", "tomorrow", "yesterday", weekday names like "fri" or "next tuesday", a bare day number, and offsets such as "+7", "-3", "+2w", "+1m", "+1y". Times accept "9", "930", "9:30", "9:30 AM", "9p", "14:30", "noon", "midnight", and "+30" or "-15". Enter or moving to another field applies what you typed; text that cannot be read as a date or time puts the previous value back rather than guessing.

Two behaviours worth knowing. **Stepping a time past midnight carries the date with it** — 11:50 PM stepped up becomes 12:00 AM the next day, instead of wrapping round and leaving the date behind. And **the end follows the start**: moving the start moves the end by the same amount and keeps the appointment's length, while changing the end sets a new length.

If a save is refused, QuickMail now moves focus to the field at fault and selects its text, and shows the reason on a line above the buttons that clears itself when you fix the field — so the refusal is visible and reachable however your announcement settings are set. (#400)

### Fixed: creating an appointment late in the evening

Pressing **N** for a new appointment after about 11:30 PM produced a default half-hour range whose end crossed midnight while its end *date* stayed on the day you started, so the appointment ended before it began and Save was refused over values you had never touched. The only way out was to correct the end date by hand. A new appointment started at 11:45 PM now ends at 12:15 AM the next day and saves. (#378)

---

## Keyboard and navigation

### Fixed: typing a folder name in the folder picker now works

Typing letters in the tree view of the folder picker — the view you get when moving or copying a message — did nothing. The v0.8.32 notes said typing a folder name there would jump to it; the mechanism behind that claim turned out never to have worked, and nothing else was wired in its place. The tree now has the same type-ahead as the main window's folder tree: type the first letters of a folder's name and the selection jumps to it, keep typing to narrow the match, repeat a letter to cycle through folders that share it. ([#418](https://github.com/kellylford/QuickMail/issues/418))

Two related repairs in the same picker:

- **The flat "Go to Folder" list matched against the wrong text.** Typing a letter there searched an internal name rather than the folder names on screen, so it went nowhere useful. It now matches the folder path you see.
- **Typing "o" or "c" could press a button.** The Open and Cancel buttons carried shortcut letters that fire on a bare keypress when focus is in a list, so an unmatched type-ahead letter could activate one of them — "c" closed the picker. The same problem was fixed for the New Folder button in v0.8.32; Open and Cancel now follow. Enter still opens the selected folder and Escape still cancels.

**In the main window's folder tree, type-ahead now continues a prefix.** Typing "s", "e" in quick succession finds "Sent" rather than treating each letter as a fresh first-letter search. The v0.5.5 notes described the folder tree working this way, but the code never actually did — each letter was always a fresh search; the message list is where continuation really lived. The tree now genuinely does what those notes said.

**Repeating a letter now cycles through matches everywhere.** Pressing "s" twice quickly used to build the prefix "ss", which matches nothing, so rapid repeats went dead until the timeout passed. A repeated letter now keeps the single-letter prefix and moves to the next match — the standard list behavior — in the message list, the grouped views, both folder trees, and the picker.

One more small change in the same code: **a capital letter now works for type-ahead.** Shift+S was silently ignored in the message list and grouped views; it now matches the same as "s". (Matching was always case-insensitive once the letter got through.)

### Fixed: arrowing through settings options now chooses them

In Settings, arrowing through a group of options — **Message open mode**, **Message list density**, **Log format**, **Spelling suggestions verbosity** — moved between them without choosing one; you had to press Space on the option you had landed on, which is not how these behave elsewhere in Windows. Arrow keys now select the option they move to. Tab still enters the group on the option already chosen and changes nothing on the way in or out. The same applies to the **Change: this event only / all events in the series** choice when editing a repeating appointment.

QuickMail also stopped speaking the option name itself when you choose one. That announcement was added to paper over this same bug — choices went unannounced because arrowing never made one — and with the groups behaving correctly it was QuickMail talking over software you have already set up to speak the way you want. ([#441](https://github.com/kellylford/QuickMail/issues/441))

---

## Appearance

### New: message list density

**Message List Density** offers **Comfortable** and **Compact**. It is in **Settings → Appearance**, and also directly on the **View → Density** submenu so you can change it without opening Settings. Choosing one announces "Comfortable density." or "Compact density."

Comfortable is the new default and puts a little more space around each message row. Compact is the tighter spacing QuickMail has always used, so choosing it returns the message list to exactly what you had before. The setting changes spacing only — the rows say the same thing either way, and nothing about keyboard navigation changes.

Both options are also available as commands, **Density: Comfortable** and **Density: Compact**, in the Command Palette and in Settings → Keyboard, so you can give either one a shortcut. Neither has a shortcut by default. (#421)

### Changed: themes

- **Ember, Fjord, and Heather are complete themes now.** Each of them previously supplied only four colors and borrowed everything else from Parchment. Each now carries its own full set — text, borders, links, selection, focus, and status colors included.
- **The toolbar follows your theme.** Its buttons, separators, and background used stock Windows colors in every theme, which is why the toolbar looked out of place in the dark themes in particular.
- **Borders are darker against their backgrounds** in Parchment and Parchment Dark, so the edges of boxes, fields, and panes stand out more clearly.
- **The focus indicator gained an outline behind it**, which keeps it visible against backgrounds close to its own color.
- **The Markdown preview window** no longer opens with a white background under a dark theme.

### Changed: Manage Themes moved to the View menu

**Manage Themes…** is on the **View** menu now rather than Tools — choosing how the app looks belongs with the rest of what View controls. The **Next Theme** and **Previous Theme** menu items were removed; both are still commands, so they remain in the Command Palette and keep any shortcut you assigned them.

### Fixed: the Theme Manager's Import button at large text sizes

At a Windows text size of 150% the buttons beside the theme list ran past the bottom of the window and **Import** could not be reached at all. That column scrolls now, and moving to a button with the keyboard brings it into view.

---

## Windows on ARM

### New: a version built for ARM PCs

QuickMail now has a build for PCs with an ARM processor — the Snapdragon X models of Surface Laptop and Surface Pro, and similar machines from other manufacturers. Until now those PCs ran the regular build through Windows' built-in emulation, which works but costs speed and battery life. The ARM build runs on the processor directly.

Nothing changes for anyone on a regular PC, and nothing changes for you automatically: automatic updates stay on whichever version you installed, and QuickMail will never move you across on its own.

If you are on an ARM PC running the regular build, QuickMail says so once, and the **Help** menu keeps a **Get the ARM Version** entry that opens the download page. That entry appears only on an ARM PC running the regular build — which makes it the way to check where you stand. Once you are on the ARM version, it is gone.

**Switching is a manual uninstall and reinstall, and the uninstall is not optional:**

1. Uninstall QuickMail from **Settings → Apps**. When the uninstaller offers to delete your data, choose **No**.
2. Download and run **`QuickMail-win-arm64.msi`**.
3. Start QuickMail. Your accounts, settings, contacts, rules, templates, saved views, and cached mail are all as you left them — your data lives separately from the program itself.

Running the ARM installer on top of a regular QuickMail of the same version does **not** replace it. Windows treats the two as separate programs, installs the second beside the first, reports success, and leaves the regular build running. Nothing in QuickMail would look wrong afterwards, which is exactly why it is worth saying plainly: uninstall first, and check the Help menu once you have restarted. If it has already happened, uninstalling and running the ARM installer again puts it right — and note that **Settings → Apps** cannot tell you which build you have, since both report the same name and version there. (#18)

---

## Diagnostics and troubleshooting

### Fixed: adding an account no longer shows every other account as disconnected

Adding a new account made the accounts you already had report **disconnected** in the account list, and they stayed that way until you restarted QuickMail. This has been reported several times, and each previous attempt fixed a real connection bug that turned out not to be this one.

**Nothing was ever actually disconnecting.** Your accounts stayed connected the whole time — mail kept arriving, folders kept working. What broke was the account list's picture of them. Adding an account makes QuickMail re-read your account file, and connection status is live information that is deliberately never written to that file, so every account came back from the re-read reporting "disconnected". Accounts that were already working were then skipped by the reconnect pass — correctly, since nothing was wrong with them — so nothing ever corrected the status, and the wrong label stuck.

The status now survives a re-read. Adding, editing, or removing an account leaves the others reporting exactly what they were.

This one hid for so long because it looks exactly like a connection failure: it appears the moment you touch your accounts, it hits every account at once, and it lasts until a restart. It was found by recording what QuickMail's connections were actually doing at the moment it happened — which is the feature below. (#312)

### New: Connection Diagnostics, for when something looks wrong

**Settings → Advanced → Record connection diagnostics** turns on a record of how QuickMail connects to your mail servers. It is **off by default**, and most people will never need it.

Turn it on when an account reports the wrong status, mail stops arriving, or an action reports a failure you cannot explain — then reproduce the problem. It starts recording the moment you switch it on, so you do not have to restart first and lose what you were trying to capture.

While it is on, a **Connection Diagnostics** item appears in the **Help** menu. It shows each account with what QuickMail believes about it alongside what its mail server actually says, and a **Test this account** button checks an account directly on a brand-new connection. That is the question this exists to answer: whether the problem is your connection or what QuickMail is reporting about it. **Copy report** and **Save report** produce a plain-text file you can attach to a bug report.

**What it records:** your account names, your mail server names and addresses, connection attempts and their results, and error messages. **What it never records:** passwords, authentication tokens, and the contents of your mail. Your account names may be email addresses, and mail server names identify your provider, so it is worth knowing what is in a report before you share one.

The record goes into a file named `connection.log`, kept beside QuickMail's other settings. It is capped in size, and it stops the moment you turn the setting off.

### Changed: Delete QuickMail logs removes every diagnostic file

QuickMail can leave three kinds of diagnostic file on your computer, and **Settings → Advanced → Delete QuickMail logs** used to remove only the first of them:

- **The application log** (`quickmail.log`) — a running record of what QuickMail did. It is always written, and in more detail if you start QuickMail with the `/debug` switch.
- **The connection log** (`connection.log`) — the connection record described just above, written only while **Record connection diagnostics** is switched on.
- **Debug screenshots** — pictures of QuickMail's own windows, saved to a folder. This is a development tool for checking how the app looks, and it is not something you will meet in ordinary use: it exists only when QuickMail is started with the `/debug` switch, has to be switched on deliberately in Settings once you are there, lasts only until you close the app, and puts " - SCREENSHOTS ON" in every window title while it is running — so it can never be capturing without your knowing.

Delete QuickMail logs now removes all three, and says so before it does it. The usual reason to delete these files is that they carry your email addresses and mail server names — and screenshots hold pictures of your actual mail — so leaving one behind because it happens to live in a different folder would quietly defeat the point. Screenshots are cleared even when you are running normally, in case an earlier `/debug` session left some behind. (#436)

---

## Thank You to Contributors

Thanks to everyone who reported problems and tested fixes for this release. The reports behind the Microsoft 365 mail-reappearing investigation (#366), the silent send failures (#396), the compose Enter-sends-the-message report (#201), and the rules-scoping and attachment feedback shaped a great deal of what shipped here.

---

## Internal

Everything below is developer detail — implementation notes, test coverage, and build changes. Nothing here is needed to use QuickMail.

### Microsoft 365 / Graph

- **Immutable ids everywhere (#419).** `GraphHeaders` adds the immutable-id preference header; every read (`GraphMailService` summary/detail/delta paths) and every write (delete, move, mark-read, flag) now round-trips an id that survives a folder move, as does `GraphChangeNotifier`'s delta poll. Graph's default ids change on move, so a cached id went stale the moment a server rule or another client filed the message — the delete then 404'd and the next sync resurrected it.
- **One-time cache rebuild.** `App.OnStartup` clears `MessageDetail`, `MessageSummary`, and `DeltaToken` rows for `BackendKind.MicrosoftGraph` accounts only, guarded by a `.immutable-id-rebuilt` marker file in the profile directory. `CalendarEvent` rows are deliberately untouched, and IMAP caches are untouched so IMAP invite-source links survive. A failed clear is caught, logged, and leaves the marker unwritten so the next launch retries; the rebuild is skipped in `--online` and `--ui-probe`. `SyncService.SeedRebuildBaseline` makes the first full sync per folder cache without running rules, so the refetched backlog does not re-fire rules — a crash between the wipe and that first Inbox sync can still lose the in-memory baseline, tracked as **#454**. Note the downgrade hazard: an older build run against a rebuilt cache treats the stored immutable ids as mutable.
- **`IsAlreadyGone` (#416)** is retained as belt-and-braces on delete/move even though #419 removed the cause; it still covers a message genuinely deleted from another device. The remaining #366 work — reconciling a 404 by re-syncing the affected folder — is not in this release, and **#366 is still open**.
- **`internetMessageId` is selected on Graph summaries (#429).** Without it `MessageDeduplicator.CollapseKeyFor` fell back to the per-folder key (account + folder + id), which cannot merge copies, so Graph messages doubled in every aggregate view.
- **`GraphMessage.IsRead` becomes `bool?`**, mapped `?? false` at both sites. As a non-nullable `bool` it threw `JsonException: Cannot get the value of a token type 'Null' as a boolean` mid-batch, and because the throw escaped the whole deserialization, one message took down the entire folder fetch.
- **`OAuthService.PromptForSignIn(firstConnect, username)`** centralizes the MSAL prompt choice: the add-account path forces `Prompt.Consent`, re-auth keeps `ForceLogin`/`SelectAccount`. Scopes stay `.default` for work/school, so requested-equals-declared still holds by construction (#208) and Azure resolves the set per account type — an explicit org-only scope list would have broken personal accounts on a custom domain. `SignInInteractiveAsync(account, ct)` is the add path and is reached from both `AddAccountViewModel` and `AccountManagerViewModel`'s **Sign in** button, since both derive from `AccountEditorViewModel`. The #202 identity-mismatch guard still refuses to adopt a different identity that completes sign-in, so the protection moves from prevention to detection rather than disappearing. `.default` never surfaces a *newly declared* permission to a user who already holds an older grant — any permission added in future must be requested explicitly at the point it is needed, as contacts and calendar already do.

### Rules

- **`SyncService.ApplyRulesToArrivalsAsync` is the single chokepoint** for rule application, called from the full sync and from both IDLE paths (cached and online). Previously only the full sync applied rules, so IDLE-delivered mail permanently escaped them. Dedupe authority is the store in cached mode (an id snapshot taken *before* the upsert) and an in-session set in online mode. Rule-removed messages are stripped from the returned batch and from the store so they never flash in the origin folder.
- **The #336 Inbox gate** is `folder.Kind == SpecialFolderKind.Inbox || folder.FullName == "INBOX"`. For Graph accounts `FullName` is an opaque id that never equals `"INBOX"`, so `Kind` is the *only* thing keeping client rules alive on a Graph inbox — pinned by `SyncServiceRuleApplicationTests.GraphInbox_ByKind_RunsRules_EvenWithOpaqueFolderId`. Any new sync entry point handing this method a Graph inbox with `Kind == None` would silently stop running rules on it.
- **Online mode baselines the first fetch per folder** (the last-50 reconciliation batch) as seen without running rules, so a move/delete rule never rewrites up to fifty pre-existing messages on the first reconciliation.
- **`RuleService.MigrateAllAccountRules`** runs from `LoadRules()` and is not feature-gated. It defers entirely when the account list is empty — an empty read can be transient (startup ordering, a locked `accounts.json`) and migrating against it would drop every unscoped rule. A genuine Graph-only profile still drops, and logs each dropped rule by name. `AccountOptions` lists every account including Graph ones, so a user can rescope a dropped rule and it will run.
- **`ApplyRulesToExistingAsync` takes an account→Inbox-`FullName` map** and filters cached mail to those pairs Ordinal, skipping accounts absent from the map **fail-closed** rather than guessing an Inbox. The caller builds the map from `CachedFolders`, logs unresolved accounts, and runs under a 60-second timeout. The Rules Manager's `Closed` handler no longer calls it — it only refreshes the status text.
- **Server rules remain gated.** `FeatureFlag.ServerRules` defaults to `false` in `ConfigFeatureGate.Defaults`, and `MainWindow` builds `UnifiedRulesWindow` only when the flag is on, a `ServerRuleService` exists, and a Graph account is present; otherwise the existing client-only `RulesManagerWindow` is used with `serverRulesVm: null`, which leaves `ServerRulesSection` collapsed. The classifier, the server-rule editor VM, and the unified list are all in the tree and all unreachable in a shipped build — they are deliberately absent from the user-facing notes above. There is no Settings UI for the flag; only `--feature ServerRules` or `config.ini`.

### Message-list row speech

- **`RowFieldCatalog` / `RowLayoutService` / `RowSpeechBuilder`** replace the six hand-written accessible-name converters. The catalog is the single source of field ids, display names, labels, bound property paths, and formats; the default order is `["flag", "status", "attachments", "from", "subject", "preview", "date", "folder"]`, which reproduces the previously shipped speech exactly except that empty fields are skipped rather than emitting a bare `". "`. `MessageAccessibleNameConverter` is gone.
- **The legacy `AnnounceFlagStatus` setting is honoured once** at seed time in `RowLayoutService`: false means the Flag field starts unchecked. The Settings checkbox is deleted; the config key remains readable so an existing `config.ini` still lands correctly.
- **`FieldCheckList` uses real `CheckBox` controls, not a `ListBox`.** A `ListBoxItem` wrapper carries a second copy of the row's name; making each row the actual check box means role and checked state come from the platform and `Space` toggles natively. The cost is that `Home`/`End` and first-letter navigation — free in a `ListBox` — are implemented on the control, using `TypeAheadPrefixTracker` rather than WPF `TextSearch` (which requires a `Selector`). It is therefore **not** a `TypeAheadWiringTests` site.
- **`StampFolderDisplayNames`** fills `FolderDisplayName` on aggregate rows only; `AggregateSpansMultipleAccounts` decides account-qualification by view identity, and an uncached folder appends nothing rather than a raw backend id.

### Type-ahead

- **`TypeAheadPrefixTracker` + `TypeAheadMatcher` (new)** — the hand-rolled prefix accumulator and wrap-around matcher, extracted from `MainWindow` so they can be tested without a window (#415). The tracker takes a `TimeProvider`, so `TypeAheadLogicTests` exercises the 1-second reset window deterministically, including its exact boundary — coverage that previously required synthesized keystrokes racing a real clock (#380/#414). The tracker's peek/commit split also closes a latent double-append: the `PreviewKeyDown` route now peeks and commits only on a match, so an unmatched keystroke is recorded once (by `PreviewTextInput`), not twice.
- **`TypeAheadWiringTests` now fails any `TreeView` declaring `TextSearch.TextPath`.** WPF disables text search by default on `TreeView`/`TreeViewItem` (verified against the control defaults; `ListBox`/`ListView`/`ComboBox` enable it), and even enabled it matches one level's items only — which is how the picker's inert attribute shipped in v0.8.32 with a release note claiming it worked. Trees must wire `PreviewTextInput` to the shared tracker instead.

### Connection diagnostics and account status

- **`MainViewModel.LoadAccountList` carries `IsConnected`/`TotalUnread` across a reload**, keyed by account id. Both are runtime state deliberately excluded from `accounts.json`, so rebuilding the models produced objects defaulting to disconnected, and replacing the `Accounts` collection made the whole list read that way at once. `RefreshAccountList` reconnects only accounts failing `AccountsNeedingConnect`, so healthy accounts were never re-evaluated and the false label persisted until restart. The carry-over is skipped when the account's connection identity changed (host, port, login, auth or security settings), so an edited account is not vouched for by connections belonging to the old server; duplicate ids in `accounts.json` no longer throw on reload. Guarded by `AccountListReloadStatusTests` (4 of its 5 fail with the carry-over removed) and `AccountListCarryOverGuardTests`.
- **The instrumentation could not see this directly**, and that is the lesson worth keeping: the state was lost by *object replacement*, not assignment. `ApplyAccountStatus` genuinely is the only writer of `IsConnected`, so no write was ever observable — the journal's silence next to a plainly visible symptom was itself the evidence. `LoadAccountList` now records an `accounts-reloaded` event closing that blind spot, and `ApplyAccountStatus` takes a `source` tag from all eight call sites.
- **`ConnectionJournal` (new)** — bounded, self-rotating `connection.log` plus a 2000-event in-memory ring backing the diagnostics window. Gated on `ConfigModel.ConnectionDiagnostics` (default `false`). `Record` returns on a volatile read before allocating, **but arguments to the eager overload are evaluated first** — so call sites whose detail invokes `HostConnectionCensus` (which resolves DNS) use a `Func<string>` overload or check `Enabled` themselves. An independent review caught the original version computing the census on every pool rent with the feature off. Enable and disable each write a marker, so a journal that stops is distinguishable from one switched off mid-investigation. File writes take a separate lock from the ring, so a background disk write never blocks a UI-thread `Snapshot()`.
- **`HostConnectionCensus` (new)** — live socket counts per host with cached DNS resolution, and shared-address detection. Our cap is per *account*; a server's is per *user+IP*, and on shared hosting per IP overall. Registrations live in a `ConditionalWeakTable` keyed by client so release is idempotent. Documented limitation: the count is decremented only via `Released()`, which every disposal path funnels through — a client collected *without* being disposed would leave the counter high, so an implausibly high census is to be treated as suspect rather than as proof and cross-checked against the `pool=` figure on the same line.
- **`IConnectionProbe` / `ConnectionTruthProbe` (new)** — independent reachability verification on a connection sharing nothing with the pools or watchers, emitting a greppable `verdict` line stating what the UI shows against what the server said. Serialized process-wide and rate-limited per account, since a per-IP limit is a plausible suspect and the probe must not become part of what it measures. **Gated on the setting at every entry point**: an independent review found the first version starting its verification loop regardless, so a single IDLE failure on a default install opened an authenticated connection outside the pool cap every 60 seconds, indefinitely — against exactly the host already refusing connections. `RetainOnly` also abandons verification for an account removed while it was showing disconnected, which otherwise probed a deleted mailbox for the rest of the session.
- **`ProbeResult` carries a three-state `ProbeOutcome`** (`Reachable`/`Unreachable`/`NotSupported`). The first live run wired the probe straight to `ImapMailService`, which answered "not registered with the IMAP service" for a **Graph** account; collapsed to a boolean that read as unreachable, it reported a healthy account as broken. `Unreachable` is false for `NotSupported`, so the two cannot be conflated again. `GraphMailService` implements the interface via the Inbox counts, and `MailServiceRouter` dispatches per account with **no default-backend fallback** — defaulting to IMAP was the original defect.
- **`ImapMailService.RaiseReachability`** funnels `AccountReachabilityChanged` so every raise carries a reason. Worth noting for anyone reading the connection code: the IDLE watcher is still the only source of reachability, and it marks an account unreachable after a *single* failure, before any retry (#314).
- **`ConnectionDiagnosticsWindow`** is modeless per the modal-dialog rules, with its own F6 ring, Escape handling, focus restoration, a `CancellationTokenSource` field cancelled in `OnClosing` (closing mid-test previously left a probe holding the process-wide probe semaphore for up to 45 seconds), and a **window-scoped** command palette listing its own actions rather than the main window's. Pane names on F6 announce as `Status`, not `Hint`, so turning hints off does not make the ring silent. `Refresh` preserves the event filter — rebuilding the combo dropped its selection, wrote null back through the TwoWay binding, and blanked the journal pane permanently. `help.connectionDiagnostics` is registered and unregistered by `ApplyConnectionDiagnosticsSetting` rather than at startup, so the palette and the Help menu never disagree about whether the feature exists.
- **Delete QuickMail logs** removes `quickmail.log`, `connection.log` (and its rolled-over `.1`, clearing the in-memory ring with it), and the whole `debug-screenshots` tree. `ScreenshotCaptureService.DeleteAllCaptures` is static and profile-keyed so it works in a normal launch where only the null capture service is wired; in-flight PNG saves are flushed first so nothing survives by holding a handle (#436).

### Accounts, provider catalog, and sending

- **`AccountModel.LoginUsername`** (persisted, nullable) plus computed `AuthUsername` = `LoginUsername ?? Username`. Every **password** authentication uses `AuthUsername` — IMAP, all three SMTP entry points, and the iCloud CardDAV/CalDAV Basic auth in `ICloudContactSource` and `GraphCalendarSyncService`, which is the same credential pair. OAuth still uses `Username`, the mailbox the token was issued for. `Username` is now documented as the email address and nothing else — it is the From header, the provider-catalog match, and the autodiscovery domain. `SameConnectionSettings` includes `LoginUsername` so a corrected login invalidates the pooled client.
- **`EmailAddressValidator` (new)** parses with `AllowAddressesWithoutDomain = false` — MimeKit's default accepts a bare local part, which is exactly the input that produced `MAIL FROM:<fastfinge>`. Deliberately does not require a dot in the domain. `TryNormalize` returns `mailbox.Address`, and that normalized form is what both editors save: `MailboxAddress.TryParse` accepts `Kelly Ford <kelly@example.com>`, an angle-addr, and padded input, while the `MailboxAddress(name, address)` constructor `MimeMessageBuilder` calls throws on all three — so validating without normalizing would only have moved the failure from a refused save to a rejected send. Enforced in `AccountEditorViewModel.IsEmailAddressUsable` (shared by `IsReadyToSave` and `AccountManagerViewModel.SaveAccount`) and as a pre-send guard in `ComposeViewModel.SendAsync`.
- **`AccountStartupRepair` (new)** runs in `OnStartup` against the loaded account list and does two things. It corrects `ImapUseSsl`/`SmtpUseSsl` where the account carries no `ProviderId` (the marker for predating the catalog — `SaveAccount` backfills it, so a deliberate pairing the user has saved is never overruled), the host equals a catalog provider's host, *and* the port equals that provider's published port; a leg moved to STARTTLS also gets `RequireStartTls`, matching what `ApplyProvider` sets for the same host and port, so a repaired account is not left weaker than a freshly added one. It also copies a non-address `Username` into `LoginUsername` on password accounts, so correcting the address does not destroy the working login. Matched on host rather than `ProviderCatalog.Resolve`, whose email-domain fallback would claim an address relayed through a third-party server.
- **`SmtpService.DisconnectQuietlyAsync`** replaces the in-`try` `DisconnectAsync` in `SendAsync`, `SendIcsReplyAsync`, and `VerifyAsync`, so a failed sign-off cannot be reported as a failed send.
- **`AnnouncementCategory StatusCategory`** on `ComposeViewModel` and `AccountEditorViewModel`, with `SetStatusOutcome`/`SetProgress`. **One-shot** — it returns to `Status` after every raise, matching `MainViewModel.StatusAnnouncementCategory`, because both VMs assign `StatusText` directly in dozens of places and a latched `Result` would re-classify all of them as interrupting outcomes. The setter also clears `StatusText` first so an identical repeated message still raises `PropertyChanged`; without that, pressing a button twice on the same unfixed field announced nothing the second time. Replaces `AddAccountDialog`'s local `_statusCategory` field, and removes three double-announce sites in `ComposeWindow`. `AccountManagerViewModel.EmailAddressRejected` lets the View open Advanced settings and focus the address box, since the refusal names a control behind a collapsed expander.
- **`FeatureFlag.GoogleAuth` default flips to `false`.** It gates only the *offer* — no runtime authentication path consults it, so saved `AuthType.OAuth2Google` accounts are unaffected. New `ProviderCatalog` entry `gmail-oauth` ("Gmail (sign in with Google)"), `DefaultAuthType = OAuth2Google`, no app-password hint, exposed as `IProviderCatalog.GmailGoogleSignIn`. It carries the gmail.com domains but sits after the plain Gmail entry, so `MatchByEmail` and `Resolve`'s host fallback still answer `gmail` for every Gmail address; it is reached only by an explicit pick or a saved `ProviderId`. `AccountEditorViewModel.Providers` is an `ObservableCollection<MailProvider>` built from the catalog minus the Google entry, with `EnsureGoogleSignInListed()` inserting it after Gmail — absent from the list rather than collapsed, so it is out of the keyboard order and the accessibility tree entirely. `ShowGoogleAuthOption` is `IsGoogleAuthEnabled || AuthType == AuthType.OAuth2Google`, and that second clause is what keeps an existing Google account's Authentication combo populated. `ConfigModel.Features` is an `OrdinalIgnoreCase` dictionary so `SettingsViewModel.GoogleSignIn` updates the existing key instead of adding a second one in different capitalization.
- **Account Manager's Provider value is a read-only `TextBox`, not a `TextBlock`.** A TextBox exposes its `Text` as a value alongside the `LabeledBy` name; on a TextBlock, `LabeledBy` overrode the automatic name (its own text) and left no value behind it, so the binding was announced as nothing.

### Theming, density, and visual verification

- **Theme tokens.** `Theme.FocusHaloThickness` (= `FocusThickness + 2`) is consumed by `AccessibleStyles.xaml` as a halo stroke under the focus dash. `ThemedControls.xaml` gained a `ToolBarTray` style and the seven `ToolBar.*StyleKey` item styles — WPF default chrome ignores theming, which is why the unstyled ToolBar shipped washed-out for months and passed every sighted spot-check. Ember, Fjord, and Heather went from 4-token files inheriting from Parchment to full 26-token palettes. `ThemedControlCoverageTests` requires every WPF control type used in `Views/` and `Controls/` to have an implicit style or a reviewed exemption; contrast is computed by the WCAG math in `BuiltInThemeTests`, never eyeballed.
- **Density is padding only.** `Theme.ListRowPadding` is `Thickness(2,1,2,1)` compact versus `Thickness(4,3,4,3)` comfortable; the UIA tree and announcements are identical in both modes by design. Compact reproduces the message list's original shipped rendering, whose row template hardcoded `2,1`. `ThemeService` now separates its publish signature from its announce signature so a density change does not announce a theme change.
- **Debug-only screenshot capture (#175).** The real `ScreenshotCaptureService` is constructed only under `/debug`; otherwise a null service is wired, so the feature is structurally inert rather than merely hidden. The Settings group is bound to `IsDebugDiagnosticsVisible` (`LogService.DebugMode && _screenshotCapture != null`), capture defaults off and is session-only, output goes to `<profileDir>\debug-screenshots\<yyyyMMdd-HHmmss>\`, and every window title gains a " - SCREENSHOTS ON" suffix while it is running.
- **The ui-probe harness (#180)** is developer tooling: `scripts/ui-probe.ps1`, `scripts/ui-probe-plan.json`, `scripts/ui-review-prompt.md`, and the `Tools/QuickMail.Fixtures` project, which is referenced only by the test project and never packaged (`installer/quickmail.iss` ships a single file). In-app it is a launch mode only — `--ui-probe <surface>` implies `/debug`, forces offline services, drives the surface, captures, and exits; malformed args shut down with exit 64. The script refuses to run on a locked desktop, since DWM never composites new windows there and captures come out white.
- **`ComposeWindow`'s form grid was not the `DockPanel` fill child.** The autocomplete `Popup` was declared last, so `LastChildFill` applied to it — a `Popup` occupies no layout space — and the grid fell back to `Dock=Left` at content width. Declaring the popup before the grid fixes it; a `Popup`'s position in the child list has no layout or rendering effect because placement is set in code-behind. Element set, tab order, and UIA tree are unchanged.

### Controls and accessibility infrastructure

- **`RadioGroupNavigation` (new, `Helpers/`)** — an opt-in attached behaviour, `SelectionFollowsFocus="True"`, set on the container of a radio group. Arrow keys move to the next or previous enabled button of the same `GroupName` (wrapping, to match `DirectionalNavigation="Cycle"`), focus it, and check it; every other key is left to WPF. Walks the **logical** tree, so it works before the group has been rendered. It checks the button **before** focusing it, so the focus that follows lands on an option already selected and the state reported is the new one, and it announces nothing itself. Applied to the four `SettingsDialog` groups and the `EventEditorWindow` edit-scope group. Deliberately **not** applied to the FlagManager colour swatches, whose buttons carry a `Command`: `ButtonBase` invokes it from `OnClick()`, which a programmatic `IsChecked = true` does not raise, so arrowing there would show a swatch as chosen while the command that applies the colour never ran.
- **`SettingsDialog.RadioButton_Checked` is deleted, not suppressed.** f71f86f added it so that choosing an option was announced at all, on the reading that a bare `StackPanel` is not a UIA selection container. That was treating a symptom: the reason a choice went unannounced is that arrowing never made one — it moved focus and left the selection behind. With selection following focus the platform reports the change itself, and an app that speaks over that is overriding a decision the user has already made in their own software. The suppression flag the first cut of this fix introduced (`IsMovingSelection`) is gone with it; a mechanism whose only job is to mute an announcement that should not exist is a sign the announcement is the bug. Guarded by `RadioGroupWiringTests.SettingsRadioButtons_DoNotAnnounceThemselves`.
- **`Controls/DateTimeField`** is a plain `TextBox` with no automation peer of its own. Three shapes were built and evaluated with three screen readers before this one was chosen: an edit field, an edit field claiming `AutomationControlType.Spinner`, and a purpose-built spinner implementing `IValueProvider` and `IRangeValueProvider`. All three announced correctly, so the one that invents nothing won — replacing `TextBox.Text` raises the UIA value-change event screen readers already act on. There is **no** `AccessibilityHelper.Announce` in the stepping path, and there must not be: a programmatic announcement is filtered by the user's announcement settings, a native value change is not. Stepping and parsing live in `Helpers/DateTimeFieldParser.cs` (pure, unit-tested); a time field holds a full instant rather than a `TimeSpan`, so stepping past midnight carries into the date, and `TryParseTime` explicitly **rejects date-shaped text** because `DateTime.TryParse("8/3")` succeeds with a `TimeOfDay` of zero.
- **`MailMessageDetail.Attachments` keeps the inherited `HasAttachments` flag in sync** — nothing else ever set it on a detail. `ImapMailService`, `GraphMailService`, and `LocalStoreService.LoadDetailAsync` all populate the list only, and `MainViewModel` patched the *summary* after loading, which is exactly why the reading pane looked right. `MessageWindow.xaml` binds the attachment list's `Visibility` to `MessageDetail.HasAttachments` with `FallbackValue=Collapsed`, so the always-false flag hid the list, and `FocusAttachmentList`'s visibility check then reported "No attachments." Fixing the three producers one at a time would have left the next one free to reintroduce it. `MessageDetailAttachmentTests` covers the invariant, the local-store round trip, and reads `MessageWindow.xaml` to assert that whatever property the Visibility binding names really is true for a message with attachments.

### Tests and build

- **Keep non-WPF tests out of the `WpfTests` collection.** The radio-group wiring tests are plain XML parsing and were first written inside `RadioGroupNavigationTests`, which carries `[Collection("WpfTests")]`. That reliably broke six `AccountDialogHintTests` ("focusing AccountNameBox produced no hint. Heard: (nothing)") across three full runs, while `main` was green under the same conditions — the collection exists to serialize window-loading tests, and non-STA tests scheduled inside it disturb them. Moving them to their own class fixed it. Worth remembering the next time a full-suite run goes red in a class the change never touched.
- New or extended suites in this release: `AccountStartupRepairTests` (17), `AccountLoginUsernameTests` (20), `ComposeViewModelSendFeedbackTests` (13), `EmailAddressValidatorTests` (26), `GoogleSignInOptInTests` (20), `RadioGroupNavigationTests` (10), `RadioGroupWiringTests` (6), `SyncServiceRuleApplicationTests`, `MessageDetailAttachmentTests`, `TypeAheadLogicTests`, `ConnectionDiagnosticsTests`, `ConnectionDiagnosticsSettingTests`, `ConnectionDiagnosticsWindowTests`, `ConnectionDiagnosticsReviewFixTests`, `AccountListCarryOverGuardTests`, `AccountListReloadStatusTests`, `ImapConnectionInstrumentationTests` (real connect path against a closed port and a hang-up listener, asserting socket error codes are captured and the census does not drift), plus login-identity tests in `CardDavContactSyncTests` and `CalDavCalendarSyncTests`. `StatusAnnouncementRecorder` captures announcements inside the `PropertyChanged` notification, the way the View does — asserting the category afterwards would pass against a broken implementation now that the category is one-shot.
- **Test-harness fixes:** `LogServiceTests` is immune to parallel writers; the capture service no longer crashes across threads in parallel runs (#433); two flaky tests that depended on the real clipboard and on window activation were repaired (#410); and synthesized-input type-ahead tests are gated behind `QUICKMAIL_RUN_INPUT_TESTS` (#414).
- **Build:** `bin`/`obj` are excluded from the default `Compile` glob via `Directory.Build.props`, and the RID and TFM no longer appear in output paths, so Dependabot can read the dependency graph (#382). CI's `-warnaserror` gate is now real rather than nominal (#446).

---

## Reporting Issues

Found a problem or have a suggestion? There are three ways to reach us — pick the one that fits:

1. **Report a Bug → Send** (Help menu, inside QuickMail). Files the report for you anonymously — it includes no email address or other identifying information, so there is no way to follow up with you. **Best when you don't want any follow-up.**
2. **Report a Bug → Copy report and open GitHub** (Help menu). Opens a pre-filled issue that you submit under your own GitHub account, so your GitHub contact information is attached. **Best when you have a GitHub account and want automatic filing plus direct contact.**
3. **Email** [quickmailissues@theideaplace.net](mailto:quickmailissues@theideaplace.net). **Best when you don't mind sending email and want a personal follow-up.**

Full details, including exactly what a report contains (and what it never contains), are in the [Reporting Issues section of the User Guide](https://kellylford.github.io/QuickMail/reporting-issues.html).
