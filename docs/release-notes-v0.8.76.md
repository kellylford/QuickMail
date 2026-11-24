# QuickMail v0.8.76 Release Notes

## Changed

- **Favorite folders.** A new **Favorites** section in the folder tree holds the folders you mark as favorites, from any of your accounts. Select a folder and press **Ctrl+D** to mark it; press **Ctrl+D** on a favorite to remove it. The section is not in the folder tree until you mark your first favorite, and it disappears again if you remove every favorite. (#1056)
- **Other ways to mark a favorite.** **Favorite Folder** is on the folder tree's context menu and on the **Folder** menu, checked when the folder is already a favorite, and **Toggle Favorite Folder** is in the Command Palette. In the folder tree these act on the folder you are on; anywhere else, on the folder whose messages you are reading. (#1056)
- **A favorite opens its folder.** A favorite is the folder itself, not a copy: opening it opens the folder, and it shows the folder's unread count. With more than one account, each favorite says whose it is, for example "GitHub - Kelly". (#1056)
- **Arrange the folder tree.** The top of the folder tree is now one list of Calendar, Favorites, Views, All Mail and each of your accounts, and you can put them in any order. In the folder tree, **Alt+Up**, **Alt+Down**, **Alt+Home** and **Alt+End** move the account or section you are on past whatever is beside it. Each move says where the item landed, for example "Moved above Views", and leaves you on it. (#1058)
- **Arrange your favorites.** Inside Favorites, the same four keys move a favorite among the others. A new favorite goes at the end. (#1056, #1058)
- **Reorder on the context menu.** The four moves are also on a new **Reorder** submenu of the folder tree's context menus. (#1058)
- **Account order follows the folder tree.** Moving an account past another account also changes their order in the Accounts list and the compose **From** list. Moving an account past a section only changes where it sits in the tree. (#1058)
- **The folder tree's order and your favorites are in exported settings.** QuickMail remembers the order you choose, and both it and your favorites travel with your settings when you export them. Favorites go with their account. (#1056, #1058)
- **Move Account Up and the other account moves are renamed.** The four commands are now **Move Up**, **Move Down**, **Move to Start** and **Move to End**, since they move sections and favorites too. Keys you gave them still work. (#1058)
- **Calendar at Top of Folder List and Calendar at Bottom of Folder List are gone.** Calendar now moves like any other section: press **Alt+End** on it to put it at the bottom, or use **Reorder** on its context menu. If you had put Calendar at the bottom, it stays there. (#1058)
- **Send and Receive with F9.** Press **F9**, or choose **Message → Send and Receive**, to send and check for mail in one step. QuickMail sends anything waiting in the Outbox, then checks the Inbox of every account and says how many new messages arrived, for example "2 new messages" or "No new messages". **F5** still refreshes only the folder you are in. (#874)
- **Open in New Window and Open in New Tab are on the Message menu.** Open in New Window uses **Ctrl+Enter**, and you can give either command a different key in **Settings → Keyboard Shortcuts**. (#1010)
- **Rename a folder.** Select a folder in the folder tree and press **F2**, type the new name and press **Enter**. **Rename Folder…** is also on the folder tree's context menu, the **Folder** menu and the Command Palette. It works in every kind of account, and you land back on the folder with its new name. (#1083)
- **Rules and settings follow a renamed folder.** Rules that file mail into the folder, or into a folder inside it, are updated for you. So are saved views, the startup folder, favorites, hidden folders and the folder you last moved messages to, and mail already downloaded stays downloaded. QuickMail says what it updated, and tells you if something couldn't be. (#1083)
- **Rename a folder in a folder picker.** In **Move to Folder**, **Copy to Folder**, a rule's folder picker and **Go to Folder**, press **F2** on a folder to rename it without leaving the picker. (#1083)
- **Standard folders keep their names.** Inbox, Sent, Drafts, Trash, Junk, Archive and a POP3 account's four built-in folders can't be renamed, as in Outlook. QuickMail says why if you try. (#1083)

## Fixed

- **Ctrl+Enter opens a message in a new window.** Settings said Ctrl+Enter did this, but it opened the message the same way Enter does. Now, in the message list, Ctrl+Enter always opens the message in its own window, whatever your **Message Open Mode** is. On a draft or a message in the Outbox, Ctrl+Enter still opens it for editing. (#1010)
- **Move to Archive in a message window.** A message window now has **Move to Archive** on its **Message** menu, on **Ctrl+Shift+M** as in the main window. The window closes afterward, as it does after Delete. If the account has no Archive folder, the window stays open and tells you. (#1010)
- **The Flagged filters are in the Command Palette.** **Show Flagged Only** and one **Show Flagged** command for each of your flags are now in the Command Palette, and you can give them keys, like the other filters. (#1010)
- **A setting that did nothing is gone.** **Confirm before closing a tab with unsaved changes** in **Settings → Windowing** never did anything, because a tab only ever shows a message, which has nothing to save. It has been removed. (#1010)
- **Deleting in an account with no Trash folder.** Delete used to take the messages out of the list without saying anything, and the server kept them all. QuickMail now asks whether to delete them permanently. If you choose **No**, they stay in the list. (#999)
- **The Sent to condition is easier to find.** In the rule editor, **Sent to any of these addresses** used to be in the **Advanced conditions & actions** section, which starts collapsed, below the actions. It is now in the main conditions, right after **From any of these addresses**. (#1065)
- **Sent to now checks Cc as well as To.** Rules that QuickMail runs itself checked only the To line, so a rule never matched mail that only copied you. That covers every IMAP and POP3 rule, and Microsoft 365 rules that use **Mark as read** or **Play a sound**. These rules now check both To and Cc, the same as Outlook's "Sent to" condition, for every kind of account. (#1065)
- **Sent to matches whole addresses.** `bob@example.com` no longer matches `jimbob@example.com`, and an address that appears only in someone's display name doesn't count. `@example.com` still matches any address at that domain. (#1065)
- **Create Rule from Message fills in Sent to.** The rule now starts with the message's To and Cc addresses in **Sent to any of these addresses**, except your own address in that account. The box starts unchecked, like the subject. To make a rule for mail sent to a mailing list or one of your aliases, delete the addresses you don't want and check the box. (#1065)
- **Mail you already had.** New mail works with these changes as soon as you update. For mail QuickMail downloaded before this version, the Cc line is available right away in POP3 accounts and for messages you had opened. Other older mail gets it the next time you open its folder. Until then, **Run on Existing Mail** in the Rules window checks only the To line of that older mail. (#1065)
- **Opening a message could show one you had replied to.** With messages set to open in a new window, replying to a message from its window could leave QuickMail stuck on it: every message you opened afterwards, with Enter, the mouse or **Open in New Window**, showed the one you had replied to, until you quit QuickMail. Forwarding, or opening or saving an attachment from that window, could do the same. Opening a message now always shows that message. (#1016)
- **Reply from a message window goes to that window's message.** After you moved to another message in the window with **Next** or **Previous**, Reply, Reply All and Forward could go to a different message: the newest message of the conversation selected in the message list. (#1016)
- **Delete in the calendar deletes the appointment.** If an email was still selected from before you opened the calendar, pressing **Delete** on an appointment could move that email to Trash instead. Now, in the calendar, **Delete** acts only on the appointment. (#1014)
- **Other mail keys do nothing to an email while you're in the calendar.** **Ctrl+Q**, **Ctrl+Shift+M**, **Ctrl+P** and the other keys that act on the selected email used to reach an email left selected from before. In the calendar they now do nothing. To act on an email you opened from an appointment, move to the message first. (#1014)
- **The Message menu and toolbar don't act on a hidden email.** While the calendar is showing, **Delete** and **Move to Archive** on the **Message** menu and the toolbar no longer act on an email you can't see. (#1014)
- **Keyboard Shortcuts warns about QuickMail's own keys.** Settings warned you only when a key was one you had assigned yourself. If you chose a built-in key, such as **Ctrl+Shift+B** for the address book, it gave no warning, and the built-in command quietly stopped working. Now the warning names that command. (#1044)
- **A built-in command can be left with no shortcut.** If you choose **OK** at that warning, the command that had the key is left with no shortcut until you use **Restore Default** on it. This lasts after you restart QuickMail and when you export your settings. (#1044)
- **The arrow keys choose a flag color.** In the Flag Manager, the arrow keys now choose a color as they move. They used to move between colors without choosing one until you pressed Space. (#1080)
- **All four arrow keys go through every flag color.** Up and Down used to reach only the color above or below, because the twelve colors sit in two rows. Now all four arrow keys go through the colors in order. (#1080)
- **The flag's current color is selected.** When you Tab to the colors, you land on the flag's own color. The built-in flag's color can't be changed, so its colors are unavailable when it is selected. (#1080)


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
