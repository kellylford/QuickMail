# QuickMail v0.8.38 Release Notes

The last public release was **v0.8.37**.

## Contents

- [Mail rules for Microsoft 365](#mail-rules-for-microsoft-365)
- [Reporting Issues](#reporting-issues)

## Mail rules for Microsoft 365

QuickMail's Rules Manager now works with the **server-side rules** on a Microsoft 365 (Exchange) mailbox — the same rules Outlook calls "Inbox rules." A server rule runs on Microsoft's servers, so it acts on your mail **even when QuickMail is closed**, and wherever else you read that mailbox. Until now the Rules Manager only knew about rules that run inside QuickMail; now it shows and manages both.

A few things to know:

- **The Rules Manager is now one account at a time for Microsoft 365 users.** When you have a Microsoft 365 account, the Rules Manager opens on a single account chosen in an **Account** list at the top, instead of listing every account's rules together. If you are used to seeing all your accounts' rules in one list, this is the change you will notice first — your rules are not gone, they are behind the account picker. (With only one account there is no picker.)
- **Server and QuickMail rules share one list**, each row marked **on server** or **in QuickMail**. You create, edit, enable or disable, reorder, and delete them the same way.
- **QuickMail decides where a new rule lives.** It saves a rule as a server rule when it can, so the rule keeps working while QuickMail is closed; a rule that needs something only QuickMail can do (today, **Mark as unread**) is saved as a QuickMail rule, and QuickMail tells you why.
- **Some rules built in Outlook are shown read-only.** If a rule uses conditions or actions QuickMail cannot represent exactly, QuickMail lets you read it but not change it here, so it cannot be turned into something you did not mean. Change those in Outlook.

**For most work or school accounts this requires your administrator to allow QuickMail to read and change your mailbox rules.** If that permission is not in place, you will see a message about it rather than your server rules. See the [Mail Rules section of the User Guide](https://kellylford.github.io/QuickMail/) for the full walkthrough.

<!-- Reporting Issues footer: keep in sync with docs/reporting-issues-footer.md and the User Guide. -->

---

## Reporting Issues

Found a problem or have a suggestion? There are three ways to reach us — pick the one that fits:

1. **Report a Bug → Send** (Help menu, inside QuickMail). Files the report for you anonymously — it includes no email address or other identifying information, so there is no way to follow up with you. **Best when you don't want any follow-up.**
2. **Report a Bug → Copy report and open GitHub** (Help menu). Opens a pre-filled issue that you submit under your own GitHub account, so your GitHub contact information is attached. **Best when you have a GitHub account and want automatic filing plus direct contact.**
3. **Email** [quickmailissues@theideaplace.net](mailto:quickmailissues@theideaplace.net). **Best when you don't mind sending email and want a personal follow-up.**

Full details, including exactly what a report contains (and what it never contains), are in the [Reporting Issues section of the User Guide](https://kellylford.github.io/QuickMail/reporting-issues.html).
