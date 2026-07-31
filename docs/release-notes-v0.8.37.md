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

## Changed: Google sign-in for Gmail is now something you turn on

Google stopped granting QuickMail new authorizations, so Gmail accounts sign in with an app password. But a small number of accounts were authorized before that happened and still work perfectly well over Google sign-in, and this release makes sure they keep their route in.

**If your Gmail account already uses Google sign-in, nothing changes and you need do nothing.** It keeps signing in, keeps syncing mail, contacts, and calendar, and Manage Accounts still shows it as a Google account. Only the *offer* of Google sign-in to a new account is affected.

**If you want to add another Gmail account over Google sign-in**, turn the option on:

1. **Tools → Settings → Advanced**, check **Sign in with Google for Gmail accounts**, and select **Save**.
2. Restart QuickMail — the setting is read at startup.

The **Provider** list then has a **Gmail (sign in with Google)** entry directly below plain **Gmail**. Choose it and there is no password box at all: Gmail's servers fill in as usual and a **Sign in with Google** button stands where the password would be. Contact and calendar sync are offered too, granted as part of the same sign-in. The Google choice also returns to **Advanced settings → Authentication**.

The same switch is available as `GoogleAuth = true` under `[features]` in `config.ini`, or `--feature GoogleAuth` at launch, for anyone who would rather not use the Settings dialog.

**Why it is off by default.** It was previously on for everyone, which meant the one path Google refuses was the one on offer — a sign-in that ends in "This app has been blocked" tells you nothing about what to do instead. Off by default, the app-password route is what a new Gmail account gets, and the users the sign-in still works for have a supported way to ask for it.

If you turn the setting on and sign-in still ends in **"This app has been blocked,"** your account is not one of the ones authorized earlier and no QuickMail setting can change that. Use a Gmail app password — the User Guide's [Gmail section](https://kellylford.github.io/QuickMail/) has the steps.

---

## Fixed: adding an account no longer shows every other account as disconnected

Adding a new account made the accounts you already had report **disconnected** in the account list, and they stayed that way until you restarted QuickMail. This has been reported several times, and each previous attempt fixed a real connection bug that turned out not to be this one.

**Nothing was ever actually disconnecting.** Your accounts stayed connected the whole time - mail kept arriving, folders kept working. What broke was the account list's picture of them. Adding an account makes QuickMail re-read your account file, and connection status is live information that is deliberately never written to that file, so every account came back from the re-read reporting "disconnected". Accounts that were already working were then skipped by the reconnect pass - correctly, since nothing was wrong with them - so nothing ever corrected the status, and the wrong label stuck.

The status now survives a re-read. Adding, editing, or removing an account leaves the others reporting exactly what they were.

This one hid for so long because it looks exactly like a connection failure: it appears the moment you touch your accounts, it hits every account at once, and it lasts until a restart. It was found by recording what QuickMail's connections were actually doing at the moment it happened - which is the feature below.

## New: Connection Diagnostics, for when something looks wrong

**Settings -> Advanced -> Record connection diagnostics** turns on a record of how QuickMail connects to your mail servers. It is **off by default**, and most people will never need it.

Turn it on when an account reports the wrong status, mail stops arriving, or an action reports a failure you cannot explain - then reproduce the problem. It starts recording the moment you switch it on, so you do not have to restart first and lose what you were trying to capture.

While it is on, a **Connection Diagnostics** item appears in the **Help** menu. It shows each account with what QuickMail believes about it alongside what its mail server actually says, and a **Test this account** button checks an account directly on a brand-new connection. That is the question this exists to answer: whether the problem is your connection or what QuickMail is reporting about it. **Copy report** and **Save report** produce a plain-text file you can attach to a bug report.

**What it records:** your account names, your mail server names and addresses, connection attempts and their results, and error messages. **What it never records:** passwords, authentication tokens, and the contents of your mail. Your account names may be email addresses, and mail server names identify your provider, so it is worth knowing what is in a report before you share one.

The record is written to `connection.log` beside QuickMail's other settings, is capped in size, and stops the moment you turn the setting off. **Delete QuickMail logs** in the same Advanced section removes it along with the application log.

## Fixes

