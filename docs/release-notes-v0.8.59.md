# QuickMail v0.8.59 Release Notes

## Fixed

### Auto-save no longer stops after a save that never finishes

If saving a draft hung, for example on a connection that had quietly dropped, auto-save stopped
for the rest of the session without saying so, and edits made after that could be lost. A save
that does not finish within about 35 seconds is now given up on: the draft is kept on this
computer, as it already is when you are offline, and auto-save carries on. Saving with **Ctrl+S**
and saving when you close the window give up the same way, so the window can always close. If the
abandoned save reaches the server later, QuickMail tidies up so you are not left with two copies
(#781).

### A sender is listed once in a conversation's Properties

**Properties** (**Alt+Enter**) on a conversation listed a sender twice in Participants when they
had written once with a display name and once without: once as their name and address, and again
as the address alone. They are now listed once, with their name. Someone who never gave a name is
still listed by their address (#814).

Contributed by [valiant8086](https://github.com/valiant8086).

### Address book groups say which group they are, and how many members they have

In the address book's Groups list and in the Group Manager, a group with no members was shown and
read as just "Empty group", so several empty groups could not be told apart. It now gives its name
first, such as "Work, no members" (#649).

The member count beside each group's name in the address book was never drawn on screen. It now shows
beside the name, and the heading above the member list reads "Members of Work".
Screen readers now read both headings as shown, instead of a fixed label (#650).

### Rows in All Mail say their folder as soon as the folder list arrives

In All Mail and the other views that gather several folders, each message says which folder it is
in. When the message list appeared before QuickMail knew the folder names, such as on the first
start after an upgrade, those messages said no folder until you left the view and came back. They
now pick up the folder as soon as the list arrives, and pick up a folder's new name when it is
renamed (#451).

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
| [**QuickMail-0.8.59-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.59/QuickMail-0.8.59-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.59-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.59/QuickMail-0.8.59-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.59/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.59/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.
