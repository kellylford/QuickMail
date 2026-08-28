# QuickMail v0.8.43 Release Notes

<!-- Add "What is new" sections here (Changed/Fixed/New), then the Reporting Issues footer, then the Download footer last. -->

## Changed: a bug report now says which kind of account you use

A report sent with **Report a Bug** already describes the setting it happened in — your QuickMail
version, your Windows version, the theme, the view, the sort order, and where messages open. It did
not say how your accounts connect, and that turns out to be one of the first things worth knowing:
an account connected over IMAP, over POP3, and through Microsoft 365 genuinely behave differently
from one another, so a report that leaves it out can take a while to place.

Reports now include one more line — how many accounts you have set up and the protocols they use,
for example *"2 (IMAP, Microsoft 365)"*. It is the protocol names and a count, nothing else: no
address, no server name, no account name. As always the **Preview** in the report window shows the
complete text before you send it, so you can read exactly what is going.
([#639](https://github.com/kellylford/QuickMail/issues/639))

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
| [**QuickMail-0.8.43-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.43/QuickMail-0.8.43-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.43-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.43/QuickMail-0.8.43-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.43/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.43/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 8 runtime — you do not need to install .NET separately.
