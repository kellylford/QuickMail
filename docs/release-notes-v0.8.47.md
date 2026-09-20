# QuickMail v0.8.47 Release Notes

## Added

### Save and print messages

You can now save a message to a file, or print it. Until now the only thing you could save was an attachment.

- **Save** (**Ctrl+S**) saves without asking, in your default format and folder. Out of the box that is the original message, in your Documents folder.
- **Save As…** (**F12**) opens the Save dialog, where you choose the folder and the format.
- **Print…** (**Ctrl+P**) opens the Windows Print dialog.

All three are on the **File** menu and in the command palette, and Save As and Print are on a message's context menu. They work from the message list, the reading pane, a tab, and a message window, including while you are reading inside the message itself. Save and Save As act on everything selected: several messages, or a whole conversation or sender group when its heading is selected. Each message is saved in its own file, named from its subject, sender and date. Saving never marks a message read, and never replaces or duplicates a file without asking. If a message was saved there before, Save opens the Save As dialog on that name.

There are four formats:

- **Email message (.eml)** is the original message exactly as your server holds it, attachments included. Any mail program can open it.
- **Text file (.txt)** starts with the message's details in plain words: sender, recipients, date, account, folder, status, attachments and more. The message's text follows.
- **Web page (.html)** has the same details, then the message with its formatting, headings, tables and links. Nothing in it runs or loads from the internet.
- **PDF (.pdf)** is made from the web page. It is a tagged PDF, so headings and tables come through to a screen reader.

Set the default format and folder in **Settings → General → Saving Messages**.

An email message file can only come from your mail server. If the server cannot be reached, or no longer has the message, QuickMail says why. It then asks whether you want to save in another format, instead of quietly writing something else. The other formats work offline for any message whose text is on this computer.

Saved web pages and PDFs leave pictures out, as the reading pane does, with each picture's description in its place.

[#728](https://github.com/kellylford/QuickMail/issues/728)

## Changed

### A new rule starts with no conditions checked

The rule editor used to open a new rule with every condition already checked — **Sender contains**, **Subject contains**, and several inside **Advanced conditions & actions** that you could not see without expanding it. The boxes beside them were empty, so the rule did not actually test anything, but the form said otherwise, and there was no telling at a glance which of the ticked conditions had anything in them.

Now **Enabled** is the only box ticked on a new rule. Check a condition when you want it. Until you do, its box is read-only and skipped by Tab, so the order is: check the condition, then Tab into its box and type.

**Create Rule from Message** (**Ctrl+Shift+T**) checks what it has filled in for you — the sender — and nothing else. The subject still comes across unchecked, as before: a rule matching one sender *and* one exact subject line matches, in practice, only the thread it was made from.

Editing an existing rule is unchanged: it opens with exactly the conditions that rule uses, checked.

---

### The rule editor stops offering what an account cannot use

On an account that can only have client-side rules — any IMAP or POP3 account, a personal Outlook.com, Hotmail or Live.com account, and a work or school account added over Standard IMAP/SMTP — the rule editor used to offer every option a server-side rule can use, and only refuse them when you pressed **Save**, by which point you had built the rule.

Those options are now turned off instead: **Sent to me**, **Sent only to me**, **Importance is**, **Set importance to**, **Forward to**, and **Stop processing more rules**. A line under **Enabled** names them and says why, and it is a Tab stop while it is there — a turned-off option is skipped by Tab, so without it they would simply go missing rather than read as unavailable. They are also turned off while you edit a rule that already runs in QuickMail, because editing never changes a rule's kind. On a Microsoft 365 account a **new** rule still offers them — choosing one is what makes the rule server-side.

[#682](https://github.com/kellylford/QuickMail/issues/682)

---

### Client-side rules can match more

A client-side rule — which is every rule on an IMAP account, on a personal Outlook.com, Hotmail or Live.com account, and on a work or school account added over Standard IMAP/SMTP — can now use four conditions that until now only a server-side rule could:

- **From addresses** and **Sent to addresses** take more than one address. A message matches when it has **any one** of them, so one rule covers a sender's several addresses instead of one rule each.
- **Sender contains** can be used together with **From addresses**. Both have to match, so you can narrow a list of addresses to the ones whose sender also carries some text.
- **Subject or body contains** looks for your text in either place. As with **Body contains**, the body a client-side rule reads is the start of the message QuickMail keeps as its preview, not the whole message.

Conditions are still ANDed with each other: a message has to satisfy every condition you checked. The choice of addresses lives inside the one condition.

**Sender contains** and **From addresses** have swapped places in the editor. **Sender contains** is now the first condition under **Apply when a message matches**, and **From addresses** has moved to **Advanced conditions & actions**. **Create Rule from Message** (**Ctrl+Shift+T**) fills in **Sender contains**, which is the right field for it: a sender reads as a name rather than an address — an organization's address book writes one as "Last, First" — and a comma in an address field is a separator, so a rule for one person would have become a rule for anyone with either name. If you made such a rule in an earlier version, opening it now shows it under **Sender contains**, unchanged, and saving it keeps it that way.

The editor already offered all four, and until now **Save** refused them on an account that can only have client-side rules. That refusal is gone. One combination is still refused, because a client-side rule has a single subject condition: **Subject contains** and **Subject or body contains** at the same time. Use one or the other.

If you go back to an earlier version, such a rule matches on the first of its addresses, and on the subject half of **Subject or body contains** — less mail than the rule covers, not more. The exception is a rule using **Sender contains** *and* **From addresses** together: an earlier version has no **Sender contains**, so it matches on the first address alone — which is not the same set of messages, and can include some this version would leave alone. Saving rules in an earlier version drops the rest either way, as it does with a rule that has several actions.

[#682](https://github.com/kellylford/QuickMail/issues/682)

---

### POP3 keeps the original of every message

POP3 accounts now keep the whole original of every message they download. Before, they kept it only for messages with attachments. This is what makes it possible to save a POP3 message as an email message file. Messages downloaded before this version that had no attachments are downloaded again when you save them, if they are still on the server. The extra copy uses some disk space, about the size of the mail itself.

[#728](https://github.com/kellylford/QuickMail/issues/728)

## Fixed

### Alt in a message opens the menu bar

While reading a message, pressing Alt opened the window's system menu (Restore, Move, Size and so on) instead of the menu bar. It now goes to the menu bar with File selected, exactly as Alt does everywhere else in the window, and Escape returns you to the message. Alt with a letter opens that menu, so Alt+F opens File, and Alt+A still goes to the attachment list. This works in the reading pane, tabs and message windows. Alt+Space still opens the system menu.

### A message could open a web page without being clicked

A specially written message could make QuickMail open a web page in your browser just by being previewed. It could also make your browser look up the sender's server as the message was shown, which tells the sender it was read. QuickMail now opens a web page only when you activate a link, and the cleanup applied to every message's formatting now catches the trick these messages used. The problem was found during the security review of saving messages.

[#728](https://github.com/kellylford/QuickMail/issues/728)

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
| [**QuickMail-0.8.47-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.47/QuickMail-0.8.47-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.47-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.47/QuickMail-0.8.47-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.47/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.47/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 8 runtime — you do not need to install .NET separately.
