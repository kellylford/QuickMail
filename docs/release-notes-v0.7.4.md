# QuickMail v0.7.4 Release Notes

## Download

Two options are available for v0.7.4:

| Download | When to use |
|----------|-------------|
| **`quickmail-v0.7.4-setup.exe`** — Windows installer | Recommended for most users. Installs per-user with no elevation required, checks for the WebView2 Runtime, and registers an uninstaller. |
| **`QuickMail.exe`** — standalone portable executable | No installation required. Copy it anywhere and run. |

Both downloads include the .NET 8 runtime — you do not need to install .NET separately.

---

## New Features

### Gmail Support via Google Sign-In

You can now add a Gmail account using your Google account instead of an app password.

**Adding a Gmail account:** Open **Settings → Accounts → Add Account** (or press **Ctrl+Shift+A**). Choose **Google OAuth** from the account type list. Press **Sign in with Google** — your default browser opens to a Google sign-in page. Sign in and grant QuickMail permission to read and send mail. When the browser confirms success, switch back to QuickMail and press **Save**. Server settings are filled in automatically; you do not need to enter them.

Your sign-in credential is stored in Windows Credential Manager, the same secure store QuickMail uses for other accounts. The access token refreshes automatically in the background; you will not be prompted to sign in again unless you revoke access from your Google account settings.

**Sign out:** Open **Settings → Accounts**, select your Gmail account, and press **Sign Out**. The stored credential is removed from Windows Credential Manager.

This feature requires a Google client ID and client secret, which the QuickMail project registers with Google. If you are building QuickMail from source, see `docs/GOOGLE-AUTH-SPEC.md` for how to configure your own credentials.

### Grab Addresses: Add to Group

When grabbing addresses from an open message (`Ctrl+Shift+G`), you can now add the selected contacts directly to an address book group in the same step.

**How it works:**

1. Press `Ctrl+Shift+G` to open the **Add to Address Book** window.
2. The message's addresses appear as checkboxes, all checked by default. Uncheck any you do not want.
3. Check **Add to group** to show the group options.
4. Choose an existing group from the **Group** combo box, or choose **Create new group** and type a name in the **New group name** field.
5. Press **Save** (or Enter). Each checked contact is added to your address book and, if you chose a group, added to that group.

If you type a new group name and leave it blank when Save is pressed, QuickMail announces "Enter a name for the new group" and keeps the window open. No contacts are saved until a valid group name is provided or you remove the name and save without the group option checked.

### Grab Addresses: Tab Navigation Fix

Previously, Tab cycled through every address checkbox in the list, requiring many Tab presses to reach the Save or Cancel button. Tab now stops on the address list once — pressing Tab again exits the list and moves to the **Add to group** checkbox, then to the group controls, then to Save and Cancel. Arrow keys still move between individual address checkboxes within the list.

---

## Bug Fixes

- **Resource leak in compose windows.** Each compose window left a timer handle open when closed because `ComposeViewModel`'s auto-save cancellation token source was never cancelled or disposed. The handle is now cancelled and disposed when the compose window closes.
- **SemaphoreSlim handles leaked on app exit.** `ContactService` and `TemplateService` each hold a `SemaphoreSlim` whose underlying `WaitHandle` was never released. Both services are now disposed on application exit.

---

## Thank You to Contributors

Thank you to everyone who has contributed to QuickMail through code, bug reports, feature suggestions, and other feedback. Your contributions make the project better for everyone.

---

## Internal

### Google OAuth

- `AuthType.OAuth2Google` enum value; `FeatureFlag.GoogleAuth` (default on).
- `IGoogleOAuthService` / `GoogleOAuthService`: browser sign-in via `LocalServerCodeReceiver` (Google.Apis.Auth); access token refreshed automatically; refresh token persisted in Windows Credential Manager via `CredentialService.SaveSecret` / `GetSecret` / `DeleteSecret`.
- `OAuthRouter`: routes `IOAuthService` calls to MSAL or `GoogleOAuthService` by `AuthType`, so `ImapMailService` and `SmtpService` need only a one-line guard change.
- `ConfigModel` / `ConfigService`: `[google]` section reads `GoogleClientId` / `GoogleClientSecret` from `config.ini`; credentials are gitignored and supplied via GitHub Actions secrets in CI.
- `AddAccountViewModel`: auto-fills `imap.gmail.com:993 (SSL)` and `smtp.gmail.com:587` when `OAuth2Google` is selected, matching the Microsoft auto-fill pattern.
- `AccountEditorViewModel`: `SignInGoogleCommand` (async, opens browser flow), `IsGoogleOAuth` computed property, updated `AuthTypeIndex` (0 = Password, 1 = Microsoft, 2 = Google).
- `AddAccountDialog` / `AccountManagerDialog`: Google OAuth combo item and **Sign in with Google** button, both gated by `ShowGoogleAuthOption` / `FeatureFlag.GoogleAuth`.
- `AccountPropertiesBuilder`, `MainViewModel`, `AccountManagerViewModel`: updated to handle `OAuth2Google` in auth display and sign-out paths.
- `docs/privacy.html`: privacy policy page hosted on GitHub Pages at `https://kellylford.github.io/QuickMail/privacy.html`; required for Google OAuth verification.
- `StubGoogleOAuthService` added to test project.

### Grab Addresses

- `GrabAddressesDialog` shown modelessly (`.Show()`) instead of via `.ShowDialog()`. Modal nesting over a WebView2 host + editable text field + screen reader caused a hard UI-thread deadlock on focus. Modeless windows have no `DialogResult`, so Escape and Cancel are wired to `Close()` explicitly; Escape is guarded against the group combo's open dropdown.
- `Save_Click` service calls wrapped in try/catch: failures are logged and announced (`Result` category); the dialog stays open for retry rather than crashing on an unhandled `async void` exception.
- `UpsertContactAsync`: set `contact.Id` for existing contacts so `AddMemberAsync` receives a valid ID.
- `GrabAddressesDialogTests` rewritten: 8 tests covering focus on open, Tab exit from address list, static group controls, modeless Escape-close, Save with new group, Save without group, and empty-name validation.

### IDisposable

- `ComposeViewModel.Dispose()`: calls `_autoSaveCts.Cancel()` before `Dispose()` (prevents `ObjectDisposedException` in in-flight autosave tasks per the IDisposable rules in `CLAUDE.md`).
- `ComposeWindow.OnClosed`: calls `_vm.Dispose()`.
- `App.OnExit`: `ContactService` and `TemplateService` promoted to private fields and disposed.

---
