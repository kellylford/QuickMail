# QuickMail v0.8.72 Release Notes

## New

- **Install QuickMail with winget.** QuickMail is now in the Windows Package Manager, so `winget install quickmail` installs it, picking the right version for your computer, whether it has an Intel, AMD or ARM processor. You only need it once; QuickMail keeps itself up to date from then on. See [Installing with winget](https://kellylford.github.io/QuickMail/) in the User Guide.
- **Hide folders you never use.** Choose **Hide Folder** on the folder tree's context menu, on the **Folder** menu, or in the Command Palette to take a folder, and everything inside it, out of the folder tree and **Go to Folder**. **Show All Hidden Folders**, in the same places, brings them all back. Hidden folders still sync, still count in All Mail and the other combined folders, and are still offered when you move or copy messages or set up a rule. The Inbox, accounts, calendars and saved views cannot be hidden. See [Hiding Folders](https://kellylford.github.io/QuickMail/) in the User Guide.
- **Confirm before sending.** Turn on **Settings → General → Composing → Confirm before sending a message** for a safety check before sending or queuing mail. The setting is off by default and takes effect for open compose windows when you save Settings. **No** is selected initially; choose **Yes** to send, or **No**, **Cancel**, or **Escape** to keep editing.

## Changed

- **Hear when you reach the top or bottom of the message list.** Pressing **Up** or **Home** on the first message, or **Down** or **End** on the last, used to do nothing you could hear, so there was no telling the end of the list from a key that didn't register. Now QuickMail plays a new sound, **Reached the end of a list**: one short, soft note. If sounds are off or muted, your sound scheme has no sound for it, or another sound is still playing, QuickMail says "Top of list" or "Bottom of list" instead, without interrupting what your screen reader is saying. It is an action result, so **Announce action results** decides whether you hear it. It works in every view: Messages, Conversations, From and To. Each press at the end tells you again; holding the key down tells you once. **Glass**, **Soft bell** and **Wood** have the new sound. Schemes you made yourself or added from the Sound Scheme Gallery have no sound for it until you choose one in **Settings → Sounds** (or the gallery offers an update), and QuickMail says "Top of list" or "Bottom of list" instead. Thanks to the person who reported this. (#923)

## Fixed

- **Repeated Send commands cannot send the same compose twice.** QuickMail ignores another Send command while confirmation or sending is underway, and after the message has been sent or queued. This also protects messages when send confirmation is off. If a draft is still saving, Send tells you to try again in a moment.
- **QuickMail builds from a fresh clone of the source.** `build.bat` and `dotnet build` failed with four "does not exist in the current context" errors until two credential files were written by hand. The build now writes empty placeholders for them when they are missing. ([#859](https://github.com/kellylford/QuickMail/issues/859))
- **Web and email addresses are no longer called misspellings.** In a compose window, moving through `https://…`, `www.…`, `mailto:` links, addresses such as `github.com/kellylford/QuickMail`, or email addresses no longer says "Misspelling" (the text of a Markdown link is still checked), and Next and Previous Misspelling (Ctrl+F7 and Ctrl+Shift+F7), Check Spelling (F7) and the Alt+1, 2 and 3 suggestions pass over them.
- **Next Misspelling says the misspelling once.** Ctrl+F7 and Ctrl+Shift+F7 said "Misspelling: …" twice for the same word; they now say it once, in plain text and HTML. Pressing Ctrl+F7 again in plain text now moves on to the next misspelling instead of staying on the same word.
- **The offline auto-save message no longer interrupts editing.** "Auto-save is keeping your draft on this computer until you're online" was spoken while you were typing or moving through the message. It now waits until you pause for a couple of seconds, and is said at most once per compose window; the status row at the bottom still shows each time the draft is kept on this computer.

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
| [**QuickMail-0.8.72-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.72/QuickMail-0.8.72-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.72-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.72/QuickMail-0.8.72-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.72/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.72/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.

You can also install QuickMail with the Windows Package Manager: `winget install quickmail`. It picks the right version for your computer, whether it has an Intel, AMD or ARM processor, and QuickMail keeps itself up to date from then on.
