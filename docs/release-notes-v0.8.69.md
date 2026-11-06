# QuickMail v0.8.69 Release Notes

## New

- **Copy, cut and paste messages, as in Outlook.** In the message list, press **Ctrl+C** to copy messages or **Ctrl+X** to cut them, then go to a folder in the folder tree and press **Ctrl+V**. Copied messages are copied there; cut messages move there. As in Explorer, cutting removes nothing until you paste. **Ctrl+V** in the message list pastes into the folder it is showing. The commands are also on the **Message** menu and the context menus. (#929)
- **Paste a copied message into a new message to attach it.** A message copied with **Ctrl+C** pastes into a message you are writing as an attached email, as Outlook does, and into Explorer or onto the desktop as an email file. (#929)
- **Copy attachments.** Press **Ctrl+C** on an attachment, or choose **Copy** or **Copy All** in the attachment list's context menu, then paste the files into Explorer or into a message you are writing. (#929)
- **Move and copy messages between accounts.** **Move to Folder**, **Copy to Folder** and paste now offer your other accounts' folders. A copy leaves the original where it was; a move puts the original in its own account's Trash once the copy is safely in place. Microsoft 365 accounts cannot take messages from other accounts yet. (#929)
- **Open email files.** An email attached to a message now opens in QuickMail, in its own window, instead of being handed to Windows. A .eml file in Explorer, such as a message you pasted there, opens in QuickMail too: it is offered in **Open with**, without becoming the default. Reply and Forward go from your default account. (#939)
- **Save a message's attachments without opening it.** Open the context menu on a message in the message list (Shift+F10 or the Applications key) and choose **Save All Attachments…**. QuickMail asks for a folder and saves every attachment there. It works on several selected messages at once, and on a whole conversation or sender group when its heading is selected. If there is nothing to save, QuickMail says the message has no attachments instead of asking for a folder. It is in the command palette too, and you can give it a key in keyboard customizations. (#931)
- **Sound schemes: make your own set of sounds.** The Style box in **Settings → Sounds** is now **Sound scheme**, and each scheme keeps its own choices: changing an event in Glass no longer changes Wood. Choose **New scheme…** to start a scheme of your own with every event silent, then give sounds only to the events you want, using any built-in sound in any tone or your own files. **Copy scheme…** starts from the scheme showing instead. Schemes of your own can be renamed and deleted. Your existing choices move into the scheme you had chosen. (#932)
- **Reset a built-in scheme.** **Reset scheme…** puts Glass, Soft bell or Wood back to how it ships, so you no longer need to remember what each event used to be. (#932)
- **Share a scheme.** **Export scheme…** saves a scheme and its sound files as one `.qmsounds` file, and **Import scheme…** adds one. If you make a set of sounds you like, we would love to hear it. (#932)
- **Browse a gallery of sound schemes.** **Browse schemes…** in **Settings → Sounds** lists schemes made and shared by others, and **Add** brings one in. Every scheme in the gallery is checked before it is published, and QuickMail checks the gallery's signature and each file again before using it. Want to make one? See [the gallery on GitHub](https://github.com/kellylford/quickmail-sounds). (#937)
- **Remove a sound file.** **Remove file…** deletes one of your own sound files from QuickMail, after saying which schemes and rules still use it. Until now files could only pile up. (#932)
- **Choose the default rule sound.** "A rule matched" is now in the list of events, and is what a rule set to **Default sound** plays. (#932)

## Changed

- **The message list knows which IMAP messages have attachments.** Until now an IMAP message showed as having attachments only after you had opened it. QuickMail now learns it when the message arrives, as it already did for Microsoft 365 and POP3, so the Attachments field in the message list, the `has:attachment` search, and rules that look for attachments all work on IMAP mail you have not opened. After you update, QuickMail checks the mail it already has once, quietly in the background after the first sync; if an account is offline it carries on at the next start. Rules with a **Has attachments** condition on an IMAP account may now match mail they used to skip. (#931)
- **Saving attachments no longer replaces files with the same name.** When two attachments share a name, or the folder already has a file by that name, the new copy is saved as, for example, "image001 (2).png". This applies to **Save All…** in the attachment list as well. (#931)
- **After you choose a sound file, you hear which one.** Choosing a file in Settings or the rule editor now puts focus on the sound box, which reads the file's name, instead of back on the button. (#932)

## Fixed

- **Folders made in QuickMail now show in other mail programs.** A folder created, copied or renamed in QuickMail on an IMAP account was on the server, but programs such as Outlook didn't show it, because QuickMail didn't subscribe to it. It does now, and renaming or deleting a folder updates its subscription. A folder made before this release stays hidden in those programs until you subscribe to it there, for example with **IMAP Folders** in Outlook. (#943)
- **A forwarded or attached email arrived as a file.** An email attached to a message you sent was encoded in a way the standard does not allow, so many mail programs showed it as a file rather than an email the recipient could open. It is now attached as an email. (#929)
- **A failed Copy to Folder was not always spoken.** It now says so whatever your announcement settings for background progress. (#929)
- **Screen readers could read an internal name instead of a message.** You could hear "QuickMail.Models.MailMessageSummary" in the message list, at times that were hard to predict. QuickMail now gives a screen reader the message for a row that isn't on screen yet, normally in the fields and order you chose in Message List Fields, which should stop this. (#928)

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
| [**QuickMail-0.8.69-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.69/QuickMail-0.8.69-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.69-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.69/QuickMail-0.8.69-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.69/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.69/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.
