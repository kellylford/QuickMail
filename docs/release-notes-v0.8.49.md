# QuickMail v0.8.49 Release Notes

## Changed

### Forward is now Ctrl+Shift+F

Forward has moved from **Ctrl+F** to **Ctrl+Shift+F**, in the main window, the reading pane, and a
message opened in its own window. A message body is web content, and some screen readers use
**Ctrl+F** there for their own find command, so pressing it while reading a message never reached
QuickMail. **Ctrl+Shift+F** works wherever you are.

To make room, **Search Folders** has moved from **Ctrl+Shift+F** to **Ctrl+Shift+Y**, next to
**Ctrl+Y**, which moves to the folder tree.

If you would rather keep the old keys, give either command the key you want in **File → Settings →
Keyboard Shortcuts**. Keys you have already customized are not changed.

### Ctrl+K inserts a link in the message body

In the compose window, **Ctrl+K** now inserts a link when you are in the message body, as it does in
Outlook. Anywhere else in the compose window, such as the To, Cc and Bcc fields, it still checks
addresses. **Ctrl+L** still inserts a link too. Insert Link has moved from the Format menu to the new
**Insert** menu.

## Added

### Pictures in messages, each with a description

You can now put pictures in the body of a message in Markdown or HTML mode. Every picture has to have
a description (alternative text) or be marked decorative before it goes in, so the people you write
to always get one or the other.

- **Insert → Image** (**Ctrl+Shift+I**) lets you choose picture files and opens the **Image
  Description** window for each. **OK** is not available until you type a description or check
  **Decorative image**.
- Pasting a picture, or dropping picture files on the message body, opens the same window.
- **Image Properties** (**Alt+Enter** on a picture) changes a picture's description, marks it
  decorative, or removes it.
- In HTML mode a picture is part of the text. Arrow onto it to hear its description; Shift+Arrow
  selects it.
- A photo wider than 1600 pixels is offered a shrink as it goes in, and QuickMail warns once if a
  message grows past 10 MB.
- Pictures stay with the message through drafts, the Outbox and reopening, and a forward or HTML
  reply brings the original message's pictures along.
- In Plain Text mode, Insert Image offers to attach the picture as a file instead.
- The information a phone or camera stores inside a photo, including where it was taken, is
  removed as the photo goes in.

The User Guide has a new [Pictures](https://kellylford.github.io/QuickMail/pictures.html) page covering
putting pictures in, sending them, what QuickMail shows when you read mail, and how pictures are
kept safe.

### Pictures sent inside a message are now shown

A picture that travels with a message — a photo in the body, a logo in a signature, or a picture you
put in with QuickMail — now shows in the reading pane, in message tabs and in message windows.
Showing it contacts no one but your own mail server, because it is part of the message itself, so
the sender cannot tell you looked. Screen readers read it by its description, and pictures with no
description or marked decorative are passed over, as before.

To show descriptions only, as before, uncheck **Display pictures sent inside messages** in
**Settings → General**.

### Load pictures from the web when you choose to

Pictures a message links to on the web are still not loaded on their own, because fetching them
tells the sender you opened the message. Now, when a message has them, its first line says
"Pictures from the web are not shown." with a **Load pictures** link. Activate the link, press
**Ctrl+Shift+U**, or choose **View → Load Pictures** to show that message's pictures. This works in
the reading pane, message tabs and message windows, and lasts while the message is open.

To load them in every message, check **Load pictures from the web automatically** in **Settings →
General**. It is off by default.

QuickMail fetches these pictures itself rather than letting the message do it. It sends no cookies
and does not say which message a picture is in. It contacts only public internet addresses, never
your computer or local network. It keeps a file only if it really is a picture, and skips pictures
sized like tracking pixels. See [Pictures](https://kellylford.github.io/QuickMail/pictures.html) in the User
Guide.

## Fixed

### Forwarding a message with pictures sent broken pictures

A forwarded HTML message kept its pictures' references but not the pictures themselves, so the
people you forwarded to saw broken images. The pictures now travel with the forward.

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
| [**QuickMail-0.8.49-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.49/QuickMail-0.8.49-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.49-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.49/QuickMail-0.8.49-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.49/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.49/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.
