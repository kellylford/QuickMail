# QuickMail v0.8.68 Release Notes

## Fixed

### Focus stays where you left it

Focus in the message list could move with no key pressed. It should only move when you do something, or when a dialog comes up. These no longer move it:

- **New mail arriving.** Focus and selection could jump to a different message when new mail came in, and Delete or Archive would then act on that message. Focus now stays on the message you were on, however much mail arrives.
- **Coming back online, or finishing the startup sync.** QuickMail reloads the open folder at these moments. A reload used to put you at the top of the list, even if QuickMail was in the background at the time. It now keeps your place, and it no longer closes an open message or clears the search box when a saved view is open.
- **Refresh (F5).** Refreshing now keeps you on the same message even when new mail changes the position of the rows. It used to keep the position rather than the message.
- **Conversations, From and To views.** A sync with nothing new no longer rebuilds the tree. When mail does change it, you stay on the same group, or on the same message inside an expanded group, instead of going back to the top. Returning to QuickMail in To view now puts focus back in the list, too.

When the message you are on really is gone:

- **Deleted in another mail client.** You land on the next message rather than the top of the list.
- **Deleted while you are reading it.** The message closes and focus goes to the next message, rather than staying on an empty page.
- **No longer matching the list while open**, such as a message you read in the Unread view. Nothing is selected until you close it, so Delete and Archive cannot act on a message you cannot see.

Choosing a folder still starts at the top of that folder.

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
| [**QuickMail-0.8.68-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.68/QuickMail-0.8.68-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.68-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.68/QuickMail-0.8.68-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.68/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.68/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.
