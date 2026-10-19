# QuickMail v0.8.61 Release Notes

## Fixed

### Adding an account: focus stays in the password box, and the buttons tab in the order you see them

These are the first changes from a review of adding an account (#834).

- **Focus no longer jumps out of the Password box.** For an address QuickMail doesn't recognize, it looks up the server settings after you leave the Email address field. When nothing was found, focus moved to the IMAP host field a few seconds later, usually while you were typing your password, so the rest of the password went into the server field, where it can be read. Focus now stays where you are. Advanced settings still opens, and the message saying nothing was found is now read even if you have turned off status announcements. Pressing **Add Account** with no server settings still takes you to IMAP host.
- **The buttons at the bottom tab in the order they appear.** In Add Account they read Add Account, Test Connection, Cancel, but Tab reached Cancel first, and Shift+Tab from the first field went straight to Add Account. Manage Accounts had the same problem with Save, Test Connection and Close. Tab now goes through the form, then the buttons from left to right, so Cancel and Close are the last stops. **Enter** and **Escape** work as before.
- **Alt+N opens New in Manage Accounts.** New was Alt+W, which it shared with the Password field and the Sign in buttons, so pressing it with an account selected only moved focus to New. Other shared keys are fixed too. In Manage Accounts:
  - Import Settings is now **Alt+I**
  - Set Default is **Alt+F**
  - Convert to Microsoft 365 is **Alt+3**
  - Account name is **Alt+M**
  - Sender display name is **Alt+Y**
  - Use SSL is **Alt+9**

  Cancel, Close, Authentication, the IMAP and POP3 port fields, and the second Accept invalid certificates checkbox no longer have an access key: Escape closes the dialog, and the fields are a Tab away.
- **Delete in Manage Accounts always asks first.** It used to ask only when the account had shared mailboxes. Now that **Alt+D** reaches Delete directly, one keystroke would otherwise remove an account.

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
| [**QuickMail-0.8.61-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.61/QuickMail-0.8.61-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.61-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.61/QuickMail-0.8.61-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.61/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.61/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.
