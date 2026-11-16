# QuickMail v0.8.73 Release Notes

## Changed

- **Updated mail and storage libraries.** QuickMail now uses MailKit 5, the library behind its IMAP, POP3 and SMTP connections and the reading and building of messages, and version 11 of its SQLite library for the local mail store. Nothing should look or sound different. Your existing mail store opens as before. If something about connecting, sending or reading mail behaves differently than in 0.8.72, please report it.
- **Messages with very long headers keep working.** The new mail library rejects by default any message with a single header longer than 16 KB, such as a To or Cc line with a few hundred people on it. QuickMail raises that limit, so such a message is still collected over POP3, opens from a .eml file, and can be attached or copied to another account.
- **A reply or forward goes through when the original is slightly malformed.** Replying to a message whose Message-ID is malformed sends the reply without linking it to the original, and an attachment whose type can't be read goes out as a generic file, instead of the send failing.
- **A clearer message when an attachment can't be read.** If the mail server sends an attachment in a form that can't be read, saving it now says so, instead of "Object reference not set to an instance of an object."

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
| [**QuickMail-0.8.73-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.73/QuickMail-0.8.73-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.73-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.73/QuickMail-0.8.73-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.73/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.73/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.

You can also install QuickMail with the Windows Package Manager: `winget install quickmail`. It picks the right version for your computer, whether it has an Intel, AMD or ARM processor, and QuickMail keeps itself up to date from then on.
