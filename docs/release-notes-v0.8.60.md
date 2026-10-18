# QuickMail v0.8.60 Release Notes

## Fixed

### Server rules made in QuickMail work with Outlook Classic again

Server-side rules that QuickMail made on a Microsoft 365 account could break rule management in Outlook Classic. Outlook could not show them, and when it saved its own rules it reported that "one or more rules cannot be uploaded" and switched QuickMail's rule off. There were two causes, and both are fixed (#810).

- **Names on addresses are kept.** A rule's From, Sent to and Forward to entries now keep the name that goes with each address, as Outlook expects. Editing a rule made in Outlook no longer strips those names. The address boxes show a named entry as `Name <address>`, and you can type entries in that form yourself.
- **Mark as read is now a client-side action**, as it is in Outlook Classic. A new rule that marks messages as read is saved as a client-side rule, and its checkbox reads "Mark as read (client-side only)". A server-side rule that already marks messages as read is never changed for you. Editing one explains why it can't be saved as it is; to save your changes, clear the Mark as read checkbox, then press Save.

### Moving to the next unread message no longer reads another message first

Pressing **Alt+Down** or **Alt+Up** to move to the next or previous unread message could make screen readers speak the subject of some other message before the one you landed on. The subject came from a tooltip that opened when the list scrolled under the mouse pointer. Message rows no longer have that tooltip. The subject is still on screen and read as part of each row (#638).

### Focus stays in the address book list after you delete something

Deleting a contact, a group, or a member of a group in the address book left keyboard focus nowhere, so the next arrow key started again from the top of the list. Focus now moves to the next item in the list, or to the one before it if you deleted the last. With a search typed in, it moves to the next match still showing. Screen readers read where you landed, and the arrow keys carry on from there (#265).

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
| [**QuickMail-0.8.60-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.60/QuickMail-0.8.60-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.60-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.60/QuickMail-0.8.60-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.60/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.60/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.
