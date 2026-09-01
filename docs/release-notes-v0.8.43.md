# QuickMail v0.8.43 Release Notes

## Changed: a bug report now says which kind of account you use

A report sent with **Report a Bug** already describes the setting it happened in — your QuickMail
version, your Windows version, the theme, the view, the sort order, and where messages open. It did
not say how your accounts connect, and that turns out to be one of the first things worth knowing:
an account connected over IMAP, over POP3, and through Microsoft 365 genuinely behave differently
from one another, so a report that leaves it out can take a while to place.

Reports now include one more line — how many accounts you have set up and the protocols they use,
for example *"2 (IMAP, Microsoft 365)"*. Shared mailboxes are counted on their own, as in *"2
(Microsoft 365), plus 1 shared mailbox"*, because a shared mailbox is not one of your own accounts.
It is counts and protocol names, nothing else: no address, no server name, no account name. As
always the **Preview** in the report window shows the complete text before you send it, so you can
read exactly what is being sent.
([#639](https://github.com/kellylford/QuickMail/issues/639))

## Fixed: a sender's email address sometimes went missing

Opening a message, or replying to one, sometimes showed the sender's full address — name and email
address together, with Message Properties able to report it — and sometimes showed nothing but a
name. Replying to one of the latter put a bare name in the **To** field, where it stayed as ordinary
typed text instead of becoming an address you could act on.

Which one you got depended on whether the message was being read from the server or from QuickMail's
own copy on your PC, so the same message could behave either way at different times. QuickMail now
keeps the sender's address with its copy of the message. Messages saved before this release get
their address filled back in the next time they are opened.

Message Properties also reports the sender's full address now, matching the **To** line right below
it. ([#636](https://github.com/kellylford/QuickMail/issues/636))

## New: create a folder while you are writing a rule

Writing a rule is often where you decide a folder ought to exist — "everything from the school
goes in a folder called School", and there is no School folder yet. Until now that meant leaving
the rule half-written, going back to the main window to make the folder, and starting the rule
again.

The folder picker a rule opens for **Move to folder** now has the same **New Folder** button the
message move and copy pickers have had for a while. Activate it (or press **Alt+N**) with a folder
selected, type a name, and the new folder is created under that one, appears in the tree, and is
selected ready for you to choose as the rule's target — without leaving the rule you were writing.
It works in both the Rules Manager and, where Microsoft 365 server rules are available, the rule
editor there.

Two places the button does not appear, both on purpose. A POP3 account has no folders on the
server to create, so there is nothing the button could do. And a rule the picker cannot tie to one
particular account — a rule that has no account of its own, or an account whose folders QuickMail
has not read yet — falls back to showing every account's folders, where a new folder would land in
one mailbox while the rule files mail in another; picking an existing folder is still offered
there, creating one is not.
([#645](https://github.com/kellylford/QuickMail/issues/645))

## Fixed: Shift+Tab goes back to the folder list

The window that asks you to choose a folder — moving or copying messages, moving or copying a
folder, choosing a rule's target, choosing the startup folder — let you tab forward from the list
of folders to the buttons, but not back. Shift+Tab landed on **Cancel** instead of returning to the
folders, so the only way back to the list was to keep tabbing forward all the way round.

Shift+Tab now returns to the folder list, on the folder you had selected rather than at the top of
it. Tab and Shift+Tab move round the window in a ring: the folders, **New Folder** where it is
offered, **Open**, **Cancel**, and back to the folders.

Two smaller things came with it. **Open** is unavailable while the selection is an account name, or
a folder path that is not itself a folder, and Tab stopped dead there instead of moving on to a
button you could actually use; it now goes to the next available one. And in **Go to Folder**, Tab
from the search box reached the buttons before it reached the list of folders those buttons act on
— the list comes first now.

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
| [**QuickMail-0.8.43-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.43/QuickMail-0.8.43-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.43-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.43/QuickMail-0.8.43-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.43/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.43/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 8 runtime — you do not need to install .NET separately.
