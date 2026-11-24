# QuickMail v0.8.77 Release Notes

## Fixed

- **Blank lines in a message you write are read as blank.** Arrowing onto an empty line in the message body read the line after it, so that line was heard twice, once on the blank line and again when you reached it. Screen readers now say "blank" there, in every compose mode and in the signature box. Replies no longer read "On … wrote:" twice either. (#891, #1018, #1042)
- **HTML mode: lines are read once when you arrow onto a shorter line, or next to a list.** Moving up or down onto a line shorter than the one you left could read it together with the line after it, so a line just above a bulleted list was read with the first bullet ("Gamma • Item one"). An empty line or empty bullet next to a list is now read as "blank". (#891)
- **Ctrl+Up and Ctrl+Down move by paragraph in Plain Text and Markdown.** They used to jump to the very start or end of the message. Now Ctrl+Down goes to the start of the next paragraph and Ctrl+Up to the start of this one, then the one before, as in Word and Notepad. A paragraph is a line ended by Enter, and an empty line counts as one. Screen readers that read the paragraph as you move now get just that paragraph, not the whole message. (#1077)


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
| [**QuickMail-0.8.77-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.77/QuickMail-0.8.77-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.77-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.77/QuickMail-0.8.77-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.77/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.77/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.

You can also install QuickMail with the Windows Package Manager: `winget install quickmail`. It picks the right version for your computer, whether it has an Intel, AMD or ARM processor, and QuickMail keeps itself up to date from then on.

Note: a new release can take a while to appear in winget, because a volunteer moderator approves each one. If you installed QuickMail with winget, its automatic updates, built in and on by default, keep you current. Winget is meant for installing QuickMail the first time, or for scripts that need an unattended install.
