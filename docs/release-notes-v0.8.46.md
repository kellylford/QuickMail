# QuickMail v0.8.46 Release Notes

<!-- What is new in 0.8.46 goes here, above the footers: Changed/Fixed/Added sections, each ending with its issue link and a --- divider. -->

## Added

### The command palette filters as you type

The command palette (**Ctrl+Shift+P**) opens with a filter box, and typing narrows the list to what matches instead of only jumping to a name that starts with what you typed.

You do not have to know how a command's name begins. Typing `arch` finds **Move to Archive**; `gtf` finds **Go to Folder** from its initials; `mail` lists the Mail commands by category; and `arch mail` finds the Mail category's Archive command with the words in either order. The best match is always at the top and is what **Enter** runs.

Focus stays in the filter box the whole time, so you can keep typing to narrow further without moving anywhere first. The command at the top is reported as it changes, and so is each command you reach with **Up** and **Down** — by Windows itself, from the list, rather than by QuickMail announcing over the top of it, so no announcement setting can silence it. When a keystroke narrows the list without changing the command at the top, you hear the new count on its own — "3 commands" — since nothing else would tell you the list had moved under you.

**Escape** clears the filter if you have typed something and closes the palette if the box is already empty. **Page Up** and **Page Down** move ten at a time. When nothing matches you hear "No matching commands", and Enter says so rather than doing nothing silently.

This applies to every palette in the app, not just the main window's — the compose window, an open message, the address book, the rules window and the rest all get it.

A search box shipped here once before and was taken out again, because it moved focus onto the list on every keystroke and announced a new top match on every character. This one does neither: focus never leaves the box, and nothing is announced that Windows already reports.

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
| [**QuickMail-0.8.46-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.46/QuickMail-0.8.46-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.46-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.46/QuickMail-0.8.46-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.46/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.46/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 8 runtime — you do not need to install .NET separately.
