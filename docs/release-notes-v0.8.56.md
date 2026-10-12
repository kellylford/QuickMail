# QuickMail v0.8.56 Release Notes

## New: folders of your own on a POP3 account

A POP3 account used to have only Inbox, Sent, Drafts and Trash, so there was nowhere to file a
mailing list or anything else (#794). Now you can create folders on a POP3 account the same way as
on any other: **Folder → New Folder…**, the folder tree's context menu, or **New Folder** in the
folder picker, including while choosing a rule's folder. Move and copy messages into them, point
rules at them, and move, copy or delete them like any other folder.

A POP3 server has no folders, so these folders and the mail in them exist only on this computer.
Deleting one moves its mail, and the mail of any folders inside it, to Trash. Inbox, Sent, Drafts
and Trash stay as they are and cannot be moved or deleted. Exporting your settings does not
carry these folders or their mail to another computer.

## Fixed

### A rule whose move or delete failed could lose the message

When a rule could not move or delete a message (because the folder it files into had been
deleted, say, or the connection dropped), QuickMail still treated the message as gone and took it
out of its folder on this computer. On an IMAP or Microsoft 365 account the message was still on
the server and came back later. On a POP3 account this computer holds the only copy, so the
message was lost. Now a message stays where it is when its rule's move or delete fails (#794).

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
| [**QuickMail-0.8.56-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.56/QuickMail-0.8.56-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.56-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.56/QuickMail-0.8.56-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.56/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.56/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.
