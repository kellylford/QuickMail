# QuickMail v0.8.65 Release Notes

## Added

### Go to Startup Folder

After moving to another folder, press **Ctrl+Shift+I**, or choose **View → Go to Startup Folder**,
to go straight back to the folder QuickMail opens in. It works like Go to Inbox in other mail
programs, where your inbox is whatever you chose as your startup folder: one account's Inbox,
All Inboxes, or All Mail if you have not chosen one. Focus moves to the message list.

## Fixed

- **Focus no longer jumps back to the message list after you have moved on.** Going to a folder
  with Enter in the folder tree, Go to Folder or Go to Startup Folder puts you in its message list
  as soon as the messages already on this computer appear. QuickMail then goes on checking the
  server, which for All Mail means every account, and when that finished it moved focus to the list
  again, pulling you back if you had gone to another pane or opened a message in the meantime. It
  now leaves you where you are.

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
| [**QuickMail-0.8.65-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.65/QuickMail-0.8.65-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.65-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.65/QuickMail-0.8.65-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.65/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.65/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.
