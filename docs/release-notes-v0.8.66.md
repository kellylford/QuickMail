# QuickMail v0.8.66 Release Notes

## Added

### Turn off spell checking as you type

**Settings → General → Composing → Check spelling as I type** is on by default. Turn it off if a
message stalls, or your screen reader reads lines twice or skips them, after you paste a long
text. Windows' spelling checker, as WPF uses it, can hold the compose window for 10 to 15 seconds
while it checks a long paste full of names, model numbers and web addresses, and screen readers
lose their place in the meantime. With the setting off there are no underlines or spelling
announcements while you write. **Check Spelling (F7)** and **Next/Previous Misspelling
(Ctrl+F7 / Ctrl+Shift+F7)** still work: the first one you use in a message starts spell checking
for that message. The setting applies to messages you open after changing it. The underlying
problem is reported to Microsoft as dotnet/wpf#6106. (#883)

## Fixed

This build has the first batch of efforts on improving the editing experience in QuickMail. Please
share feedback on other improvements you want, preferably through **Report a Bug** or the
support@theideaplace.net email address. Be as specific as possible about the scenario.

- **Alt+1, Alt+2 and Alt+3 no longer replace the wrong letters.** In Plain Text and Markdown, if
  you typed after hearing a misspelling, the replacement used the word's old position and mangled
  the text around it ("I xxrecieve mail" became "I receiveve mail"). Now, if the word has moved,
  QuickMail tells you the misspelling at the cursor instead, and the next Alt+1 acts on that. (#885)
- **Ctrl+Z undoes a spelling replacement.** Replacing a word with Alt+1 used to wipe the undo
  history in Plain Text and Markdown. (#886)
- **Switching to HTML and back no longer double-spaces your message.** Going from Plain Text to
  HTML and back put a blank line between every line, and the plain-text part of HTML messages you
  sent was double-spaced the same way. Switching back to Plain Text also no longer asks whether
  to lose formatting when there isn't any. (#887)
- **Insert Template puts the template where your cursor is**, on lines of its own, and Ctrl+Z
  takes it out again. In Plain Text and Markdown it used to go at the end of the message, stuck to
  the last line, and couldn't be undone. In HTML it went into the middle of the line you were on. (#888, #889)
- **Bold and italic in Markdown leave a selected trailing space outside the markers.** Selecting a
  word with Ctrl+Shift+Right and pressing Ctrl+B gave `**bold **please`, which isn't bold. (#890)
- **Shift+Tab from the message body no longer goes to Send.** It now goes back to the field before
  the body, rather than one Enter away from sending a half-written message. (#897)

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
| [**QuickMail-0.8.66-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.66/QuickMail-0.8.66-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.66-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.66/QuickMail-0.8.66-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.66/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.66/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.
