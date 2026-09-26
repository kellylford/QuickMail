# QuickMail v0.8.50 Release Notes

## Added

### Import .ics files into your calendar

QuickMail could export an appointment as a `.ics` file but could not bring one in. Now it can.
Choose **File → Import Calendar File (.ics)…** (it is also on the Calendar node's context menu and in
the command palette), pick one or more `.ics` files, and the appointments go into the **Local
Calendar**. QuickMail then shows the calendar with the first imported appointment selected and tells
you how many were imported and how many were skipped.

- Importing the same file twice updates the appointments instead of adding copies.
- Repeating appointments keep their pattern. An occurrence the file moved to another time shows on
  its new day, and occurrences the file removed stay removed.
- Cancelled appointments are skipped. A file with a meeting invitation in it is imported as a plain
  appointment, and nothing is sent to the organizer.
- A file with nothing QuickMail can use gets a message saying so.

The command has no default key; you can assign one in **File → Settings → Keyboard Shortcuts**.

## Fixed

### Reminder text no longer replaces an appointment's description

Calendar files from Google and Outlook carry a reminder inside each appointment, and the reminder has
its own description ("This is an event reminder"). QuickMail read that as the appointment's
description, so an appointment from such a file lost its real notes. This affected iCloud calendar
sync as well as the new import.

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
| [**QuickMail-0.8.50-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.50/QuickMail-0.8.50-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.50-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.50/QuickMail-0.8.50-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.50/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.50/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.
