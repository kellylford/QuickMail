# QuickMail User Guide

QuickMail is a keyboard and screen reader friendly email program for Windows. Gmail, iCloud, Outlook.com, Microsoft 365, and IMAP/SMTP providers in general are all supported.

---

## Contents

- [System Requirements](#system-requirements)
- [Installing and Updating QuickMail](#installing-and-updating-quickmail)
- [Accounts](#accounts)
- [For Microsoft 365 Administrators and Tenant Owners](#for-microsoft-365-administrators-and-tenant-owners)
- [Main Window](#main-window)
- [Reading Mail](#reading-mail)
- [Composing Mail](#composing-mail)
- [Address Book](#address-book)
- [Grab Addresses from a Message](#grab-addresses-from-a-message)
- [Flags](#flags)
- [Mail Rules](#mail-rules)
- [Saved Views](#saved-views)
- [Calendar](#calendar)
- [What Syncs and What Doesn't](#what-syncs-and-what-doesnt)
- [Notifications](#notifications)
- [Tools Menu](#tools-menu)
- [Connection Diagnostics](#connection-diagnostics)
- [Reporting Issues](#reporting-issues)
- [Settings](#settings)
- [Themes](#themes)
- [Message List Fields](#message-list-fields)
- [Screen Reader Announcements](#screen-reader-announcements)
- [Keyboard Shortcuts](#keyboard-shortcuts)

---

## System Requirements

- Windows 10 (1703 or later) or Windows 11
- Microsoft Edge WebView2 Runtime (the installer adds this automatically when missing; included with Windows 11 and current Windows 10)
- An email account. QuickMail supports Microsoft 365 / Exchange Online and Outlook.com (through Microsoft 365 directly), Gmail, iCloud, and any standard IMAP/SMTP provider. Work or school Microsoft 365 accounts may first need approval from an organization administrator — see [For Microsoft 365 Administrators and Tenant Owners](#for-microsoft-365-administrators-and-tenant-owners).

---

## Installing and Updating QuickMail

QuickMail installs with a standard setup wizard and then keeps itself up to date. The guiding principle is that **you are in control**: the defaults are designed to keep you current with no effort, you are told when a new version has been installed, and every part of the automatic behavior can be turned off — QuickMail never stops you from updating manually instead.

### Which download do you want?

Most people want **QuickMail-win.msi**. Choose it unless you know your PC has an ARM processor.

If your PC has a Snapdragon processor — the Surface Laptop and Surface Pro models sold with Snapdragon X chips, and similar machines from other manufacturers — choose **QuickMail-win-arm64.msi** instead. It is built specifically for those processors and runs noticeably faster on them.

To check which you have: open **Settings → System → About** and read **System type**. "ARM-based processor" means the ARM64 download; anything else means the regular one.

If you are unsure, take the regular **QuickMail-win.msi**. It works on every supported PC, including ARM ones — just not as quickly on those. The ARM64 download will not start at all on a non-ARM PC, so that is the guess worth avoiding.

The two versions are otherwise identical: same features, same settings, same data.

### Installing for the first time

1. Download **QuickMail-win.msi** — or **QuickMail-win-arm64.msi** for a Snapdragon PC, as described above — from the [releases page](https://github.com/kellylford/QuickMail/releases) and run it.
2. The setup wizard walks through a welcome page, the license agreement, and installation. QuickMail installs for the current user only — no administrator permission is needed. If the WebView2 component QuickMail uses to display mail is missing from your PC, setup adds it automatically.
3. A Start Menu entry is created. The first time QuickMail starts, it asks whether to also add a desktop shortcut — either answer is remembered, and you can change your mind anytime in **Settings → General** under **Desktop Shortcut**.

### If you already have QuickMail installed

Versions before 0.8.0 used a different installer, so moving onto the self-updating track takes one manual step:

1. Uninstall your current QuickMail from **Settings → Apps**. When the uninstaller offers to delete your data, choose **No**.
2. Download and run **QuickMail-win.msi** as described above.
3. Start QuickMail. All of your accounts, settings, contacts, rules, templates, saved views, and cached mail are exactly as you left them — your data lives in a separate location the installer never touches, and passwords stay safely in Windows Credential Manager.

### Moving to the ARM version on a Snapdragon PC

If you have been running the regular QuickMail on a Snapdragon PC, you can switch to the ARM version to get better speed and battery life. QuickMail will not make this switch for you — automatic updates stay on the version you installed — so it is a one-time manual change.

QuickMail mentions this once when it notices it is running on an ARM PC, and the **Help** menu then keeps a **Get the ARM Version** entry that brings you back to this section, where step 2 links to the download. That entry appears only on ARM PCs running the regular build; you can also reach it from the command palette. To switch:

1. Uninstall QuickMail from **Settings → Apps**. When the uninstaller offers to delete your data, choose **No**.
2. Download and run **QuickMail-win-arm64.msi** from the [releases page](https://github.com/kellylford/QuickMail/releases).
3. Start QuickMail. Everything is as you left it, for the same reason as above — your accounts, settings, and mail are stored separately from the program itself.

**Do not skip step 1.** Running the ARM installer on top of a regular QuickMail of the same version does not replace it — Windows treats the two as separate programs, leaves the regular one in place, and reports success. QuickMail keeps starting exactly as before, so nothing tells you the switch did not happen. Uninstalling first is what makes it work.

To confirm the switch worked, open the **Help** menu after restarting. **Get the ARM Version** is gone once you are on the ARM version, so if it is still listed, you are still running the regular build.

If it is still listed, the ARM installer did not replace anything. Uninstall QuickMail from **Settings → Apps** — choosing **No** again when asked about your data — and then start over from step 1. Do not try to judge this from **Settings → Apps** itself: both builds report the same name and the same version number there, and only one QuickMail entry is ever listed, so it looks identical whichever one you have. The **Help** menu is the reliable check.

From then on, automatic updates keep you on the ARM version. This is a one-time step — there is nothing further to do, and no need to repeat it for future releases.

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

`QuickMail.exe` on the same releases page — or `QuickMail-arm64.exe` for a Snapdragon PC — is a single-file version that runs from anywhere with no installation — nothing is written to Program Files or the registry, and it never updates itself. The Help menu tells you when a new version is available; updating is a manual download of the new exe, replacing the old one. Your data is shared with an installed copy, so you can move between the two freely.

### Uninstalling

Remove QuickMail from **Settings → Apps** as usual. After the app is removed, QuickMail asks whether to also delete your data — accounts, settings, contacts, rules, templates, saved views, cached mail, and saved passwords. Choose **No** (the default) to keep everything, so reinstalling later picks up exactly where you left off; choose **Yes** to remove it all.

---

## Accounts

Adding an account, changing one, testing it, and removing it all happen in **Manage Accounts**. Open it from **File → Manage Accounts…**, from the **Accounts** button on the toolbar, or from the Command Palette (**Ctrl+Shift+P**) by choosing **Manage Accounts**. It has no keyboard shortcut of its own to begin with; you can give it one under **File → Settings → Keyboard Shortcuts**.

Manage Accounts opens with focus on the first entry in the **Accounts** list, so you can arrow through your accounts straight away. **New**, **Delete**, and **Set Default** sit below the list; the settings for whichever account is selected fill the rest of the window; **Save**, **Test Connection**, and **Close** are at the bottom. When you have no accounts yet there is nothing to land on, so focus goes to **New** instead.

For most accounts you need three things: your provider, your email address, and your password. QuickMail knows the server settings for the providers it lists, and looks them up for the ones it does not.

### Choosing a provider

**Provider** is the first field in the Add Account dialog, because what you choose there fills in everything below it. The list holds:

- **Other (enter settings manually)** — where the list starts, and what you use for any provider QuickMail has no entry for
- **Gmail**
- **Gmail (sign in with Google)** — only when you have turned Google sign-in on; see [Gmail (Google Account)](#gmail-google-account)
- **Outlook.com / Microsoft 365**
- **Yahoo Mail**
- **iCloud Mail**

**Other** comes first rather than last so that Down arrow reaches the rest from where the dialog puts you. Choosing a provider announces what it did — "Gmail settings applied", or the app-password requirement where the provider has one. Choosing **Other** tells you to enter your server settings under Advanced settings, and opens that section for you.

Often you need not touch the list at all: typing an address at one of these providers selects it for you. Correcting the address afterwards drops the provider again, so an address you have retyped is never saved against the previous provider's servers.

A provider is fixed when the account is created. Manage Accounts shows it as text at the top of the account's settings rather than as a list you can change — to move an account to a different provider, remove it and add it again.

### Adding an account

Press **New** in Manage Accounts. The Add Account dialog opens with focus on the **Provider** list.

1. Choose your **Provider** — or skip to step 2 and let your address choose it.
2. Tab to **Email address** and type it. Leaving this field is also what starts the settings lookup for an address QuickMail does not already know; see [Automatic settings lookup](#automatic-settings-lookup).
3. Tab to **Password** and enter it. Gmail, Yahoo Mail, and iCloud Mail all need an **app password** rather than your ordinary account password; QuickMail says so above the box and links to the page where you create one. Microsoft accounts have no password box at all — a **Sign in with Microsoft** button stands in its place.
4. **Account name** and **Sender display name** are both optional. Leave the account name blank and the account is labelled with your email address, which is what most people want; give it a name when you have two accounts at the same provider. The sender display name is the name recipients see on messages you send.
5. **Sync contacts from this account** and **Sync calendar from this account** appear for iCloud accounts and for accounts that sign in — Microsoft, and Gmail when you choose Google sign-in. Check them *before* signing in, so the permission can be part of the same sign-in. (A Gmail account using an app password has no contact or calendar sync, so the boxes do not appear.)
6. Press **Add Account**.

Whether that permission really is part of the same sign-in depends on the account. Google sign-in folds it in, and so does a personal Outlook.com account set to connect over **Microsoft 365 (Graph)** — one screen lists mail, contacts, and calendar together. A work or school Microsoft account asks separately once the account is added, on purpose: organizations that restrict what their users may approve would turn one over-asking screen into a failed sign-in, and no account at all.

Server names, ports, and SSL settings are behind **Advanced settings**, an expander that stays closed unless you need it. Tab reaches its header before its contents, so one keystroke moves past the whole thing. **Sign in with Microsoft** and **Sign in with Google** come last of all, after Advanced settings and everything inside it — signing in is the last thing you do, so it is the last thing you tab to. The same is true in Manage Accounts.

If something required is missing, **Add Account** does not close the dialog. It says what is missing and moves focus to the field that needs attention — the email address, the password, or, when there are no server settings at all, the **IMAP host** with Advanced settings opened for you.

### Automatic settings lookup

If you type an address QuickMail does not recognize — a work or university address, for example — it looks the settings up when you leave the Email field. You hear "Looking up settings for *yourdomain*" while it runs, then one sentence when it finishes: the servers QuickMail settled on and where they came from, or that nothing was found.

QuickMail tries, in order:

1. Its built-in list of providers. Instant, offline, and nothing leaves your computer.
2. Mozilla's public autoconfig database — the one Thunderbird uses. Only the **domain** is sent, never your address.
3. Your domain's own **Autodiscover** service, the same one Outlook uses. Your address is sent to your own mail provider's server.
4. Your domain's public DNS records — its MX record and its autodiscover entry — which say where its mail is actually delivered. Only the **domain** is sent.

Step 4 matters for business mail. Most Microsoft 365 and Google Workspace customers use their own domain — `you@yourcompany.com` — and publish nothing that steps 2 and 3 can read. Without it, a perfectly ordinary work account ends at "enter your IMAP host", for an account that has no IMAP host to enter: the way in is a sign-in. When your domain's mail is delivered to Microsoft, QuickMail selects **Outlook.com / Microsoft 365** and offers **Sign in with Microsoft**; when it is delivered to Google, it selects **Gmail**.

This step reads where your mail *goes*, rather than who your organisation is with. That distinction matters: a domain keeps its Microsoft account long after its mail has moved elsewhere, and plenty of companies run their own mail while using Microsoft 365 for everything else. Asking the first question would put Microsoft's servers on an account that is not on Microsoft's mail, and your password would be rejected. If QuickMail still gets it wrong, change the **Provider**, or open **Advanced settings** and enter your own servers.

Settings that arrive over the network are never accepted unless they are encrypted, so a lookup can never quietly hand your password to a server that would take it in the clear. Servers you type yourself are your own business and are left alone.

When nothing is found, **Advanced settings** opens automatically and focus moves to the **IMAP host** field, so you can type the settings yourself. The message also names the way out for a work or school Microsoft 365 account, which has no IMAP host to type: choose **Outlook.com / Microsoft 365** as the provider and sign in.

To turn off steps 2, 3, and 4, set `AutoDiscoverOnline = off` in `config.ini`. The built-in provider list keeps working either way.

### Entering settings yourself

Choose **Other**, or open **Advanced settings** for any provider, to enter:

- **Login username** — leave this blank unless your mail server logs in under a different name than your email address. See [When your login is not your email address](#when-your-login-is-not-your-email-address).
- **Connection method** — Standard IMAP/SMTP, Microsoft 365 (Graph) for Microsoft accounts, and POP3/SMTP when you have turned POP3 on. The incoming fields below follow whichever you choose; see [POP3 accounts](#pop3-accounts).
- **IMAP host**, port, **Use SSL** (port 993), and whether to accept invalid certificates — or **POP3 host**, port and **Use SSL** (port 995) for a POP3 account
- **SMTP host**, port, implicit SSL on connect (port 465 — leave it unchecked for STARTTLS on port 587), and whether to accept invalid certificates
- **Authentication** — Password or Microsoft, plus Google when you have turned Google sign-in on (or when the account already uses it)
- Your **Signature**, which is added to the end of new messages, replies, and forwards

Advanced settings opens by itself in the two cases where you have no choice but to use it: when you choose **Other**, and when a settings lookup finds nothing.

Anything you enter here is yours to keep. Once you have changed a server field — a host, a port, or an SSL setting — no provider match and no later lookup overwrites it.

There is one exception, and it applies only to accounts you have never saved in Manage Accounts. At startup, if such an account points at a server QuickMail ships settings for, on the exact port QuickMail publishes for that server, and the encryption setting disagrees with the published one, the encryption setting is corrected. That pairing cannot work — implicit SSL against a STARTTLS port fails every send about a second after you press the button — so leaving it alone would only preserve a broken account. One of those servers on any other port is left exactly as you set it, and once you have saved an account yourself nothing touches it again.

### When your login is not your email address

For almost every account these are the same thing, and the **Login username** box under **Advanced settings** stays empty.

They come apart when your mail provider keeps a separate account name. An iCloud mailbox that receives mail at your own domain still signs in under the Apple ID; some hosted servers want a bare user name rather than a full address. In that case:

- **Email address** is your real address — `you@yourdomain.com`. This is what recipients see in the From line of everything you send, so it has to be an address that works.
- **Login username** is what the server wants at sign-in — the Apple ID, the bare name, whatever your provider told you.

Putting the login name in the **Email address** box instead sends mail with a From line that is not an address, and servers reject it. QuickMail will not save an account whose email address is missing its domain; the message points you at the **Login username** box.

If you already had an account set up that way, QuickMail copies the login name into **Login username** for you the first time it starts after the update — so when you correct the **Email address**, the login that was working goes on working. You only need to enter the address.

### Testing a connection

**Test Connection** is on both the Add Account dialog and Manage Accounts, so it is as useful on an account that has stopped working as on one you are still setting up. In Manage Accounts it appears as soon as an account is selected.

QuickMail checks **incoming and outgoing mail separately** and reports both — "IMAP: OK. SMTP: OK." A failure names its own side and gives the reason, so a working inbox with a misconfigured send server is caught here rather than the first time you try to send. Neither check waits longer than 30 seconds; one that runs out of time reports that it timed out rather than leaving you waiting.

Microsoft 365 accounts are tested too, by asking Microsoft for the signed-in mailbox: "Microsoft 365 connection successful." Sign in first, so there is an account to test.

The result is announced and stays on screen as status text near the buttons. In the Add Account dialog the button disables itself while the test runs, and focus returns to it when the result arrives.

### Microsoft 365 / Outlook.com

QuickMail signs in to Microsoft mailboxes rather than asking for a password — work or school **Microsoft 365 / Exchange Online** accounts and personal **Outlook.com / Hotmail / Live.com** accounts alike.

1. In the Add Account dialog, set **Provider** to **Outlook.com / Microsoft 365** — or just type your address, which selects it for you. The password box disappears; Microsoft accounts sign in instead.
2. Activate **Sign in with Microsoft** — the last tab stop in the dialog, after Advanced settings. A Microsoft sign-in window opens inside QuickMail. Sign in and approve the permissions QuickMail requests; the window closes itself and returns you to the dialog.
3. Activate **Add Account**.

Sign in as **the same address you typed** into the account. If the account you sign in with does not match, QuickMail warns you and keeps the address you entered rather than silently switching to a different mailbox — this matters most in organizations where an administrator signs in at the approval screen.

**Work or school accounts connect through Microsoft 365 directly** (the Microsoft Graph service), so there are no server names or ports to enter. QuickMail arranges that for you: an address on your organization's own domain — anything other than outlook.com, hotmail.com, live.com, msn.com, or passport.com — moves onto that connection method as you type it. The reason is that most organizations have never approved the separate IMAP and SMTP permissions, and many switch IMAP off altogether, so a work account left on IMAP ends its sign-in at "your administrator needs to make a change" for a mailbox that signs in perfectly well the other way.

**Personal Outlook.com, Hotmail, and Live.com accounts** connect over IMAP with the same Microsoft sign-in. There is no organization involved and nothing to approve, so this route needs no change.

You can also choose for yourself. Open **Advanced settings** and use the **Connection method** list to pick **Standard IMAP/SMTP** or **Microsoft 365 (Graph)**; choosing Graph announces that IMAP and SMTP settings are not required, and choosing IMAP fills the Outlook server settings back in. This choice is fixed when the account is created — to change it later, remove the account and add it again.

> **Work or school accounts may need your administrator's approval first.** Many organizations require an administrator to approve a new app for the whole organization before anyone can sign in. If your sign-in ends at a **"needs admin approval"** message with no way to continue, QuickMail is working correctly — your organization has not yet approved it. Send your IT administrator to [For Microsoft 365 Administrators and Tenant Owners](#for-microsoft-365-administrators-and-tenant-owners); once they approve QuickMail, sign-in works normally. Personal Outlook.com accounts are not affected and need no approval.

To bring this account's contacts into your address book, check **Sync contacts from this account** before signing in. See [Syncing Contacts from Your Accounts](#syncing-contacts-from-your-accounts).

To show this account's calendar in the Calendar view, check **Sync calendar from this account**. See the [Calendar](#calendar) section for details.

### Gmail (Google Account)

**Use a Gmail app password.** Google sign-in is currently blocked for new QuickMail accounts, so an app password is what works today, and it is what QuickMail selects when you choose Gmail or type a Gmail address.

1. Turn on 2-Step Verification for your Google account at **myaccount.google.com/security** — app passwords are not offered until you do.
2. Create an app password at **myaccount.google.com/apppasswords**. Google gives you a 16-character password. The Add Account dialog links straight to this page.
3. In QuickMail, choose **Gmail** (or type your Gmail address), enter the 16-character app password, and activate **Add Account**.

Enter the app password, not your regular Google password. Gmail's server settings fill in automatically.

App passwords are not offered if your account is enrolled in Google's Advanced Protection Program. This route gives you full mail — send, receive, folders, search — but not Google calendar or contact sync, which need the Google sign-in described below and available only to accounts authorized before Google closed it.

#### Google sign-in, for accounts authorized before it closed

Google stopped granting QuickMail new authorizations, so for most people the sign-in route can only end in a refusal — which is why QuickMail no longer offers it by default. If your Google account was authorized before that happened, it still works, and you can turn the option back on:

1. Open **File → Settings**, go to the **Advanced** tab, and check **Sign in with Google for Gmail accounts**. Select **Save**.
2. Restart QuickMail. The setting is read at startup.

You now have a **Gmail (sign in with Google)** entry in the **Provider** list, sitting directly below plain **Gmail**, and a **Google OAuth (Gmail)** choice under **Advanced settings** → **Authentication**.

Choose **Gmail (sign in with Google)** and there is no password box at all — Gmail's servers fill in as usual and a **Sign in with Google** button takes the password's place. Activate it; your browser opens to a Google sign-in page. Complete the sign-in, grant QuickMail permission to read and send mail, then close the browser window and activate **Add Account**.

If you would rather not use the Settings dialog, the same switch is `GoogleAuth = true` under `[features]` in `config.ini`, or `--feature GoogleAuth` on the command line.

**Accounts you already have are not affected by this setting.** A Gmail account that already signs in with Google keeps working whether the setting is on or off, keeps syncing mail, contacts, and calendar, and still shows **Google OAuth (Gmail)** as its authentication in Manage Accounts. The setting governs only whether the option is *offered* when you set an account up.

To bring this account's contacts into your address book, check **Sync contacts from this account** before signing in — for Google this is folded into the same sign-in consent. See [Syncing Contacts from Your Accounts](#syncing-contacts-from-your-accounts).

To show this account's calendar in the Calendar view, check **Sync calendar from this account** — Google's calendar permission is part of the same sign-in. See the [Calendar](#calendar) section for details.

With Google sign-in you may see a message that no password was saved for the account. This is expected — Gmail signs in through your Google account rather than a stored password, so there is no password to save. The sign-in itself is stored securely in Windows Credential Manager and refreshes automatically.

Google also shows a warning that QuickMail is an unverified app, and may end the sign-in with **"This app has been blocked."** That message means your account is not one of the ones authorized earlier, and no setting in QuickMail can change it — Google's app-verification process can take several weeks and may require an expensive third-party security assessment. The app password route above is the reliable path.

### Yahoo Mail

Choose **Yahoo Mail** or type your Yahoo address (`@yahoo.com`, `@ymail.com`, `@rocketmail.com`, and the regional variants).

**App password required.** Yahoo does not accept your regular account password from third-party mail apps. Generate one at **login.yahoo.com/account/security** under App passwords, and enter it in the Password field. QuickMail links to that page when Yahoo is selected.

### iCloud

Choose **iCloud Mail** or type your iCloud address (`@icloud.com`, `@me.com`, or `@mac.com`) — QuickMail fills in Apple's server settings automatically.

**App-specific password required.** Apple does not allow third-party apps to use your Apple ID password directly. Generate an app-specific password at **appleid.apple.com** (Sign-In & Security → App-Specific Passwords) and enter it in the Password field. QuickMail shows a reminder above the password box, with a link to that page.

To bring in your iCloud data, check **Sync contacts from this account** and/or **Sync calendar from this account** — QuickMail uses the same app-specific password for both, so there's nothing else to set up. iCloud **contacts** are read-only; iCloud **calendars** you can also add, edit, and delete single (non-repeating) appointments on. See [Syncing Contacts from Your Accounts](#syncing-contacts-from-your-accounts) and the [Calendar](#calendar) section for details.

### POP3 accounts

Some mail services offer POP3 and nothing else. QuickMail can collect mail over POP3 and send over
SMTP as usual — but POP3 works differently enough from IMAP that it is worth reading this section
before you choose it.

**Turning it on.** POP3 is off until you ask for it. In **File → Settings** (Ctrl+comma), open the
**Advanced** tab, and under **Account Types** check **Offer POP3 when adding an account**. It takes
effect the next time QuickMail starts. (Equivalently, `Pop3Backend = true` under `[features]` in
`config.ini`, or `--feature Pop3Backend` at launch — they are the same switch, so there is no need to
set more than one.)

**Adding the account.** Add it as you would any other: **New** in Manage Accounts, then open
**Advanced settings** and choose **POP3/SMTP** under **Connection method**. The **Incoming Mail
(POP3)** fields replace the IMAP ones. Gmail, Outlook.com and Yahoo Mail fill in their POP3 server
for you; for anything else, type the server, port and security yourself. iCloud is not offered a POP3
server because Apple does not run one — iCloud is IMAP only.

Two things most services require before POP3 will answer at all: POP3 switched on in the service's
own web settings, and — for Gmail and Yahoo — an app password rather than your account password.

**What is different from IMAP:**

- **Messages are downloaded whole and kept on this computer.** Reading a message, opening its
  attachments, and searching all work with no network.
- **Four folders — Inbox, Sent, Drafts and Trash — and they belong to QuickMail.** A POP3 server has
  no folders to show, so these are QuickMail's own. Creating, renaming, moving and deleting folders
  are not offered for a POP3 account; QuickMail says so rather than failing at the server.
- **Read and unread, flags, and moves between those folders stay on this computer.** POP3 has nowhere
  on the server to record them, so another mail program reading the same mailbox will not see them.
- **New mail arrives on the sync interval**, not the moment it lands. POP3 has no equivalent of the
  live connection QuickMail holds open for IMAP, so there is no instant notification.
- **Sending is unchanged.** A POP3 account sends over SMTP exactly like an IMAP one, and a copy of
  each sent message is filed in QuickMail's own Sent folder.

**Keep mail on the server after downloading.** This is checked by default, and while it is checked
QuickMail never deletes anything from your mail service: collect mail here and it is still there for
your phone, webmail, or anything else you use.

Clear it and QuickMail removes each message from the server once it is safely stored here — the
classic POP3 behaviour, and the reason POP3 has the reputation it has. **This computer then holds the
only copy**, so think about backups before choosing it. You can change the setting later in Manage
Accounts. Clearing it also applies to mail collected earlier, as POP3 programs have traditionally
worked: on the next collection QuickMail removes the server copies of messages it already downloaded
— every one of them is already stored here, so nothing is lost, but if your phone or another program
has not collected that mailbox yet, leave the setting checked until it has.

Either way, deleting a message in QuickMail is two steps, as it is everywhere else: to Trash, then
permanently. Only the permanent delete can reach the server, and only for an account set to remove
collected mail. QuickMail confirms it is deleting the right message first — a message some other
program has already collected is left alone rather than deleted by position.

With **keep mail on the server** checked, a permanent delete (including emptying the Trash) is local
and final: the message is gone from QuickMail for good, the server copy stays where it is for
whatever else reads that mailbox, and QuickMail remembers the deletion so the message is never
downloaded again.

**Where your POP3 mail lives.** In QuickMail's data folder for that profile (`%APPDATA%\QuickMail` by
default), in `mail.db`. For an IMAP account that file is a cache and can be deleted safely; for a
POP3 account with "keep mail on the server" cleared, it is your mail. Back it up accordingly, and see
[Moving to a new computer](#moving-to-a-new-computer).

### Editing an account

Select an account in the **Accounts** list and its settings appear beside it. You can change:

- **Account name** and **Sender display name**
- **Email address**, and for password accounts the **Password**. The box is filled from Windows Credential Manager, so you only need to touch it when the password itself has changed.
- **Sync contacts from this account** and **Sync calendar from this account**, on iCloud accounts and on accounts that sign in (Microsoft, or Gmail with Google sign-in)
- Under **Advanced settings**: **Login username**, **Authentication**, the IMAP and SMTP servers, and your **Signature**

Press **Save** — the default button, so Enter is enough — to keep the changes. The two sync checkboxes are the exception: they apply the moment you change them, with no Save step. Switching one on asks for the permission it needs and pulls the first batch immediately; switching it off removes what had been synced. iCloud prompts for nothing, because it uses the app-specific password you already entered.

**Sign in with Microsoft** and **Sign in with Google** are here too, for when a sign-in needs renewing.

**Set Default** marks the selected account as your default — the account new messages are sent from unless you choose another, and the one new mail rules are created for. The default account is announced as "default" alongside its name in the list.

Server settings sit behind the same **Advanced settings** expander as in Add Account, so an account that is working needs no scrolling past hosts and ports to reach the settings people actually change. The **Provider** at the top is fixed when the account is created; to change it, remove the account and add it again.

### Removing an account

Select the account and press **Delete**. There is no confirmation step, and the removal happens at once: QuickMail forgets the account's password from Windows Credential Manager, deletes the mail it had cached, removes any contacts synced from it, and for Microsoft and Google accounts signs out as well. You hear "Account deleted. Cleaning up…", then "Account deleted." when the tidying has finished.

**Nothing is deleted from the mail server.** Removing an account removes it from QuickMail only — add it again and your mail is still there.

### Shared mailboxes

A shared mailbox is a mailbox several people read and send from — a `support@`, `info@`, or `sales@` address that belongs to a team rather than a person. If your organization has given you access to one, you can add it to QuickMail and it appears as its own mailbox in the folder tree, alongside your own accounts.

Shared mailboxes are a **Microsoft 365 work or school** feature. You add one through an account you already have: QuickMail reads and sends the shared mailbox using your existing sign-in, so there is no separate password and no separate sign-in for it. Personal Outlook.com accounts and non-Microsoft (IMAP) accounts do not have shared mailboxes, so the option is offered only when you have a work or school Microsoft 365 account to read it through.

**To add a shared mailbox:** open **Account Manager** (Tools menu), activate **Add shared mailbox**, choose the work or school account that has access to it if you are asked, and type the shared mailbox's email address. Press **Add**. The mailbox connects using that account and its folders load. You do not need access to a password — access comes from your own account's permission to the mailbox, which your organization grants.

**Reading and sending.** A shared mailbox reads like any other: select it in the folder tree and open its folders. To send from it, choose the shared address in the **From** list when composing — the message goes out as the shared mailbox, not as you.

**Freshness.** A shared mailbox updates on a timer rather than the instant new mail arrives: **shared mailboxes update every few minutes, not instantly.** Your own mailboxes still update live; only the shared one waits for the next check.

**New-mail notifications are off for a shared mailbox by default.** A shared mailbox is often a busy team address, so QuickMail does not pop a notification for every message that lands in it. If you do want notifications for a particular shared mailbox, select it in Account Manager and turn on **Notify me of new mail in this shared mailbox**. The main **Show a Notification When New Mail Arrives** setting still has to be on for any notification to appear.

**Removing.** Removing the account that a shared mailbox reads through also removes the shared mailbox — you are told which shared mailboxes will go when you delete their parent account. Removing a shared mailbox on its own leaves the account it read through untouched.

---

## For Microsoft 365 Administrators and Tenant Owners

This section is for whoever **owns or administers** a Microsoft 365 organization whose users want to use QuickMail — a tenant owner can make these changes; a dedicated IT administrator is not required. **Personal Outlook.com / Hotmail / Live.com users do not need any of this** — it applies only to work or school (Microsoft 365 / Exchange Online) accounts.

**The short version:** QuickMail is a Microsoft-registered desktop application that signs each user in to their own mailbox with their own credentials. Many organizations require an administrator to approve a new application once, for the whole organization, before users can sign in. Until you do, your users will hit a **"needs admin approval"** wall and cannot proceed. Granting approval is a one-time action and takes a couple of minutes.

### What QuickMail is, in Microsoft terms

- QuickMail is a **public client / desktop application** registered in Microsoft Entra ID (Azure AD). Your users authenticate against that single registration; there is nothing to deploy into your tenant.
- It requests **delegated permissions only**. That means QuickMail acts **only as the signed-in user**, entirely within **that user's own mailbox**, and only while they are using the app. It holds **no application permissions** — it cannot run in the background, and it cannot read or send mail for any user who has not personally signed in.
- Sign-in uses the modern authentication flow (OAuth 2.0 / MSAL). Passwords are never seen or stored by QuickMail; access tokens are held in an encrypted cache on the user's own PC, protected by Windows DPAPI and readable only by that user's Windows account. (Where a user connects over IMAP/SMTP with a password instead, that password is stored in Windows Credential Manager.) Sign-in honours your Conditional Access and MFA policies.

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

Any of these roles can grant approval — **Global Administrator is not required**: **Cloud Application Administrator** or **Application Administrator**.

**Option A — Entra admin center.** Sign in at [entra.microsoft.com](https://entra.microsoft.com) → **Entra ID → Enterprise applications → QuickMail → Security → Permissions → "Grant admin consent for &lt;your organization&gt;"**, review the list, and approve. (If QuickMail is not yet listed under Enterprise applications, use Option B — the first admin consent creates it.)

**Option B — single-step consent URL.** Sign in as one of the admin roles above and open:

```
https://login.microsoftonline.com/organizations/adminconsent?client_id=bcdc84f1-d37c-4581-b14a-a01f7b3a1312
```

**Open this in a private/InPrivate browser window.** A cached Microsoft sign-in session will skip the consent screen and send you straight to an error page (see Troubleshooting).

Replace `organizations` with your tenant ID or a verified domain to target a specific tenant. Review the permission list Microsoft shows and approve. This grants consent tenant-wide in one step.

After approval, your users sign in normally with no further prompts.

### If you would rather not grant blanket approval

- **Let users request it.** Enable the **admin consent workflow** (Entra ID → Enterprise applications → Consent and permissions → Admin consent settings). The dead-end prompt becomes a **"Request approval"** flow routed to reviewers you designate, so you approve per request instead of up front.
- **You stay in control.** Because QuickMail uses delegated permissions and standard modern auth, your existing **Conditional Access**, MFA, device-compliance, and app-consent policies all apply. You can restrict or block QuickMail like any other enterprise application at any time.

### Troubleshooting

- **Users see "needs admin approval" with no continue button.** Your tenant requires admin consent and QuickMail has not been approved yet. Follow the steps above. This is expected until approval is granted.
- **The consent link opens, asks you to sign in, then goes straight to an error page with no permissions to approve.** Your browser is reusing a cached Microsoft sign-in session, which skips the consent screen. **Open the link in a private/InPrivate browser window** and sign in fresh — the permission list will appear. This also applies to the "Grant admin consent" button in the Entra portal.
- **After approving, the browser shows "can't reach this page" at `http://localhost`.** That is expected and means it worked. QuickMail is a desktop app, so Microsoft redirects to a local address with nothing listening. Your approval is recorded *before* that redirect. Confirm it under **Enterprise applications → QuickMail → Security → Permissions**, which will now list the granted permissions.
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

Select **All Inboxes** at the top of the folder tree to see messages from all accounts merged into one list, sorted by date. The same **All Mail** group also holds **All Drafts**, **All Sent**, and **All Trash**, each merging that kind of folder across every account; **All Archive**, which follows each account's own archive setting; **All Flagged**, which collects flagged messages from every folder rather than from one kind of folder; and **Watched Conversations**, described below.

In any of these merged views, each row also says which folder the message came from — qualified with the account name when the view spans more than one account, so "Work, Archive" and "Home, Archive" are told apart. In an ordinary single-folder view the folder is already known, so nothing extra is said. You can move where this falls in the row, or turn it off, in [Message List Fields](#message-list-fields).

### Watched Conversations

When a conversation matters — a release announcement, a bug thread, a trip itinerary — you can watch it, and every message in it collects in one place, including the replies that have not arrived yet.

Press `Ctrl+Shift+W` on any message in the conversation, or choose **Message → Watch Conversation**. You will hear "Watching conversation" and the subject. From then on, **Watched Conversations** — the last item in the **All Mail** group of the folder tree — lists every message in that conversation, from every folder and every account, newest first. New replies join on their own as they arrive; you do not have to mark them.

This is what makes watching different from flagging. A flag marks a message you already have, so you have to notice each new one first. A watch is a standing subscription to the conversation, so the ones you have not seen yet are already collected for you.

To stop watching, press `Ctrl+Shift+W` again on any message in the conversation — the same key does both. If you are in the Watched Conversations folder at the time, that conversation's messages leave the list straight away and the selection moves to the next message. Watching changes nothing on the mail server and nothing in other mail programs; it is remembered on this computer only.

Everything else in the message list works here as usual: view modes, filters, sorting, and the message list fields. **Conversations** view mode is a natural fit — each watched conversation becomes a single group you can expand. You can also save Watched Conversations as one of your own views, the same way you would save any other folder.

Two things worth knowing. QuickMail groups a conversation by its subject, ignoring any `Re:` and `Fwd:` prefixes, so a reply is recognised automatically — but two unrelated messages that happen to share a subject count as one conversation. And a message with no subject at all cannot be watched; if you try, you will hear "Cannot watch a conversation with no subject" and nothing changes.

If you would like each row to say whether its conversation is watched, turn on the **Watched** field in [Message List Fields](#message-list-fields). It is off to begin with.

**To see what you are watching**, choose **Tools → Watched Conversations…**. The window lists every watch with how many cached messages it has collected and when you started it. Press **Enter** on one to jump to that conversation, **Delete** to stop watching it, or **Rename** to give it a clearer label. Renaming changes the label only — which messages the watch collects is decided by the subject and does not change. Type a letter to jump down the list. The window stays open while you work, so you can leave it up and keep going; **Escape** closes it.

**To be told when a reply arrives**, turn on **Settings → Notifications → Show a notification when a watched conversation gets a reply**. Unlike the ordinary new-mail notification, this one applies to every folder rather than just the inbox, because a watched thread's next message can land anywhere. The two settings are independent; if a message is both new inbox mail and part of a watched conversation you get one notification, the watched one.

**To narrow any folder to watched mail**, choose **View → Filter → Watched**. That works in any folder, and can be saved as part of one of your own views.

### Jumping to an Item by Typing

In the folder tree, the message list, and the Conversations, From, and To trees, type the first letter of what you want to reach and the selection jumps to the next item starting with that letter. Keep typing to narrow it — letters typed within about a second of each other build up a longer prefix, so "arc" reaches Archive rather than stopping at Address book. Pause, and the next letter starts a fresh prefix. Pressing the same letter repeatedly steps through every item beginning with it, and the search wraps around to the top when it runs off the end.

What each list matches is the text you would expect to hear first: the folder name in the folder tree (only folders currently expanded into view), the sender in the message list — or the subject when a message has no sender — the conversation subject in Conversations, and the person's name in the From and To trees.

The same typing works in the **Go to Folder** picker, in the address book's contact, group, and member lists, and in the [Message List Fields](#message-list-fields) window.

### Expanding and Collapsing Folders

Right and Left arrow still open and close one folder at a time, and that has not changed. What is new is four actions for doing it in bulk — useful when an account has folders nested several levels deep, or when several accounts have filled the tree.

- **Expand Folder** opens the selected folder and everything inside it, all the way down, rather than one level.
- **Collapse Folder** closes it and everything inside it, so the whole branch folds back to a single line.
- **Expand All Folders** opens every folder in the tree.
- **Collapse All Folders** closes everything, account headers included, leaving the tree as a short list of accounts.

Each is reachable three ways:

- Choose **Folder → Expand Folder**, **Collapse Folder**, **Expand All Folders**, or **Collapse All Folders** from the menu bar.
- Press **Shift+F10** on the folder tree and choose it from the context menu. On a calendar the first two read **Expand Calendar** and **Collapse Calendar**; they do the same thing.
- Open the Command Palette (**Ctrl+Shift+P**) and choose it there. None of the four has a shortcut key to begin with, so if you use one often, assign it your own in [Keyboard Customization](#keyboard-customization).

The two "all folders" actions say what they did — "All folders collapsed" — because you can start them from the menu bar with focus anywhere. The two single-folder actions say nothing extra when you use the context menu: the folder keeps focus and your screen reader reports its own expanded or collapsed state. Started from the Folder menu or the Command Palette with focus outside the tree, where nothing is there to report it, they say what they did instead.

If collapsing hides the folder you were on, the selection moves up to the nearest folder still showing rather than disappearing into a closed branch. Choosing Expand or Collapse Folder on a folder with nothing inside it says so instead of doing nothing.

How you leave the tree is how you find it: a folder you collapse stays collapsed while QuickMail refreshes its folder list, and coming back to the tree with **F6** or **Ctrl+2** leaves it collapsed rather than re-opening the branch holding the folder you are reading. Go to a different folder and the tree opens up to show it again, as it always has. Expansion is not remembered between runs — QuickMail starts each account open, and opens whatever it needs to reach the folder it lands in.

### Creating Folders

Create a new folder in any of these ways:

- Choose **Folder → New Folder…** from the menu bar.
- Open the Command Palette (**Ctrl+Shift+P**) and choose **New Folder…**. You can assign this command your own keyboard shortcut in [Keyboard Customization](#keyboard-customization).
- Press **Shift+F10** on the folder tree and choose **New Folder** from its context menu.

The new folder is created under the folder currently selected in the folder tree, or under the account root when a header or nothing is selected.

### Moving and Copying Folders

Select a folder in the folder tree, press **Shift+F10**, and choose **Move Folder…** or **Copy Folder…**. Both open a picker showing the same hierarchical tree used in the folder panel — folders nested under their parent, everything already expanded. Arrow to the folder you want the selected folder to live under and press Enter. Copying brings the folder's messages and any subfolders with it.

Typing jumps to a folder here as it does in the folder tree: type the beginning of a folder name and the selection moves to it. Because a bare letter belongs to that typing, the picker's buttons take **Alt+O** for Open and **Alt+C** for Cancel. Enter and Escape work as usual. There is no **New Folder** button in this picker — create the folder first, then move into it.

Two things the picker leaves out on purpose:

- **Folders belonging to your other accounts.** A folder can only move or copy within the account it already lives in, so only that account's folders are offered.
- **The folder you are moving, and everything inside it.** A folder cannot be moved or copied into itself or into one of its own subfolders.

The picker opens on the folder you came from. Since the folder you are moving is not one of the destinations, it opens on the folder that one currently sits under, so you start where you were rather than at the top of the list. For a top-level folder there is no such parent, and it opens on the first folder instead.

Because it opens there, pressing Enter straight away would put the folder back where it started. QuickMail says so rather than doing it — as it does for messages already in the folder you picked.

If those two exclusions leave nowhere to go — an account whose only folder is the one you are moving — QuickMail says so instead of opening an empty picker. The same happens if you choose Move or Copy Folder on a view rather than a folder: All Inboxes, All Mail, and the per-account All Mail entries look like folders in the tree but are collections of messages from several folders, so there is nothing to move.

### Moving and Copying Messages

Select one or more messages (or a sender/recipient group, or a conversation) and choose **Move to Folder…** or **Copy to Folder…** from the context menu (Shift+F10) or the command palette. Both open a folder picker showing the same hierarchical tree used in the main folder panel — folders nested under their parent, with account names as headers when more than one account is present. Arrow through the tree and press Enter to complete the move or copy. If you need a destination that does not exist yet, activate **New Folder** (or **Alt+N**) in the picker to create one under the selected folder and move into it without leaving the dialog.

**The picker opens on the folder you last filed to**, so filing several messages to the same place takes one keystroke after the first: open the picker, press Enter. Move and copy are remembered separately — copying to a reference folder does not change where Move to Folder opens — and each account keeps its own, since a folder on one account is not a destination for another.

Before you have moved anything on that account, and whenever the remembered folder is no longer there — you have deleted or renamed it — the picker opens on the folder the messages are in instead, so you still start somewhere useful. The same applies when the messages you selected come from more than one account: no single remembered folder is right for all of them.

Only the folder you choose in the picker is remembered. Archiving and deleting file messages away too, but they do not change where the picker opens. In a combined view such as All Inboxes that is the message's own folder, not the view. If neither is available the picker opens on the first folder; it never opens with nothing selected.

Typing jumps to a folder here too, exactly as it does in the main folder tree: type the beginning of a folder name and the selection moves to it. Because a bare letter belongs to that typing, the picker's buttons take **Alt+O** for Open, **Alt+C** for Cancel, and **Alt+N** for New Folder rather than plain letter shortcuts. Enter and Escape still work as usual.

### Message List Views

Press `Ctrl+Shift+V` (or use the **View** menu) to switch how messages are grouped:

- **Messages** — flat list, newest first
- **Conversations** — messages grouped by thread
- **From** — messages grouped by sender
- **To** — messages grouped by recipient

### Each Folder Remembers How You Left It

Folders do not all want the same treatment. An inbox reads well grouped into **Conversations**; a
folder full of receipts that all share one subject reads terribly that way — it collapses into a
single conversation of a hundred messages.

So QuickMail remembers. Change the view mode, filter, or sort in a folder and that folder opens
that way from then on, including after a restart. Set your inbox to Conversations and your receipts
folder to Messages once, and each stays put.

A folder you have never changed follows the **Display mode** in **Settings → General**, which also
tracks the last choice you made anywhere. So the first time you open a new folder it looks like the
last folder you set up — change it once and it is that folder's own setting from then on.

To hand a folder back to the default, use **Reset Folder View** from the **View → Views** menu or
the command palette. QuickMail confirms with "Folder view reset."

To turn the whole behaviour off, clear **Remember view settings for each folder** in
**Settings → General**. The view mode and sort then apply to every folder at once, as they did
before. Your per-folder settings are kept, so switching it back on restores them.

### Message List Density

**View → Density** sets how much space each message row takes: **Comfortable** (the default) leaves room around each row, **Compact** tightens them so more messages fit on screen. The change applies at once and is remembered. The same choice is in **Settings → Appearance** under **Message List Density**, and **Density: Comfortable** and **Density: Compact** are in the command palette.

Density changes spacing only. What a row says, and what a screen reader reports about it, is identical either way — that is set in [Message List Fields](#message-list-fields).

### Searching

Press `Ctrl+Shift+S` to open the search box. Type your query and press Enter. Results appear in the message list. Press Escape to clear the search and return to the full folder.

### Searching Folders

Press `Ctrl+Shift+F` to search folder names. Type to filter the tree, press Enter to navigate to the matching folder.

### Refreshing

Press **F5** to manually refresh the current folder.

### Command Palette

Press **Ctrl+Shift+P** to open the command palette. Type any part of a command name to find it. Press Enter to run it. This is the fastest way to discover and run any action in the app.

### Keyboard Customization

Open **File → Settings → Keyboard Shortcuts** to reassign any shortcut to a different key. Select a command's field and press the key combination you want — QuickMail captures it, shows you what it heard, and asks you to confirm with **OK**, pick another with **Change**, or **Cancel**. If the combination is already assigned to something else, a warning names the existing command so you can decide whether to reassign it. Changes take effect immediately and survive restarts.

**Bare keys as shortcuts.** You can assign a shortcut with no modifier key using **Delete, Backspace, Insert, or any function key (F1–F24)** — for example, bare **Delete** to delete a message, or **F5** to sync. (Bare **Delete** ships as a default binding for exactly this reason.) Plain letters, digits, and the Spacebar cannot be bound on their own — they would swallow ordinary typing — so pair them with **Ctrl**, **Alt**, or **Shift**. **Enter**, **Escape**, and **Tab** are reserved for navigating dialogs and are never captured as shortcuts.

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

The reading pane renders HTML messages with WebView2. Links open in your default browser.

Images from remote sources are not loaded — fetching them tells the sender your address is live, which is what a tracking pixel in a newsletter is for. Where the sender wrote a description for a picture, QuickMail shows that description in its place, so a picture that is also a link reads by what it is ("Facebook link") rather than by its web address. A picture the sender marked as decorative contributes nothing, which is what marking it that way asks for.

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
| `Ctrl+Shift+W` | Watch or unwatch this message's conversation |
| `Alt+A` | Focus the attachment list |

Deleting a message from its window closes the window and returns focus to the originating position in the message list.

### Attachments on a Received Message

When a message has attachments, they appear in an **attachments list** just below the header fields (Subject, From, To, Date). This works the same way in all three reading modes — reading pane, tab, and window.

Reach the attachment list in either of two ways:

- Press **Alt+A** to jump straight to it from anywhere in the open message. If the message has no attachments, QuickMail announces "No attachments."
- Press **Shift+Tab** from the message body to step back into the header area; when the message has attachments, focus lands on the attachment list.

Once focus is on the list, arrow between attachments and use:

| Shortcut | Action |
|----------|--------|
| `Enter` | Open the selected attachment |
| `Alt+Enter` | Show the attachment's properties |
| `Shift+F10` (or the Menu key) | Open the context menu: **Save…**, **Save All…**, **Open** |

### Message Properties

Press **Alt+Enter** on any message to open a properties window showing sender, recipients, date, size, and flags.

### Marking as Read

Press `Ctrl+Q` to mark the selected message or messages as read. Messages are also marked read automatically when you open them (configurable in **Settings → General**).

### Deleting Messages

Press **Delete**. Deleted messages go to Trash. Press `Ctrl+Shift+E` to empty the Trash for the selected account.

### Archiving Messages

Press **Ctrl+Shift+M** to archive the selected message — move it to your account's Archive folder instead of deleting it. Use this for mail you want out of your inbox but want to keep. **Delete** is unchanged and still moves messages to Trash.

The command is named **Move to Archive** — it is on the message menu and the message context menu (Shift+F10), and in the command palette. Archiving works from every view. In the **From**, **To**, and **Conversations** groupings, archiving a group moves the whole group at once, the same way Delete does. The **Ctrl+Shift+M** shortcut can be changed in **File → Settings → Keyboard Shortcuts**.

**Each account archives to its own folder** — there is no single shared Archive folder. QuickMail uses the folder your provider marks as the Archive folder automatically, so most accounts need no setup. To choose a different folder, select a folder in the folder tree, open its context menu (Shift+F10), and choose **Set as Archive Folder**; choose **Use Automatic Archive Folder** to return to the automatic one.

Gmail has no dedicated Archive folder, so pick one for the account. The recommended choice is a label of your own: create a Gmail label named **Archive**, then select it in the folder tree and choose **Set as Archive Folder**. Archiving a message moves it out of the inbox and onto that label — which is what archiving means in Gmail — and it gives you a folder that holds only what you archived.

Setting **[Gmail]/All Mail** as the Archive folder also works and archives correctly, but that folder holds your entire mailbox, not just archived mail. Prefer a label unless you specifically want All Mail.

If an account has no Archive folder and you have not set one, QuickMail tells you rather than doing nothing.

**All Archive** in the folder tree gathers every account's archived mail into one list, alongside All Inboxes, All Drafts, All Sent, and All Trash. It follows each account's own archive setting — including a folder you chose with **Set as Archive Folder** — so it shows exactly the mail **Move to Archive** put there. Accounts with no Archive folder simply contribute nothing. This is the other reason to prefer a Gmail label over **[Gmail]/All Mail**: an account pointed at All Mail contributes its entire mailbox to All Archive, because for that account All Mail *is* the archive.

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

### Choosing Which Account Sends

Press **Alt+M** to reach the **From** list, then arrow to the account you want. Press **Enter** and QuickMail confirms your choice — "IdeaPlace used as From address". You can also expand the list with **Alt+Down Arrow**, arrow to an account, and press Enter; the same confirmation follows.

Arrowing past accounts does not announce anything extra, since your screen reader is already reading each account name as you go. The confirmation comes when the choice settles — on Enter, when the expanded list closes on a different account, or when you leave the From field having changed it. It uses the **Announce action results** setting, so turning that off turns this off with it.

Enter in the From list never sends the message. Sending is **Alt+S** or **Ctrl+Enter**.

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

Control announcement behavior in **File → Settings → General → Screen Reader Announcements**:

- **Announce spelling suggestions** — turn off to hear only the misspelled word without suggestions.
- **Spelling Suggestions Verbosity** — choose **Numbers with suggestions** (default) to hear "1: the, 2: then" so `Alt+1/2/3` maps directly to what is spoken, or **Just suggestions** to hear "the, then, them" without numbers.

### Attachments

Attach files with `Ctrl+Shift+A` in the compose window, by pasting files from the clipboard (`Ctrl+V`), or by dragging and dropping them onto the window. A screen reader announces how many files were attached.

Press **Alt+A** to move focus to the list of files already attached — the same key that reaches the attachment list of a message you are reading. Arrow through the list and press **Delete** to remove the selected file. If the message has nothing attached yet, QuickMail announces "No attachments."

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

### Filtering by Account

When several accounts sync contacts into QuickMail, the list can get long. The **Filter** button, just to the right of the search box, narrows the list to one account at a time.

1. Press **Tab** once from the search box, or press **Alt+F** from anywhere on the Contacts tab, to reach the button. Its label reports what is showing now — for example, "Filter: All accounts."
2. Press **Enter** or **Alt+F** to drop the menu. It opens on the filter currently in effect, which is marked as checked.
3. Press **Up Arrow** and **Down Arrow** to move through the choices: **All accounts**, **Local address book**, then one entry per account.
4. Press **Enter** to apply the choice. The list narrows and the result is announced — for example, "Work, 214 contacts." Press **Escape** instead to close the menu and leave the filter alone.

The filter and the search box work together: with a filter applied, searching looks only inside that account. The filter stays in effect while the address book is open, including across a contact sync, and resets to **All accounts** the next time you open the window.

**Filter Addresses by Account** is also in the address book's Command Palette (**Ctrl+Shift+P**).

### Jumping to a Contact by Typing

With focus on the contact list, type the first letter of a contact's name to jump straight to it. Type more letters quickly to match a longer beginning ("br" goes to Brenda rather than Bob), or press the same letter again to move to the next contact starting with that letter. Contacts stored without a name are matched on their address instead. The same typing shortcut works in the **Groups** list and the **Group members** list.

### Finding Mail From or To a Contact

From the contact list, you can pull up everything a person sent you, or everything you addressed to them.

1. Select the contact and press **Shift+F10** (or the Applications key) to open the context menu.
2. Choose **Find mail from this contact** or **Find mail to this contact**.
3. The address book closes and the message list fills with the matches, newest first. Focus moves to the message list and the count is announced — for example, "12 messages from Bob Baker." The window title shows **Mail from Bob Baker** so you can tell the results apart from a folder.

Both actions are also in the address book's Command Palette (**Ctrl+Shift+P**) as **Find Mail From Contact** and **Find Mail To Contact**. Commands that live inside the address book are not listed in File → Settings → Keyboard Shortcuts, which covers the main window's commands; reach them from the palette.

Press **Escape** with focus in the message list to close the results and go back to the folder you started from; the folder name and its message count are announced. A **Close** button at the top of the results does the same, and **Close Contact Mail Results** is in the Command Palette. Selecting any folder in the folder tree also leaves the results.

The search covers every account and folder QuickMail has cached — not just the folder you were in. Mail older than your sync range is not stored locally, so it is not included. **Find mail to this contact** matches the To line, so a message where the person was only in Cc does not appear.

### Syncing Contacts from Your Accounts

QuickMail can fill the address book from the contacts already stored in your Microsoft, Google, and iCloud accounts, so the people your account knows are available for autocomplete when you address a message.

- **Turn it on per account.** When adding a Microsoft, Google, or iCloud account, check **Sync contacts from this account** in the Add Account dialog. For an account you already have, open **File → Manage Accounts…**, select the account, and check the same box — it takes effect immediately, with no separate Save step. Enabling sync asks your account for read-only access to your contacts (for Google this is part of the normal sign-in; for Microsoft it is granted right after sign-in; **for iCloud it uses the app-specific password you already entered**, so there is no extra prompt).
- **Synced contacts are read-only.** They come from the server into QuickMail only — QuickMail never writes changes back to your account. Synced people appear in the list but cannot be edited or deleted there. Contacts you added yourself stay fully editable, even if someone has the same address. Turning the switch off removes that account's synced contacts from QuickMail.
- **Refreshing.** QuickMail refreshes synced contacts quietly in the background about twice a day. To pull the latest right away, use the **Sync Now** button in the address book, or the **Sync Contacts Now** command in the Command Palette.

Contact sync is one-directional and read-only in this release: no changes are written back. For Microsoft and Google it also pulls the people you've recently emailed; iCloud brings in your saved contacts (its CardDAV service has no separate "recent recipients" list).

### Changing or Deleting a Synced Contact

Because sync only runs one way, **the address book is not the place to change or remove a contact that came from an account.** With a synced contact selected, the **Edit** and **Delete** buttons are unavailable, and the matching Command Palette entries do nothing. This is deliberate: a deletion made here could not be sent to your account, so the contact would simply reappear the next time QuickMail refreshed — the change would look like it worked and then quietly undo itself.

To change or remove a synced contact for good, edit it where it actually lives, then bring the change back:

1. Open the account's own contacts — **Google Contacts** (contacts.google.com), **Outlook / Microsoft 365 People**, or **iCloud Contacts** — and make the change there.
2. Return to QuickMail's address book and press the **Sync Now** button (or run **Sync Contacts Now** from the Command Palette).
3. The list is rebuilt from the account, so an edited contact shows its new details and a deleted one is gone.

Without step 2 the address book keeps the old copy until the next background refresh, which happens about twice a day.

**"Other contacts" from Google.** Google keeps two separate lists: the contacts you saved, and **Other contacts** — addresses Google collected automatically from people you have emailed. QuickMail pulls both, which is why the address book can hold far more people than you ever saved. Removing one of these is the same two-step job as above, but do it in the **Other contacts** section of Google Contacts specifically; deleting from your saved contacts does not touch it. Google also re-collects an address if you email that person again.

**If you only want them out of QuickMail**, and not out of the account, clear **Sync contacts from this account** for that account in **File → Manage Accounts…**. That drops every contact synced from it, leaving the ones you added yourself alone. Nothing on the account is changed.

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

Flags mark messages for follow-up.

The **built-in flag** is the standard one your mail server already understands, so setting or clearing it in QuickMail shows up in every other mail program you use, and a message flagged elsewhere arrives in QuickMail already flagged. **Named flags you create yourself are stored on this computer only** — the name and color mean nothing to the server, so another program will not see them, and neither will QuickMail on a second computer. See [What Syncs and What Doesn't](#what-syncs-and-what-doesnt).

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

By default the flag name comes first when you navigate to a flagged message — for example, "Urgent. Unread. Kelly Ford. Budget deadline." This makes it immediately clear a message needs attention.

The flag is one of the pieces a row speaks, so where it falls — and whether it is spoken at all — is yours to set in **View → Message List Fields…**. Turn the **Flag** field off to leave it out, or move it later in the line to hear the sender first. See [Message List Fields](#message-list-fields). (There was previously an **Announce flag status** checkbox in Settings; it has been replaced by the Flag field in that window. If you had it turned off, the Flag field starts out turned off to match.)

---

## Mail Rules

A mail rule watches your Inbox and acts on messages that match what you describe — filing newsletters into a folder, marking a mailing list as read, deleting something you never want to see.

Open the **Rules Manager** from the **Tools** menu (`Ctrl+Shift+L`) or the command palette. It opens with the list of rules on the left and the selected rule's settings on the right.

### What a rule is made of

- **Rule name** — how you recognize it in the list.
- **Enabled** — turn a rule off without deleting it.
- **Account** — which mailbox the rule watches. Every rule belongs to exactly one account; see [Rules belong to one account](#rules-belong-to-one-account).
- **Conditions** — **From**, **To**, **Subject**, **Body**, and **Has attachments**. Check the ones you want and type the text to look for; the match is not case sensitive and looks for your text anywhere in the field. A message must satisfy **every** condition you checked, so leaving a condition unchecked is how you say "don't care".
- **Action** — one of **Mark as read**, **Mark as unread**, **Move to folder**, or **Delete**. Choosing Move to folder adds a **Choose Folder…** button; the button then shows the folder you picked. The picker is the same folder tree used everywhere else you choose a destination, showing the folders of the rule's own account — a rule files mail within one mailbox. It opens on the folder the rule already files into, or on the first folder for a new rule.

**Test Rule** runs the rule against the messages currently in your list and tells you how many it would match, so you can check a rule before letting it loose. **Save** stores the rule; **New Rule** and **Delete** manage the list.

Every enabled rule for the account is tried, in list order, and each one that matches acts. A rule that moves or deletes a message takes it out of the running for the rest of the list.

### When rules run

Rules run **on your Inbox, on mail as it arrives** — including mail that arrives while QuickMail is sitting open, not only at a full sync. This is deliberate: a rule never reaches back into Sent, Archive, Junk, Trash, or a folder you filed something into by hand, so nothing you deliberately put somewhere is moved or deleted behind your back. Mail already in the mailbox before a rule existed is left alone too.

To apply your rules to mail that is already there, use **Run on Existing Mail**. It runs every enabled rule over the Inbox of each account — again, the Inbox only — across the mail QuickMail has stored locally, and tells you how many messages were moved or deleted. (Mark as read and mark as unread still happen; they are not counted.) An account whose Inbox QuickMail has not read yet in this session is skipped rather than guessed at, and the skip is noted in the log.

### Rules belong to one account

Each rule watches one account. Choose it in the **Account** list in the rule's settings; a new rule starts on your default account. The account each rule belongs to is shown beside its name in the rules list.

If you had rules from an earlier version that applied to **all accounts**, QuickMail converts each one into a separate rule per account the first time it starts, so they keep doing what they did. The one exception is a profile whose only accounts connect through Microsoft 365 directly: those mailboxes have no client-side rule to convert to, so an old all-accounts rule is dropped and the reason is written to the log.

### Reading the rules list

Each row in the rules list says the rule's name and the account it belongs to. Turn on **Show field labels in the rules list** in **Settings → General → Screen Reader Announcements** to hear those pieces named ("Name … account …") rather than run together.

### Creating a Rule from a Message

Select a message and choose **Create Rule from Message** from the context menu or the command palette. QuickMail opens a new rule pre-filled with a condition matching the sender and, if present, the subject — a quick starting point you can adjust before saving.

### Microsoft 365: server-side rules

If you have a **work or school** Microsoft 365 (Exchange) account, the Rules Manager does a little more. Alongside the rules that run inside QuickMail, it shows the **server-side rules** on your Exchange mailbox — the same rules Outlook calls "Inbox rules." A server rule runs on Microsoft's servers, so it acts on your mail **even when QuickMail is closed**, and it applies wherever you read that mailbox.

Server-side rules are an organization feature, so **personal Outlook.com, Hotmail, and Live.com accounts do not have them** — even when connected through Microsoft 365 directly. For a personal account the Rules Manager shows only the rules that run inside QuickMail, the same as any other non-Exchange account.

**One account at a time.** With a Microsoft 365 account present, the Rules Manager opens on a single account chosen in an **Account** list at the top, rather than listing every account's rules together. Choose the account whose rules you want, and the list below shows just that mailbox's rules. If you have only one account there is no picker. This is the first thing to notice if you are used to seeing every account's rules in one list: your rules are not gone, they are behind the account picker.

**One list, marked where each rule runs.** Server rules and QuickMail rules appear together in a single list. Each row says where the rule runs — **on server** or **in QuickMail** — along with its name and whether it is enabled. Creating, editing, enabling or disabling, reordering, and deleting all work the same way whichever kind a rule is. When you open the Rules Manager, or switch to another account with the account picker, QuickMail announces that account's rule mode — that its rules run in QuickMail while it is open, or that the account also supports server-side rules — so an empty list is never a mystery about which kind the account can have.

**QuickMail chooses where a new rule lives.** When you create a rule, QuickMail saves it as a server rule whenever it can, so it keeps working while QuickMail is closed. A rule that needs something only QuickMail can do — today that is **Mark as unread** — is saved as a QuickMail rule instead, and QuickMail tells you why.

**Some server rules are read-only.** A rule you built in Outlook may use conditions or actions QuickMail cannot yet represent exactly. Rather than risk turning it into something you did not intend, QuickMail shows that rule as **read-only**: you can read it, but Edit, Delete, and Move are turned off. Change that rule in Outlook.

**Testing.** **Test** runs a rule against the messages in your list and reports how many it would match. It works on **QuickMail rules only**; for a **server rule** the Test control is turned off, the same way Edit and Delete are for a read-only rule — a server rule runs in Exchange, so there is nothing local to test it against.

**Run on Existing Mail.** This applies **the selected account's** QuickMail rules to the mail already in its Inbox — matching the one-account-at-a-time layout, so it never acts on rules for an account you are not looking at. It is available only when that account has at least one enabled QuickMail rule; a Microsoft 365 account whose rules are all server-side has nothing for it to do (server rules run in Exchange and cannot be applied to existing mail from here), so the control is turned off in that case.

**What your organization may need to allow.** For most work or school accounts, an administrator has to permit QuickMail to read and change your mailbox rules before this works. If that permission is not in place, you will see a message about it rather than your server rules; ask your administrator to grant QuickMail access.

---

## Saved Views

A saved view is a named filter you can return to instantly — for example, "Unread from work" or "Flagged in the last 7 days."

Open the **View Manager** from the **View** menu or the command palette. Create a view by choosing a folder (or All Inboxes), a message filter, and optionally a date limit. Assign a hotkey to jump to it directly.

Press the assigned hotkey from anywhere in the main window to switch to that view immediately.

### Leaving a view

A view is a temporary overlay, not a change to your settings. **Clear View** — on the
**View → Views** menu and in the command palette — puts everything back the way the folder was:
grouping, filter, sort, and any date limit the view applied. Nothing a view does is kept once you
leave it. (If the view spans several folders, clearing it returns you to All Mail, since the folders
themselves were the view.)

You can also just change something. Adjusting the view mode, filter, or sort while a view is active
leaves the view — the title stops showing its name — and what you chose becomes that folder's own
setting.

Only what you changed comes with you. If you are in "Flagged this week" and you change the sort,
you leave the view with your new sort and the folder back to its usual self — you are not left
quietly filtered to flagged messages from the last seven days. The saved view itself is never
altered; press its hotkey again to go back to it.

---

## Startup

By default QuickMail opens to **All Mail**. You can tell it to open anywhere you like instead — one account's Inbox, a project folder, or **All Inboxes**.

### Choose your startup folder

The quickest way is from the folder tree itself:

1. Move to the folder you want (**Ctrl+1** puts you in the folder tree).
2. Press the **Applications** key, or **Shift+F10**, to open the folder's context menu.
3. Choose **Set as Startup Folder**.

QuickMail confirms the change, and opens there every time it starts from then on.

You can also do it from **File → Settings → Startup**, where the **Opens in** field shows your current choice. Select **Choose…** to pick a different folder from the folder tree, or **Clear** to go back to All Mail. **Clear Startup Folder** in the folder context menu does the same thing.

If the folder later disappears — you delete it, rename it, or remove the account — QuickMail opens in All Mail and tells you why. Nothing is lost and nothing needs repairing.

### Choose how much is checked at startup

Also under **File → Settings → Startup**, **Startup Sync** controls how much mail QuickMail checks while it is starting. This matters most if you have several accounts and a lot of folders.

- **Just my startup folder** (recommended) — checks only what your startup folder shows. If your startup folder is All Inboxes, that is every inbox; if it is All Mail, that is everything.
- **Every account's inbox** — checks each account's inbox, whichever folder you open in.
- **Every folder** — checks everything before settling. This is how QuickMail behaved before this setting existed.

New mail still arrives in your inboxes straight away whichever you choose, so notifications are unaffected. Other folders are caught up by the background check — the **Check for new mail every** setting on the **General** tab. If you have set that to **Off**, other folders are only checked when you open them.

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

### Choosing a default calendar for new appointments

If most of your appointments belong on one calendar, tell QuickMail which one and it will open the appointment editor on that calendar every time.

1. In the folder tree, move to the calendar you want — **Local Calendar**, an account, or one of the calendars beneath an account.
2. Open the context menu with **Shift+F10** or the Applications key (or right-click).
3. Choose **Use as Default Calendar for New Appointments**.

QuickMail confirms the choice in the status bar and announces it, and the calendar is marked **(default)** in the folder tree — its name is announced as "…, default calendar" — so you can always tell which one is set. The choice is remembered between sessions.

To go back to saving new appointments on your local calendar, choose **Clear Default Calendar** from the same menu.

Two things this does not do. It does not change which events you are *looking at* — that is still whatever source you have selected in the tree. And it is only a starting point: the **Calendar** picker in the appointment editor still lets you send any individual appointment somewhere else.

**All Calendars** cannot be the default, because it is several calendars at once and so names no single place to save to. If you choose it, QuickMail says so and leaves your existing default alone.

Both commands are also in the Command Palette (**Ctrl+Shift+P**) — as **Use as Default Calendar for New Appointments** and **Clear Default Calendar** — and you can assign them keys in **File → Settings → Keyboard Shortcuts**.

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

You can also answer from the email itself. Open the invitation and the message starts with a short card giving the title, when the meeting is, where, and who organized it, followed by **Accept**, **Tentative**, and **Decline**. The card is worth reading even when you plan to answer elsewhere: for many invitations it is the only place the date and time appear at all — a meeting sent from Outlook for a Zoom call is a body of join links and dial-in numbers, and the when is carried in the attachment rather than written out. The card appears however you have QuickMail set to open messages, and in a message window the three responses are on that window's Command Palette (**Ctrl+Shift+P**) as well.

### Creating an appointment

Press **N** (or the **New** toolbar button) to open the appointment editor. It is a normal window you can tab through:

- **Title** — required.
- **All day** — check this for an all-day event; the time fields then switch off. Turning it back off restores the times you had.
- **Starts / Ends** — a date and a time for each. See **Entering dates and times** below.
- **Location** — optional.
- **Repeat** — leave as "Does not repeat" for a one-off, or set up a repeating appointment (see below).
- **Notes** — free text.
- **Calendar** — when you have a Microsoft, Google, or iCloud account connected, a picker lets you choose where the new appointment is saved: your **Local Calendar** or a connected account. For iCloud the picker lists each of your Apple calendars (Home, Family, …) so you can choose which one. With no connected calendar this picker does not appear and everything is saved locally. The picker starts on your [default calendar](#choosing-a-default-calendar-for-new-appointments) if you have set one, and on **Local Calendar** if you have not.

Press **Enter** (or the **Save** button) to save, or **Escape** to cancel. If something is wrong, QuickMail puts focus on the field at fault and shows the reason on an error line above the buttons; the message clears itself as soon as you fix it.

### Entering dates and times

Every date and time field in the appointment editor works the same way, and so does the date in **Go to date**. Each one is an ordinary edit field: you can type into it, and you can change it with the arrow keys without typing anything.

In a **date** field:

| Key | Moves by |
| --- | --- |
| Up / Down arrow | One day |
| Shift+Up / Shift+Down | One week |
| Page Up / Page Down | One month |
| Ctrl+Page Up / Ctrl+Page Down | One year |

In a **time** field:

| Key | Moves by |
| --- | --- |
| Up / Down arrow | 15 minutes, landing on the quarter hour |
| Ctrl+Up / Ctrl+Down | One minute |
| Shift+Up / Shift+Down | One hour |
| Page Up / Page Down | One hour |
| Ctrl+Page Up / Ctrl+Page Down | One day |

Stepping a time past midnight moves the date with it, so 11:50 PM stepped up becomes 12:00 AM the next day.

You can also just type. Dates accept "8/3", "August 3", "2026-08-03", "today", "tomorrow", "friday", "next tuesday", a bare day number like "3" for that day of the month shown, and offsets like "+7", "-3", "+2w", "+1m", "+1y". Times accept "9", "930", "9:30", "9:30 AM", "9p", "14:30", "noon", "midnight", and offsets like "+30" or "-15". Press Enter or move to another field to apply what you typed; if it isn't something QuickMail can read as a date or a time, the field puts its previous value back.

**The end follows the start.** When you change the start date or start time, the end moves by the same amount, so a 30-minute appointment stays 30 minutes long and you are never sent back to fix an end you didn't touch. Changing the end directly sets a new length, which the start then preserves from that point on.

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

### Events your account will not let QuickMail change

Some events on a connected calendar are managed by the provider itself, not by you, and the provider refuses to let **any** outside mail or calendar program edit or delete them. Google is the one you are most likely to meet. Its protected events are:

- **Birthdays** — generated from your Google Contacts, usually titled "Happy birthday!"
- **Out of office**
- **Working location**
- **Focus time**
- **Events Gmail created for you** from a message, such as a flight, hotel, or delivery.

Pressing **Delete** or saving an edit on one of these fails, and QuickMail currently reports it only as **"Could not update your online calendar."** The event is not damaged and nothing is lost — Google simply declined the change. A clearer message naming the reason is planned.

What to do instead, depending on the event:

- **Birthday** — it follows the contact, so change or clear the birthday on that person in **Google Contacts**. To stop seeing birthdays altogether, hide the **Birthdays** calendar in Google Calendar's settings; QuickMail will stop showing them at the next sync.
- **Out of office, Working location, Focus time** — create, change, or remove these in **Google Calendar** on the web or in its mobile app, where the special editors for them live.
- **An event Gmail created** — open the original message in Gmail and remove it there, or turn the feature off in Google Calendar's settings under events from Gmail.

After making the change on the provider's side, press **F5** in the calendar (or run **Sync Calendars Now**) to pull the corrected events down.

Repeating events from a connected calendar, and meeting invitations, are read-only in QuickMail for a different reason — QuickMail does not write those back yet. In those cases QuickMail tells you so before anything is attempted.

### Reminders

Reminders are **off by default**. To turn them on, open **Settings → General**, check **Remind me before appointments**, and set the **Minutes before** value (10 by default). When a reminder is due, QuickMail shows a Windows notification and announces it, telling you how long until the appointment and where it is. Each reminder fires once per session.

There is a single reminder lead time for all appointments; per-appointment reminder times and snoozing are not offered.

### Exporting an appointment

To share an appointment as a standard calendar file, select it and choose **Export Appointment as .ics** from the command palette (**Ctrl+Shift+P**) or the toolbar/menu. QuickMail writes a `.ics` file you can send to someone or import elsewhere. (Exporting one occurrence of a repeating appointment exports the whole series.) This action has no default keyboard shortcut, but you can assign one in **File → Settings → Keyboard Shortcuts**.

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
- Edit or delete the events your provider manages for you — Google birthdays, out-of-office, working location, focus time, and events Gmail created from a message. Google blocks this for every outside program, not just QuickMail; see [Events your account will not let QuickMail change](#events-your-account-will-not-let-quickmail-change).
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

Export as `.ics`, the invitation responses (Accept, Tentative, Decline), and the two [default-calendar](#choosing-a-default-calendar-for-new-appointments) commands are available from the command palette (**Ctrl+Shift+P**) and have no default key until you assign one.

---

## What Syncs and What Doesn't

QuickMail talks to your accounts for three kinds of information — mail, contacts, and calendar — and it treats each one differently. Everything else QuickMail knows about is stored on this computer and never leaves it.

The direction matters, so it is worth being blunt about it: a **two-way** item is safe to change in QuickMail, and the change reaches your account and every other program you read mail with. A **download only** item is a copy — changing it in QuickMail either is not offered, or would be undone the next time QuickMail refreshed. **This computer only** means exactly that: reinstall Windows or move to a second machine and you start over.

| What | Direction | What that means for you |
|------|-----------|--------------------------|
| **Messages** | Two-way | Read and unread, moving between folders, deleting, and sending all go to the server. Another program sees the same state. **POP3 accounts are the exception** — the protocol has nowhere to record any of it, so everything but sending stays on this computer. See [POP3 accounts](#pop3-accounts). |
| **The built-in flag** | Two-way | Set or cleared in QuickMail, it shows up everywhere else, and a message flagged elsewhere arrives here already flagged. |
| **Named flags you create** | This computer only | The name and color exist in QuickMail alone. Other programs, and QuickMail on another computer, will not see them. |
| **Contacts** | Download only | QuickMail reads your Microsoft, Google, and iCloud contacts and never writes back. Synced contacts cannot be edited or deleted in the address book — [make the change at the account and re-sync](#changing-or-deleting-a-synced-contact). |
| **Calendar events** | Mostly two-way | Single (non-repeating) events on a connected calendar can be created, edited, and deleted from QuickMail. Repeating events, meeting invitations, and the events your provider manages for you are [download only](#events-your-account-will-not-let-quickmail-change). |
| **Meeting responses** | Two-way | Accept, Tentative, and Decline are emailed to the organizer and update your calendar. |
| **Mail rules** | This computer only | Rules run inside QuickMail as mail arrives. They are not server rules — your provider does not know about them, and they do nothing while QuickMail is closed. |
| **Everything else in QuickMail** | This computer only | Settings, themes, keyboard customizations, signatures, message templates, saved views, message-list field choices, contact groups, and the contacts you typed in yourself. |

### If something you changed came back

That is nearly always a download-only item. QuickMail refreshed from the account, the account still had the old version, and the old version won. The fix is the same in every case: make the change where the item actually lives — your account's own contacts or calendar on the web — and then refresh QuickMail (**Sync Now** in the address book, **F5** in the calendar).

### Moving to a new computer

Your mail, your contacts, and your connected calendars come back on their own once you add your accounts again, because they live on the server. The **This computer only** row above does not: rules, flags you named, templates, signatures, saved views, and your settings are stored in QuickMail's data folder (`%APPDATA%\QuickMail`) and need to be copied across if you want them.

**A [POP3 account](#pop3-accounts) is the exception to the first sentence.** If it is set to remove mail from the server once collected, the server has nothing left to give back and `mail.db` in that data folder is your mail — copy it across, or back it up, like any other document.

---

## Notifications

New-mail notifications and the option to keep QuickMail running in the notification area when you close the window are both **off by default**. To turn them on, go to **Settings → General → Notifications**.

### Show a Notification When New Mail Arrives

When this setting is on, QuickMail shows a Windows notification as new mail arrives in any inbox. The notification is announced by screen readers and appears in the Windows notification center, which you open with **Win+N** on Windows 11 (or **Win+A** on Windows 10). Pressing **Enter** on a notification brings QuickMail to the foreground and opens that message.

If multiple messages arrive together, the notification shows a count ("5 new messages") and brings QuickMail to the foreground when activated. Single-message notifications show the sender's name and subject and open that message.

**Shared mailboxes are an exception:** they do not notify by default, even with this setting on, because a shared mailbox is often a busy team address. To get notifications for a specific shared mailbox, turn on **Notify me of new mail in this shared mailbox** for it in Account Manager. This setting still has to be on for those notifications to appear. See [Shared mailboxes](#shared-mailboxes).

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

- **Address Book…** (`Ctrl+Shift+B`)
- **Rules…** (`Ctrl+Shift+L`) — opens the [Rules Manager](#mail-rules).
- **Command Palette…** (`Ctrl+Shift+P`)

**Choosing how QuickMail looks and reads is on the View menu**, not here — **Manage Themes…** opens the [Theme Manager](#themes), **Density** sets [message list density](#message-list-density), and **Message List Fields…** opens the [field chooser](#message-list-fields). **Next Theme** and **Previous Theme** have no menu items; run them from the Command Palette, or give them a key of your own in **File → Settings → Keyboard Shortcuts**.

---

## Connection Diagnostics

Connection Diagnostics records what QuickMail is doing when it talks to your mail servers, so a problem that is hard to describe can be looked at directly instead of guessed at.

It is **off by default** and most people will never need it. While it is off, nothing is recorded, no log file is created, no connections are made on its behalf, and no **Connection Diagnostics** item appears in the **Help** menu.

### When you might use it

Turn it on if:

- An account shows as **disconnected** when it seems to be working, or shows as connected when mail is not arriving.
- Mail stops arriving for one account but not others.
- An action reports a failure you cannot explain — a delete or move that "may not have completed", for example.
- You are filing a bug report about any of the above and want to include something concrete.

It is a troubleshooting tool, not something to leave on. Turn it off again once you have what you need.

### Turning it on

1. Open **Settings** (Ctrl+comma) and go to the **Advanced** tab.
2. Check **Record connection diagnostics**.
3. Select **Save**.

Recording starts immediately — you do not need to restart QuickMail first. That matters when a problem is happening right now, since restarting would clear the very thing you are trying to capture.

Once it is on, **Connection Diagnostics** appears in the **Help** menu.

### The Connection Diagnostics window

Open it from **Help → Connection Diagnostics**. Focus starts in the accounts list.

**Accounts** lists every account with two things: the status QuickMail is currently showing for it, and what its mail server actually said the last time it was checked. When those two disagree, the list says so in plain words — for example that an account shown as disconnected answered normally, which means the status is wrong rather than the connection.

**Test this account** — press Enter on an account, or use the button, to check it right now. QuickMail opens a brand-new connection that shares nothing with the connections it is already using, signs in, and reads your Inbox. The result is announced and added to the list. This is the question the window exists to answer: whether the problem is your connection or what QuickMail is reporting about it.

**Connection events** is the running record, newest first. Each line is one event — a connection being opened, reused, or dropped, a sign-in failing, a status changing. The dropdown above the list filters it to a single account.

**Copy report** puts a complete report on the clipboard. **Save report** writes it to a text file. Either can be attached to a bug report or pasted into an email.

**Keyboard:** F6 and Shift+F6 move between the accounts list, the events list, and the buttons. Ctrl+Shift+P opens the command palette. Escape closes the window and returns focus to where you were.

### What it records

- Your account names, and the names and network addresses of your mail servers.
- Connection attempts and what happened to them, including how long they took and the exact error when one fails.
- Changes to the connection status QuickMail shows for each account, and what caused each change.
- The results of any checks it runs.

### What it never records

- Passwords or authentication tokens.
- The contents, subjects, senders, or recipients of your mail.
- Anything you type.

Your account names may be your email addresses, and your mail server names identify your provider, so it is worth knowing that before you share a report. If you would rather not send one, describe what you saw instead — that is still useful.

### Where it is kept, and removing it

The record is written to `connection.log` in your profile directory (usually `%APPDATA%\QuickMail`), next to QuickMail's other settings. It is capped in size and older entries are discarded, so it cannot grow without limit — a full session of ordinary use produces roughly 50 KB.

Recording stops as soon as you turn the setting off. To remove what has already been recorded, use **Delete QuickMail logs** on the **Advanced** tab, which deletes the connection log along with the application log.

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

Alongside what you type, an in-app report (options 1 and 2) adds a short **Environment** section so a problem can be reproduced in the right context: the QuickMail version, your Windows version, the .NET runtime version, the active color theme, the current view, the current sort order, and the message open mode (reading pane, tab, or window).

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

Accounts are not part of Settings — they have a window of their own. Open **File → Manage Accounts…** to add, edit, test, or remove one. See [Accounts](#accounts).

### Advanced

**Account Sign-In**

- **Sign in with Google for Gmail accounts** — off by default. Google no longer authorizes QuickMail for new accounts, so Gmail normally uses an app password. Turn this on only if your Google authorization predates that change; it adds a **Gmail (sign in with Google)** provider and a Google choice under **Authentication** in the account dialogs. Takes effect the next time QuickMail starts. Gmail accounts already using Google sign-in keep working whether this is on or off. See [Gmail (Google Account)](#gmail-google-account).

**Account Types**

- **Offer POP3 when adding an account** — off by default. Adds **POP3/SMTP** to the **Connection method** list under **Advanced settings** in the account dialogs, for mail services that offer no IMAP. Takes effect the next time QuickMail starts. POP3 downloads each message to this computer and keeps it there, in QuickMail's own Inbox, Sent, Drafts and Trash, so the local copy is the only copy once a message leaves the server — read [POP3 accounts](#pop3-accounts) before turning it on. Accounts already using POP3 keep working whether this is on or off.

**QuickMail Logging**

- **Enable logging** — when checked, QuickMail writes activity to `quickmail.log` in your profile directory (usually `%APPDATA%\QuickMail`). Uncheck to stop writing the log file. Changes take effect when you select **Save**.
- **Delete QuickMail logs** — deletes QuickMail's diagnostic files immediately after confirmation: the application log (`quickmail.log`), the connection diagnostics log (`connection.log`), and any screenshots saved by the debug capture option below. If logging or connection diagnostics are still on, new files are created the next time something is recorded.

> **Note:** If QuickMail was launched with the `/debug` flag, logging always runs regardless of the Enable logging setting. The `/debug` flag is intended for diagnosing problems and overrides this preference so that nothing is missed.

**Log Format**

Controls the order of timestamp and message text in each log line. **Action first** (default) places the message before the timestamp, which is easier to scan since the log is already in chronological order. **Time first** uses the original format with the timestamp at the start of each line.

**Connection Diagnostics**

- **Record connection diagnostics** — off by default. Records how QuickMail connects to your mail servers and adds **Connection Diagnostics** to the **Help** menu. Takes effect as soon as you select **Save**, with no restart. See [Connection Diagnostics](#connection-diagnostics).

**Diagnostics (debug)**

- **Capture screenshots of new windows** — off by default, and a developer aid rather than an everyday setting. It saves a picture of each window as you open it, so someone reviewing how QuickMail looks has something to look at. It lasts for the current session only — QuickMail turns it back off when it closes — and every window's title bar says so while it is on. The images stay on your computer and are removed by **Delete QuickMail logs**.

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

**Message List Density** — **Comfortable** (the default) or **Compact**, changing how much space each message row takes. The same choice is on the **View** menu under **Density**; see [Message List Density](#message-list-density). It changes spacing only, never what a row says.

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
| Announce spelling errors when typing | Misspellings called out as you type them |
| Announce spelling errors while navigating | Misspellings called out as you move the cursor through the message |
| Announce spelling suggestions | Suggestions included when a misspelling is announced |
| Spelling Suggestions Verbosity | Numbers with suggestions (default) or just suggestions |
| Show field labels in the contact list | When on, address-book rows speak field names ("Name … email … account …"); when off, they speak concise field data only |
| Show field labels in the calendar event list | When on, calendar rows speak field names ("Subject … when … location …") |
| Show field labels in the rules list | When on, rules-list rows speak field names ("Name … account …") |

All settings default to on except the three **Show field labels** switches and **Announce spelling errors when typing**, which are off. **Spelling Suggestions Verbosity** defaults to **Numbers with suggestions**. Turn off **Custom Announcements** to silence everything at once; turn it back on to restore your individual preferences.

**Message list rows are set elsewhere.** What each row in a message list says, in what order, and whether those pieces are labelled, is set in **View → Message List Fields…** rather than here. See [Message List Fields](#message-list-fields).

---

## Themes

QuickMail's color scheme is controlled through **Settings → Appearance** (see [Settings](#settings)) and managed in more detail through the **Theme Manager**.

### Theme Manager

Choose **Manage Themes…** from the **View** menu, or open the Command Palette (**Ctrl+Shift+P**) and choose **Manage Themes**. The Theme Manager is a separate, non-blocking window, so you can leave it open while you try a theme against real messages. From the theme list, press Tab to reach the actions:

- **Apply** — switch to the selected theme immediately.
- **Duplicate** — copy a theme as a starting point for your own. A name field appears with a suggested name.
- **Rename** / **Delete** — for your own themes (built-ins cannot be changed or deleted).
- **Export…** — save a theme as a `.quickmailtheme` file to share or move to another machine.
- **Import…** — load a `.quickmailtheme` file. If a theme file has a problem, QuickMail tells you exactly what is wrong (for example, which color value is not a valid hex color).
- **Open themes folder** — opens the folder where your themes are stored, for hand-editing.

Below the theme list and actions, a read-only **Theme description** box always shows a plain-language account of the currently selected theme — its overall look, its fonts, and every individual color together with where in the app that color is used. This box is there so you can understand and compare themes by ear or by reading, without needing to see the colors. See [Built-in Themes](#built-in-themes) below for the description of each theme that ships with QuickMail.

The Command Palette also offers **Next Theme** and **Previous Theme** to cycle through themes, and a **Theme: [name]** command for each theme. None of these have a default keyboard shortcut — assign one in **File → Settings → Keyboard Shortcuts** if you want direct access.

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

## Message List Fields

Each row in a message list is spoken as a single line — sender, subject, date, and so on. **View → Message List Fields…** lets you decide which of those pieces are spoken and in what order. It is also in the command palette as **Message List Fields…**. It has no keyboard shortcut of its own; assign one in **File → Settings → Keyboard Shortcuts** if you want one.

Open it and you get a list of every available field, each one a real check box. Check a field to have it spoken, uncheck it to leave it out, and move it to change where it falls in the line.

| Key | Action |
|-----|--------|
| `Up` / `Down` | Move between fields; stops at the first and last one |
| `Space` | Turn the focused field on or off |
| `Alt+Up` / `Alt+Down` | Move the focused field earlier or later in the spoken order |
| `Home` / `End` | First / last field |
| *a letter* | Jump to the next field starting with that letter; press again to reach the next match |
| `F6` / `Shift+F6` | Cycle: Row type, Fields, Field options, Spoken preview, buttons |
| `Ctrl+Shift+P` | This window's own command palette |
| `Escape` | Close |

**Move Up**, **Move Down**, **Reset to Defaults**, and **Close** are buttons as well. Moving a field says where it landed — "Moved down. Position 4 of 13." — and adds "Not spoken" when the field you moved is switched off, so a move that changes nothing you can hear says so.

The **Spoken preview** box at the bottom shows the message you had selected when you opened the window, read exactly as the list would read it, updating as you go.

There is no OK or Cancel — changes save as you make them, and take effect immediately. The window is modeless, so you can leave it open, arrow through the message list behind it, and hear the result.

### Row types

The **Row type** list at the top chooses which kind of row you are editing. Each keeps its own arrangement:

- **Messages** — individual messages, in the flat list and inside all three group trees.
- **Conversation groups** — the top-level rows in the Conversations view.
- **Sender and recipient groups** — the top-level rows in the From and To views.

### Fields for messages

| Field | Speaks |
|-------|--------|
| Flag | The flag's name, when the message is flagged |
| Status (combined) | One word: "replied", "forwarded", "unread", or "read" |
| Attachments | "attachments" |
| From, Subject, Preview, Date, To | The field's text |
| Source folder | Where the message lives — only in aggregate views such as All Archive, where it is account-qualified when the view spans accounts |
| Unread, Replied, Forwarded | Each state on its own, so you can arrange them separately |
| Mailing list | "mailing list" |

### Fields for group rows

**Conversation groups** offer Subject, Message count, Sender, Flag, Has unread, Preview, and Date. **Sender and recipient groups** offer Sender, Message count, Flag, Has unread, Preview, Date, and Newest subject. A message count is spoken as "1 message" or "3 messages".

### Speaking a state only when it applies

Fields such as **Unread**, **Replied**, and **Attachments** are states rather than text, so selecting one offers a choice:

- **Speak only when true** — say "unread" on unread messages and nothing at all on read ones.
- **Always speak** — say "unread" or "read", whichever applies.

This is how you get "tell me about unread but never say read": turn **Status (combined)** off, turn **Unread** on, and leave it on *Speak only when true*. Fields with no meaningful opposite — Attachments, for example — stay silent when false in either mode.

**Status (combined) and the separate states overlap.** Both say the word "unread", so turning on Unread while Status (combined) is still on says it twice. Whenever that would happen, the **About this field** note in the options pane says so and tells you which one to turn off. The note is a focusable box, so it is reachable from the keyboard rather than being text only a sighted user would notice.

### Field labels

**Speak field labels** prefixes each text field with its name, so a row reads "From: Chris Lee. Subject: Budget review." rather than "Chris Lee. Budget review." It applies to all three row types. States and counts are never labelled, since "unread" and "3 messages" already say what they are.

### Starting over

**Reset to Defaults** restores the shipped arrangement for the row type you are currently editing, leaving the other two alone.

Until you change anything, rows read the way they always have. Empty fields are skipped rather than leaving a gap, so a message with no preview or no subject reads without a pause where it would have been.

This window replaces the old **Announce flag status** checkbox in Settings — the flag is now the **Flag** field here, which you can turn off, or move anywhere in the line. If you had that checkbox turned off, the Flag field starts out turned off to match; from then on this window owns the choice.

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
| `Ctrl+Shift+W` | Watch / unwatch the selected message's conversation |
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
| `Escape` | Close contact mail results (message list focus) |

**Move to Folder…** and **Copy to Folder…** are available from the context menu (Shift+F10) or the command palette; they have no default keyboard shortcut. **Manage Themes**, **Next Theme**, **Previous Theme**, **Message List Fields…**, **Density: Comfortable**, **Density: Compact**, **Manage Flags…**, **Expand Folder**, **Collapse Folder**, **Expand All Folders**, **Collapse All Folders**, and **Report a Bug** likewise have no default key — reach them from the menus or the command palette, or assign a shortcut yourself in File → Settings → Keyboard Shortcuts.

`Ctrl+1`, `Ctrl+2`, and `Ctrl+3` jump to a pane when no message tabs are open; with tabs open they select tab 1, 2, and 3 instead. **`Ctrl+Alt+1`, `Ctrl+Alt+2`, and `Ctrl+Alt+3` always** go to the account list, folder tree, and message list, whatever else is open. `Ctrl+0` moves to the toolbar.

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
| `Alt+A` | Focus attachment list |
| `Ctrl+K` | Check addresses |
| `Ctrl+Shift+P` | Command palette |
| `Escape` | Close window (when no menu or dropdown is open) |

**Insert Template…** and **Save as Template** are available from the command palette; they have no default keyboard shortcut.
