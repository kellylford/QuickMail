# QuickMail v0.8.70 Release Notes

## New

- **The sound gallery shows which schemes you already have.** In **Settings → Sounds → Browse schemes…**, a scheme you have added says "Added." When the gallery has a newer version of it, it says "Update available." and **Add** becomes **Update**, which replaces your copy's sounds with the new version after asking. Your copy keeps its name. (#937)

## Changed

- **Sound schemes work the same way in QuickMail and The Chat Place.** A `.qmsounds` file now says it is for QuickMail and keeps the scheme's description. If you import a scheme made for another app, QuickMail says which app it is for. (#937)
- **Export scheme now turns MP3 sounds into WAV files inside the exported file**, so the scheme works in any app and in the gallery. Your own MP3s stay as they are, and QuickMail still plays them. A sound that can't be turned into a WAV file of 5 MB and 30 seconds or less is left out, and **Export scheme…** says how many events have no sound in the file. Schemes exported from QuickMail 0.8.69 still import with their MP3s. (#937)
- **One large sound no longer stops an import.** A sound file in a scheme that is too large is left out, and QuickMail says how many events have no sound, instead of refusing the whole scheme. (#937)
- **Sound file names with hidden formatting characters are no longer used.** A name containing an invisible character that changes how text is shown, such as a right-to-left mark, could make one file look like another, so QuickMail no longer accepts it. If one of your own sounds has a name like that, QuickMail stops playing it; rename the file and choose it again. Emoji in names are fine. (#937)
- **Each sound scheme keeps its sound files in a folder of its own, and deleting a scheme deletes them.** A sound file you choose for an event, in any scheme including Glass, Soft bell and Wood, is copied into that scheme's folder, and an imported scheme's sound files go into its folder too. **Delete scheme…** now says it deletes the scheme's sound files, and no other scheme is affected; if you choose **Cancel** instead of **Save**, the scheme and its files stay. **Copy scheme…** copies the sound files along with the scheme, and **Remove file…** now removes the file from the scheme showing only. A rule's own sound file is kept separately, so changing schemes never affects it. The first time QuickMail 0.8.70 starts, it moves the sound files you already have into the folder of each scheme that uses them, copying a file into each scheme if more than one uses it; this happens once, and your schemes sound the same afterwards. A file that no scheme uses is left where it is, and so is one a rule uses. Earlier versions of QuickMail look for sound files only in the old shared folder, so if you go back to one, your schemes' own sound files play a built-in sound instead. (#948)
- **Test and Run on Existing Mail now say what they counted.** In the Rules Manager, **Test** says it checked only the selected rule against the message list, for example "This rule would match 1,108 of the 2,526 messages in the message list for the XS4All account." **Run on Existing Mail** says how many rules ran, on how many stored Inbox messages, and what each rule moved or deleted, for example "Ran 3 rules on 2,900 stored Inbox messages for the XS4All account. They moved 1,936 messages and deleted 6: techtalk moved 1,886; Newsletters moved 50; Spam deleted 6." A rule name that could be mistaken for part of the sentence, because it contains a number, a period, comma, colon or semicolon, or the word moved, deleted or failed, is shown in quotation marks, such as “Top 10” moved 5. Before, the two numbers could not be compared, and looked wrong side by side. Both messages now group large numbers, such as 1,942 rather than 1942. (#949)

## Fixed

- **New Folder in the folder picker can now create a folder at the top level of a single account.** When the picker showed only one account, such as when choosing a rule's move to folder or moving messages that all come from one account, a new folder was always made inside the selected folder. The picker now shows the account as the top of the tree, as the main folder tree does. Select the account and choose **New Folder** (or press **Alt+N**) to create the folder beside Inbox and Sent. The picker still opens on the folder you came from. (#950)
- **Move Folder and Copy Folder can now put a folder at the top level of its account.** The picker for **Move Folder…** and **Copy Folder…** offered only other folders, so a folder inside another one could never be moved back out beside Inbox and Sent. The picker now shows the account name at the top of the tree; select it and press Enter (or **Alt+O**) to move or copy the folder to the top level. This works for IMAP, Microsoft 365 and POP3 accounts. Some IMAP servers keep every folder inside Inbox; on those, QuickMail says the folder cannot go above Inbox instead of moving it. A folder that is already at the top level is left where it is, and QuickMail says so. The picker still opens on the folder you came from. (#954)

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
