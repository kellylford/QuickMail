# QuickMail v0.8.59 Release Notes

## Changed

### Create Rule from Message fills in the sender's address and their name

**Create Rule from Message** (`Ctrl+Shift+T`) now puts the sender's email address in **From any of
these addresses**, checked, so the rule matches everything from that address and nothing from
anyone else using the same name. Their name goes in **Sender contains**, unchecked; check it as
well if you want a rule that needs both the address and the name. If QuickMail doesn't have the
sender's address yet, it fills in the name alone, as before.

### The rule editor's address conditions

**From addresses** has moved out of **Advanced conditions & actions** to sit straight after
**Sender contains**, so the sender's name and address are together. It is now called **From any
of these addresses**, and **Sent to addresses** is now **Sent to any of these addresses**, since
a message matches when it has any one of the addresses listed.

In the Rules Manager's list, a client-side rule now reads "If from" and the address, or "sent
to", the same way a Microsoft 365 server-side rule already did, rather than "from contains" or
"to contains".

## Fixed

### Switched-off From addresses are kept

If you switched **From any of these addresses** off and **Sender contains** on, then saved, the
addresses were thrown away, so switching back found an empty box. They are now kept, switched
off, like the text of every other condition you turn off.

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
