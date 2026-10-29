# QuickMail v0.8.63 Release Notes

## Changed

### Updated mail and encryption libraries

QuickMail now uses MailKit and MimeKit 4.18.1, which bring the Bouncy Castle encryption library up
to 2.7.0.

### Contact addresses no longer in the debug log

With debug logging on, QuickMail no longer writes the email address of each contact you save
to its log.

### Shortcut keys are reported to screen readers

Menu items, toolbar buttons and context menu items now give screen readers their shortcut keys,
so Reply's Ctrl+R can be heard along with it. Until now most menus only showed the key on
screen. Your screen reader's settings decide whether the key is read out. (#849)

- Keys you reassign in **Settings → Keyboard Shortcuts** now show in the menus, toolbar tooltips
  and command palette, and are the keys screen readers are given. Before, all of them kept
  showing the original key.
- Toolbar buttons no longer read their name and shortcut twice.
- A key you assign to a command that has none by default, such as Calendar at Bottom of Folder
  List, now shows on its menu item too, in the context menus and the Help menu as well as the
  main menus. (#854)
- The message list's context menu now shows the keys for Reply, Reply All, Forward and Delete,
  and the folder menu shows Delete for Delete Folder.

## Fixed

- **First-letter navigation in Settings → Keyboard Shortcuts.** Typing a letter in the list of
  commands now moves to the next command whose name starts with it, as in the folder list.
  Before, typing a letter did nothing. (#852)
- **Ctrl+, opens Settings.** The File menu has long shown Ctrl+, for Settings, but the key did
  nothing. (#849)

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
| [**QuickMail-0.8.63-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.63/QuickMail-0.8.63-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.63-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.63/QuickMail-0.8.63-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.63/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.63/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.
