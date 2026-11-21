# QuickMail v0.8.75 Release Notes

## Changed

- **QuickMail tells you when an address from the suggestions goes in.** Pressing **Tab** to enter a suggestion in To, Cc, Bcc or an appointment's Invite field used to happen in silence. Now QuickMail says what went where, for example "jane@uni.edu added to To field", or for a group, "3 addresses from group 'Team' added to To field". The same applies when you choose one in the list with **Enter**. It is an action result, so you hear it while **Announce action results** is on, under **Screen Reader Announcements** in **Settings → General**. (#1048)
- **Screen readers can now recognize the address suggestions as a suggestion list.** While suggestions show, the To, Cc, Bcc and Invite fields tell Windows they control the list, the way Windows' own search boxes do. The suggestion **Tab** would enter is selected, with focus left in the field. A screen reader that supports suggestion lists can then tell you the list appeared, read that suggestion with its place in the list, give the count when nothing is selected, and tell you when the list closes. QuickMail used to announce the list itself; it no longer does, because with the list marked up that announcement repeated what the screen reader said. Typing another letter that leaves the list as it was no longer reports the same suggestion again. (#1048)
- **Two new sound events: Address suggestions appeared, and Address inserted from suggestions.** Each has its own sound in Glass, Soft bell and Wood, **Suggest** and **Inserted**, and both answer the key you pressed, so each plays straight away or not at all. **Address suggestions appeared** is set to **None** in the built-in schemes, because screen readers that support suggestion lists play their own sound when the list appears; choose its sound in **Settings → Sounds** to hear QuickMail's as well. The Maya Voicing Glass scheme in the gallery says both. (#1048)
- **Replies to formatted messages open in HTML, so the original is quoted without `>` on every line.** A reply used to open in your default compose mode, which is Plain Text unless you have changed it. In Plain Text, every line of the original starts with `>`, the usual way plain-text email marks a quote, and a screen reader reads that out on every line. Now a reply to a message that has formatting (HTML) opens in HTML mode, with the original as a real quote block and no `>` characters, the way a forward of that message already did. Replies to plain-text messages still use your default mode. To have every reply use your default mode, turn off **Reply to HTML messages in HTML** in **Settings → General → Composing**. (#1050)

- **The rule editor's address conditions have clearer names, and From is no longer hidden.** **From addresses** is now **From any of these addresses**, and has moved out of **Advanced conditions & actions** to sit next to **Sender contains**. **Sent to addresses** is now **Sent to any of these addresses**. The new names say that a message matches when it has any one of the addresses listed. With the two sender conditions now side by side, the difference is this: **From any of these addresses** is for email addresses, one or more, and **Sender contains** is for a name or part of one, such as `Jane`. Check both and a message has to match both. (#858, from valiant8086)
- **Create Rule from Message fills in the sender's address and their name separately.** **Create Rule from Message** (`Ctrl+Shift+T`) now puts the sender's email address in **From any of these addresses**, checked, so the rule matches everything from that address and nothing from anyone else using the same name. Their name goes in **Sender contains**, unchecked; check it too if you want a rule that needs both. If QuickMail doesn't have the sender's address yet, it fills in the name alone, as before. (#858)
- **The Rules Manager's list words client-side and server-side rules the same way.** A client-side rule matching a whole address or domain, such as `jane@example.com` or `@example.com`, now reads "If from" and the address, as a Microsoft 365 rule already did, rather than "from contains". A rule holding a name or part of an address, such as `@example`, still says "from contains", since that is still matched anywhere in the sender. (#858)

## Fixed

- **A message you reopen from the Outbox comes back in the mode you wrote it in.** It used to open in your default compose mode, so a message written in HTML or Markdown and queued while offline could come back as plain text. (#1050)

- **Switched-off From addresses are kept.** In a client-side rule, if you switched **From any of these addresses** off and **Sender contains** on and saved, the addresses box was empty when you later turned that condition back on. The addresses are now kept, read-only, like the text of every other condition you turn off. (#858)


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
| [**QuickMail-0.8.75-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.75/QuickMail-0.8.75-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.75-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.75/QuickMail-0.8.75-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.75/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.75/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.

You can also install QuickMail with the Windows Package Manager: `winget install quickmail`. It picks the right version for your computer, whether it has an Intel, AMD or ARM processor, and QuickMail keeps itself up to date from then on.
