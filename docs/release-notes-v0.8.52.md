# QuickMail v0.8.52 Release Notes

## Added

### Start QuickMail when you sign in to Windows

QuickMail can now start by itself when you sign in to Windows. It is off unless you turn it on: in
**File → Settings → Startup**, check **Start QuickMail automatically when I sign in to Windows**. With
**Start minimized** (on by default) it starts without opening its window over what you are doing — in
the notification area if you keep QuickMail running there, otherwise minimized on the taskbar.

QuickMail uses the standard Windows startup list, so it appears in Task Manager's **Startup apps** and
in Windows **Settings → Apps → Startup**, and you can turn it off from either. When you do,
QuickMail's own setting shows it as off. Uninstalling QuickMail removes the startup entry. This is
available in the installed version; a portable copy can be moved, which would leave Windows pointing at
nothing. (#770)

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
| [**QuickMail-0.8.52-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.52/QuickMail-0.8.52-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.52-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.52/QuickMail-0.8.52-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.52/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.52/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.
