# QuickMail v0.8.51 Release Notes

## Added

### Reconnect a disconnected account from the account list

An account whose sign-in had expired stayed disconnected, even after a restart, until you signed in
again from **Manage Accounts**. Now you can reconnect it where you are: select it in the **Accounts** list and choose
**Reconnect** from its context menu (**Shift+F10** or the Applications key), or choose **Reconnect
Account** in the command palette. If the sign-in has expired, the Microsoft sign-in window opens, or
for a Google account a sign-in page in your browser. A shared mailbox reads its mail through the account it belongs to, so reconnecting one signs
that account in and connects it first. If you sign in as someone else, for example an administrator
approving permissions, the account is not connected and QuickMail tells you who you signed in as. The
command has no default key; you can assign one in **File → Settings → Keyboard Shortcuts**. (#615)

### Start QuickMail when you sign in to Windows

QuickMail can now start by itself when you sign in to Windows. It is off unless you turn it on: in
**File → Settings → Startup**, check **Start QuickMail automatically when I sign in to Windows**. With
**Start minimized** (on by default) it starts without opening its window over what you are doing — in
the notification area if you keep QuickMail running there, otherwise minimized on the taskbar.

QuickMail uses the standard Windows startup list, so it appears in Task Manager's **Startup apps** and
in Windows **Settings → Apps → Startup**, and you can turn it off from either. When you do,
QuickMail's own setting shows it as off. Uninstalling QuickMail removes the startup entry. This is
available in the installed version; a portable copy can be moved, which would leave Windows pointing at
nothing. (#770)

## Fixed

### Rules are on hand when a message is open in its own window

With messages set to open in a new window, the message window had no way to make a rule from the
message or to open the Rules Manager — not on a menu, not on a key, and not in its command palette.
Both are there now, with the same keys as the main window:

- **Message → Create Rule from Message…** (**Ctrl+Shift+T**) starts a rule for the sender of the
  message you are reading. After **Previous** or **Next** in the window, it is the message now shown,
  not the one selected in the main window's message list.
- **Tools → Rules…** (**Ctrl+Shift+L**) opens the Rules Manager on that message's account.

When you close the Rules Manager, you are back in the message window. On a message in a shared
mailbox, Create Rule from Message says that the shared mailbox's rules are managed in Outlook, as it
does in the main window.

The main window's **Message** menu now has **Create Rule from Message…** too. Until now it was only on
the message list's context menu and its key.

### Keys you reassign apply in a message window too

A key you reassigned in **File → Settings → Keyboard Shortcuts** worked in the main window, but a
message open in its own window kept the original key. The message window now uses your keys for the
actions it shares with the main window: reply, forward, delete, save, print, watch, rules and the
rest. **Save**, **Print** and **Save As** also now work while focus is on the window's toolbar or
headers, not only in the message itself. A change in Settings applies to message windows you open
afterwards.

### Sending from a disconnected shared mailbox queues the message instead of failing

A shared mailbox sends through the sign-in of the account it belongs to. While that account was signed
out, sending from the shared mailbox failed in the compose window. Now the message waits in the
**Outbox** — you hear "Message queued. It will be sent when" followed by the shared mailbox's name and
"is connected again." — and it goes out once the account it belongs to is signed in again, for
example after you reconnect the shared mailbox from the **Accounts** list. The message waits in the
one **Outbox** QuickMail keeps for all accounts, at the end of the **All Mail** group in the folder
tree, not in a folder of the shared mailbox; its row names the account it will be sent from. A
queued message waiting for a sign-in is not marked Failed, and **Send Outbox Now** says what it is
waiting for. (#614)

### Calendar, Contacts and Tasks no longer appear as mail folders on Microsoft accounts over IMAP

A Microsoft 365 or Outlook.com account set up with IMAP listed the mailbox's calendar, contacts and
task folders in the folder tree as if they held mail, and they could not be opened. Calendar,
Contacts, Suggested Contacts, Tasks and Journal are now left out, as calendar, contacts and task
folders already were for accounts that use Microsoft's own connection. The Outbox, Sync Issues and
Conversation History folders are left out too, as mailbox housekeeping rather than mail. Notes and
RSS Feeds stay, since their items open like mail. Only the English folder names are recognized so
far, so a mailbox set to another language still shows them. Other IMAP servers are not affected.
(#613)

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
| [**QuickMail-0.8.51-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.51/QuickMail-0.8.51-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.51-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.51/QuickMail-0.8.51-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.51/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.51/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.
