# QuickMail v0.8.32 Release Notes

## Download

Two options are available for v0.8.32:

| Download | When to use |
|----------|-------------|
| **`QuickMail-0.8.32-win.msi`** — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

This release adds **contact sync**, which pulls the contacts and prior recipients from your Microsoft and Google accounts into QuickMail's address book, and makes **creating folders** easy to reach from the keyboard. It also restores **personal Microsoft (Outlook.com / Hotmail) sign-in**, which had stopped working, and fixes replies to HTML-only messages coming through with an empty quote. If you installed QuickMail from the MSI, this update is delivered automatically. Everything from v0.8.31 — single-instance launch, plain-text reading, Windows notifications for new mail, and running in the background when you close the window — is included.

---

## New in 0.8.32

### Contact Sync — bring your accounts' contacts into the address book

QuickMail can now fill its address book from the contacts and prior recipients already stored in your Microsoft and Google accounts. Turn it on per account and the people you correspond with become available for autocomplete when you address a message — no more typing full addresses for people your account already knows.

- **Off by default, one switch per account.** When you add a Microsoft or Google account, the Add Account dialog has a **Sync contacts from this account** checkbox you can check before you sign in. For an account you already have, open **Settings → Accounts**, edit the account, and check the same box — it takes effect immediately, with no separate Save step.
- **Read-only, one direction.** Synced contacts flow from the server into QuickMail only; QuickMail never writes changes back to your account. Synced people appear in the address book but cannot be edited or deleted there (your own local contacts stay fully editable, even when someone has the same address). Turning the switch off again removes that account's synced contacts from QuickMail.
- **One-time permission.** Enabling sync asks your account for read-only access to your contacts. For Google this is folded into the same sign-in you already do; for Microsoft it is granted right after sign-in.
- **Automatic and manual.** QuickMail refreshes synced contacts quietly in the background (about twice a day). To pull the latest immediately, use the **Sync Now** button in the address book, or the **Sync Contacts Now** command in the Command Palette.
- **An Account column in the address book.** Each contact now shows where it came from — **Local address book** for the ones you added yourself, or the account name for synced ones. A screen reader reads each row as its plain contents ("Kelly Ford, kelly@example.com, Local address book"), the same concise style the message list uses. If you prefer to hear the field names spoken, turn on **Show field labels in the contact list** in **Settings → General → Accessibility**.

### New Folder is now easy to find

Creating a folder used to be available only from the folder tree's right-click / Shift+F10 menu, which made it hard to discover from the keyboard. It now has proper, discoverable entry points:

- A **Folder → New Folder…** menu item.
- A **New Folder…** command in the Command Palette (**Ctrl+Shift+P**), which you can assign your own shortcut to in keyboard customizations.
- A **New Folder** button in the folder picker you use when moving or copying a message — create the destination folder right there and move into it without leaving the dialog.

---

## Fixed in 0.8.32

### Personal Microsoft (Outlook.com / Hotmail) sign-in works again

Signing in with a personal Microsoft account had begun failing outright with a "scope … is not valid" error, which blocked adding or re-authorizing Outlook.com, Hotmail, and Live.com accounts on shipped builds. QuickMail now requests the correct, explicit mail permissions for these accounts. Personal and work Microsoft accounts both sign in normally again.

### Replying to an HTML-only message keeps the original text

Reply and Reply All to a message that had no plain-text part produced a reply containing only the "On … wrote:" attribution line, with none of the original message quoted beneath it. Replies now fall back to the message's HTML content when there is no plain-text part, so the original text is always quoted — matching how Forward already worked.

### Launching QuickMail after closing a message window works again

If you had opened a message in its own window and then closed the main window, that stray message window could keep QuickMail running invisibly in the background, which in turn stopped you from launching a fresh copy. Message windows are now closed together with the main window, so relaunching always works.

### Keyboard fixes in the folder tree and lists

- **Pressing K in the folder tree** no longer toggles a message flag. The single-key **K** flag shortcut now applies only when the message list or a sender/recipient group has focus; in the folder tree, K does folder type-ahead as expected.
- **Shift+F10 in the folder list and account list** now opens that list's own context menu instead of the message list's, and the very first Shift+F10 right after launch no longer opens the Windows system menu by mistake.
- In the move/copy folder picker, plain letter keys again jump through the folder tree by typing (the New Folder button is reached with **Alt+N**).

---

## Accessibility

- **Contact sync** feeds the same autocomplete you already use to address messages, so the benefit is simply that more of the right people are there when you type — no new interaction to learn. The address book's new **Account** column is spoken as part of each row's single utterance, keeping navigation concise; the optional **Show field labels in the contact list** setting is there for anyone who prefers to hear the field names.
- Because the address book runs as a modal window, contact sync deliberately never pops a sign-in window while it is open (that combination can freeze the interface with a screen reader active). Consent is gathered up front when you enable sync; if a grant later lapses, QuickMail tells you to turn sync off and on again rather than surprising you with a sign-in prompt.
- The **K** flag key is now scoped to the message area, so it no longer competes with folder-tree type-ahead — a plain-key gesture behaves the same way as the calendar's plain-key shortcuts, which are scoped to their own list.
- **New Folder** is reachable by menu, Command Palette, and an assignable shortcut, so folder creation no longer depends on finding a context menu.

---

## Thank You to Contributors

Thank you, as always, to everyone who contributes to QuickMail through code, bug reports, feature suggestions, and other feedback — including the reports that pinned down the broken personal-Microsoft sign-in, the empty HTML-only reply, the K-in-the-folder-tree flag, and the launch problem caused by a lingering message window.

