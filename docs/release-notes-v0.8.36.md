# QuickMail v0.8.36 Release Notes

## Download

Two options are available for v0.8.36:

| Download | When to use |
|----------|-------------|
| **`QuickMail-win.msi`** — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

---

## Fixed: reaching attachments when a message opens in its own window

When you set messages to open in a new **window** (Settings → Windowing → Message Open Mode → Window), attachments were hard to find — a screen reader could miss them entirely. This release makes attachments reachable in every reading mode:

- **New shortcut: `Alt+A`** jumps straight to the attachment list of the open message, from anywhere in that message — the message list, the header fields, or inside the message body. If the message has no attachments, QuickMail says so instead of doing nothing. The command is also in the Command Palette ("Focus Attachment List") and can be rebound in Settings → Keyboard.
- **Shift+Tab** from the message body now lands on the attachment list in window mode too, matching how the reading pane already worked.

Once you are on the attachment list, arrow between files and press **Enter** to open, **Alt+Enter** for properties, or **Shift+F10** (or the Menu key) for Save / Save All / Open. (#350)

## Improved: meeting-invitation feedback you can hear

When you Accept, Tentatively accept, or Decline a meeting invitation from the reading pane, QuickMail now confirms your choice right inside the invitation card, announced immediately so screen-reader users get clear feedback that the response was recorded. (#329)

## Improved: bind bare keys as keyboard shortcuts

You can now assign **Delete, Backspace, Insert, and the function keys (F-keys)** as custom shortcuts in Settings → Keyboard. Previously these could not be bound on their own. (#330)

## Improved: Rules Manager accessibility and stability

Several fixes to the Rules Manager (Tools → Rules…, `Ctrl+Shift+L`):

- The Rules Manager window no longer risks freezing the app when opened over a message; it now opens as a modeless window. (#345)
- Each rule row now announces which account it belongs to, so multi-account rule lists are clear. (#347)
- Screen readers now read the Rules Manager's own window title correctly. (#347)
- Added a field-labels display option and other row-level accessibility refinements. (#344, #346, #348)

---

## Internal

- **Bug reports now include the message open mode.** In-app bug reports (Help → Report a Bug) add the current reading mode — reading pane, tab, or window — to the Environment section, so mode-specific problems like the attachment issue above can be reproduced in the right context. As before, reports never include message content, addresses, or credentials. (#350)
- **Security: updated vulnerable transitive NuGet packages** and enabled transitive dependency auditing so future advisories surface at build time.
- **Groundwork for server-side mail rules.** Internal service and view-model layers for rules that run on the Microsoft 365 server (rather than only inside QuickMail) landed this release, with tests. This is not yet exposed in the user interface — no user-facing change yet. (#333)
- Continuous-integration and test-infrastructure updates (`actions/setup-dotnet` v6; live-content testing plan). (#292, #304)

---

## Thank You to Contributors

Thanks to everyone who reported problems and tested fixes for this release — the attachment-in-window-mode report (#350) and the meeting-invitation and keyboard-shortcut feedback directly shaped what shipped here.

---

## Reporting Issues

Found a problem or have a suggestion? There are three ways to reach us — pick the one that fits:

1. **Report a Bug → Send** (Help menu, inside QuickMail). Files the report for you anonymously — it includes no email address or other identifying information, so there is no way to follow up with you. **Best when you don't want any follow-up.**
2. **Report a Bug → Copy report and open GitHub** (Help menu). Opens a pre-filled issue that you submit under your own GitHub account, so your GitHub contact information is attached. **Best when you have a GitHub account and want automatic filing plus direct contact.**
3. **Email** [quickmailissues@theideaplace.net](mailto:quickmailissues@theideaplace.net). **Best when you don't mind sending email and want a personal follow-up.**

Full details, including exactly what a report contains (and what it never contains), are in the [Reporting Issues section of the User Guide](https://kellylford.github.io/QuickMail/reporting-issues.html).
