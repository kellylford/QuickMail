# QuickMail User Guide

QuickMail is a keyboard and screen reader friendly email program for Windows. Gmail, iCloud, Outlook.com, Microsoft 365, and IMAP/SMTP providers in general are all supported.

---

## Contents

- [System Requirements](#system-requirements)
- [Installing and Updating QuickMail](#installing-and-updating-quickmail)
- [Adding Accounts](#adding-accounts)
- [For Exchange / Microsoft 365 Administrators](#for-exchange--microsoft-365-administrators)
- [Main Window](#main-window)
- [Reading Mail](#reading-mail)
- [Composing Mail](#composing-mail)
- [Address Book](#address-book)
- [Grab Addresses from a Message](#grab-addresses-from-a-message)
- [Flags](#flags)
- [Mail Rules](#mail-rules)
- [Saved Views](#saved-views)
- [Calendar](#calendar)
- [Notifications](#notifications)
- [Tools Menu](#tools-menu)
- [Reporting Issues](#reporting-issues)
- [Settings](#settings)
- [Themes](#themes)
- [Screen Reader Announcements](#screen-reader-announcements)
- [Keyboard Shortcuts](#keyboard-shortcuts)

---

## System Requirements

- Windows 10 (1703 or later) or Windows 11
- Microsoft Edge WebView2 Runtime (the installer adds this automatically when missing; included with Windows 11 and current Windows 10)
- An email account. QuickMail supports Microsoft 365 / Exchange Online and Outlook.com (through Microsoft 365 directly), Gmail, iCloud, and any standard IMAP/SMTP provider. Work or school Microsoft 365 accounts may first need approval from an organization administrator — see [For Exchange / Microsoft 365 Administrators](#for-exchange--microsoft-365-administrators).

---

## Installing and Updating QuickMail

QuickMail installs with a standard setup wizard and then keeps itself up to date. The guiding principle is that **you are in control**: the defaults are designed to keep you current with no effort, you are told when a new version has been installed, and every part of the automatic behavior can be turned off — QuickMail never stops you from updating manually instead.

### Installing for the first time

1. Download **QuickMail-win.msi** from the [releases page](https://github.com/kellylford/QuickMail/releases) and run it.
2. The setup wizard walks through a welcome page, the license agreement, and installation. QuickMail installs for the current user only — no administrator permission is needed. If the WebView2 component QuickMail uses to display mail is missing from your PC, setup adds it automatically.
3. A Start Menu entry is created. The first time QuickMail starts, it asks whether to also add a desktop shortcut — either answer is remembered, and you can change your mind anytime in **Settings → General** under **Desktop Shortcut**.

### If you already have QuickMail installed

Versions before 0.8.0 used a different installer, so moving onto the self-updating track takes one manual step:

1. Uninstall your current QuickMail from **Settings → Apps**. When the uninstaller offers to delete your data, choose **No**.
2. Download and run **QuickMail-win.msi** as described above.
3. Start QuickMail. All of your accounts, settings, contacts, rules, templates, saved views, and cached mail are exactly as you left them — your data lives in a separate location the installer never touches, and passwords stay safely in Windows Credential Manager.

This is a one-time step. From then on, updates arrive on their own.

### How updating works

Each time QuickMail starts, it quietly checks for a newer release in the background. The top entry of the **Help** menu always shows the result — **"No updates available — running version X.Y.Z"** or **"Update available: vX.Y.Z"** — so you can confirm where you stand at any moment.

When an update is found, QuickMail announces it once, downloads it quietly in the background, and installs it automatically the next time you exit and reopen the app. There is no download page, no installer to run, and no security warning — and nothing interrupts what you are doing.

If you would rather not wait for your next restart, activate the Help menu update entry. The **QuickMail Update** dialog offers three choices:

- **Restart to Update** — applies the update immediately and reopens QuickMail.
- **See what's new** — opens that version's release notes in your browser.
- **Exit** (or Escape) — closes the dialog; the update still installs on your next normal restart.

So you always know when a version change has happened, the first start after an update shows a **QuickMail Update Installed** dialog confirming the new version, with the same **See what's new** link. Press **Exit** or Escape to dismiss it; it appears only once per update.

An update never touches your mail, accounts, or settings.

### Staying in control

Two settings in **Settings → Advanced**, under **Updates**, put the whole mechanism under your control:

- **Download and install updates automatically** — on by default. Turn it off and QuickMail returns to notification-only behavior: the Help menu still tells you when a new version exists and takes you to the download page, but nothing is downloaded or installed unless you do it yourself. The change takes effect the next time QuickMail starts, and you can turn it back on whenever you like.
- **Show a message after an update has been installed** — on by default. Turn it off to skip the QuickMail Update Installed dialog; the Help menu entry still reflects your current version.

### The portable version

`QuickMail.exe` on the same releases page is a single-file version that runs from anywhere with no installation — nothing is written to Program Files or the registry, and it never updates itself. The Help menu tells you when a new version is available; updating is a manual download of the new exe, replacing the old one. Your data is shared with an installed copy, so you can move between the two freely.

### Uninstalling

Remove QuickMail from **Settings → Apps** as usual. After the app is removed, QuickMail asks whether to also delete your data — accounts, settings, contacts, rules, templates, saved views, cached mail, and saved passwords. Choose **No** (the default) to keep everything, so reinstalling later picks up exactly where you left off; choose **Yes** to remove it all.

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

### Microsoft 365 / Outlook.com

QuickMail connects to Microsoft mailboxes — work or school **Microsoft 365 / Exchange Online** accounts and personal **Outlook.com / Hotmail / Live.com** accounts — through Microsoft 365 directly (the Microsoft Graph service), so there are no server names or ports to enter.

1. In the Add Account dialog, set **Account type** to **Microsoft 365 / Outlook.com**. The IMAP/SMTP server fields disappear — a Microsoft account needs none.
2. Activate **Sign in with Microsoft**. A Microsoft sign-in window opens inside QuickMail. Sign in and approve the permissions QuickMail requests; the window closes itself and returns you to the dialog.
3. Activate **Add Account**.

Sign in as **the same address you typed** into the account. If the account you sign in with does not match, QuickMail warns you and keeps the address you entered rather than silently switching to a different mailbox — this matters most in organizations where an administrator signs in at the approval screen.

> **Work or school accounts may need your administrator's approval first.** Many organizations require an administrator to approve a new app for the whole organization before anyone can sign in. If your sign-in ends at a **"needs admin approval"** message with no way to continue, QuickMail is working correctly — your organization has not yet approved it. Send your IT administrator to [For Exchange / Microsoft 365 Administrators](#for-exchange--microsoft-365-administrators); once they approve QuickMail, sign-in works normally. Personal Outlook.com accounts are not affected and need no approval.

To bring this account's contacts into your address book, check **Sync contacts from this account** before signing in. See [Syncing Contacts from Your Accounts](#syncing-contacts-from-your-accounts).

To show this account's calendar in the Calendar view, check **Sync calendar from this account**. See the [Calendar](#calendar) section for details.

**Prefer IMAP instead?** You can still connect a Microsoft account over IMAP/SMTP: choose **Standard IMAP/SMTP** as the account type and select **Microsoft OAuth** as the authentication method — the Outlook server settings fill in automatically. The Microsoft 365 / Outlook.com option above is the recommended choice for most people.

### Gmail (Google Account)

Enter your Gmail address in the **Email / Username** field — QuickMail then automatically selects Google authentication for the account, so you do not need to set the authentication type yourself. Activate the **Sign in with Google** button; your browser opens to a Google sign-in page. Complete the sign-in, grant QuickMail permission to read and send mail, then close the browser window. Back in QuickMail, activate **Add Account**. Gmail's server settings fill in automatically.

To bring this account's contacts into your address book, check **Sync contacts from this account** before signing in — for Google this is folded into the same sign-in consent. See [Syncing Contacts from Your Accounts](#syncing-contacts-from-your-accounts).

To show this account's calendar in the Calendar view, check **Sync calendar from this account** — Google's calendar permission is part of the same sign-in. See the [Calendar](#calendar) section for details.

You may see a message that no password was saved for the account. This is expected with Google authentication — Gmail signs in through your Google account rather than a stored password, so there is no password to save. The Google sign-in itself is stored securely in Windows Credential Manager and refreshes automatically; you are not prompted to sign in again unless you revoke access from your Google account settings.

When you sign in, Google shows a warning that QuickMail is an unverified app. This is expected — choose **Advanced** and continue to **Go to QuickMail (unsafe)**. Google's app-verification process can take several weeks and may require an expensive third-party security assessment. If you would rather avoid the warning, generate a Gmail app-specific password from your Google Account security settings and use it with the standard **Password** authentication method instead.

### iCloud

Enter your iCloud address (`@icloud.com`, `@me.com`, or `@mac.com`) in the **Email / Username** field — QuickMail recognises it and fills in Apple's server settings automatically.

**App-specific password required.** Apple does not allow third-party apps to use your Apple ID password directly. Generate an app-specific password at **appleid.apple.com** (Sign-In & Security → App-Specific Passwords) and enter it in the Password field. QuickMail shows a reminder in the password area when it detects an iCloud address.

To bring in your iCloud data, check **Sync contacts from this account** and/or **Sync calendar from this account** — QuickMail uses the same app-specific password for both, so there's nothing else to set up. iCloud **contacts** are read-only; iCloud **calendars** you can also add, edit, and delete single (non-repeating) appointments on. See [Syncing Contacts from Your Accounts](#syncing-contacts-from-your-accounts) and the [Calendar](#calendar) section for details.

### Managing Accounts

Open **Settings → Accounts** to rename, edit, or remove an account. Removing an account does not delete mail from the server. For OAuth accounts (Microsoft or Google), removing the account also clears the stored credential from Windows Credential Manager. Managing an account is also where you turn **Sync contacts from this account** (Microsoft/Google) and **Sync calendar from this account** (Microsoft, Google, or iCloud) on or off after the fact; each change applies immediately.

---

## For Exchange / Microsoft 365 Administrators

This section is for the **IT administrator or tenant owner** of a Microsoft 365 organization whose users want to use QuickMail. **Personal Outlook.com / Hotmail / Live.com users do not need any of this** — it applies only to work or school (Microsoft 365 / Exchange Online) accounts.

**The short version:** QuickMail is a Microsoft-registered desktop application that signs each user in to their own mailbox with their own credentials. Many organizations require an administrator to approve a new application once, for the whole organization, before users can sign in. Until you do, your users will hit a **"needs admin approval"** wall and cannot proceed. Granting approval is a one-time action and takes a couple of minutes.

### What QuickMail is, in Microsoft terms

- QuickMail is a **public client / desktop application** registered in Microsoft Entra ID (Azure AD). Your users authenticate against that single registration; there is nothing to deploy into your tenant.
- It requests **delegated permissions only**. That means QuickMail acts **only as the signed-in user**, entirely within **that user's own mailbox**, and only while they are using the app. It holds **no application permissions** — it cannot run in the background, and it cannot read or send mail for any user who has not personally signed in.
- Sign-in uses the modern authentication flow (OAuth 2.0 / MSAL). Passwords are never seen or stored by QuickMail; tokens live in Windows Credential Manager on the user's own PC and honour your Conditional Access and MFA policies.

### App registration details

| | |
| --- | --- |
| **Application (client) ID** | `bcdc84f1-d37c-4581-b14a-a01f7b3a1312` |
| **Name in Enterprise applications** | QuickMail |
| **Publisher** | Kelly Ford (the QuickMail project) |
| **Supported accounts** | Work/school and personal Microsoft accounts |

### Permissions QuickMail requests

All are **delegated** (act as the signed-in user, in their own mailbox):

| Permission | Why |
| --- | --- |
| `Mail.ReadWrite`, `Mail.Send` | Read, organize, delete, draft, and send the user's mail |
| `Calendars.ReadWrite` | Read and update the user's own calendar (only if they enable calendar sync) |
| `Contacts.Read`, `People.Read` | Read the user's own contacts and frequent correspondents (only if they enable contact sync) |
| `MailboxSettings.ReadWrite` | The user's own mailbox settings, for a planned server-side rules feature |
| `User.Read`, `User.ReadBasic.All` | Resolve the signed-in user and display recipient names |

If users connect over IMAP/SMTP instead of the default Microsoft 365 option, two Exchange Online scopes (`IMAP.AccessAsUser.All`, `SMTP.Send`) are used as well.

### How to approve QuickMail for your organization

Any of these roles can grant approval — **Global Administrator is not required** (QuickMail has no application permissions): **Cloud Application Administrator**, **Application Administrator**, or **AI Administrator**.

**Option A — Entra admin center.** Sign in at [entra.microsoft.com](https://entra.microsoft.com) → **Entra ID → Enterprise applications → QuickMail → Security → Permissions → "Grant admin consent for &lt;your organization&gt;"**, review the list, and approve. (If QuickMail is not yet listed under Enterprise applications, use Option B — the first admin consent creates it.)

**Option B — one-click consent URL.** Sign in as one of the admin roles above and open:

```
https://login.microsoftonline.com/organizations/adminconsent?client_id=bcdc84f1-d37c-4581-b14a-a01f7b3a1312
```

Replace `organizations` with your tenant ID or a verified domain to target a specific tenant. Review the permission list Microsoft shows and approve. This grants consent tenant-wide in one step.

After approval, your users sign in normally with no further prompts.

### If you would rather not grant blanket approval

- **Let users request it.** Enable the **admin consent workflow** (Entra ID → Enterprise applications → Consent and permissions → Admin consent settings). The dead-end prompt becomes a **"Request approval"** flow routed to reviewers you designate, so you approve per request instead of up front.
- **You stay in control.** Because QuickMail uses delegated permissions and standard modern auth, your existing **Conditional Access**, MFA, device-compliance, and app-consent policies all apply. You can restrict or block QuickMail like any other enterprise application at any time.

### Troubleshooting

- **Users see "needs admin approval" with no continue button.** Your tenant requires admin consent and QuickMail has not been approved yet. Follow the steps above. This is expected until approval is granted.
- **A user signed in successfully but delete or move fails with an error.** Their token predates a permission grant. Have them remove and re-add the account (or sign in again) to pick up a fresh token.
- **You approved QuickMail but a newly added feature still prompts.** Admin consent covers the permissions declared at the moment it was granted. Re-grant consent (Option A or B) to pick up any newly requested permission.

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

### Creating Folders

Create a new folder in any of these ways:

- Choose **Folder → New Folder…** from the menu bar.
- Open the Command Palette (**Ctrl+Shift+P**) and choose **New Folder…**. You can assign this command your own keyboard shortcut in [Keyboard Customization](#keyboard-customization).
- Press **Shift+F10** on the folder tree and choose **New Folder** from its context menu.

The new folder is created under the folder currently selected in the folder tree, or under the account root when a header or nothing is selected.

### Moving and Copying Messages

Select one or more messages (or a sender/recipient group, or a conversation) and choose **Move to Folder…** or **Copy to Folder…** from the context menu (Shift+F10) or the command palette. Both open a folder picker showing the same hierarchical tree used in the main folder panel — folders nested under their parent, with account names as headers when more than one account is present. Arrow through the tree and press Enter to complete the move or copy. If you need a destination that does not exist yet, activate **New Folder** (or **Alt+N**) in the picker to create one under the selected folder and move into it without leaving the dialog.

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

QuickMail checks for a newer release in the background each time it starts. The top entry of the **Help** menu always shows the result: **"No updates available — running version X.Y.Z"** when you are current, or **"Update available: vX.Y.Z"** when a newer release exists. If an update is found, a spoken announcement follows a few seconds after launch; the background check itself is silent when you are already up to date.

Installed copies download and install updates automatically, and both that behavior and its notifications are configurable — see [Installing and Updating QuickMail](#installing-and-updating-quickmail) for the full walkthrough.

**The portable exe does not update itself.** If you run the standalone `QuickMail.exe`, the Help menu entry still tells you when a new version exists; activating it opens the releases page, and updating remains a manual download of the new exe.

The **Help** menu also has a **Keyboard Tutorial** entry, a short interactive walkthrough of core navigation (F6 pane cycling, Ctrl+1/2/3, the command palette, and Escape) for anyone new to the app.

### Reporting a Bug

Choose **Report a Bug** from the **Help** menu (also available from the command palette) to open a report window without needing a GitHub account. Fill in a summary and, optionally, what happened, what you expected, and steps to reproduce; a **Preview** box always shows exactly what will be sent. Press **Send** to file it directly, or **Copy report and open GitHub** to submit it under your own GitHub account.

This is one of three ways to report a problem. See **[Reporting Issues](#reporting-issues)** for all three — including email — and guidance on which to choose.

---

## Reading Mail

### Opening a Message

Press Enter on a message in the list to open it in the reading pane (or in a new tab or window, depending on your **Reading Mode** setting in **Settings → General**).

Press **Ctrl+Enter** to open a message in a new tab regardless of the Reading Mode setting.

### Reading Pane

The reading pane renders HTML messages with WebView2. Links open in your default browser. Images from remote sources are blocked by default.

Press **F6** or **Shift+F6** to move between the reading pane and other panes.

### Plain Text View

Toggle a sticky preference to read all messages as plain text instead of HTML. When on, QuickMail renders each message from its original plain-text part (or from text extracted from the HTML if the sender included no plain-text part). This gives a cleaner, low-noise read — useful when you want a simpler layout, or when you want to inspect a suspicious message's raw text.

Toggle plain text view three ways:

| Method | How |
|--------|-----|
| **Command Palette** | Press `Ctrl+Shift+P`, type "Toggle Plain Text View", press Enter |
| **View menu** | Open the **View** menu and activate the **Plain Text View** item (a checkable toggle) |
| **Keyboard shortcut** | Press **Ctrl+Shift+H** |
| **Settings** | Open Settings (Ctrl+,), navigate to the General tab, and check **Read messages as plain text** |

The setting persists across app restarts and applies to the reading pane, message tabs, and standalone message windows. When a message has no plain-text part, QuickMail shows a note before the extracted text: "This message has no plain-text version; showing text extracted from the HTML."

### Message Windows

When Reading Mode is set to **Window**, messages open in a separate window. Each window has a full menu bar (**File, Message, Navigate**), a toolbar, and a command palette. Shortcuts work the same as in the main window:

| Shortcut | Action |
|----------|--------|
| `Ctrl+R` | Reply |
| `Ctrl+Shift+R` | Reply All |
| `Ctrl+F` | Forward |
| `Delete` | Delete |
| `Ctrl+Shift+M` | Move to Archive |
| `Ctrl+Q` | Mark as Read |
| `Ctrl+Shift+G` | Grab Addresses |

Deleting a message from its window closes the window and returns focus to the originating position in the message list.

### Message Properties

Press **Alt+Enter** on any message to open a properties window showing sender, recipients, date, size, and flags.

### Marking as Read

Press `Ctrl+Q` to mark the selected message or messages as read. Messages are also marked read automatically when you open them (configurable in **Settings → General**).

### Deleting Messages

Press **Delete**. Deleted messages go to Trash. Press `Ctrl+Shift+E` to empty the Trash for the selected account.

### Archiving Messages

Press **Ctrl+Shift+M** to archive the selected message — move it to your account's Archive folder instead of deleting it. Use this for mail you want out of your inbox but want to keep. **Delete** is unchanged and still moves messages to Trash.

The command is named **Move to Archive** — it is on the message menu and the message context menu (Shift+F10), and in the command palette. Archiving works from every view. In the **From**, **To**, and **Conversations** groupings, archiving a group moves the whole group at once, the same way Delete does. The **Ctrl+Shift+M** shortcut can be changed in **Settings → Keyboard**.

**Each account archives to its own folder** — there is no single shared Archive folder. QuickMail uses the folder your provider marks as the Archive folder automatically, so most accounts need no setup. To choose a different folder, select a folder in the folder tree, open its context menu (Shift+F10), and choose **Set as Archive Folder**; choose **Use Automatic Archive Folder** to return to the automatic one.

Gmail has no dedicated Archive folder — its archive is **All Mail**. To archive Gmail messages, set **[Gmail]/All Mail** as that account's Archive folder; archiving then removes the message from the inbox, which is exactly what archiving means in Gmail. If an account has no Archive folder and you have not set one, QuickMail tells you rather than doing nothing.

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

The address book lists everyone you have sent mail to or explicitly added, plus — if you turn on contact sync — the contacts stored in your Microsoft and Google accounts. You can search by name or address, edit contact details, and organize contacts into groups. An **Account** column shows where each contact came from: **Local address book** for the ones you added yourself, or the account name for synced ones.

### Syncing Contacts from Your Accounts

QuickMail can fill the address book from the contacts already stored in your Microsoft, Google, and iCloud accounts, so the people your account knows are available for autocomplete when you address a message.

- **Turn it on per account.** When adding a Microsoft, Google, or iCloud account, check **Sync contacts from this account** in the Add Account dialog. For an account you already have, open **Settings → Accounts**, select the account, and check the same box — it takes effect immediately, with no separate Save step. Enabling sync asks your account for read-only access to your contacts (for Google this is part of the normal sign-in; for Microsoft it is granted right after sign-in; **for iCloud it uses the app-specific password you already entered**, so there is no extra prompt).
- **Synced contacts are read-only.** They come from the server into QuickMail only — QuickMail never writes changes back to your account. Synced people appear in the list but cannot be edited or deleted there. Contacts you added yourself stay fully editable, even if someone has the same address. Turning the switch off removes that account's synced contacts from QuickMail.
- **Refreshing.** QuickMail refreshes synced contacts quietly in the background about twice a day. To pull the latest right away, use the **Sync Now** button in the address book, or the **Sync Contacts Now** command in the Command Palette.

Contact sync is one-directional and read-only in this release: no changes are written back. For Microsoft and Google it also pulls the people you've recently emailed; iCloud brings in your saved contacts (its CardDAV service has no separate "recent recipients" list).

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

QuickMail has a full, keyboard-first calendar. You can create and edit your own appointments, keep repeating events, get reminders, respond to meeting invitations, and — if you connect an online account — see and (for some providers) change the events already on your Microsoft, Google, or iCloud calendar. Everything is stored locally so the calendar works offline.

This page is long because the calendar does a lot. If you just want the essentials: press **Ctrl+Shift+C** to open the calendar, use the **Up and Down arrows** to move through your events, and on any appointment press **Tab** once for a details box with everything about it or **Enter** to open the full appointment. Press **N** to create a new one. The rest of this page fills in the detail, including a clear list of [what the calendar does and does not do](#what-the-calendar-does-and-does-not-do).

### Opening the Calendar

There are two ways in:

- Press **Ctrl+Shift+C** from anywhere in the main window.
- Select the **Calendar** node in the folder tree. It sits alongside your mail folders.

Either way, the message list is replaced by your list of events, and a small toolbar appears above it with buttons for the views, date navigation, and creating or editing appointments. Everything on that toolbar has a keyboard shortcut too, listed at the end of this page.

> The calendar needs QuickMail's local cache, so it is **not available when you run QuickMail in online mode** (`--online`). In that mode the calendar announces that it is unavailable.

### Calendars (sources)

Expand the **Calendar** node in the folder tree to choose which events you are looking at:

- **All Calendars** — everything, merged together.
- **Local Calendar** — only the appointments you created in QuickMail, stored on this computer.
- **One entry per account** — the events from each account you turned calendar sync on for (see [Connecting an online calendar](#connecting-an-online-calendar)). Selecting the account shows all of its calendars merged.
- **Each calendar under an account** — if an account has more than one calendar (for example iCloud's Home, Family, and Work, or several Google or Outlook calendars), each appears as its own node beneath the account, so you can look at just that one.

Selecting a source filters the list to it. "All Calendars" is the usual choice for day-to-day use.

### The four views

Your events can be shown four ways. Switch between them with the toolbar buttons or these keys while the event list has focus:

- **Agenda** (press **A**) — a running list of upcoming events, oldest first. This is the default and the simplest to browse. Press **T** to filter it down to just today, and **T** again to show everything.
- **Day** (press **D**) — a single day's events.
- **Week** (press **W**) — one week, starting on whatever your Windows region setting uses as the first day of the week.
- **Month** (press **M**) — a grid of the whole month. Arrow keys move day by day and week by week; press **Enter** on a day to open that day in Day view.

The header above the list always tells you which view and which period you are looking at (for example, "Week of March 9 to March 15").

### Moving around the calendar

In Day, Week, and Month views:

- **Ctrl+Left** and **Ctrl+Right** move to the previous or next day, week, or month (whichever view you are in).
- **T** jumps back to today.
- **Ctrl+G** opens **Go to Date**: type or pick a date, then press **Go**. Day, Week, and Month views keep their view and recenter on the date you chose; from the Agenda list, Go to Date switches to Day view for that date. A **Go To Date…** button on the toolbar does the same thing.

(In Agenda view there is no "previous/next period" — the list is already continuous — so **Ctrl+Left/Right** do nothing there, and **T** toggles the today filter instead of jumping.)

### Reading an appointment

Move through the list with the **Up and Down arrows**. The list has **Subject**, **When**, **Status**, and **Calendar** columns; the Calendar column shows which calendar the appointment belongs to — for example **Apple: Family**, or **Local** for an appointment you created here. Each row is announced as a short summary that includes the calendar — for example, "Team standup, today 10:00 to 10:30, Accepted. Location: Zoom, calendar Apple: Family."

From any event you have two quick ways to see more:

- Press **Tab** once to move into a **details box** below the list that shows the full appointment (title, when, whether it repeats, location, and for meeting invitations the organizer and your response status), read from the top so a screen reader can review it line by line.
- Press **Enter** to act on the appointment. On an invitation you **haven't answered yet**, Enter opens a short menu — **Accept**, **Tentative**, **Decline**, or **Open full appointment** — so you can respond right from the calendar (see below). On your own appointment, Enter opens the editor; on an invitation you've already answered, it opens the source email.

If you prefer each row spoken with field labels ("Subject …, when …, location …") instead of the concise form, turn on **Show field labels in the calendar event list** in **Settings → General**. It takes effect immediately.

### Responding to a meeting invitation

Invitations you receive by email show in the calendar with a **Pending** status until you answer them. You can respond without opening the email:

1. Arrow to the pending invitation and press **Enter**. A menu opens with focus on the first choice.
2. Choose **Accept**, **Tentative**, or **Decline** — or **Open full appointment** to read the original email instead. Press **Escape** to close the menu without responding.

QuickMail sends your reply to the organizer from the account that received the invitation and updates the appointment's status, so it no longer shows as pending. Accept, Tentative, and Decline are also in the Command Palette (**Ctrl+Shift+P**) when a pending invitation is selected.

### Creating an appointment

Press **N** (or the **New** toolbar button) to open the appointment editor. It is a normal window you can tab through:

- **Title** — required.
- **All day** — check this for an all-day event; the time fields then switch off.
- **Starts / Ends** — a date and a time for each. You can type the time naturally: "9", "9:00", "9:00 AM", or "14:30" all work. If you leave the end time blank, QuickMail uses 30 minutes after the start.
- **Location** — optional.
- **Repeat** — leave as "Does not repeat" for a one-off, or set up a repeating appointment (see below).
- **Notes** — free text.
- **Calendar** — when you have a Microsoft, Google, or iCloud account connected, a picker lets you choose where the new appointment is saved: your **Local Calendar** or a connected account. For iCloud the picker lists each of your Apple calendars (Home, Family, …) so you can choose which one. With no connected calendar this picker does not appear and everything is saved locally.

Press **Enter** (or the **Save** button) to save, or **Escape** to cancel.

### Repeating appointments

In the editor's **Repeat** field choose Daily, Weekly, Monthly, or Yearly. You can then set:

- **Every N** — for example "every 2 weeks."
- **Until** — an optional date the repetition stops on.
- **On days** (Weekly only) — check the days of the week it should fall on. Leave them all unchecked to repeat on the same weekday as the start date.

When you later edit or delete one occurrence of a repeating appointment, QuickMail asks whether you mean **this event only** or **all events in the series**:

- **This event only** — changes (or removes) just that one date and leaves the rest of the series alone.
- **All events in the series** — changes (or removes) the whole repeating appointment.

### Editing and deleting

- Press **E** (or the **Edit** button) to edit the selected appointment.
- Press **Delete** (or the **Delete** button) to delete it. QuickMail always confirms before deleting.

What you can change depends on where the appointment lives:

- **Your own (Local Calendar) appointments** — fully editable and deletable.
- **Single events from a connected Microsoft, Google, or iCloud calendar** — editable and deletable; your change is sent back to that account.
- **Repeating events from a connected online calendar** — read-only for now; QuickMail tells you so rather than changing them.
- **Meeting invitations** — read-only. QuickMail explains why instead of failing silently.

If saving a change to an online account ever fails, QuickMail saves your appointment to the Local Calendar instead so your work is never lost, and tells you it did.

### Reminders

Reminders are **off by default**. To turn them on, open **Settings → General**, check **Remind me before appointments**, and set the **Minutes before** value (10 by default). When a reminder is due, QuickMail shows a Windows notification and announces it, telling you how long until the appointment and where it is. Each reminder fires once per session.

There is a single reminder lead time for all appointments; per-appointment reminder times and snoozing are not offered.

### Exporting an appointment

To share an appointment as a standard calendar file, select it and choose **Export Appointment as .ics** from the command palette (**Ctrl+Shift+P**) or the toolbar/menu. QuickMail writes a `.ics` file you can send to someone or import elsewhere. (Exporting one occurrence of a repeating appointment exports the whole series.) This action has no default keyboard shortcut, but you can assign one in **Settings → Keyboard**.

### Searching your appointments

Press **Ctrl+Shift+S** while the calendar is open to search. Type to filter the list by title, location, or notes; the count of matches is announced as you type. Press **Escape** to clear the search and return to the full list.

### Responding to meeting invitations

When you open an email that contains a meeting invitation, QuickMail adds an event card to the top of the message with three buttons: **Accept**, **Tentative**, and **Decline**. Choosing one sends your reply to the organizer and updates your calendar right away — no restart or refresh needed. If the invitation has been cancelled by the organizer, the card says so instead of offering buttons, and the matching calendar entry is removed. From the calendar list, pressing **Enter** on an invitation-based event opens the original email it came from.

### Connecting an online calendar

Calendars connect **per account**, the same way contact sync does. When you add an email account — or later, in **Manage Accounts** — check **Sync calendar from this account** and QuickMail shows that account's calendar in the Calendar view. Nothing is synced until you check the box, and unchecking it removes that account's events again. The checkbox is offered for the providers QuickMail can read:

| Provider | See your events | Create / edit / delete from QuickMail | Setup |
|----------|-----------------|----------------------------------------|-------|
| **Local Calendar** | — (they live here) | Full | Nothing to set up; it is always there. |
| **Microsoft** (Outlook.com, Microsoft 365) | Yes | Yes, for single (non-repeating) events | Check **Sync calendar from this account**. Microsoft asks once for calendar permission. |
| **Google** | Yes | Yes, for single (non-repeating) events | Check **Sync calendar from this account**. Permission was granted when you signed in for mail. |
| **iCloud** | Yes | Yes, for single (non-repeating) events | Check **Sync calendar from this account**. QuickMail uses the app-specific password you already entered for the account — no separate setup. When you create an appointment you can choose which iCloud calendar (Home, Family, …) it goes on. |

**Turning it on for an account you already have:** open **Manage Accounts**, select the account, and check **Sync calendar from this account**. It applies immediately — a **Calendar → [account]** node appears in the folder tree and the events sync in the background. Unchecking it removes them.

Connected calendars refresh automatically in the background (roughly every 15 minutes, and once shortly after startup). Press **F5** to refresh on demand, or use **Sync Calendars Now** from the command palette. Syncing is a one-directional download plus, for Microsoft and Google, the single-event write-back described above; it never opens a sign-in prompt on its own — if an account needs you to sign in again for calendar access, QuickMail tells you.

### What the calendar does and does not do

To set expectations clearly:

**It does:**

- Let you create, edit, and delete your own appointments, including all-day and repeating ones.
- Show four views (Agenda, Day, Week, Month) and jump to any date.
- Remind you before appointments (when you turn reminders on).
- Respond to meeting invitations and keep your reply in sync.
- Download events from Microsoft, Google, and iCloud calendars, and create, edit, and delete single (non-repeating) events on any of them.
- Export any appointment as a `.ics` file.

**It does not (yet):**

- Work in online mode — it needs the local cache.
- Send **repeating** appointments to an online account (repeating appointments are saved to the Local Calendar).
- Edit or delete **repeating** events that came from an online calendar (repeating server events are read-only).
- Connect calendars from generic CalDAV servers other than iCloud (Fastmail, Nextcloud, and the like).
- Subscribe to public `.ics` calendar feeds (holidays, sports schedules, and the like).
- Offer multiple named calendars, calendar colors, per-appointment reminder times, or reminder snoozing.

By default, invitations you have **declined** are hidden. Showing them is an advanced option set in the configuration file (`ShowDeclinedEvents`) and takes effect after a restart.

Repeating appointments are expanded for browsing from about a week ago through the next twelve months; events far outside that window may not appear in the continuous Agenda list until you navigate to their date.

### Calendar keyboard shortcuts

These work while the calendar list (or, where noted, the Month grid) has focus:

| Shortcut | Action |
|----------|--------|
| `Ctrl+Shift+C` | Open the Calendar |
| `A` / `D` / `W` / `M` | Agenda / Day / Week / Month view |
| `T` | Today (filter to today in Agenda; jump to today in Day/Week/Month) |
| `Ctrl+Left` / `Ctrl+Right` | Previous / next day, week, or month |
| `Ctrl+G` | Go to Date |
| `Enter` | Respond to a pending invitation (Accept / Tentative / Decline menu); or edit your own appointment; or open an answered invitation's source email; or (in Month view) open the selected day |
| `N` | New appointment |
| `E` | Edit appointment |
| `Delete` | Delete appointment |
| `Ctrl+Shift+S` | Search appointments |
| `Escape` | Clear search / leave the calendar |
| `F5` | Refresh |
| `F6` | Move to the next pane |

Export as `.ics`, and the invitation responses (Accept, Tentative, Decline), are available from the command palette (**Ctrl+Shift+P**) and have no default key until you assign one.

---

## Notifications

New-mail notifications and the option to keep QuickMail running in the notification area when you close the window are both **off by default**. To turn them on, go to **Settings → General → Notifications**.

### Show a Notification When New Mail Arrives

When this setting is on, QuickMail shows a Windows notification as new mail arrives in any inbox. The notification is announced by screen readers and appears in the Windows notification center, which you open with **Win+N** on Windows 11 (or **Win+A** on Windows 10). Pressing **Enter** on a notification brings QuickMail to the foreground and opens that message.

If multiple messages arrive together, the notification shows a count ("5 new messages") and brings QuickMail to the foreground when activated. Single-message notifications show the sender's name and subject and open that message.

Notifications require **Windows 10 1809 or later**.

### Keep Running in the Notification Area When You Close the Window

When this setting is on, closing the main window hides QuickMail to the notification area (system tray) instead of exiting, so it keeps running and new-mail notifications continue to arrive. To restore the window:

- **From a notification:** press **Enter** on any new-mail notification.
- **From the tray icon:** move focus to the notification area with **Win+B**, arrow to the QuickMail icon, and press **Enter** (or the **Menu** key, then arrow to **Open QuickMail** and press **Enter**).
- **Double-activate the tray icon:** move focus to the notification area (**Win+B**) and double-activate QuickMail.

To quit when this setting is on:

- Use **File → Exit** from the app menu bar, or
- From the tray icon, press the **Menu** key and arrow to **Exit QuickMail**.

The first time you hide the window to the tray, a notification explains that QuickMail is still running — this message appears once only.

---

## Tools Menu

The **Tools** menu is always available from the main window menu bar and groups together the commands used less often than day-to-day mail actions:

- **Manage Themes…** — opens the [Theme Manager](#themes).
- **Next Theme** / **Previous Theme** — cycle through your available themes.
- **Address Book…** (`Ctrl+Shift+B`)
- **Rules…** (`Ctrl+Shift+L`) — opens the [Rules Manager](#mail-rules).
- **Command Palette…** (`Ctrl+Shift+P`)

---

## Reporting Issues

QuickMail improves because people report problems and suggest changes. There are **three** ways to do it. They mostly differ in one thing: **whether you can be contacted for follow-up**. Pick whichever fits.

| Way | Where | Follow-up? | Best when |
|-----|-------|-----------|-----------|
| **1. Report a Bug → Send** | Help menu, in QuickMail | No | You want to report a problem but don't want any follow-up. |
| **2. Report a Bug → Copy report and open GitHub** | Help menu, in QuickMail | Yes (via GitHub) | You have a GitHub account and want automatic filing plus direct contact. |
| **3. Email** | `quickmailissues@theideaplace.net` | Yes (by email) | You'd rather use email and want a personal reply. |

### 1. Report a Bug — Send it directly (no account needed, anonymous)

Choose **Report a Bug** from the **Help** menu (it's also in the command palette). A report window opens with a **Summary** and — all optional — **What happened**, **What you expected**, and **Steps to reproduce**. A **Preview** area always shows exactly what will be sent, built fresh from those fields as you type.

Press **Send**. QuickMail files the report for you and shows a link to the issue it created.

Because a Send report includes **no email address or other identifying information**, there is no way for anyone to follow up with you about it. Choose this option when you want to report something but **don't want any direct follow-up**.

### 2. Report a Bug — Copy report and open GitHub (filed under your account)

In the same **Report a Bug** window, choose **Copy report and open GitHub** instead of Send. QuickMail copies the report to your clipboard and opens a pre-filled new-issue page on GitHub. You submit it there under **your own GitHub account**, so your GitHub contact information is attached and you'll be notified as the issue is discussed.

Choose this option when you **have a GitHub account** and want automatic bug reporting **plus** the ability to be contacted and to follow along.

### 3. Email us (personal follow-up)

If you'd rather not use the in-app tool at all, email **[quickmailissues@theideaplace.net](mailto:quickmailissues@theideaplace.net)**. Describe the problem in your own words — the more detail (what you did, what happened, what you expected), the better.

Choose this option when you **don't mind sending an email** and want a **personal follow-up**.

### What's included in a report — and what's never included

Alongside what you type, an in-app report (options 1 and 2) adds a short **Environment** section so a problem can be reproduced in the right context: the QuickMail version, your Windows version, the .NET runtime version, the active color theme, the current view, and the current sort order.

**No message content, email addresses, account settings, passwords, or log file content is ever collected or sent.** The Preview shows the full report verbatim, so you always see exactly what is included before it leaves your computer.

---

## Settings

Press **Ctrl+,** to open Settings.

### General

- **Reading Mode** — Reading Pane, Tab, or Window
- **Mark messages read** — automatically on open, or manually only
- **Default compose mode** — Plain Text, Markdown, or HTML
- **Auto-save drafts** — on/off and interval
- **Read messages as plain text** — when on, display all messages as plain text instead of HTML
- **Notifications** — two checkboxes:
  - **Show a notification when new mail arrives** — enable Windows notifications for new mail in inboxes (requires Windows 10 1809 or later)
  - **Keep running in the notification area when I close the window** — closing the main window hides QuickMail to the tray instead of exiting

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
| Announce delete and archive actions | Delete and archive outcomes ("1 message archived"). Turn off to stop these from interrupting the screen reader as it reads the next message; failures are still announced |
| Announce formatting while navigating | Block type announced when caret enters a new paragraph type in HTML compose |
| Announce flag status | Flag name prepended to message row when navigating the list |
| Announce spelling suggestions | Suggestions included when a misspelling is announced |
| Spelling Suggestions Verbosity | Numbers with suggestions (default) or just suggestions |
| Show field labels in the contact list | When on, address-book rows speak field names ("Name … email … account …"); when off, they speak concise field data only |

All settings default to on except **Announce flag status**, **Show field labels in the contact list**, and **Announce spelling while typing** (off by default). **Spelling Suggestions Verbosity** defaults to **Numbers with suggestions**. Turn off **Custom Announcements** to silence everything at once; turn it back on to restore your individual preferences.

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

In every theme, the selected item in a list or tree is a solid band of the theme's accent color with white text, so the current message is unmistakable; supporting text (previews, timestamps, unread counts) is a step lighter than body text but kept clearly readable; and a thin divider separates message rows.

**Parchment** (light, default) — an off-white background (Snow) with very dark cool-gray text and a dark muted-blue accent (Dark Slate Blue) used for buttons, the unread marker, and the selected item. Panels and toolbars use warm off-white tones (White Smoke, Linen); links are medium blue. This is QuickMail's standard light look.

**Parchment Dark** — the dark counterpart to Parchment: a very dark gray background with light gray text and a light muted-blue accent. The selected item is a medium-blue band with white text. Panels and toolbars use slightly lighter dark-gray tones for depth; links are light blue. Status colors (error, warning, success, information) are lightened versions of Parchment's, chosen for contrast against the dark background.

**Ember** — a warm light theme: a warm off-white background (Floral White) with very dark cool-gray text and a dark red accent (Sienna) in place of Parchment's blue. The selected item is a terracotta band with white text. Links remain medium blue for consistency across themes.

**Fjord** — a cool light theme: an off-white background with a faint cool cast (Ghost White) and a dark muted-cyan accent (Dark Slate Gray) in place of Parchment's blue. The selected item is a dark teal band with white text.

**Heather** — a muted light theme: an off-white background (Ghost White) with a cool gray accent (Dim Gray) instead of a saturated color. The selected item is a plum-gray band with white text. This is the most subdued of the built-in themes.

The four light themes are close cousins. Ember, Fjord, and Heather each change only four colors from Parchment: the main window background tint, the accent color, the soft accent-fill color, and the selection color (which matches the accent, so selection is where each theme's personality shows most). Everything else — panels and toolbars, borders, body and secondary text, the medium-blue hyperlink color, the focus outline, and the four status colors (error, warning, success, information) — is inherited unchanged from Parchment. Parchment Dark is the only theme with a fully dark palette.

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
| `Ctrl+Shift+M` | Move to Archive (the account's Archive folder) |
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
| `Ctrl+Shift+H` | Toggle Plain Text View |
| `Ctrl+Shift+G` | Grab Addresses from Message |
| `Ctrl+Shift+B` | Address Book |
| `Ctrl+Shift+L` | Rules Manager |
| `Ctrl+,` | Settings |
| `F1` | User Guide |
| `Shift+,` | First message in group |
| `Shift+.` | Last message in group |

**Move to Folder…** and **Copy to Folder…** are available from the context menu (Shift+F10) or the command palette; they have no default keyboard shortcut. **Manage Themes**, **Next Theme**, **Previous Theme**, and **Report a Bug** are likewise command-palette-only unless you assign a shortcut yourself in Settings → Keyboard.

**Calendar** (`Ctrl+Shift+C` to open): `A`/`D`/`W`/`M` switch views, `T` goes to today, `Ctrl+Left`/`Ctrl+Right` move between periods, `Ctrl+G` goes to a date, `N`/`E`/`Delete` create/edit/delete appointments, and `Ctrl+Shift+S` searches. See the [Calendar](#calendar) section for the full list.

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
