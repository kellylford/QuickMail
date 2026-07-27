# QuickMail v0.8.37 Release Notes

## Download

Two options are available for v0.8.37:

| Download | When to use |
|----------|-------------|
| **`QuickMail-win.msi`** — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

---

## New: setting up an account asks for three things

Adding an account used to ask for an IMAP host, port, SSL setting, and certificate rule, and then the same four again for SMTP — even when QuickMail already knew every one of those values. It now asks for a **provider**, your **email address**, and your **password**, and works the rest out.

The dialog opens on a new **Provider** list: **Other**, **Gmail**, **Outlook.com / Microsoft 365**, **Yahoo Mail**, and **iCloud Mail**. Other is first rather than last, so Down arrow reaches the rest from where the dialog puts you. Choosing a provider fills in every server setting and says what it did; typing an address at one of them selects it for you, so most people never touch the list. Server names, ports, and SSL settings moved behind an **Advanced settings** expander that stays closed unless you need it — Tab reaches its header before its contents, so one keystroke moves past the whole thing.

**Account name is now optional.** Leave it blank and the account is labelled with its email address.

**An address QuickMail does not recognize gets looked up** when you leave the Email field. Four things are tried in turn: the built-in provider list (offline — nothing leaves your computer), Mozilla's public autoconfig database, your domain's own Autodiscover service, and finally your domain's public DNS records, which say where its mail is actually delivered. Only the **domain** is sent for the second and fourth; the third sends your address to your own provider's server, exactly as Outlook does. That last step is what makes ordinary business mail work: most Microsoft 365 and Google Workspace customers use their own domain and publish nothing the earlier steps can read, and a work mailbox has no IMAP host to type — the way in is a sign-in. When nothing is found at all, Advanced settings opens and focus moves to the **IMAP host** field, with a message naming the sign-in route for a work or school account. Set `AutoDiscoverOnline = off` in `config.ini` to skip everything but the built-in list.

**Gmail now defaults to an app password.** Google sign-in is currently blocked for new QuickMail accounts, so the app password is the path that works today, and the dialog links straight to the page where you create one. Google sign-in stays available under **Advanced settings → Authentication**. Yahoo Mail and iCloud Mail need app passwords too, and say so above the password box. (#369)

**Work or school Microsoft 365 accounts connect through Microsoft 365 directly.** An address on your organization's own domain now moves onto that connection method as you type it, instead of being left on IMAP — where sign-in ended at "your administrator needs to make a change" for a mailbox that signs in perfectly well the other way. Personal Outlook.com, Hotmail, and Live.com accounts are unaffected and stay on IMAP.

**Test Connection checks both halves of your mail.** Incoming and outgoing are probed separately and reported separately — "IMAP: OK. SMTP: OK." A working inbox with a misconfigured send server used to pass this test and then fail on your first message. Microsoft 365 accounts can be tested now as well. The button is on the Manage Accounts window too, so it is as useful on an account that has stopped working as on one you are setting up.

Manage Accounts gained the same **Advanced settings** expander, so an account that is working needs no scrolling past hosts and ports to reach the settings people actually change, and it shows which provider the account belongs to.

The [Accounts section of the User Guide](https://kellylford.github.io/QuickMail/accounts.html) covers the whole lifecycle — choosing a provider, adding an account, entering settings by hand, testing, editing, and removing.

## Changed: mail rules are now per-account

The "All accounts" rule option has been replaced by scoping each rule to a specific account. This happens automatically the first time rules load after updating:

- Existing "All accounts" rules are **copied to each of your standard (IMAP/SMTP) accounts**, so they keep working exactly as before.
- **If you use *only* Microsoft 365 accounts**, any old "All accounts" rules are **removed** — those rules run inside QuickMail, and Microsoft 365 accounts are moving to server-side rules instead. If you had such rules, recreate them scoped to the account you want. (This affects very few people; every removal is recorded in the log.)
- New rules now ask which account they apply to (defaulting to your default account) instead of offering "All accounts". (#333)

## New: find a contact's mail from the address book

Select someone in the address book, press **Shift+F10**, and choose **Find mail from this contact** or **Find mail to this contact**. The address book closes and the message list fills with the matches, newest first, drawn from every account and folder QuickMail has cached — not just the folder you were in. Focus lands on the message list and the count is announced ("12 messages from Bob Baker."), and the window title reads **Mail from Bob Baker** so the results are easy to tell apart from a folder.

Press **Escape** in the message list to close the results and return to the folder you started from — the destination and its message count are announced. There is also a **Close** button above the results, and **Close Contact Mail Results** in the Command Palette.

Both actions are also in the address book's Command Palette (**Ctrl+Shift+P**) as **Find Mail From Contact** and **Find Mail To Contact**, so you can give them a keyboard shortcut in Settings → Keyboard.

Two things to know about the results: mail older than your sync range is not stored locally, so it is not searched, and **Find mail to this contact** matches the To line — a message where the person was only in Cc does not appear. (#370)

## Fixed: typing a letter jumps to a contact in the address book

With focus on the address book's contact list, typing a letter did nothing. Now it jumps to the first contact starting with that letter, the same way lists behave elsewhere in Windows. Type several letters quickly to match a longer beginning ("br" goes to Brenda rather than Bob), or press the same letter again to move to the next contact starting with it. Contacts saved without a name are matched on their address. The **Groups** and **Group members** lists work the same way. (#371)

---

## Reporting Issues

Found a problem or have a suggestion? There are three ways to reach us — pick the one that fits:

1. **Report a Bug → Send** (Help menu, inside QuickMail). Files the report for you anonymously — it includes no email address or other identifying information, so there is no way to follow up with you. **Best when you don't want any follow-up.**
2. **Report a Bug → Copy report and open GitHub** (Help menu). Opens a pre-filled issue that you submit under your own GitHub account, so your GitHub contact information is attached. **Best when you have a GitHub account and want automatic filing plus direct contact.**
3. **Email** [quickmailissues@theideaplace.net](mailto:quickmailissues@theideaplace.net). **Best when you don't mind sending email and want a personal follow-up.**

Full details, including exactly what a report contains (and what it never contains), are in the [Reporting Issues section of the User Guide](https://kellylford.github.io/QuickMail/reporting-issues.html).
