# QuickMail User Guide

QuickMail is a keyboard and screen reader friendly email program for Windows. All features are reachable from the keyboard; no mouse is required.

---

## Contents

- [System Requirements](#system-requirements)
- [Adding Accounts](#adding-accounts)
- [Main Window](#main-window)
- [Reading Mail](#reading-mail)
- [Composing Mail](#composing-mail)
- [Address Book](#address-book)
- [Grab Addresses from a Message](#grab-addresses-from-a-message)
- [Flags](#flags)
- [Mail Rules](#mail-rules)
- [Saved Views](#saved-views)
- [Calendar](#calendar)
- [Tools Menu](#tools-menu)
- [Settings](#settings)
- [Themes](#themes)
- [Screen Reader Announcements](#screen-reader-announcements)
- [Keyboard Shortcuts](#keyboard-shortcuts)

---

## System Requirements

- Windows 10 (1703 or later) or Windows 11
- Microsoft Edge WebView2 Runtime (the installer checks for this automatically)
- An IMAP/SMTP email account (Gmail, Outlook.com, Microsoft 365, or any standard IMAP provider)

---

## Adding Accounts

Open **Settings → Accounts** (`Ctrl+,` then navigate to Accounts) or press **Ctrl+Shift+A** from the main window. Press **Add Account**.

### Standard IMAP/SMTP

Choose **Standard IMAP/SMTP** as the account type and fill in:

- **Display name** — shown as the "From" name on outgoing mail
- **Email address**
- **Password**
- **IMAP server**, port, and whether to use SSL
- **SMTP server**, port, and whether to use SSL

Press **Verify** to test the connection before saving.

### Microsoft Account (Outlook.com / Microsoft 365)

Choose **Microsoft OAuth**. The server fields fill in automatically. Activate **Sign in with Microsoft** — your browser opens to a Microsoft sign-in page. Sign in and grant QuickMail permission, then close the browser window. Back in QuickMail, activate **Add Account**.

### Gmail (Google Account)

Enter your Gmail address in the **Email / Username** field — QuickMail then automatically selects Google authentication for the account, so you do not need to set the authentication type yourself. Activate the **Sign in with Google** button; your browser opens to a Google sign-in page. Complete the sign-in, grant QuickMail permission to read and send mail, then close the browser window. Back in QuickMail, activate **Add Account**. Gmail's server settings fill in automatically.

You may see a message that no password was saved for the account. This is expected with Google authentication — Gmail signs in through your Google account rather than a stored password, so there is no password to save. The Google sign-in itself is stored securely in Windows Credential Manager and refreshes automatically; you are not prompted to sign in again unless you revoke access from your Google account settings.

When you sign in, Google shows a warning that QuickMail is an unverified app. This is expected — choose **Advanced** and continue to **Go to QuickMail (unsafe)**. Google's app-verification process can take several weeks and may require an expensive third-party security assessment. If you would rather avoid the warning, generate a Gmail app-specific password from your Google Account security settings and use it with the standard **Password** authentication method instead.

### iCloud

Enter your iCloud address (`@icloud.com`, `@me.com`, or `@mac.com`) in the **Email / Username** field — QuickMail recognises it and fills in Apple's server settings automatically.

**App-specific password required.** Apple does not allow third-party apps to use your Apple ID password directly. Generate an app-specific password at **appleid.apple.com** (Sign-In & Security → App-Specific Passwords) and enter it in the Password field. QuickMail shows a reminder in the password area when it detects an iCloud address.

### Managing Accounts

Open **Settings → Accounts** to rename, edit, or remove an account. Removing an account does not delete mail from the server. For OAuth accounts (Microsoft or Google), removing the account also clears the stored credential from Windows Credential Manager.

---

## Main Window

The main window has four panes reachable by pressing **F6** to cycle forward or **Shift+F6** to cycle backward:

1. **Account list** — your email accounts
2. **Folder tree** — folders for the selected account, or all accounts in unified view
3. **Message list** — messages in the selected folder
4. **Reading pane** — the currently selected message

You can also jump directly to any pane:

| Shortcut | Destination |
|----------|-------------|
| `Ctrl+1` | Account list |
| `Ctrl+2` | Folder tree |
| `Ctrl+3` | Message list |
| `Ctrl+9` | Status bar |

### Unified Inbox

Select **All Inboxes** at the top of the folder tree to see messages from all accounts merged into one list, sorted by date.

### Moving and Copying Messages

Select one or more messages (or a sender/recipient group, or a conversation) and choose **Move to Folder…** or **Copy to Folder…** from the context menu (Shift+F10) or the command palette. Both open a folder picker showing the same hierarchical tree used in the main folder panel — folders nested under their parent, with account names as headers when more than one account is present. Arrow through the tree and press Enter to complete the move or copy.

### Message List Views

Press `Ctrl+Shift+V` (or use the **View** menu) to switch how messages are grouped:

- **Messages** — flat list, newest first
- **Conversations** — messages grouped by thread
- **From** — messages grouped by sender
- **To** — messages grouped by recipient

### Searching

Press `Ctrl+Shift+S` to open the search box. Type your query and press Enter. Results appear in the message list. Press Escape to clear the search and return to the full folder.

### Searching Folders

Press `Ctrl+Shift+F` to search folder names. Type to filter the tree, press Enter to navigate to the matching folder.

### Refreshing

Press **F5** to manually refresh the current folder.

### Command Palette

Press **Ctrl+Shift+P** to open the command palette. Type any part of a command name to find it. Press Enter to run it. This is the fastest way to discover and run any action in the app.

### Keyboard Customization

Open **Settings → Keyboard** to reassign any shortcut to a different key. Type in the field for a command to capture a new key combination. Changes take effect immediately and survive restarts.

### Checking for Updates

QuickMail checks for a newer release in the background each time it starts. The top entry of the **Help** menu always shows the result: **"No updates available — running version X.Y.Z"** when you are current, or **"Update available: vX.Y.Z"** when a newer release exists. Activating the entry opens the matching page on the QuickMail releases site in your browser, so it is always useful — even when there is nothing new. If an update is found, a spoken announcement follows a few seconds after launch; the background check itself is silent when you are already up to date.

The **Help** menu also has a **Keyboard Tutorial** entry, a short interactive walkthrough of core navigation (F6 pane cycling, Ctrl+1/2/3, the command palette, and Escape) for anyone new to the app.

---

## Reading Mail

### Opening a Message

Press Enter on a message in the list to open it in the reading pane (or in a new tab or window, depending on your **Reading Mode** setting in **Settings → General**).

Press **Ctrl+Enter** to open a message in a new tab regardless of the Reading Mode setting.

### Reading Pane

The reading pane renders HTML messages with WebView2. Links open in your default browser. Images from remote sources are blocked by default.

Press **F6** or **Shift+F6** to move between the reading pane and other panes.

### Message Windows

When Reading Mode is set to **Window**, messages open in a separate window. Each window has a full menu bar (**File, Message, Navigate**), a toolbar, and a command palette. Shortcuts work the same as in the main window:

| Shortcut | Action |
|----------|--------|
| `Ctrl+R` | Reply |
| `Ctrl+Shift+R` | Reply All |
| `Ctrl+F` | Forward |
| `Delete` | Delete |
| `Ctrl+Q` | Mark as Read |
| `Ctrl+Shift+G` | Grab Addresses |

Deleting a message from its window closes the window and returns focus to the originating position in the message list.

### Message Properties

Press **Alt+Enter** on any message to open a properties window showing sender, recipients, date, size, and flags.

### Marking as Read

Press `Ctrl+Q` to mark the selected message or messages as read. Messages are also marked read automatically when you open them (configurable in **Settings → General**).

### Deleting Messages

Press **Delete**. Deleted messages go to Trash. Press `Ctrl+Shift+E` to empty the Trash for the selected account.

### Tabs

QuickMail can open messages in tabs, keeping multiple messages visible at once.

| Shortcut | Action |
|----------|--------|
| `Ctrl+Tab` | Next tab |
| `Ctrl+Shift+Tab` | Previous tab |
| `Ctrl+W` | Close tab |
| `Ctrl+Shift+`` ` | Tab list (navigate by name) |
| `Ctrl+Shift+T` | Focus tab strip |

---

## Composing Mail

### Opening a Compose Window

| Shortcut | Action |
|----------|--------|
| `Ctrl+N` | New message |
| `Ctrl+R` | Reply |
| `Ctrl+Shift+R` | Reply All |
| `Ctrl+F` | Forward |

### Compose Panes

Press **F6** to cycle between the address fields (To, Cc, Bcc), the subject, and the message body. You can also jump directly to a field:

| Shortcut | Destination |
|----------|-------------|
| `Alt+U` | Subject field |
| `Alt+M` | From account |
| `Alt+Y` | Message body |

### Address Autocomplete

Start typing a name or address in To, Cc, or Bcc. QuickMail searches your address book and recent contacts. Arrow down to choose a suggestion; press Enter or Tab to accept. Press Escape to dismiss without accepting.

### Editing Modes

Every compose window offers three modes, switchable at any time with `Ctrl+Shift+1/2/3` or the **View** menu:

| Mode | Shortcut | Description |
|------|----------|-------------|
| Plain Text | `Ctrl+Shift+1` | Unformatted text |
| Markdown | `Ctrl+Shift+2` | Write Markdown; sent as formatted HTML |
| HTML | `Ctrl+Shift+3` | Rich text editor with real formatting |

Switching from a rich mode to Plain Text asks for confirmation because formatting would be lost.

Messages composed in Markdown or HTML are sent with both an HTML part and a plain text part.

### Formatting (Markdown and HTML Modes)

| Command | Shortcut |
|---------|----------|
| Bold | `Ctrl+B` |
| Italic | `Ctrl+I` |
| Underline (HTML only) | `Ctrl+U` |
| Strikethrough | `Ctrl+Shift+X` |
| Heading 1 / 2 / 3 | `Ctrl+Alt+1` / `Ctrl+Alt+2` / `Ctrl+Alt+3` |
| Bullet list | `Ctrl+Shift+L` |
| Numbered list | `Ctrl+Shift+N` |
| Insert link | `Ctrl+L` |
| Clear formatting | `Ctrl+Space` |
| Announce formatting at cursor | `Ctrl+T` |
| Show formatting in browsable list | `Ctrl+Shift+T` |

**Nested lists:** In a list, press **Tab** to indent an item (creating a sub-list); press **Shift+Tab** to dedent.

### Checking Formatting (HTML Mode)

- **`Ctrl+T`** — announces a one-line summary: "Heading 2. Bold on, Italic off, Underline off."
- **`Ctrl+Shift+T`** — opens a small window listing the same details one per row. Arrow through them; press Escape or Enter to close.

### Preview (Markdown and HTML Modes)

Press **F8** to open a rendered preview in a separate window. The preview is fully focusable, so you can browse the formatted output exactly as a recipient would. Links open in your default browser. Press **Escape** or **Ctrl+W** to close the preview.

### Check Spelling (Full Dialog)

Press **F7** (or choose **Tools → Check Spelling**) to review the whole message in the classic spelling dialog. The check covers the message body first, then the subject line, and finishes with a confirmation that reports how many words were changed.

For each word that is not in the dictionary, the Spelling window shows the word in the line where it appears, a list of suggestions, and a "Change to" box pre-filled with the top suggestion. A screen reader announces "Not in dictionary:" followed by the word, and focus lands on the suggestions list with the first suggestion selected so it is spoken automatically. Arrow through the list to hear other choices — the highlighted suggestion fills the Change to box — or type your own correction.

| Key | Action |
|-----|--------|
| `Alt+C` or `Enter` | Change — replace the word with the Change to text |
| `Alt+L` | Change All — also fix every later occurrence this session |
| `Alt+I` | Ignore this occurrence |
| `Alt+G` | Ignore All — skip this word for the rest of the check |
| `Alt+A` | Add to Dictionary — never flag this word again |
| `Alt+R` | Read the line containing the word |
| `Alt+T` / `Alt+S` / `Alt+N` | Move to the Change to box / Suggestions list / context |
| `F6` / `Shift+F6` | Cycle between the context, suggestions, and buttons |
| `Escape` | Close the dialog and return to the message |

Words you add to the dictionary are stored in `custom.lex` in your QuickMail profile folder and apply everywhere spell checking runs, permanently. To remove a word, edit that file in a text editor while QuickMail is closed (one word per line). Ignore All lasts only for the current check.

The message stays visible and editable behind the Spelling window, with the current word selected, so you can always see the correction in place.

### Inline Spell Check

| Shortcut | Action |
|----------|--------|
| `Ctrl+F7` | Jump to next misspelling |
| `Ctrl+Shift+F7` | Jump to previous misspelling |
| `Alt+1` / `Alt+2` / `Alt+3` | Accept first / second / third spelling suggestion |

> **Changed keys:** inline navigation was previously `F7` / `Shift+F7`. `F7` now opens the Check Spelling dialog, matching the binding word processors have used for decades. If you had customized these shortcuts, your bindings are unchanged; to restore the old behavior, reassign them in the keyboard customizations dialog.

Inline navigation wraps around the message so it always finds misspellings wherever the cursor starts.

When a screen reader is active, QuickMail announces each misspelling along with up to three suggestions. By default, each suggestion is numbered — for example: "Misspelling: teh. 1: the, 2: then, 3: them." Press `Alt+1`, `Alt+2`, or `Alt+3` to replace the misspelled word with that numbered suggestion without leaving the compose area.

Control announcement behavior in **Settings → Screen Reader Announcements**:

- **Announce spelling suggestions** — turn off to hear only the misspelled word without suggestions.
- **Spelling Suggestions Verbosity** — choose **Numbers with suggestions** (default) to hear "1: the, 2: then" so `Alt+1/2/3` maps directly to what is spoken, or **Just suggestions** to hear "the, then, them" without numbers.

### Attachments

Attach files with `Ctrl+Shift+A` in the compose window, by pasting files from the clipboard (`Ctrl+V`), or by dragging and dropping them onto the window. A screen reader announces how many files were attached.

### Message Templates

Save a message you write often — a standard reply, a form response — as a reusable template.

- **Save as Template** (command palette) saves the current subject and body as a new template, titled from the subject line (or "Untitled" if the subject is empty).
- **Insert Template…** (command palette) opens a search-and-select picker: type to filter by title, arrow to a template, and press **Insert** or Enter to add its subject (if the Subject field is empty) and body into the message you are composing.

Templates can include `{sender}`, `{date}`, and `{time}` placeholders, which are replaced with your display name and today's date and time when the template is inserted. Templates are plain text; in HTML mode, only the text is inserted.

### Checking Addresses

Press `Ctrl+K` to check every address in the To, Cc, and Bcc fields. QuickMail looks up any bare name against your address book — if exactly one contact matches, it fills in that contact's address automatically. Addresses that are not valid and cannot be resolved are flagged as invalid. A screen reader announces how many addresses were resolved and how many are invalid.

### Sending

| Shortcut | Action |
|----------|--------|
| `Alt+S` or `Ctrl+Enter` | Send |

### Auto-Save Drafts

QuickMail saves your compose as a draft automatically every 2 minutes (on by default). A quiet status line in the compose window shows "Auto-saved 3:42 PM" after each save — no announcement interrupts your writing. If a save fails, it is announced once. You can check the last auto-save time from the command palette: **Ctrl+Shift+P → Announce Last Auto-Save**.

Control auto-save in **Settings → General → Composing**: turn it off, change the interval (30 seconds to 10 minutes), and set the default compose mode for new messages.

### Forwarding with Attachments

When forwarding a message that has attachments, QuickMail opens an **Include Attachments** dialog before downloading. All attachments are checked by default. Arrow between files and press Space to toggle individual ones. Press Tab to reach Forward (include checked files) or Cancel.

---

## Address Book

Press **Ctrl+Shift+B** to open the address book.

The address book lists everyone you have sent mail to or explicitly added. You can search by name or address, edit contact details, and organize contacts into groups.

### Groups

Groups let you write to multiple people with a single address. Select a group in the address book and press Enter to expand it and see its members. To compose to a group, type the group name in the To or Cc field and select it from autocomplete.

### Managing Groups

Open the address book and use the **Groups** pane to create, rename, and delete groups, and to add or remove members.

Group names must be unique, regardless of letter case ("Team" and "team" count as the same name). If you try to create or rename a group to a name that already exists, QuickMail tells you the name is already in use and leaves your text in place so you can choose a different one. Groups are never merged.

---

## Grab Addresses from a Message

When reading a message with many recipients, you can save all of them to your address book — and optionally add them to a group — in one step.

1. Open a message and press **Ctrl+Shift+G**.
2. The **Add to Address Book** window lists every address from the message (From, To, and Cc), all checked by default. Uncheck any you do not want.
3. **To add contacts only:** press **Save** (or Enter).
4. **To add contacts to a group:** check **Add to group**, then:
   - Choose an existing group from the **Group** combo box, or
   - Choose **Create new group** and type a name in the **New group name** field.
   - Press **Save**.
5. Press **Cancel** or **Escape** to close without saving.

If you choose **Create new group** and type a name that already exists, QuickMail will not create a second group with that name. It tells you the group already exists and keeps the window open so you can enter a different name, or pick the existing group from the list instead.

Tab moves through the address list (one Tab stop for the whole list — arrow keys move between individual checkboxes), then to **Add to group**, then to the group combo, then to the name field, then to Save and Cancel.

---

## Flags

Flags mark messages for follow-up and sync across all mail clients.

### Basic Flagging

Press **K** to toggle the default flag on the selected message. Press **K** again to clear it. A screen reader announces the result: "Flagged: Urgent." or "Unflagged."

In Conversations, From, or To view, pressing **K** on a group row flags every message in the group.

### Named Flags

Press **Ctrl+Shift+K** to open the flag picker. Arrow to a flag and press Enter to apply it. A **Clear flag** option at the bottom removes the current flag.

### Creating and Managing Flags

Open the **Flag Manager** from the command palette (**Ctrl+Shift+P → Manage Flags**). You can create up to 20 named flags, each with an optional color. Use **Set as K default** to make any flag the one that **K** applies.

### Filtering by Flag

Open the View menu or the filter combo box and choose **Flagged** to see only flagged messages in the current folder.

### All Flagged Mail

The **All Flagged Mail** virtual folder in the folder tree aggregates flagged messages across all accounts.

### Flag Accessibility

The flag name is announced before the read status when you navigate to a flagged message — for example, "Urgent. Unread. Kelly Ford. Budget deadline." This makes it immediately clear a message needs attention. You can turn this off in **Settings → General → Accessibility → Announce flag status**.

---

## Mail Rules

Mail rules run automatically when mail arrives and can move, flag, mark as read, or delete messages based on sender, recipient, subject, or other criteria.

Open the **Rules Manager** from the **Tools** menu (`Ctrl+Shift+L`) or the command palette. Each rule has:

- **Conditions** — criteria the message must match (any or all)
- **Actions** — what to do when conditions are met (move to folder, flag, mark read, delete)
- **Active** toggle — turn a rule on or off without deleting it

Rules run in order. Drag to reorder, or use the Move Up / Move Down commands.

### Creating a Rule from a Message

Select a message and choose **Create Rule from Message** from the context menu or the command palette. QuickMail opens a new rule pre-filled with a condition matching the sender and, if present, the subject — a quick starting point you can adjust before saving.

---

## Saved Views

A saved view is a named filter you can return to instantly — for example, "Unread from work" or "Flagged in the last 7 days."

Open the **View Manager** from the **View** menu or the command palette. Create a view by choosing a folder (or All Inboxes), a message filter, and optionally a date limit. Assign a hotkey to jump to it directly.

Press the assigned hotkey from anywhere in the main window to switch to that view immediately.

---

## Calendar

QuickMail tracks upcoming appointments drawn from meeting-invitation emails (`.ics` attachments) in your mailbox. This is not a general-purpose calendar — it exists so that once you accept a meeting invitation in QuickMail, you have a place to see what you accepted.

A **Calendar** node appears in the folder tree. Select it to replace the message list with a list of upcoming events, sorted by date and time.

- **Arrow keys** move through the event list.
- **Enter** opens the email the selected event came from. If that message is no longer in your local cache, QuickMail announces this and does nothing rather than attempting a failing fetch.
- **T** filters the list to today's events.
- **F5** refreshes the calendar.
- **F6** cycles to the next pane as usual.

### Responding to Invitations

Open a meeting invitation email as you would any message. Below the invitation details (organizer, time, location, description), the message body shows three response links: **Accept**, **Tentative**, and **Decline**. Activating one records your response and updates the Calendar immediately — no restart or refresh needed. These links are not shown on a cancellation notice. When you receive an updated or cancelled invitation, the corresponding calendar entry updates or is removed automatically.

---

## Tools Menu

The **Tools** menu is always available from the main window menu bar and groups together the commands used less often than day-to-day mail actions:

- **Manage Themes…** — opens the [Theme Manager](#themes).
- **Next Theme** / **Previous Theme** — cycle through your available themes.
- **Address Book…** (`Ctrl+Shift+B`)
- **Rules…** (`Ctrl+Shift+L`) — opens the [Rules Manager](#mail-rules).
- **Command Palette…** (`Ctrl+Shift+P`)

---

## Settings

Press **Ctrl+,** to open Settings.

### General

- **Reading Mode** — Reading Pane, Tab, or Window
- **Mark messages read** — automatically on open, or manually only
- **Default compose mode** — Plain Text, Markdown, or HTML
- **Auto-save drafts** — on/off and interval

### Accounts

Add, edit, or remove accounts. Sign out of OAuth accounts.

### Advanced

**QuickMail Logging**

- **Enable logging** — when checked, QuickMail writes activity to `quickmail.log` in your profile directory (usually `%APPDATA%\QuickMail`). Uncheck to stop writing the log file. Changes take effect when you select **Save**.
- **Delete QuickMail log** — deletes the log file immediately after confirmation. If logging is still on, a new log file is created the next time an activity is logged.

> **Note:** If QuickMail was launched with the `/debug` flag, logging always runs regardless of the Enable logging setting. The `/debug` flag is intended for diagnosing problems and overrides this preference so that nothing is missed.

**Log Format**

Controls the order of timestamp and message text in each log line. **Action first** (default) places the message before the timestamp, which is easier to scan since the log is already in chronological order. **Time first** uses the original format with the timestamp at the start of each line.

### Keyboard

Reassign shortcuts for any registered command.

### Appearance

Choose how QuickMail looks and adjust it to your vision needs.

**Theme** — the color scheme for the whole app:

- **System** (default) — follows the Windows light or dark setting automatically.
- **Parchment** — the standard light look: warm off-whites with a muted steel-blue accent.
- **Parchment Dark** — the dark counterpart.
- **Ember**, **Fjord**, **Heather** — warm, cool, and muted variations on the light look.
- Any theme you created or imported in the Theme Manager also appears here.

Theme changes apply immediately — no restart. Open messages re-render in the new colors.

**Font** — override the app font. **(Theme default)** uses the theme's own font.

**Text size** — a dropdown with fixed stops at 100%, 110%, 125%, 150%, 175%, and 200%, independent of Windows display scaling.

**Vision settings:**

- **Always underline links** — underlines every link in message content, even when the sender removed the underline.
- **Thicker keyboard focus indicators** — doubles the width of the keyboard focus ring.
- **Apply theme colors to message content** — overrides the colors and fonts chosen by a message's sender with your theme's colors. Turn on when messages arrive with hard-to-read colors; turn off to see messages as their senders designed them.

**Windows High Contrast:** when High Contrast is on, QuickMail steps aside entirely — every color comes from your Windows High Contrast palette, and QuickMail's own styling is withdrawn. Your theme choice is remembered and returns when High Contrast is turned off. Font and text-size settings continue to apply.

See [Themes](#themes) for the Theme Manager and a description of each built-in theme.

### Screen Reader Announcements

Control which categories of announcements QuickMail makes:

| Setting | What it controls |
|---------|-----------------|
| Custom Announcements | Master on/off for all programmatic announcements |
| Announce hints | Instructional tips ("Press Escape to return") |
| Announce status | Background progress (sync, loading, connection state) |
| Announce results | Action outcomes (messages moved, addresses saved, flag changes) |
| Announce formatting while navigating | Block type announced when caret enters a new paragraph type in HTML compose |
| Announce flag status | Flag name prepended to message row when navigating the list |
| Announce spelling suggestions | Suggestions included when a misspelling is announced |
| Spelling Suggestions Verbosity | Numbers with suggestions (default) or just suggestions |

All settings default to on except **Announce flag status** and **Announce spelling while typing** (off by default). **Spelling Suggestions Verbosity** defaults to **Numbers with suggestions**. Turn off **Custom Announcements** to silence everything at once; turn it back on to restore your individual preferences.

---

## Themes

QuickMail's color scheme is controlled through **Settings → Appearance** (see [Settings](#settings)) and managed in more detail through the **Theme Manager**.

### Theme Manager

Open the Command Palette (**Ctrl+Shift+P**) and choose **Manage Themes**, or choose **Manage Themes…** from the **Tools** menu. The Theme Manager is a separate, non-blocking window, so you can leave it open while you try a theme against real messages. From the theme list, press Tab to reach the actions:

- **Apply** — switch to the selected theme immediately.
- **Duplicate** — copy a theme as a starting point for your own. A name field appears with a suggested name.
- **Rename** / **Delete** — for your own themes (built-ins cannot be changed or deleted).
- **Export…** — save a theme as a `.quickmailtheme` file to share or move to another machine.
- **Import…** — load a `.quickmailtheme` file. If a theme file has a problem, QuickMail tells you exactly what is wrong (for example, which color value is not a valid hex color).
- **Open themes folder** — opens the folder where your themes are stored, for hand-editing.

Below the theme list and actions, a read-only **Theme description** box always shows a plain-language account of the currently selected theme — its overall look, its fonts, and every individual color together with where in the app that color is used. This box is there so you can understand and compare themes by ear or by reading, without needing to see the colors. See [Built-in Themes](#built-in-themes) below for the description of each theme that ships with QuickMail.

The Command Palette also offers **Next Theme** and **Previous Theme** to cycle through themes, and a **Theme: [name]** command for each theme. None of these have a default keyboard shortcut — assign one in **Settings → Keyboard** if you want direct access.

**Editing a theme by hand:** a theme is a plain, documented JSON text file. Duplicate a built-in theme, choose **Open themes folder**, and edit the copy in any text editor. Colors are hex values like `#3D5A80`; any color you leave out is filled in from the built-in Light or Dark theme (whichever the file's `base` names). A typical minimal theme:

```json
{
  "formatVersion": 1,
  "id": "my-theme",
  "name": "My Theme",
  "base": "light",
  "colors": {
    "accent": "#8F4531",
    "windowBackground": "#FBF7F2"
  },
  "typography": { "fontFamily": "Segoe UI", "baseFontSize": 13 }
}
```

The full color token list: `windowBackground`, `surfaceBackground`, `chromeBackground`, `inputBackground`, `border`, `borderSubtle`, `inputBorder`, `textPrimary`, `textSecondary`, `textDisabled`, `textOnAccent`, `accent`, `accentSubtle`, `hyperlink`, `selectionBackground`, `selectionText`, `selectionInactive`, `focusIndicator`, `error`, `errorBackground`, `warning`, `warningBackground`, `success`, `successBackground`, `info`, `infoBackground`. Edits take effect the next time the theme is applied (reopen the Theme Manager and choose Apply, or restart).

### Built-in Themes

QuickMail ships with six themes. **System** follows Windows; the other five are always available regardless of your Windows setting. Each description below is a shorter version of what the Theme Manager's **Theme description** box reads for that theme — open the Theme Manager and select a theme to hear or read the full breakdown, including every individual color and exactly where it is used (message list, links, selection, focus outline, error/warning/success text, and so on).

**System** — follows the Windows light or dark setting. Whichever it resolves to today, it currently displays the same colors as Parchment (below): an off-white background, very dark cool-gray text, and a dark muted-blue accent.

**Parchment** (light, default) — an off-white background (Snow) with very dark cool-gray text and a dark muted-blue accent (Dark Slate Blue) used for buttons and the unread marker. Panels and toolbars use warm off-white tones (White Smoke, Linen); links are medium blue. This is QuickMail's standard light look.

**Parchment Dark** — the dark counterpart to Parchment: a very dark gray background with light gray text and a light muted-blue accent. Panels and toolbars use slightly lighter dark-gray tones for depth; links are light blue. Status colors (error, warning, success, information) are lightened versions of Parchment's, chosen for contrast against the dark background.

**Ember** — a warm light theme: a warm off-white background (Floral White) with very dark cool-gray text and a dark red accent (Sienna) in place of Parchment's blue. Selection highlights use a pale muted orange rather than blue. Links remain medium blue for consistency across themes.

**Fjord** — a cool light theme: an off-white background with a faint cool cast (Ghost White) and a dark muted-cyan accent (Dark Slate Gray) in place of Parchment's blue. Selection highlights use a light cool gray-green.

**Heather** — a muted light theme: an off-white background (Ghost White) with a cool gray accent (Dim Gray) instead of a saturated color. Selection highlights use a light cool gray-lavender. This is the most subdued of the built-in themes.

The four light themes are close cousins. Ember, Fjord, and Heather each change only four colors from Parchment: the main window background tint, the accent color, the soft accent-fill color, and the selection highlight. Everything else — panels and toolbars, borders, body and secondary text, the medium-blue hyperlink color, the focus outline, and the four status colors (error, warning, success, information) — is inherited unchanged from Parchment. Parchment Dark is the only theme with a fully dark palette.

---

## Screen Reader Announcements

QuickMail uses UIA Notification events (the correct API for desktop screen readers on Windows 10 and later) rather than ARIA live regions, which only work in web browsers.

Every announcement is optional and controlled by the settings above. No custom screen reader scripting is required; the app works out of the box with any screen reader.

---

## Keyboard Shortcuts

### Main Window

| Shortcut | Action |
|----------|--------|
| `F6` / `Shift+F6` | Cycle panes forward / backward |
| `Ctrl+1` | Focus account list |
| `Ctrl+2` | Focus folder tree |
| `Ctrl+3` | Focus message list |
| `Ctrl+9` | Focus status bar |
| `Ctrl+Shift+P` | Command palette |
| `Ctrl+N` | New message |
| `Ctrl+R` | Reply |
| `Ctrl+Shift+R` | Reply All |
| `Ctrl+F` | Forward |
| `Delete` | Delete |
| `Ctrl+Q` | Mark as Read |
| `Ctrl+A` | Select all messages (message list) |
| `Alt+Enter` | Message properties |
| `F5` | Refresh |
| `Ctrl+Shift+E` | Empty Trash |
| `K` | Toggle flag |
| `Ctrl+Shift+K` | Pick flag |
| `Ctrl+Shift+S` | Search messages |
| `Ctrl+Shift+F` | Search folders |
| `Ctrl+Shift+V` | View menu |
| `Ctrl+Shift+G` | Grab Addresses from Message |
| `Ctrl+Shift+B` | Address Book |
| `Ctrl+Shift+L` | Rules Manager |
| `Ctrl+,` | Settings |
| `F1` | User Guide |
| `Shift+,` | First message in group |
| `Shift+.` | Last message in group |

**Move to Folder…** and **Copy to Folder…** are available from the context menu (Shift+F10) or the command palette; they have no default keyboard shortcut. **Manage Themes**, **Next Theme**, and **Previous Theme** are likewise command-palette-only unless you assign a shortcut yourself in Settings → Keyboard.

**Calendar list** (when the Calendar folder is selected): `T` filters to today's events; `Enter` opens the source invitation email; `F5` refreshes; arrow keys browse.

### Tabs

| Shortcut | Action |
|----------|--------|
| `Ctrl+Enter` | Open message in new tab |
| `Ctrl+Tab` | Next tab |
| `Ctrl+Shift+Tab` | Previous tab |
| `Ctrl+W` | Close tab |
| `Ctrl+Shift+T` | Focus tab strip |
| `Ctrl+Shift+`` ` | Tab list |

### Compose Window

| Shortcut | Action |
|----------|--------|
| `F6` / `Shift+F6` | Cycle between address fields, subject, and body |
| `Alt+U` | Focus Subject field |
| `Alt+M` | Focus From account |
| `Alt+Y` | Focus message body |
| `Alt+S` or `Ctrl+Enter` | Send |
| `Ctrl+Shift+1/2/3` | Switch to Plain Text / Markdown / HTML mode |
| `F7` | Check Spelling (full dialog) |
| `Ctrl+F7` / `Ctrl+Shift+F7` | Next / previous misspelling (inline) |
| `Alt+1` / `Alt+2` / `Alt+3` | Accept first / second / third spelling suggestion |
| `F8` | Open preview (Markdown and HTML) |
| `Ctrl+B` | Bold |
| `Ctrl+I` | Italic |
| `Ctrl+U` | Underline (HTML only) |
| `Ctrl+Shift+X` | Strikethrough |
| `Ctrl+Alt+1/2/3` | Heading 1 / 2 / 3 |
| `Ctrl+Shift+L` | Bullet list |
| `Ctrl+Shift+N` | Numbered list |
| `Ctrl+L` | Insert link |
| `Ctrl+Space` | Clear formatting |
| `Ctrl+T` | Announce formatting at cursor |
| `Ctrl+Shift+T` | Show formatting in browsable list |
| `Ctrl+Shift+A` | Add attachment |
| `Ctrl+K` | Check addresses |
| `Ctrl+Shift+P` | Command palette |
| `Escape` | Close window (when no menu or dropdown is open) |

**Insert Template…** and **Save as Template** are available from the command palette; they have no default keyboard shortcut.
