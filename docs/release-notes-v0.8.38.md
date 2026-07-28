# QuickMail v0.8.38 Release Notes

## Download

Two options are available for v0.8.38:

| Download | When to use |
|----------|-------------|
| **`QuickMail-win.msi`** — Windows installer | Recommended for most users. A standard setup wizard with license agreement; installs per-user with no elevation required, adds the WebView2 Runtime if missing, and enables automatic updates. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

---

## Google sign-in for Gmail is now something you turn on

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

## Fixes

- **Editing a Gmail address no longer discards a deliberate provider choice.** Picking a Gmail entry and then typing the address swapped your choice for the other Gmail entry on the first keystroke, because a Gmail address matches both. A provider that already covers the address you are typing is now left alone. Correcting the address to a different provider still moves off it as before.
- **An account signing in with Google always shows that in Manage Accounts**, whether or not the setting above is on. Previously the Authentication box could be left blank for such an account, with nothing to say what it was using.
- **A feature flag written from Settings updates the entry already in `config.ini`** instead of adding a second one in different capitalization beside it, which could leave a setting looking as though it had not taken.

---

## Sending mail: silence, and the reasons behind it

A report of "sending an email gives no feedback, and does not close the compose window" ([#396](https://github.com/kellylford/QuickMail/issues/396)) turned out to be four separate problems stacked on top of each other. All four are fixed.

**A send that fails now says so out loud.** The failure message was classed as background progress, so if you had turned **Announce background progress** off — a reasonable thing to do, since that is the setting that stops every folder announcing itself during a sync — pressing Send produced the button greying out, coming back, and nothing else. Send failures, refusals, and confirmations are now announced as results, which is the category for the outcome of something you just did, and they interrupt rather than queue. The same fix applies to a refused save in Add Account and Manage Accounts.

**A message that was accepted is no longer reported as failed.** QuickMail closed the connection inside the same step that sent the message, so a server that hangs up the instant it takes your mail — or any hiccup during the sign-off — produced "Send failed" for a message that was already on its way. This is why the reporter saw messages sometimes arrive anyway. The sign-off is now separate and its own failure is ignored, because by then the server has your message.

**Your login and your email address can now be different things.** There was one box serving as both, and for some accounts they are not the same string: an iCloud mailbox on your own domain logs in under the Apple ID, and some hosted servers want a bare user name. Whichever one you entered, the other use of it was wrong — a login name in the box became the From address on your mail, which servers reject. **Advanced settings** in both account dialogs now has a **Login username** box, empty for almost everyone, filled in only when your server logs in under something other than your email address. The **Email address** box is now only that, and saving an account refuses an entry that is not a full address, pointing at the new box instead.

**A wrong encryption setting on a known server is corrected at startup.** An account set up by hand before QuickMail knew these providers could end up with **Implicit SSL on connect** checked while using port 587, which is a STARTTLS port. That combination fails every single send, about a second after you press the button, with an error that names a certificate rather than a checkbox. At startup QuickMail now corrects the encryption setting when — and only when — the server is one it ships settings for *and* the port is the exact port it publishes for that server. Anything else, including one of those servers on a different port, is left exactly as you set it.

---

## Internal

- `FeatureFlag.GoogleAuth` default flips to `false`. It gates only the *offer* — no runtime authentication path consults it, so saved `AuthType.OAuth2Google` accounts are unaffected.
- New `ProviderCatalog` entry `gmail-oauth` ("Gmail (sign in with Google)"), `DefaultAuthType = OAuth2Google`, no app-password hint, exposed as `IProviderCatalog.GmailGoogleSignIn`. It carries the gmail.com domains but sits after the plain Gmail entry, so `MatchByEmail` and `Resolve`'s host fallback still answer `gmail` for every Gmail address; it is reached only by an explicit pick or a saved `ProviderId`.
- `AccountEditorViewModel.Providers` becomes an `ObservableCollection<MailProvider>` built from the catalog minus the Google entry, with `EnsureGoogleSignInListed()` inserting it after Gmail. The entry is absent from the list rather than collapsed, so it is out of the keyboard order and the accessibility tree entirely.
- `ShowGoogleAuthOption` is no longer virtual: it is `IsGoogleAuthEnabled || AuthType == AuthType.OAuth2Google`, with derived VMs overriding the protected gate check. The second clause is what keeps an existing Google account's Authentication combo populated.
- `AddAccountViewModel.MatchProviderFromUsername` returns early when the selected provider already matches the typed address.
- `ConfigModel.Features` is now an `OrdinalIgnoreCase` dictionary; `SettingsViewModel.GoogleSignIn` reads and writes the `GoogleAuth` key in it rather than adding a setting of its own.
- `GoogleSignInOptInTests` — 20 tests covering the gate default and its config/CLI overrides, catalog ordering and resolution, picker contents in both states, the provider-choice regression, and the Settings round-trip.
- `AccountModel.LoginUsername` (persisted, nullable) plus computed `AuthUsername` = `LoginUsername ?? Username`. Every IMAP/SMTP **password** authentication uses `AuthUsername`; OAuth still uses `Username`, which is the mailbox the token was issued for. `Username` is now documented as the email address and nothing else — it is the From header, the provider-catalog match, and the autodiscovery domain. `SameConnectionSettings` includes `LoginUsername` so a corrected login invalidates the pooled client.
- `EmailAddressValidator` (new) parses with `AllowAddressesWithoutDomain = false` — MimeKit's default accepts a bare local part, which is exactly the input that produced `MAIL FROM:<fastfinge>`. Deliberately does not require a dot in the domain. Enforced in `AccountEditorViewModel.IsEmailAddressUsable` (shared by `IsReadyToSave` and `AccountManagerViewModel.SaveAccount`) and as a pre-send guard in `ComposeViewModel.SendAsync`.
- `AccountTransportRepair` (new) runs once in `OnStartup` against the loaded account list, correcting `ImapUseSsl`/`SmtpUseSsl` only where the host equals a catalog provider's host *and* the port equals that provider's published port. Matched on host rather than `ProviderCatalog.Resolve`, whose email-domain fallback would claim an address relayed through a third-party server.
- `SmtpService.DisconnectQuietlyAsync` replaces the in-`try` `DisconnectAsync` in `SendAsync`, `SendIcsReplyAsync`, and `VerifyAsync`.
- `AnnouncementCategory StatusCategory` on `ComposeViewModel` and `AccountEditorViewModel`, with `SetStatusOutcome`. Assign it before `StatusText` — the View announces on the `StatusText` notification. Replaces `AddAccountDialog`'s local `_statusCategory` field, and removes three double-announce sites in `ComposeWindow` that set `StatusText` and then announced the same string again.
- `AccountTransportRepairTests` (8), `AccountLoginUsernameTests` (12), `ComposeViewModelSendFeedbackTests` (7).

---

## Reporting Issues

Found a problem or have a suggestion? There are three ways to reach us — pick the one that fits:

1. **Report a Bug → Send** (Help menu, inside QuickMail). Files the report for you anonymously — it includes no email address or other identifying information, so there is no way to follow up with you. **Best when you don't want any follow-up.**
2. **Report a Bug → Copy report and open GitHub** (Help menu). Opens a pre-filled issue that you submit under your own GitHub account, so your GitHub contact information is attached. **Best when you have a GitHub account and want automatic filing plus direct contact.**
3. **Email** [quickmailissues@theideaplace.net](mailto:quickmailissues@theideaplace.net). **Best when you don't mind sending email and want a personal follow-up.**

Full details, including exactly what a report contains (and what it never contains), are in the [Reporting Issues section of the User Guide](https://kellylford.github.io/QuickMail/reporting-issues.html).