---

## Internal

### Server contact sync and prior recipients (issue #256, PRs #257, #259, #263)

- One-way (server → local) pull of contacts and prior recipients from Microsoft (Graph `/me/contacts` + `/me/people`) and Google (People `connections` + `otherContacts`) accounts into the existing local address book, deduplicated by email against local contacts and surfaced through the existing compose autocomplete with no compose-side changes. Off by default, per-account `SyncContacts` toggle (OAuth accounts only). Enabling requests read-only contact scopes (one-time consent) and pulls an initial snapshot; disabling purges that account's synced contacts. Re-sync updates in place by provider id, removes server-deleted rows, and never touches local or other accounts' rows. Prior recipients capped at 1000 per source (logged when capped).
- New `ContactSource` provenance on `ContactModel` (back-compatible with old `contacts.json`), `IContactSyncService` / `ContactSyncService`, provider sources (`GraphContactSource`, `GoogleContactSource` + `GooglePeopleClient`), scope-aware `GraphClient` overload, and `RequestContactsConsentAsync` across the OAuth services. Read-only only: no write-back, no iCloud/CardDAV (deferred to v2). Spec: `docs/planning/contact-sync-server-issue-256-pm-dev-spec.md`.
- **Refinements from testing** (PR #263): fixed the address-book → compose path opening with an empty From picker (the compose VM was created but never `Seed()`-ed); added the Add Account opt-in checkbox (`SyncContacts` moved to the shared editor base); added the address-book **Account** column with concise composed accessible names and the `ContactListShowFieldLabels` opt-in (off by default); made the Account Manager toggle apply immediately from the checkbox `Click` handler (async fire-and-forget stays in the View per the MVVM rules) rather than on Save; dropped the chatty per-row "synced, read-only" focus hint.
- **Review hardening** (PR #259): contact sync acquires its Graph token **silent-only** — no interactive fallback — because an MSAL embedded WebView2 launched inside the address book's nested modal loop can deadlock the UI thread (the documented modal-dialog hazard, with the screen-reader path as trigger). Added `IOAuthService.GetAccessTokenSilentAsync` + a `silentOnly` path through `GraphClient`. Background sync throttled to 12h via `SyncAllDueAsync`; the manual "Sync Contacts Now" command bypasses the throttle.
- 40+ new tests across `ContactServiceSyncTests`, `ContactSyncServiceTests`, `ContactSourceMappingTests`, `ContactRefinementsTests`, `ContactSyncScopeAndViewModelTests`, and `OAuthServiceScopeSelectionTests`.

### Personal Microsoft sign-in on the IMAP path (issue #239, PR #242)

- The `.default` migration (#217/#218/#234) was over-applied to the IMAP/SMTP path. `.default` on `outlook.office.com` is invalid for personal Microsoft accounts (no admin-consent model; the resource isn't in Required Resource Access for consumer sign-in), so personal-account sign-in via IMAP failed with "scope … is not valid". Because the Graph backend is feature-gated, IMAP is the only Microsoft path in shipped builds, so this broke consumer sign-in for everyone. `ImapSmtpScopes` now requests explicit `IMAP.AccessAsUser.All` + `SMTP.Send`, which work for personal and work accounts alike. Tests lock the explicit IMAP scopes; ENTRA doc + spec corrected.

### Empty quoted body when replying to HTML-only messages (issue #260)

- Reply / Reply All quoted only `detail.PlainTextBody`, so replying to a message with no plain-text part produced just the attribution line. Applied the same HTML→text fallback `CreateForward` already uses. Preserving HTML formatting in replies is tracked as a follow-up in #262.

### Folder-creation discoverability and message-window lifetime (issues #250, #252, #255, #253, PR #257)

- **#250:** Registered a discoverable `folder.new` command (Command Palette + customizable hotkey) and a **Folder → New Folder…** menu item; context menu and the new entry points share one `CreateFolderUnderNodeAsync` helper. Added a **New Folder** button to the move/copy folder picker (tree-view picker only) that creates under the selected node — or the account root — and rebuilds the picker tree in place. Re-entrancy safety: folder creation inside the picker refreshes only the folder cache, deferring the main-window tree rebuild (`_folderTreeRebuildPending` → `CommitPendingFolderTreeRebuild`, flushed at the call site after every picker close, including cancel) to avoid the documented "re-query the folder tree while the dialog's loop is active" crash. Removed the picker button's `_New` mnemonic (it fired on the type-ahead `n`), wired **Alt+N** explicitly, and set `TextSearch.TextPath="Label"` for folder type-ahead.
- **#252:** Standalone message windows are now tracked in `_openMessageWindows` and closed in `MainWindow.OnClosed`, the same way compose windows are. An orphaned message window had been keeping the process (and the per-profile single-instance mutex) alive after the main window closed, blocking relaunch.
- **#255:** The bare-**K** Toggle Flag command is gated on a new `IsMessageAreaFocused()` so it fires only when the message list or a group tree has focus; elsewhere the command is unavailable, `e.Handled` stays false, and the folder tree's type-ahead handles K.
- **Shift+F10 fixes:** the pre-focus handler now supplies a focus target only when WPF focus is genuinely null (the startup case), so the folder tree and account list open their own context menus; and when focus can't be parked synchronously on the first post-launch press (WebView2 holds Win32 focus at startup), the hook swallows the message rather than falling through to the Win32 system menu.
- **#253:** Documented (working-as-designed) that the single-instance activation signal carries no payload, so a second launch with toast deep-link args brings the window forward but drops the open-specific-message payload; marked the seam for any future IPC extension.
