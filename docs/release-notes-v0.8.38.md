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

## Internal

- `FeatureFlag.GoogleAuth` default flips to `false`. It gates only the *offer* — no runtime authentication path consults it, so saved `AuthType.OAuth2Google` accounts are unaffected.
- New `ProviderCatalog` entry `gmail-oauth` ("Gmail (sign in with Google)"), `DefaultAuthType = OAuth2Google`, no app-password hint, exposed as `IProviderCatalog.GmailGoogleSignIn`. It carries the gmail.com domains but sits after the plain Gmail entry, so `MatchByEmail` and `Resolve`'s host fallback still answer `gmail` for every Gmail address; it is reached only by an explicit pick or a saved `ProviderId`.
- `AccountEditorViewModel.Providers` becomes an `ObservableCollection<MailProvider>` built from the catalog minus the Google entry, with `EnsureGoogleSignInListed()` inserting it after Gmail. The entry is absent from the list rather than collapsed, so it is out of the keyboard order and the accessibility tree entirely.
- `ShowGoogleAuthOption` is no longer virtual: it is `IsGoogleAuthEnabled || AuthType == AuthType.OAuth2Google`, with derived VMs overriding the protected gate check. The second clause is what keeps an existing Google account's Authentication combo populated.
- `AddAccountViewModel.MatchProviderFromUsername` returns early when the selected provider already matches the typed address.
- `ConfigModel.Features` is now an `OrdinalIgnoreCase` dictionary; `SettingsViewModel.GoogleSignIn` reads and writes the `GoogleAuth` key in it rather than adding a setting of its own.
- `GoogleSignInOptInTests` — 20 tests covering the gate default and its config/CLI overrides, catalog ordering and resolution, picker contents in both states, the provider-choice regression, and the Settings round-trip.

---

## Reporting Issues

Found a problem or have a suggestion? There are three ways to reach us — pick the one that fits:

1. **Report a Bug → Send** (Help menu, inside QuickMail). Files the report for you anonymously — it includes no email address or other identifying information, so there is no way to follow up with you. **Best when you don't want any follow-up.**
2. **Report a Bug → Copy report and open GitHub** (Help menu). Opens a pre-filled issue that you submit under your own GitHub account, so your GitHub contact information is attached. **Best when you have a GitHub account and want automatic filing plus direct contact.**
3. **Email** [quickmailissues@theideaplace.net](mailto:quickmailissues@theideaplace.net). **Best when you don't mind sending email and want a personal follow-up.**

Full details, including exactly what a report contains (and what it never contains), are in the [Reporting Issues section of the User Guide](https://kellylford.github.io/QuickMail/reporting-issues.html).
