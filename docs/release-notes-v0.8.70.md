# QuickMail v0.8.70 Release Notes

## New

- **The sound gallery shows which schemes you already have.** In **Settings → Sounds → Browse schemes…**, a scheme you have added says "Added." When the gallery has a newer version of it, it says "Update available." and **Add** becomes **Update**, which replaces your copy's sounds with the new version after asking. Your copy keeps its name. (#937)

## Changed

- **Sound schemes work the same way in QuickMail and The Chat Place.** A `.qmsounds` file now says it is for QuickMail and keeps the scheme's description. If you import a scheme made for another app, QuickMail says which app it is for. (#937)
- **Exported schemes hold WAV files only.** An MP3 of your own stays out of an exported scheme, since other apps can't play it, and **Export scheme…** says how many events have no sound in the file because of it. Schemes exported from QuickMail 0.8.69 still import with their MP3s. (#937)
- **One bad sound no longer stops an import.** A sound file in a scheme that is too large or damaged is left out, and QuickMail says how many events have no sound, instead of refusing the whole scheme. (#937)

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
| [**QuickMail-0.8.70-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.70/QuickMail-0.8.70-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.70-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.70/QuickMail-0.8.70-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.70/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.70/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.
