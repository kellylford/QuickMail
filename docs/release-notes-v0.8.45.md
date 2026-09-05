# QuickMail v0.8.45 Release Notes

## Fixed: every rule condition now has a checkbox

**Create Rule from Message** (`Ctrl+Shift+T`) filled in the sender *and* the subject of the message
you were reading, and the rule editor had no way to say which of them the rule was supposed to use.
Both were conditions, both had to match, so "Rule for someone@example.com" quietly became a rule
that matched that sender only when the subject was the exact line it was made from — in practice,
the one conversation you started from.

Each text condition in the editor now has its own checkbox in front of it: **From addresses**,
**Subject contains**, and the four under **Advanced conditions & actions** (**Sender contains**,
**Sent to addresses**, **Subject or body contains**, **Body contains**). A message has to satisfy
every condition you checked, so leaving one unchecked is how you say "don't care". Clearing a
checkbox keeps the text in its box — read-only and skipped by Tab, but one keystroke from being
part of the rule again, rather than something you have to retype.

Creating a rule from a message uses that: the sender arrives checked, and the subject arrives
**unchecked** with its text ready to switch on. So the rule you get by default is the one the name
says — everything from that sender.
([#665](https://github.com/kellylford/QuickMail/issues/665))

---

## Fixed: View Mode counts its choices correctly

**View → View Mode** offers four choices — Messages, Conversations, From, To — but arrowing
through them announced *"1 of 8"*, *"2 of 8"*, and so on. The menu actually held eight items: the
four above plus the four calendar views (Agenda, Day, Week, Month), which were hidden rather than
removed while you were reading mail. Hiding a menu item takes it off the screen but leaves it in
the menu, so it still counted.

The menu now holds only the choices that apply: the four mail views while you are in a mail
folder, and the four calendar views while the calendar is open. The count you hear matches what
is there.

The **View mode** button on the toolbar (`Ctrl+Shift+V`) drops the same list, so it is fixed the
same way — it was counting seven — and it gains **Month**, which it had been missing while the
calendar is open. Two View menu entries are renamed to match what the toolbar button, the folder
tree and the user guide have always called them: **By Sender** and **By Recipient** are now **From**
and **To**. **Settings → General → View → Display mode**, which sets the same four modes, follows
suit and reads **From (grouped by sender)** and **To (grouped by recipient)**.
([#663](https://github.com/kellylford/QuickMail/issues/663))

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
| [**QuickMail-0.8.45-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.45/QuickMail-0.8.45-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.45-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.45/QuickMail-0.8.45-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.45/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.45/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 8 runtime — you do not need to install .NET separately.
