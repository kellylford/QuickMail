# QuickMail v0.8.62 Release Notes

## Fixed

### Settings export and import summaries can be read line by line

In **Export Settings**, the box below the list that says what will be exported could be read only to the end of its first line: arrowing in it went no further. The same was true of the preview in **Import Settings**, the report shown after an import, and the note in **File → Settings → Startup** explaining why starting at sign-in is not available in a portable copy. Up and Down arrow now move through every line of each. (#840)

### Context menus opened from the keyboard land on their first item at once

Opening a message's context menu with Shift+F10 or the Applications key moved focus in two steps: first to the menu itself, then a moment later to its first item, **Reply**. A screen reader started speaking the first and was cut off by the second, and on a braille display the menu seemed to open twice. Focus now goes once, straight to the first item. The same is true of every other context menu in QuickMail, including the folder and account lists and the attachment lists; the address book's **Filter** menu opens on the filter in effect, as before, and the toolbar's **Sync range** and **View mode** menus now open on the choice in effect too. Menus opened with the mouse are unchanged. (#693)

### Row Fields buttons show their whole label at larger text sizes

In the **Message List Fields** dialog, the **Move Up**, **Move Down** and **Reset to Defaults** buttons were a fixed width, so at a larger text size their labels were cut off — "Move U", "Move Dc", "Reset to Defa". The buttons now grow to fit their labels, and wrap onto a second line if the window is too narrow for all of them. (#549)

### Theme Manager shows all of its buttons

In the **Theme Manager**, the column of buttons beside the theme list was given only the height of the list, so **Open themes folder** was cut in half, and at a larger text size **Import…** disappeared below it, while the space beside the theme description stayed empty. The column now runs the full height of the window, so every button shows in full. (#546)

### Calendar times are no longer cut short in the agenda

In the calendar's list of appointments, the **When** column had a fixed width, so a longer time was cut off with nothing to show it — "11:00 AM–12:00" with the end time's "PM" gone, which reads as a complete and wrong time. **Status** was cut off the same way at larger text sizes, while an empty column sat unused at the right. The **When**, **Status** and **Calendar** columns now widen to fit what they hold, **Subject** takes the rest of the width, and the empty column is gone. If the window is too narrow for everything, a cut-off value ends in "…" so it is clear something is missing. (#547)

### Calendar says why an event can't be changed

Deleting or editing an event your account is not allowed to change used to fail with only "Could not update your online calendar." Now QuickMail says why:

- On a calendar Microsoft or Google reports as read-only for your account — one shared with you read-only, or one you subscribe to — pressing **Delete** or **E** tells you straight away, for example "Your account can only read the Holidays calendar, so this event can't be deleted here.", without trying.
- When Google refuses a change to an event it manages itself — a birthday, out of office, working location, focus time, or an event Gmail created — QuickMail names the kind of event and where to change it instead, such as "Google doesn't let other programs change birthday events. Change the birthday on the contact in Google Contacts."
- When any provider refuses because your account is not allowed to make the change, QuickMail says that rather than reporting a general failure.

The event is left as it was in every case. (#326)

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
| [**QuickMail-0.8.62-win.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.62/QuickMail-0.8.62-win.msi) — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| [**QuickMail-0.8.62-win-arm64.msi**](https://github.com/kellylford/QuickMail/releases/download/v0.8.62/QuickMail-0.8.62-win-arm64.msi) — Windows installer, ARM | The same installer for PCs with an ARM processor, such as the Snapdragon X models of Surface Laptop and Surface Pro. |
| [**QuickMail.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.62/QuickMail.exe) — standalone portable executable | No installation required. Copy it anywhere and run. |
| [**QuickMail-arm64.exe**](https://github.com/kellylford/QuickMail/releases/download/v0.8.62/QuickMail-arm64.exe) — standalone portable executable, ARM | The portable version for PCs with an ARM processor. |

The regular downloads run on every supported PC, ARM ones included — just not as quickly there. The ARM downloads will not start at all on a non-ARM PC, so if you are unsure, the regular one is the safe guess.

All downloads include the .NET 10 runtime — you do not need to install .NET separately.
