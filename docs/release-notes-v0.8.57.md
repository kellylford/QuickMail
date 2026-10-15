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
