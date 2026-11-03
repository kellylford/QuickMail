# QuickMail v0.8.67 Release Notes

## Added

### Sounds

QuickMail can now play sounds. They are **off until you turn them on**, in **Settings → Sounds → Play sounds for mail events**, and they add to QuickMail's announcements and status text rather than replacing them. (#912)

**This is a first version, and we want your feedback.** We have made our best guess at which events should have a sound and what those sounds should be. Please tell us:

- Are these the right events? Which are missing, and which would you never use?
- What do you think of the sounds? Which style do you prefer, and are any two sounds too alike, harsh, or easy to confuse with your screen reader's own sounds?
- How do the sounds work alongside your screen reader's speech?

**Report a Bug** on the Help menu or email to support@theideaplace.net both reach us.

- **Sounds for mail events:** message sent, message queued to send later, send failed, new mail, mail in a conversation you are watching, a delete, move or archive that failed, connection lost and restored, and calendar reminders. Message deleted, archived and moved have no sound until you choose one, because you may do those many times in a row.
- **Three styles:** Glass, Soft bell and Wood. Each has the same sounds with the same melodies, and the tone changes, though Wood is also a little quieter. Preview plays the style showing, so you can compare them before saving.
- **Your own sounds:** use a WAV or MP3 file of up to 5 MB and 30 seconds for any event. QuickMail keeps its own copy, and your sounds travel with your settings when you [move them to another computer](https://kellylford.github.io/QuickMail/moving-your-settings-to-another-computer.html).
- **A sound for a rule:** a client-side rule can now **Play a sound**, so you can tell mail from a particular sender by ear, or hear when something has been filed. Use a **From addresses** or **Sender contains** condition with your own sound file to give someone their own sound. A rule's sound plays even with event sounds off, as adding it to a rule already asks for it. (#129)
- **Mute All Sounds:** on the Tools menu and in the Command Palette, with no keyboard shortcut until you give it one. It silences everything, including whatever is playing, except Preview, which you have asked to hear.

Good to know:

- While **Play sounds for mail events** or **Mute all sounds** is on, notifications leave out the Windows notification sound, so you hear QuickMail's sound and not both. A notification for mail a rule has just played a sound for leaves it out as well.
- **Windows Do Not Disturb does not silence QuickMail's sounds**, because Windows gives apps no way to tell when it is on. Use **Mute All Sounds** for that. Sounds for things that happen on their own do stay quiet during a presentation or while an app is running full screen.
- New mail and rule sounds stay silent for mail that came in while QuickMail was closed. New mail also stays silent when more than five messages arrive at once, such as when your computer wakes up, and a rule when more than five messages match it at once.
- The new-mail sound is for the Inbox, and the calendar reminder sound needs calendar reminders turned on.

See [Sounds in the User Guide](https://kellylford.github.io/QuickMail/sounds.html).

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
| [**QuickMail-0.8.67-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.67/QuickMail-0.8.67-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.67-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.67/QuickMail-0.8.67-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.67/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.67/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.
