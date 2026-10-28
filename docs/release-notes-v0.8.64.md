# QuickMail v0.8.64 Release Notes

## Added

### Invite people to an appointment

The appointment editor has a new **Invite** field. Type the email addresses of the people you
want at a meeting, separated by semicolons, and save the appointment to an online calendar.
They get a meeting invitation they can accept or decline in their own mail program. This works
on iCloud, Google and Microsoft calendars. (#866)

- Changing the meeting's time, place or title sends the people invited an update. Adding
  someone invites them; removing someone sends them a cancellation.
- Deleting a meeting you organized sends everyone a cancellation. The delete confirmation tells
  you who will get one before you answer.
- After you save, QuickMail says how many people the invitation, update or cancellation went
  to, or why it could not be sent.
- The details box under the calendar list now shows who is invited to an appointment and how
  each person answered, for meetings you organized and ones you were invited to.
- On a meeting someone else organized, the Invite field shows who is invited but cannot be
  changed. Only the organizer changes the guest list.
- Inviting needs an online calendar: the Local Calendar has no server to send the invitations
  from, so the editor asks you to choose an account's calendar instead.

## Fixed

- **Editing an iCloud appointment created on an iPhone or at iCloud.com.** Saving it could make
  a second copy of the appointment or be refused, because QuickMail saved the change under the
  wrong name on the server. It also dropped the people invited to the meeting. (#866)

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
| [**QuickMail-0.8.64-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.64/QuickMail-0.8.64-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.64-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.64/QuickMail-0.8.64-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.64/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.64/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.
