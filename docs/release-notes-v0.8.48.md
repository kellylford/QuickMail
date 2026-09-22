# QuickMail v0.8.48 Release Notes

## Added

### Put your accounts in the order you want

Accounts have always appeared in the order you added them. Now you can rearrange them. Move to the
**Accounts** list, select an account, and press **Alt+Up**, **Alt+Down**, **Alt+Home** or
**Alt+End** — or use the context menu (**Shift+F10**, the Applications key, or right-click), which
offers **Move Up**, **Move Down**, **Move to Start** and **Move to End**.

Each move says where the account landed — "Moved above Work" — and leaves you on the account you
moved, so you can press the key again. At either end of the list QuickMail says so, rather than
doing nothing.

The order is not only the account list: it is the order the accounts appear in the folder tree, and
the order of the **From** list when you compose a message. Putting the account you use most at the
top puts it first everywhere. All four moves are in the command palette too, and you can give them
different keys in **File → Settings → Keyboard Shortcuts**.

### Move the Calendar to the bottom of the folder list

**Calendar** has always been the first thing in the folder tree, which puts it in the way if you
reach your mail by arrowing down from the top. Open the context menu on the **Calendar** node and
choose **Calendar at Bottom of Folder List** to move it below your accounts, or **Calendar at Top of
Folder List** to put it back. Both items are checkable, so opening the menu tells you where the
calendar is now, and the choice is remembered between sessions. Both are in the command palette as
well.

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
| [**QuickMail-0.8.48-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.48/QuickMail-0.8.48-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.48-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.48/QuickMail-0.8.48-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.48/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.48/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.
