# QuickMail v0.8.57 Release Notes

## New: hear how many messages in a conversation are unread

A conversation row could only say "Has unread", which sounds the same whether one message of
twenty is new or all twenty are. Two new fields in **Message List Fields** say how many: **Unread
count** ("2 unread") and **Unread of total** ("2 of 5 unread"). They work on conversation rows and
on the group rows in the From and To views, say nothing when everything has been read, and are off
until you turn one on, so your rows sound the same as before unless you choose otherwise. They start
at the end of the row; use **Move Up** in Message List Fields to hear the count sooner (#800).

## New: choose what goes between the fields of a message list row

Each row in the message list puts a period between its fields, and if you listen at a slow rate
the pause at every period adds up over a long list. **Message List Fields** now has a **Between
fields** choice: **Period** (the default, so nothing changes unless you choose), **Comma**, or
**Space**. Only Period ends the row with a period. Punctuation inside a field, such as the
sentences of a preview, is left as it is. The choice applies to message rows, conversation rows
and the group rows in the From and To views (#802).

## New: choose the word each field says

Each field in **Message List Fields** now has a **Spoken as** box with the word it says: a state's
word ("unread", "replied"), the word after a count ("3 messages", "2 unread"), or a text field's
label when Speak field labels is on. Change it to a shorter word, or empty it to say nothing extra:
a count then says just its number. With Unread count set to "of" just before an emptied Message
count, and the Space separator, a conversation reads "6 of 12 Budget review", or "12 Budget review"
when it's all read. **Default word** puts the shipped word back, and Reset to Defaults restores
them all (#807).

## Fixed

### Rules, Find mail from this contact, and search now can find a sender by address on every account type

On Gmail, other IMAP accounts and POP3 accounts, QuickMail kept only the sender's name for the
message list, so anything that looked for the sender's email address had nothing to find. A
client-side rule with a **From addresses** condition never matched mail from a sender who has a
display name, **Find mail from this contact** in the address book came up empty for those senders,
and a search such as `from:@example.com` missed them. Microsoft 365 and Outlook.com accounts did
support this as expected. QuickMail now properly supports it for all account types by keeping each
sender's address alongside the name on every account type. From now on, rules, Find mail from this
contact, and both kinds of search look at both the from address and the display name (#803).

**Create Rule from Message** now fills in the sender's email address instead of their name, so a
rule made from a message from "PayPal" no longer also catches someone else calling themselves
"PayPal". Where QuickMail doesn't have the address yet, it fills in the name, as before.

The first time you start this version, QuickMail fills in the address for mail it already has.
Most of it comes from what is already on this computer. For IMAP mail whose text has not been
downloaded, it asks the server for the sender alone, a batch at a time, and shows its progress in
its own part of the status bar, which goes away when it's done. To check on it, press **Ctrl+9** to
reach the status bar and **Right** to move along it. Nothing is moved, deleted or marked, and your
rules do not run again.

### A Microsoft account's message list says the sender's name

On a Microsoft 365 or Outlook.com account, each row in the message list showed and spoke the
sender's address after the name like `News <news@example.com>`, where every other kind of
account says the name alone. Microsoft accounts now work like the rest, saying the name, or the
address when the sender has none (#801).

### Message properties now always show both the sender's name and address

**Properties** (**Alt+Enter**) on an individual message now shows both the display name (when
available) and the email address, on every account, even for a message whose text has not been
downloaded. Previously, it tended to only show the display name (#801).

The same goes for a conversation, and for a sender's group in the From view: their Properties now
list each sender as name and address. A sender's group is gathered by name, so if mail under that
name came from more than one address, each address is listed, and someone else using a familiar
name shows up there.

## Reporting Issues

Found a problem or have a suggestion? There are three ways to reach us — pick the one that fits:

1. **Report a Bug → Send** (Help menu, inside QuickMail). Files the report for you anonymously — it includes no email address or other identifying information, so there is no way to follow up with you. **Best when you don't want any follow-up.**
2. **Report a Bug → Copy report and open GitHub** (Help menu). Opens a pre-filled issue that you submit under your own GitHub account, so your GitHub contact information is attached. **Best when you have a GitHub account and want automatic filing plus direct contact.**
3. **Email** [support@theideaplace.net](mailto:support@theideaplace.net). **Best when you don't mind sending email and want a personal follow-up.**

Full details, including exactly what a report contains (and what it never contains), are in the [Reporting Issues section of the User Guide](https://kellylford.github.io/QuickMail/reporting-issues.html).

## Download

There are four downloads. Take a regular one unless you know your PC has an ARM processor — to check, open **Settings → System → About** and read **System type**.

| Download | When to use |
|----------|-------------|
| [**QuickMail-0.8.57-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.57/QuickMail-0.8.57-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.57-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.57/QuickMail-0.8.57-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.57/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.57/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.
