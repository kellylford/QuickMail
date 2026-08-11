# QuickMail v0.8.39 Release Notes

## Download

There are four downloads. Take a regular one unless you know your PC has an ARM processor — to check, open **Settings → System → About** and read **System type**.

| Download | When to use |
|----------|-------------|
| **`QuickMail-0.8.39-win.msi`** — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| **`QuickMail-0.8.39-win-arm64.msi`** — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |
| **`QuickMail-arm64.exe`** — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 8 runtime — you do not need to install .NET separately.

---

## New: choose the folder QuickMail opens in

QuickMail has always opened in **All Mail**. If you wanted it to open somewhere else, the only way was to save a view of that folder and then mark it as the default in the View Manager — four steps, and a saved view left behind that you never wanted. Several people have asked for something simpler.

Now you set it where you already are. In the folder tree, move to the folder you want, open its context menu with **Shift+F10** or the Applications key, and choose **Set as Startup Folder**. That is it. **Clear Startup Folder** on the same menu goes back to All Mail, and both are in the Command Palette (**Ctrl+Shift+P**) and can be given keys in **File → Settings → Keyboard Shortcuts**.

There is also a new **Startup** tab in **File → Settings**, where **Opens in** shows your current choice, **Choose…** opens the folder tree to pick a different one, and **Clear** goes back to All Mail.

You can pick any folder — one account's Inbox, a project folder buried a few levels down, or one of the views at the top of the tree such as **All Inboxes** or **All Mail**.

**It is on screen immediately.** QuickMail now remembers your folder list between runs, so your startup folder and its messages are showing before it has contacted any server. The folder tree is fully drawn at that point too, rather than filling in a moment later.

If the folder later disappears — you delete it, rename it, or remove the account — QuickMail opens All Mail and tells you why. Nothing to repair. ([#498](https://github.com/kellylford/QuickMail/issues/498), [#516](https://github.com/kellylford/QuickMail/issues/516))

## Changed: the default view checkbox is gone, and your setting was moved for you

Because a startup folder is now a folder, the **Default view (applied on startup)** checkbox in the View Manager has been removed, along with the Options section that held it.

If you were using it, QuickMail converts your choice to the new setting the first time it starts — including a view over several folders, which it keeps working as it did. Check **File → Settings → Startup** if you want to see what it picked. Your saved views themselves are untouched: they are still named filters with hotkeys, and everything else about them works as before. ([#516](https://github.com/kellylford/QuickMail/issues/516))

## New: choose how much mail is checked at startup

If you have several accounts and a lot of folders, QuickMail used to check every one of them while starting, whether or not you were going to look at them.

**File → Settings → Startup** now has **Startup Sync** with three choices:

- **Just my startup folder** (the new default) — checks only what your startup folder shows. If your startup folder is All Inboxes that means every inbox; if it is All Mail, that still means everything, because All Mail shows everything.
- **Every account's inbox** — checks each account's inbox whichever folder you open in.
- **Every folder** — how QuickMail behaved before this setting existed.

New mail still arrives in your inboxes straight away whichever you choose, so notifications are unaffected. Other folders are caught up by the background check — the **Check for new mail every** setting on the **General** tab of Settings. If you have that set to **Off**, other folders are only checked when you open them. ([#516](https://github.com/kellylford/QuickMail/issues/516))

## New: each folder remembers how you left it

Folders do not all want the same treatment. An inbox reads well grouped into **Conversations**. A folder full of receipts that all share one subject reads terribly that way — it collapses into a single conversation of a hundred and fifty messages, which is exactly what one of you reported.

Now the choice is per folder. Change the view mode, filter, or sort in a folder and that folder opens that way from then on, including after a restart. Set your inbox to Conversations and your receipts folder to Messages once, and each stays where you put it.

A folder you have never changed follows the **Display mode** on the **General** tab of Settings, which also tracks the last choice you made anywhere — so a folder you open for the first time looks like the last one you set up, and one change makes it its own.

**Reset Folder View**, on the **View → Views** menu and in the Command Palette, hands a folder back to the default. To turn the whole behaviour off, clear **Remember view settings for each folder** in **File → Settings → General**; your per-folder choices are kept, so switching it back on restores them. ([#520](https://github.com/kellylford/QuickMail/issues/520))

## Fixed: leaving a view left some of it behind

**Clear View** was supposed to put things back the way they were. It restored two things and left four: the grouping, the sort, the filter, and any flag filter the view applied all stayed in force after the view was gone. Worse, simply *using* a view that grouped by conversation quietly made Conversations your default for every folder, permanently and across restarts — which is how several people ended up in Conversations without ever choosing it.

Both are fixed. A view is now a genuine overlay: using one changes no setting of yours, and leaving it restores everything it touched. If you change something while a view is active you leave the view, keeping the change you made and nothing else — so adjusting the sort inside "Flagged this week" no longer leaves you quietly filtered to flagged mail from the last seven days. ([#520](https://github.com/kellylford/QuickMail/issues/520))

## Fixed: three smaller things in the same area

- **Your sort order was forgotten at every launch.** Choosing Oldest First, or any sort other than Newest First, held for the session and then reset the next time QuickMail started. It now survives restarts, like every other preference.
- **Saving a view while "Flagged First" was in effect stored the wrong sort.** The view came back sorted newest-first instead. Every sort order now saves faithfully.
- **Clear View was missing from the Command Palette** and could not be given a keyboard shortcut. It is now in both, along with the new Reset Folder View. ([#520](https://github.com/kellylford/QuickMail/issues/520))

---

## Reporting Issues

Found a problem or have a suggestion? There are three ways to reach us — pick the one that fits:

1. **Report a Bug → Send** (Help menu, inside QuickMail). Files the report for you anonymously — it includes no email address or other identifying information, so there is no way to follow up with you. **Best when you don't want any follow-up.**
2. **Report a Bug → Copy report and open GitHub** (Help menu). Opens a pre-filled issue that you submit under your own GitHub account, so your GitHub contact information is attached. **Best when you have a GitHub account and want automatic filing plus direct contact.**
3. **Email** [quickmailissues@theideaplace.net](mailto:quickmailissues@theideaplace.net). **Best when you don't mind sending email and want a personal follow-up.**

Full details, including exactly what a report contains (and what it never contains), are in the [Reporting Issues section of the User Guide](https://kellylford.github.io/QuickMail/reporting-issues.html).
