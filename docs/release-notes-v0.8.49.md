# QuickMail v0.8.49 Release Notes

## Changed

### Forward is now Ctrl+Shift+F

Forward has moved from **Ctrl+F** to **Ctrl+Shift+F**, in the main window, the reading pane, and a
message opened in its own window. A message body is web content, and some screen readers use
**Ctrl+F** there for their own find command, so pressing it while reading a message never reached
QuickMail. **Ctrl+Shift+F** works wherever you are.

To make room, **Search Folders** has moved from **Ctrl+Shift+F** to **Ctrl+Shift+Y**, next to
**Ctrl+Y**, which moves to the folder tree.

If you would rather keep the old keys, give either command the key you want in **File → Settings →
Keyboard Shortcuts**. Keys you have already customized are not changed.
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
| [**QuickMail-0.8.49-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.49/QuickMail-0.8.49-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.49-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.49/QuickMail-0.8.49-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.49/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.49/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.