- **Editing a Gmail address no longer discards a deliberate provider choice.** Picking a Gmail entry and then typing the address swapped your choice for the other Gmail entry on the first keystroke, because a Gmail address matches both. A provider that already covers the address you are typing is now left alone. Correcting the address to a different provider still moves off it as before.
- **An account signing in with Google always shows that in Manage Accounts**, whether or not the setting above is on. Previously the Authentication box could be left blank for such an account, with nothing to say what it was using.
- **A feature flag written from Settings updates the entry already in `config.ini`** instead of adding a second one in different capitalization beside it, which could leave a setting looking as though it had not taken.

---

## Fixed: choosing a sending account could send the message

Pressing Enter after arrowing to an account in the compose window's **From** list sent the message ([#201](https://github.com/kellylford/QuickMail/issues/201)). Not chose the account — sent the mail, half-written, to whoever was already in the To field. Choosing a mode from the compose-mode list, or pressing Enter in the **Subject** box or the attachment list, did the same thing.

The Send button was marked as the window's default button, which in Windows means Enter activates it from anywhere in the window that does not use Enter for something of its own. A closed list is exactly such a place: arrowing through it already changes the selection, so Enter had nothing to do there and went to Send instead. That default is now removed. Enter no longer sends from anywhere in the compose window.

Send is still **Alt+S**, **Ctrl+Enter**, or Enter or Space with the Send button focused.

**Enter on the From list now confirms the account.** Since the keystroke no longer sends, it says which account you landed on — "IdeaPlace used as From address". You also hear it when you pick an account from the expanded list, and when you leave the From field having changed it. Arrowing past accounts stays quiet, because your screen reader is already reading each one. This uses the **Announce action results** setting, so turning that off turns this off with it.

---

## Fixed: sending mail gave no feedback

