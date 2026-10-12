# QuickMail v0.8.55 Release Notes

## New: move your settings to another computer

**File → Export Settings…** saves your QuickMail setup in one file, and **File → Import
Settings…** brings it in on another computer (#507). Both are off until you turn them on: in
**Settings → Advanced**, check **Allow exporting and importing settings** on each computer.

The file carries your accounts and signatures, rules, saved views, flags, watched
conversations, address book, message templates, keyboard shortcuts, themes, message list
layout, Local Calendar and preferences. You choose which of those to include. It never
contains a password or a sign-in.

Before anything changes, the import shows a preview: what is in the file, what it replaces on
this computer, and what will happen to each account. QuickMail then restarts to bring the
settings in, and lists the accounts that still need signing in on this computer so you can sign
in to each from one place.

- This computer ends up matching the file, so importing the same file twice gives the same
  result. Accounts are never removed: one that is already here keeps its sign-in and takes the
  file's settings, and one that is not is added.
- Anything that cannot be matched here is turned off and named rather than pointed at a guess:
  a rule for an account that is not on this computer, or a Microsoft 365 rule for a folder that
  account does not have.
- A copy of your previous settings is kept first, and if the import fails part-way your
  settings are put back from it.

The exported file also works as a backup of your settings. See *Moving Your Settings to Another
Computer* in the User Guide.

## Fixed

### A repeating appointment exported to a calendar file lost its deleted occurrences

Exporting a repeating appointment as a `.ics` file, or saving one to an iCloud or other CalDAV
calendar, left out the occurrences you had deleted from the series, so the file, or the copy on
the calendar server, still had them. The deleted occurrences are now written out with the series.

### Copying a whole message copied only its first line

With a screen reader running, pressing Ctrl+A and then Ctrl+C in an open message could copy only
its first paragraph, so pasting it elsewhere gave you just the opening line. This happened in the
reading pane, in a message opened in its own window, and in the Markdown preview. The whole message
is copied now.

The message's subject is still spoken when you open a message, and the window title is unchanged.

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
| [**QuickMail-0.8.55-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.55/QuickMail-0.8.55-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.55-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.55/QuickMail-0.8.55-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.55/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.55/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.
