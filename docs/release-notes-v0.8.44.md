# QuickMail v0.8.44 Release Notes

## Fixed: the address book reads out contacts again

Moving through the list of contacts in the address book announced every row as
*"QuickMail.Models.ContactModel"* followed by its position — the same thing for all 35 of your
contacts, with no way to tell one from the next except by opening it. The list of a group's
members was silent in exactly the same way. The Groups list happened to escape it, but only by
accident: it had the same underlying fault and was one small edit away from going silent too.

The rows carried the right text all along; it was attached to the wrong part of the row. Screen
readers read a list row's name from the row itself, and the address book had put the name on
something drawn inside the row instead — which looks identical on screen, and is why this went
unnoticed. Every other list in QuickMail already put it in the right place; these three now do
too.

A contact row reads as its name, its address, and where it came from — *"Alice Adams,
alice@example.com, Local address book"* — or with each part labelled, if you have turned on field
labels in the contact list. A contact saved with no name reads as its address and where it came
from. A group reads as its name and how many members it has, and a group's member reads as name
and address.

As a safety net for any list of contacts anywhere in QuickMail, a contact with nothing else to say
for itself now falls back to reading its name and address rather than its internal type name, so
this particular kind of silence cannot come back through some other list.
([#644](https://github.com/kellylford/QuickMail/issues/644))

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
| [**QuickMail-0.8.44-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.44/QuickMail-0.8.44-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.44-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.44/QuickMail-0.8.44-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.44/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.44/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 8 runtime — you do not need to install .NET separately.
