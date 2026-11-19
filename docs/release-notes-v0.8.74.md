# QuickMail v0.8.74 Release Notes

## Added

- **Suggest people you've sent mail to.** A new setting, **Remember people I send to and suggest them as I type an address** (Settings → General → Composing), suggests the people you have sent mail to as you type in To, Cc and Bcc, even when they are not in your address book. It is off by default. They are kept on this computer only and separately from your address book, so it does not fill up with everyone you have ever written to. **Forget People I've Sent To** clears them. (#1037)

## Changed

- **Tab enters the suggestion you just heard.** When address suggestions appear, a screen reader now hears the first one along with how many there are, for example "Jane Smith jane@uni.edu, 3 suggestions", instead of the count alone. Press **Tab** to enter it and stay in the field for the next address, or **Down** to choose another. This works in a message's To, Cc and Bcc and in an appointment's Invite field. (#1037)
- **The likeliest suggestion comes first.** People whose name, a word of their name, or address begins with what you typed are now listed before those who only contain it, so typing "jo" offers Joan before Marjorie. (#1037)


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
| [**QuickMail-0.8.74-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.74/QuickMail-0.8.74-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.74-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.74/QuickMail-0.8.74-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.74/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.74/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.

You can also install QuickMail with the Windows Package Manager: `winget install quickmail`. It picks the right version for your computer, whether it has an Intel, AMD or ARM processor, and QuickMail keeps itself up to date from then on.