A report of "sending an email gives no feedback, and does not close the compose window" ([#396](https://github.com/kellylford/QuickMail/issues/396)) turned out to be four separate problems stacked on top of each other. All four are fixed.

**A send that fails now says so out loud.** The failure message was classed as background progress, so if you had turned **Announce background progress** off — a reasonable thing to do, since that is the setting that stops every folder announcing itself during a sync — pressing Send produced the button greying out, coming back, and nothing else. Send failures, refusals, and confirmations are now announced as results, which is the category for the outcome of something you just did, and they interrupt rather than queue. The same fix applies to a refused save in Add Account and Manage Accounts.

**A message that was accepted is no longer reported as failed.** QuickMail closed the connection inside the same step that sent the message, so a server that hangs up the instant it takes your mail — or any hiccup during the sign-off — produced "Send failed" for a message that was already on its way. This is why the reporter saw messages sometimes arrive anyway. The sign-off is now separate and its own failure is ignored, because by then the server has your message.

**Your login and your email address can now be different things.** There was one box serving as both, and for some accounts they are not the same string: an iCloud mailbox on your own domain logs in under the Apple ID, and some hosted servers want a bare user name. Whichever one you entered, the other use of it was wrong — a login name in the box became the From address on your mail, which servers reject. **Advanced settings** in both account dialogs now has a **Login username** box, empty for almost everyone, filled in only when your server logs in under something other than your email address. The **Email address** box is now only that, and saving an account refuses an entry that is not a full address, pointing at the new box instead.

If you already had an account set up with a login name in the address box, QuickMail copies it into **Login username** for you the first time it starts. That matters: correcting the address is what you are now asked to do, and without the copy you would be deleting the very thing your account signs in with. You enter the address; the login carries on working. This also covers contact and calendar sync on iCloud, which sign in with the same name.

**A wrong encryption setting on a known server is corrected at startup.** An account set up by hand before QuickMail knew these providers could end up with **Implicit SSL on connect** checked while using port 587, which is a STARTTLS port. That combination fails every single send, about a second after you press the button, with an error that names a certificate rather than a checkbox. At startup QuickMail now corrects the encryption setting when — and only when — the account is one you have never saved yourself, the server is one it ships settings for, *and* the port is the exact port it publishes for that server. Anything else — one of those servers on a different port, or any account you have saved in Manage Accounts — is left exactly as you set it. A corrected connection also requires encryption from then on, rather than falling back to plain text if the server offers none.

---

## Fixed: work and school Microsoft 365 accounts

**Adding a work or school account now always asks your permission.** On some organizations, sign-in finished without ever showing a permission screen — and then every attempt to read mail failed. The account was added, looked connected, and could not reach a single message. It happened where the organization had already approved QuickMail for something else, such as contacts or calendar: Microsoft treats an existing approval as covering the whole request and signs you in silently, so the mail permissions were never asked for and never granted.

Adding an account now always shows the permission screen, so the full set is approved once, up front. You will see it when you add an account, and when you use **Sign in** in Manage Accounts — which is the button you reach for when an account has stopped working, so asking again there is deliberate. An account whose sign-in has merely expired renews as before, without asking. (#391)

**A folder no longer stops loading because of one message.** Microsoft 365 reports some messages — certain drafts and system-generated mail — with no read state at all. A single one of those failed the entire folder's fetch, so none of that folder's new mail arrived and nothing said why. One real session hit it 49 times. A message with no read state is now treated as unread. (#395)

## Fixed: the Provider field in Manage Accounts had no value to read

The read-only **Provider** value added in this release announced its label and nothing else — you heard "Provider" and were given no value at all, so the one thing the field exists to tell you was the one thing it never said. It now reads out the provider it is showing, and the text can be reviewed a character at a time. (#401)

---

## Fixed: typing a folder name in the folder picker now works

Typing letters in the tree view of the folder picker — the view you get when moving or copying a message — did nothing. The v0.8.32 notes said typing a folder name there would jump to it; the mechanism behind that claim turned out never to have worked, and nothing else was wired in its place. The tree now has the same type-ahead as the main window's folder tree: type the first letters of a folder's name and the selection jumps to it, keep typing to narrow the match, repeat a letter to cycle through folders that share it. ([#418](https://github.com/kellylford/QuickMail/issues/418))

Two related repairs in the same picker:

- **The flat "Go to Folder" list matched against the wrong text.** Typing a letter there searched an internal name rather than the folder names on screen, so it went nowhere useful. It now matches the folder path you see.
- **Typing "o" or "c" could press a button.** The Open and Cancel buttons carried shortcut letters that fire on a bare keypress when focus is in a list, so an unmatched type-ahead letter could activate one of them — "c" closed the picker. The same problem was fixed for the New Folder button in v0.8.32; Open and Cancel now follow. Enter still opens the selected folder and Escape still cancels.

**In the main window's folder tree, type-ahead now continues a prefix.** Typing "s", "e" in quick succession finds "Sent" rather than treating each letter as a fresh first-letter search. The v0.5.5 notes described the folder tree working this way, but the code never actually did — each letter was always a fresh search; the message list is where continuation really lived. The tree now genuinely does what those notes said.

**Repeating a letter now cycles through matches everywhere.** Pressing "s" twice quickly used to build the prefix "ss", which matches nothing, so rapid repeats went dead until the timeout passed. A repeated letter now keeps the single-letter prefix and moves to the next match — the standard list behavior — in the message list, the grouped views, both folder trees, and the picker.

One more small change in the same code: **a capital letter now works for type-ahead.** Shift+S was silently ignored in the message list and grouped views; it now matches the same as "s". (Matching was always case-insensitive once the letter got through.)

---

## Internal

- **`MainViewModel.LoadAccountList` carries `IsConnected`/`TotalUnread` across a reload**, keyed by account id. Both are runtime state deliberately excluded from `accounts.json`, so rebuilding the models produced objects defaulting to disconnected, and replacing the `Accounts` collection made the whole list read that way at once. `RefreshAccountList` reconnects only accounts failing `AccountsNeedingConnect`, so healthy accounts were never re-evaluated and the false label persisted until restart. Guarded by `AccountListReloadStatusTests`; 4 of its 5 tests fail with the carry-over removed.
- **The instrumentation could not see this directly**, and that is the lesson worth keeping: the state was lost by *object replacement*, not assignment. `ApplyAccountStatus` genuinely is the only writer of `IsConnected`, so no write was ever observable - the journal's silence next to a plainly visible symptom was itself the evidence. `LoadAccountList` now records an `accounts-reloaded` event closing that blind spot.
- `ConnectionJournal` (new) - bounded, self-rotating `connection.log` plus a 2000-event in-memory ring backing the diagnostics window. Gated on `ConfigModel.ConnectionDiagnostics` (default `false`). `Record` returns on a volatile read before allocating, **but arguments to the eager overload are evaluated first** — so call sites whose detail invokes `HostConnectionCensus` (which resolves DNS) use a `Func<string>` overload or check `Enabled` themselves. An independent review caught the original version computing the census on every pool rent with the feature off. Enable and disable each write a marker, so a journal that stops is distinguishable from one switched off mid-investigation. File writes take a separate lock from the ring, so a background disk write never blocks a UI-thread `Snapshot()`.
- `HostConnectionCensus` (new) - live socket counts per host with cached DNS resolution, and shared-address detection. Our cap is per *account*; a server's is per *user+IP*, and on shared hosting per IP overall. Registrations live in a `ConditionalWeakTable` keyed by client so release is idempotent. Documented limitation: the count is decremented only via `Released()`, which every disposal path funnels through — a client collected *without* being disposed would leave the counter high, so an implausibly high census is to be treated as suspect rather than as proof and cross-checked against the `pool=` figure on the same line.
- `IConnectionProbe` / `ConnectionTruthProbe` (new) - independent reachability verification on a connection sharing nothing with the pools or watchers, emitting a greppable `verdict` line stating what the UI shows against what the server said. Serialized process-wide and rate-limited per account, since a per-IP limit is a plausible suspect and the probe must not become part of what it measures. **Gated on the setting at every entry point**: an independent review found the first version starting its verification loop regardless, so a single IDLE failure on a default install opened an authenticated connection outside the pool cap every 60 seconds, indefinitely — against exactly the host already refusing connections. `RetainOnly` also abandons verification for an account removed while it was showing disconnected, which otherwise probed a deleted mailbox for the rest of the session.
- `ProbeResult` carries a three-state `ProbeOutcome` (`Reachable`/`Unreachable`/`NotSupported`). The first live run wired the probe straight to `ImapMailService`, which answered "not registered with the IMAP service" for a **Graph** account; collapsed to a boolean that read as unreachable, it reported a healthy account as broken. `Unreachable` is false for `NotSupported`, so the two cannot be conflated again. `GraphMailService` implements the interface via the Inbox counts, and `MailServiceRouter` dispatches per account with **no default-backend fallback** - defaulting to IMAP was the original defect.
- `ImapMailService.RaiseReachability` funnels `AccountReachabilityChanged` so every raise carries a reason. Worth noting for anyone reading the connection code: the IDLE watcher is still the only source of reachability, and it marks an account unreachable after a *single* failure, before any retry (#314).
- `ApplyAccountStatus` takes a `source` tag from all eight call sites.
- **`TypeAheadPrefixTracker` + `TypeAheadMatcher` (new)** — the hand-rolled type-ahead prefix accumulator and wrap-around matcher, extracted from `MainWindow` so they can be tested without a window (#415). The tracker takes a `TimeProvider`, so `TypeAheadLogicTests` exercises the 1-second reset window deterministically, including its exact boundary — coverage that previously required synthesized keystrokes racing a real clock (#380/#414). The tracker's peek/commit split also closes a latent double-append: the `PreviewKeyDown` route now peeks and commits only on a match, so an unmatched keystroke is recorded once (by `PreviewTextInput`), not twice.
- **`TypeAheadWiringTests` now fails any `TreeView` declaring `TextSearch.TextPath`.** WPF disables text search by default on `TreeView`/`TreeViewItem` (verified against the control defaults; `ListBox`/`ListView`/`ComboBox` enable it), and even enabled it matches one level's items only — which is how the picker's inert attribute shipped in v0.8.32 with a release note claiming it worked. Trees must wire `PreviewTextInput` to the shared tracker instead.
- `ConnectionDiagnosticsWindow` is modeless per the modal-dialog rules, with its own F6 ring, Escape handling, focus restoration, a `CancellationTokenSource` field cancelled in `OnClosing` (closing mid-test previously left a probe holding the process-wide probe semaphore for up to 45 seconds), and a **window-scoped** command palette listing its own actions rather than the main window's. Pane names on F6 announce as `Status`, not `Hint`, so turning hints off does not make the ring silent. `Refresh` preserves the event filter — rebuilding the combo dropped its selection, wrote null back through the TwoWay binding, and blanked the journal pane permanently. `help.connectionDiagnostics` is registered and unregistered by `ApplyConnectionDiagnosticsSetting` rather than at startup, so the palette and the Help menu never disagree about whether the feature exists.
- **Delete QuickMail logs** now removes `connection.log` (and its rolled-over `.1`) alongside `quickmail.log`. Deleting your logs means all of them - most often because they carry your email addresses and mail server names - and leaving a second file behind would quietly defeat that.
- The account-status carry-over is skipped when the account's connection identity changed (host, port, login, auth or security settings), so an edited account is not vouched for by connections belonging to the old server, and duplicate ids in `accounts.json` no longer throw on reload.
- `ConnectionDiagnosticsTests`, `ConnectionDiagnosticsSettingTests`, `ConnectionDiagnosticsWindowTests`, `ConnectionDiagnosticsReviewFixTests`, `AccountListCarryOverGuardTests`, `ImapConnectionInstrumentationTests` (real connect path against a closed port and a hang-up listener, asserting socket error codes are captured and the census does not drift), plus `AccountListReloadStatusTests`.

- `OAuthService.PromptForSignIn(firstConnect, username)` centralizes the MSAL prompt choice: the add-account path forces `Prompt.Consent`, re-auth keeps `ForceLogin`/`SelectAccount`. Scopes stay `.default` for work/school, so requested-equals-declared still holds by construction (#208) and Azure resolves the set per account type — an explicit org-only scope list would have broken personal accounts on a custom domain. `SignInInteractiveAsync(account, ct)` is the add path and is reached from both `AddAccountViewModel` and `AccountManagerViewModel`'s **Sign in** button, since both derive from `AccountEditorViewModel`. The add path therefore trades `Prompt.ForceLogin` for `Prompt.Consent`; the #202 identity-mismatch guard in `AccountEditorViewModel` still refuses to adopt a different identity that completes sign-in, so the protection moves from prevention to detection rather than disappearing. Note that `.default` never surfaces a *newly declared* permission to a user who already holds an older grant — any permission added in future must be requested explicitly at the point it is needed, as contacts and calendar already do.
- `GraphMessage.IsRead` becomes `bool?`, mapped `?? false` at both sites in `GraphMailService`. As a non-nullable `bool` it threw `JsonException: Cannot get the value of a token type 'Null' as a boolean` mid-batch, and because the throw escaped the whole deserialization, one message took down the entire folder fetch.
- Account Manager's Provider value is a read-only `TextBox` rather than a focusable `TextBlock`. A TextBox exposes its `Text` as a value alongside the `LabeledBy` name; on the TextBlock, `LabeledBy` overrode the automatic name (its own text) and left no value behind it, so the binding was announced as nothing. Same binding, same tab position, still read-only.
- `FeatureFlag.GoogleAuth` default flips to `false`. It gates only the *offer* — no runtime authentication path consults it, so saved `AuthType.OAuth2Google` accounts are unaffected.
- New `ProviderCatalog` entry `gmail-oauth` ("Gmail (sign in with Google)"), `DefaultAuthType = OAuth2Google`, no app-password hint, exposed as `IProviderCatalog.GmailGoogleSignIn`. It carries the gmail.com domains but sits after the plain Gmail entry, so `MatchByEmail` and `Resolve`'s host fallback still answer `gmail` for every Gmail address; it is reached only by an explicit pick or a saved `ProviderId`.
- `AccountEditorViewModel.Providers` becomes an `ObservableCollection<MailProvider>` built from the catalog minus the Google entry, with `EnsureGoogleSignInListed()` inserting it after Gmail. The entry is absent from the list rather than collapsed, so it is out of the keyboard order and the accessibility tree entirely.
- `ShowGoogleAuthOption` is no longer virtual: it is `IsGoogleAuthEnabled || AuthType == AuthType.OAuth2Google`, with derived VMs overriding the protected gate check. The second clause is what keeps an existing Google account's Authentication combo populated.
- `AddAccountViewModel.MatchProviderFromUsername` returns early when the selected provider already matches the typed address.
- `ConfigModel.Features` is now an `OrdinalIgnoreCase` dictionary; `SettingsViewModel.GoogleSignIn` reads and writes the `GoogleAuth` key in it rather than adding a setting of its own.
- `GoogleSignInOptInTests` — 20 tests covering the gate default and its config/CLI overrides, catalog ordering and resolution, picker contents in both states, the provider-choice regression, and the Settings round-trip.
- `AccountModel.LoginUsername` (persisted, nullable) plus computed `AuthUsername` = `LoginUsername ?? Username`. Every **password** authentication uses `AuthUsername` — IMAP, all three SMTP entry points, and the iCloud CardDAV/CalDAV Basic auth in `ICloudContactSource` and `GraphCalendarSyncService`, which is the same credential pair. OAuth still uses `Username`, the mailbox the token was issued for. `Username` is now documented as the email address and nothing else — it is the From header, the provider-catalog match, and the autodiscovery domain. `SameConnectionSettings` includes `LoginUsername` so a corrected login invalidates the pooled client.
- `EmailAddressValidator` (new) parses with `AllowAddressesWithoutDomain = false` — MimeKit's default accepts a bare local part, which is exactly the input that produced `MAIL FROM:<fastfinge>`. Deliberately does not require a dot in the domain. `TryNormalize` returns `mailbox.Address`, and that normalized form is what both editors save: `MailboxAddress.TryParse` accepts `Kelly Ford <kelly@example.com>`, an angle-addr, and padded input, while the `MailboxAddress(name, address)` constructor `MimeMessageBuilder` calls throws on all three — so validating without normalizing would only have moved the failure from a refused save to a rejected send. Enforced in `AccountEditorViewModel.IsEmailAddressUsable` (shared by `IsReadyToSave` and `AccountManagerViewModel.SaveAccount`) and as a pre-send guard in `ComposeViewModel.SendAsync`.
- `AccountStartupRepair` (new) runs in `OnStartup` against the loaded account list and does two things. It corrects `ImapUseSsl`/`SmtpUseSsl` where the account carries no `ProviderId` (the marker for predating the catalog — `SaveAccount` backfills it, so a deliberate pairing the user has saved is never overruled), the host equals a catalog provider's host, *and* the port equals that provider's published port; a leg moved to STARTTLS also gets `RequireStartTls`, matching what `ApplyProvider` sets for the same host and port, so a repaired account is not left weaker than a freshly added one. It also copies a non-address `Username` into `LoginUsername` on password accounts, so correcting the address does not destroy the working login. Matched on host rather than `ProviderCatalog.Resolve`, whose email-domain fallback would claim an address relayed through a third-party server.
- `SmtpService.DisconnectQuietlyAsync` replaces the in-`try` `DisconnectAsync` in `SendAsync`, `SendIcsReplyAsync`, and `VerifyAsync`.
- `AnnouncementCategory StatusCategory` on `ComposeViewModel` and `AccountEditorViewModel`, with `SetStatusOutcome`/`SetProgress`. **One-shot** — it returns to `Status` after every raise, matching `MainViewModel.StatusAnnouncementCategory`, because both VMs assign `StatusText` directly in dozens of places and a latched `Result` would re-classify all of them as interrupting outcomes. The setter also clears `StatusText` first so an identical repeated message still raises `PropertyChanged`; without that, pressing a button twice on the same unfixed field announced nothing the second time. Replaces `AddAccountDialog`'s local `_statusCategory` field, and removes three double-announce sites in `ComposeWindow`.
- `AccountManagerViewModel.EmailAddressRejected` — the View opens Advanced settings and focuses the address box, since the refusal names a control that lives behind a collapsed expander.
- `AccountStartupRepairTests` (17), `AccountLoginUsernameTests` (20), `ComposeViewModelSendFeedbackTests` (13), `EmailAddressValidatorTests` (26), plus login-identity tests in `CardDavContactSyncTests` and `CalDavCalendarSyncTests`. `StatusAnnouncementRecorder` captures announcements inside the `PropertyChanged` notification, the way the View does — asserting the category afterwards would pass against a broken implementation now that the category is one-shot.

---

## Reporting Issues

Found a problem or have a suggestion? There are three ways to reach us — pick the one that fits:

1. **Report a Bug → Send** (Help menu, inside QuickMail). Files the report for you anonymously — it includes no email address or other identifying information, so there is no way to follow up with you. **Best when you don't want any follow-up.**
2. **Report a Bug → Copy report and open GitHub** (Help menu). Opens a pre-filled issue that you submit under your own GitHub account, so your GitHub contact information is attached. **Best when you have a GitHub account and want automatic filing plus direct contact.**
3. **Email** [quickmailissues@theideaplace.net](mailto:quickmailissues@theideaplace.net). **Best when you don't mind sending email and want a personal follow-up.**

Full details, including exactly what a report contains (and what it never contains), are in the [Reporting Issues section of the User Guide](https://kellylford.github.io/QuickMail/reporting-issues.html).
