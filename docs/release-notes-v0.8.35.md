# QuickMail v0.8.35 Release Notes

## Download

Two options are available for v0.8.35:

| Download | When to use |
|----------|-------------|
| **`QuickMail-win.msi`** — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

---

## New: Microsoft 365 and Exchange Online support

QuickMail now connects to **Microsoft 365 / Exchange Online** and **Outlook.com** mailboxes through Microsoft 365 directly — no IMAP/SMTP server names or ports to enter. In the Add Account dialog, set **Account type** to **Microsoft 365 / Outlook.com**, sign in with Microsoft, and you're done. This is on by default for everyone in this release.

Work or school (Microsoft 365) accounts frequently sit in organizations that require an administrator to approve a new app before anyone can sign in. If your sign-in stops at a **"needs admin approval"** message, that is expected — QuickMail needs a one-time approval from your organization's IT administrator. We've added a full **[For Microsoft 365 Administrators and Tenant Owners](https://kellylford.github.io/QuickMail/)** section to the User Guide explaining exactly what an admin needs to do (it takes a couple of minutes). Personal Outlook.com accounts are not affected.

**Important for work and school accounts:** turning on Exchange support in QuickMail is only part of the process. Until your administrator or tenant owner completes the steps in the [administrator guide](https://kellylford.github.io/QuickMail/), QuickMail will not be able to connect to your work or school account. If you plan to use a Microsoft 365 account, share that guide with whoever manages your organization's Microsoft 365 tenant first.

Prefer IMAP? You still can — choose **Standard IMAP/SMTP** and **Microsoft OAuth** as the authentication method. The new Microsoft 365 / Outlook.com option is simply the recommended path.

## New: Accept, Tentative, and Decline meeting invitations in Microsoft 365 mail

When you open a meeting invitation in a Microsoft 365 / Outlook.com mailbox, QuickMail now adds the same **Accept / Tentative / Decline** card to the top of the message that IMAP accounts have had — choose a response and QuickMail sends your reply to the organizer and updates your calendar. Cancelled invitations say so instead of offering buttons.

Note for this first release: responding notifies the organizer and updates your calendar **inside QuickMail**. Your response is not yet written back to the Microsoft 365 server calendar, so other clients (Outlook on the web, your phone) may still show the meeting as unanswered. Fuller server-side handling is planned for a later release.

## Fixed: signing in as the wrong account, and sign-in timeouts

- When you sign in to a Microsoft or Google account and a **different** account completes the sign-in — common when an administrator signs in at an approval screen — QuickMail no longer silently switches your account to that identity. It keeps the address you entered and shows a clear warning. (#202)
- The interactive sign-in no longer has a hidden time limit. Admin-approval waits and screen-reader navigation could previously be cut off after a few minutes, tearing down the sign-in window mid-flow. You now have as long as you need; close the sign-in window yourself to cancel. (#203)

---

## Thank You to Contributors

Thanks to everyone who tested Microsoft 365 sign-in and reported the admin-approval and identity-mismatch behavior — it directly shaped this release.

---

## Reporting Issues

Found a problem or have a suggestion? There are three ways to reach us — pick the one that fits:

1. **Report a Bug → Send** (Help menu, inside QuickMail). Files the report for you anonymously — it includes no email address or other identifying information, so there is no way to follow up with you. **Best when you don't want any follow-up.**
2. **Report a Bug → Copy report and open GitHub** (Help menu). Opens a pre-filled issue that you submit under your own GitHub account, so your GitHub contact information is attached. **Best when you have a GitHub account and want automatic filing plus direct contact.**
3. **Email** [quickmailissues@theideaplace.net](mailto:quickmailissues@theideaplace.net). **Best when you don't mind sending email and want a personal follow-up.**

Full details, including exactly what a report contains (and what it never contains), are in the [Reporting Issues section of the User Guide](https://kellylford.github.io/QuickMail/reporting-issues.html).

---

## Internal

Per-PR technical changelog for this release (changes since the v0.8.34 tag):

- **Microsoft Graph mail backend on by default.** `ConfigFeatureGate` default for `FeatureFlag.GraphBackend` flipped to `true`; the Add Account dialog now offers **Microsoft 365 / Outlook.com** as an account type for everyone. Can be disabled with `GraphBackend=false` in `config.ini [features]` or `--no-feature GraphBackend` at launch. `GraphBackendGateTests` updated (default-on, config-off).
- **Graph meeting-invite card** (basic iMIP option, #332). `GraphMailService.GetMessageDetailAsync` detects meeting-request messages via the Graph `@odata.type` annotation, fetches the raw MIME (`/$value`), extracts the `text/calendar` part with MimeKit, and populates `CalendarInvite`/`CalendarIcs` — lighting up the existing reading-pane RSVP flow. Replies route through Graph `/sendMail`. Known limitation: no write-back to the server calendar (tracked in #332). Commits 6060aa5, ea7e8f3.
- **Sign-in identity mismatch guard + interactive sign-in timeout removed** (#202, #203, #322). Interactive sign-in no longer rebinds the account to a different identity than the one entered (raises `SignInIdentityMismatch`, surfaced as a focus-grabbing warning in both account dialogs); the 3-/5-minute `CancellationTokenSource` on the interactive path is dropped. `AccountEditorSignInTests` added.
- **Docs: Entra scope guidance corrected** after #323 (`Contacts.Read` requested explicitly; `.default` contradictions removed). Commits 6a35f65, 8366fcc.
- **User Guide:** rewrote the Microsoft account instructions around the Microsoft 365 / Outlook.com account type and added a **For Microsoft 365 Administrators and Tenant Owners** section (admin consent, delegated permissions, roles, one-click consent URL, troubleshooting).
