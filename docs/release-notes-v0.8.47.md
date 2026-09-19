# QuickMail v0.8.47 Release Notes

## Added

### Save and print messages

You can now save a message to a file, or print it. Until now the only thing you could save was an attachment.

- **Save** (**Ctrl+S**) saves without asking, in your default format and folder. Out of the box that is the original message, in your Documents folder.
- **Save As…** (**F12**) opens the Save dialog, where you choose the folder and the format.
- **Print…** (**Ctrl+P**) opens the Windows Print dialog.

All three are on the **File** menu and in the command palette, and Save As and Print are on a message's context menu. They work from the message list, the reading pane, a tab, and a message window, including while you are reading inside the message itself. Save and Save As act on everything selected: several messages, or a whole conversation or sender group when its heading is selected. Each message is saved in its own file, named from its subject, sender and date. Saving never replaces an existing file, and never marks a message read.

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

### POP3 keeps the original of every message

POP3 accounts now keep the whole original of every message they download. Before, they kept it only for messages with attachments. This is what makes it possible to save a POP3 message as an email message file. Messages downloaded before this version that had no attachments are downloaded again when you save them, if they are still on the server. The extra copy uses some disk space, about the size of the mail itself.

[#728](https://github.com/kellylford/QuickMail/issues/728)

## Fixed

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
