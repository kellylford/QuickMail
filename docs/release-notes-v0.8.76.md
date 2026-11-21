# QuickMail v0.8.76 Release Notes

## Changed

- **Favorite folders.** A new **Favorites** section in the folder tree holds the folders you use most, from any of your accounts. Select a folder and press **Ctrl+D** to add it; press **Ctrl+D** on a favorite to remove it. The same toggle is **Favorite Folder** on the folder tree's context menu and on the **Folder** menu, checked when the folder is already a favorite, and **Toggle Favorite Folder** in the Command Palette. In the folder tree it acts on the folder you are on; anywhere else, on the folder whose messages you are reading. A favorite is the folder itself: opening it opens the folder with its unread count. With more than one account each favorite says whose it is, for example "GitHub - Kelly". New favorites go at the end, and favorites go with their account when you export your settings. (#1056)
- **Arrange the folder tree.** The top of the folder tree is now one list of Calendar, Favorites, Views, All Mail and each of your accounts, and you can put them in any order. In the folder tree, **Alt+Up**, **Alt+Down**, **Alt+Home** and **Alt+End** move the account or section you are on past whatever is beside it, and inside Favorites they move a favorite. The same four moves are on a new **Reorder** submenu of the folder tree's context menus. Each move says where the item landed, for example "Moved above Views", and leaves you on it. Moving an account past another account also changes their order in the Accounts list and the compose **From** list; moving it past a section only changes where it sits in the tree. The order is remembered and travels with exported settings. The four commands, which used to be named Move Account Up and so on, are now **Move Up**, **Move Down**, **Move to Start** and **Move to End**, and keys you gave them still work. (#1058)
- **Calendar at Top of Folder List and Calendar at Bottom of Folder List are gone.** Calendar now moves like any other section: press **Alt+End** on it to put it at the bottom, or use **Reorder** on its context menu. If you had put Calendar at the bottom, it stays there. (#1058)


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
| [**QuickMail-0.8.76-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.76/QuickMail-0.8.76-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.76-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.76/QuickMail-0.8.76-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.76/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.76/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.

You can also install QuickMail with the Windows Package Manager: `winget install quickmail`. It picks the right version for your computer, whether it has an Intel, AMD or ARM processor, and QuickMail keeps itself up to date from then on.
