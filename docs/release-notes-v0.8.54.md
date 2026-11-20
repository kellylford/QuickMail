# QuickMail v0.8.54 Release Notes

## Fixed

### QuickMail could close while you were writing a message

With a screen reader running, pressing Enter at the end of a message just after a table, to start
a new line below it, could close QuickMail on the spot, with no warning, and lose any text that
had not been saved. Pressing Enter there adds a row to the table, and QuickMail now keeps what it
tells screen readers up to date when that happens. (#782)

### A failed draft save could leave no draft on the server

For accounts that use IMAP, saving a draft replaced the earlier copy on the server by deleting it
first and then saving the new one. If the connection dropped in between, neither copy was left.
QuickMail now saves the new copy first and removes the old one only once that has worked. At worst
you will see an extra copy of the draft in your Drafts folder. (#780)

### Auto-save and changes made during a save

Anything you typed while a draft was being saved could be treated as already saved, so auto-save
skipped it until you typed again. Those changes are now saved on the next pass. If auto-save cannot
work out whether there is anything to save, it now tells you once that auto-save failed, as it does
when a save itself fails, rather than saying nothing.

QuickMail also now notes in its log why it skips an auto-save while there are unsaved changes, to
help track down a case where auto-save stopped for several minutes without saying why. (#781)

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
| [**QuickMail-0.8.54-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.54/QuickMail-0.8.54-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.54-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.54/QuickMail-0.8.54-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.54/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.54/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.
