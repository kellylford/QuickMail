# QuickMail v0.8.37 Release Notes

## Download

Two options are available for v0.8.37:

| Download | When to use |
|----------|-------------|
| **`QuickMail-win.msi`** — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

---

## New: find a contact's mail from the address book

Select someone in the address book, press **Shift+F10**, and choose **Find mail from this contact** or **Find mail to this contact**. The address book closes and the message list fills with the matches, newest first, drawn from every account and folder QuickMail has cached — not just the folder you were in. Focus lands on the message list and the count is announced ("12 messages from Bob Baker."), and the window title reads **Mail from Bob Baker** so the results are easy to tell apart from a folder.

Both actions are also in the address book's Command Palette (**Ctrl+Shift+P**) as **Find Mail From Contact** and **Find Mail To Contact**, so you can give them a keyboard shortcut in Settings → Keyboard. (#370)

---

## Reporting Issues

Found a problem or have a suggestion? There are three ways to reach us — pick the one that fits:

1. **Report a Bug → Send** (Help menu, inside QuickMail). Files the report for you anonymously — it includes no email address or other identifying information, so there is no way to follow up with you. **Best when you don't want any follow-up.**
2. **Report a Bug → Copy report and open GitHub** (Help menu). Opens a pre-filled issue that you submit under your own GitHub account, so your GitHub contact information is attached. **Best when you have a GitHub account and want automatic filing plus direct contact.**
3. **Email** [quickmailissues@theideaplace.net](mailto:quickmailissues@theideaplace.net). **Best when you don't mind sending email and want a personal follow-up.**

Full details, including exactly what a report contains (and what it never contains), are in the [Reporting Issues section of the User Guide](https://kellylford.github.io/QuickMail/reporting-issues.html).
