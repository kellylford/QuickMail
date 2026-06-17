# Google OAuth 2.0 Authentication — Implementation Spec

## Context

QuickMail already has working Microsoft OAuth via MSAL (`OAuthService`, `AuthType.OAuth2Microsoft`).
This spec adds Google OAuth so users can add Gmail accounts using IMAP/SMTP with the XOAUTH2
mechanism — the same wire protocol already used for Microsoft accounts.

The user has already registered QuickMail as a Desktop app in Google Cloud Console and added
their Gmail address as a test user. Client ID and Client Secret are available.

---

## Scope

- Add Gmail as an account type in the Add Account dialog
- OAuth 2.0 installed-app flow (browser → localhost redirect → token exchange)
- Refresh token stored in Windows Credential Manager via `CredentialService`
- Access token fetched/refreshed in memory; passed to MailKit's existing `SaslMechanismOAuth2`
- No Google REST API usage — IMAP (`imap.gmail.com:993`) and SMTP (`smtp.gmail.com:587`) only

---

## Prerequisites

The following are assumed complete before implementation begins:

- Google Cloud Console app registered as type **Desktop app**
- Gmail API enabled on the project
- Test user (kelly@theideaplace.net or equivalent) added to the OAuth consent screen
- Client ID and Client Secret recorded and ready to add to `config.ini`

---

## Client credential storage

The Google Client ID and Secret are stored in the user's `config.ini` (in `%APPDATA%\QuickMail`),
**not** in the source repo. This keeps them out of version control and means the same binary can
work for any user who configures their own Google app.

Add two new fields to `ConfigModel`:

```csharp
public string GoogleClientId     { get; set; } = string.Empty;
public string GoogleClientSecret { get; set; } = string.Empty;
```

`ConfigService` reads/writes these automatically via its existing INI serialization. The user sets
them once and they persist across app restarts. No UI is needed for this — config.ini is a
power-user file.

---

## New AuthType value

```csharp
public enum AuthType
{
    Password,
    OAuth2Microsoft,
    OAuth2Google,       // ← new
}
```

---

## New NuGet package

`Google.Apis.Auth` (latest stable, currently ~1.68). This provides:

- `GoogleAuthorizationCodeFlow` — manages the OAuth 2.0 code flow
- `LocalServerCodeReceiver` — opens a loopback HTTP listener for the redirect URI
- `UserCredential` — wraps the access + refresh token and handles automatic refresh

No `Google.Apis.Gmail.v1` package is needed — we use IMAP/SMTP, not the Gmail REST API.

---

## Token storage

Google issues a **refresh token** once (at first consent) and short-lived **access tokens**
(valid ~1 hour). The refresh token must survive app restarts.

- **Refresh token**: stored in Windows Credential Manager via the existing `CredentialService`,
  keyed as `QuickMail/Google/<username>` (same pattern as passwords). The `CredentialService`
  already handles secure storage; no new storage mechanism is needed.
- **Access token**: held in memory inside `GoogleOAuthService`. Refreshed automatically when
  expired using `UserCredential.GetAccessTokenForRequestAsync()`.

---

## New service: `GoogleOAuthService`

New file: `QuickMail/Services/GoogleOAuthService.cs`

```csharp
public interface IGoogleOAuthService
{
    /// <summary>
    /// Returns a valid access token, refreshing silently if needed.
    /// Throws if no stored credential exists — call SignInInteractiveAsync first.
    /// </summary>
    Task<string> GetAccessTokenAsync(string username, CancellationToken ct = default);

    /// <summary>
    /// Opens the system browser for Google sign-in. Stores the resulting credential
    /// (including refresh token) and returns the access token + confirmed email address.
    /// </summary>
    Task<OAuthResult> SignInInteractiveAsync(string loginHint, CancellationToken ct = default);

    Task SignOutAsync(string username);
}
```

Implementation notes:
- Constructor receives `IConfigService` (for Client ID/Secret) and `ICredentialService` (for
  refresh token storage).
- On startup, loads any stored refresh token from `CredentialService` and reconstructs a
  `UserCredential` so silent refresh works without a new browser flow.
- `SignInInteractiveAsync` uses `LocalServerCodeReceiver` so the browser redirects to
  `http://127.0.0.1:<dynamic-port>/` — this matches Google's "Desktop app" redirect URI rules.
- After sign-in, stores the refresh token via `CredentialService.SaveCredential(key, refreshToken)`.
- `GetAccessTokenAsync` calls `credential.GetAccessTokenForRequestAsync()` which handles refresh
  transparently.

---

## OAuthRouter

Rather than modifying `OAuthService` (Microsoft-only) or making `ImapMailService` aware of two
different OAuth services, introduce a thin router.

New file: `QuickMail/Services/OAuthRouter.cs`

```csharp
public class OAuthRouter : IOAuthService
{
    private readonly IOAuthService _microsoft;
    private readonly IGoogleOAuthService _google;

    public OAuthRouter(IOAuthService microsoft, IGoogleOAuthService google) { ... }

    public Task<string> GetAccessTokenAsync(AccountModel account, CancellationToken ct = default)
    {
        return account.AuthType == AuthType.OAuth2Google
            ? _google.GetAccessTokenAsync(account.Username, ct)
            : _microsoft.GetAccessTokenAsync(account, ct);
    }

    // SignInInteractiveAsync and SignOutAsync route the same way.
}
```

`App.xaml.cs` wires this up:
```csharp
var msOAuth     = new OAuthService(profile);
var googleOAuth = new GoogleOAuthService(configService, credentialService);
var oauthRouter = new OAuthRouter(msOAuth, googleOAuth);
// pass oauthRouter wherever IOAuthService is needed
```

`ImapMailService` and `SmtpService` already call `_oauth.GetAccessTokenAsync(account, ct)` and
pass the result to `SaslMechanismOAuth2(account.Username, token)`. No changes needed there beyond
adding `AuthType.OAuth2Google` to the existing `if (account.AuthType == AuthType.OAuth2Microsoft)`
guard — change it to `if (account.AuthType is AuthType.OAuth2Microsoft or AuthType.OAuth2Google)`.

---

## AccountEditorViewModel changes

Add `SignInGoogleCommand` alongside the existing `SignInMicrosoftCommand`:

```csharp
[RelayCommand]
private async Task SignInGoogleAsync()
{
    IsBusy = true;
    StatusText = "Opening browser for Google sign-in…";
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var result = await GoogleOAuthService.SignInInteractiveAsync(Username, cts.Token);
        Username   = result.Username;
        StatusText = $"Signed in as {result.Username}";
    }
    catch (Exception ex)
    {
        StatusText = $"Sign-in failed: {ex.Message}";
    }
    finally { IsBusy = false; }
}
```

`AccountEditorViewModel` needs `IGoogleOAuthService` injected (or a combined wrapper). The
constructor signature changes to accept it.

New computed property to drive XAML visibility:
```csharp
public bool IsGoogleOAuth => AuthType == AuthType.OAuth2Google;
```

---

## AddAccountViewModel changes

Add `AuthType.OAuth2Google` as a selectable option. When selected:
- Auto-fill: `imap.gmail.com:993 SSL`, `smtp.gmail.com:587 STARTTLS`
- Clear password field
- `IsPasswordAuth = false`, `IsOAuth2 = false`, `IsGoogleOAuth = true`

The `AuthType` combo in `AddAccountDialog.xaml` gains a third item:
```xml
<ComboBoxItem>Google OAuth (Gmail)</ComboBoxItem>
```

`AuthTypeIndex` mapping expands to handle 0/1/2.

---

## AddAccountDialog.xaml changes

Add a "Sign in with Google" button, visible only when `IsGoogleOAuth` is true:

```xml
<StackPanel Visibility="{Binding IsGoogleOAuth, Converter={StaticResource BoolToVisibility}}"
            Margin="0,4,0,0">
    <Button Content="Sign in with _Google…"
            Command="{Binding SignInGoogleCommand}"
            AutomationProperties.Name="Sign in with Google account in browser"
            HorizontalAlignment="Left" MinWidth="200" TabIndex="5"/>
</StackPanel>
```

The existing Microsoft sign-in panel already uses the same pattern.

---

## FeatureFlag

Add a `GoogleAuth` flag to `FeatureFlag.cs` (default `false`):

```csharp
/// <summary>
/// Shows the Google OAuth option in Add Account.
/// Default: false. Enable in config.ini as FeatureFlag_GoogleAuth=true once tested.
/// </summary>
GoogleAuth,
```

`AddAccountViewModel` gates the Google option behind `gate.IsEnabled(FeatureFlag.GoogleAuth)`,
same as `GraphBackend` gates the Microsoft Graph option.

---

## Keyboard walkthrough

### Adding a Gmail account

1. User opens Account Manager (menu or keyboard shortcut).
2. User activates "Add Account" button.
3. Add Account dialog opens. Focus lands on "Account Name" field.
4. User fills in Account Name, Display Name, Email / Username.
5. User Tabs to the "Authentication" combo box. Currently reads "Password".
6. User presses Alt+Down, arrows to "Google OAuth (Gmail)", presses Enter (or Tab).
   Screen reader announces: "Google OAuth (Gmail)".
   IMAP host auto-fills to `imap.gmail.com`, port 993. SMTP fills to `smtp.gmail.com`, port 587.
   Password field collapses. "Sign in with Google" button appears.
7. User Tabs to "Sign in with Google" and presses Enter (or Space).
   Screen reader announces: "Opening browser for Google sign-in…" (Status announce, Result category).
   Default browser opens to Google's OAuth consent screen.
8. User signs in to Google and grants permission in the browser.
   Browser shows a plain "Authentication successful" page and can be closed.
9. Focus returns to the dialog. Status field reads: "Signed in as user@gmail.com".
   Screen reader announces the status text (it is a live region or re-read on focus).
   Username field is now populated with the confirmed Gmail address.
10. User Tabs to "Add Account" button and presses Enter.
    Account is saved. Dialog closes. Account appears in the account list.
    Screen reader announces: "Account added." (Result category).

### Token refresh (background, transparent to user)

At IMAP connect time (startup or after idle), `ImapMailService` calls
`_oauth.GetAccessTokenAsync(account)`. The router calls `GoogleOAuthService.GetAccessTokenAsync`
which calls `UserCredential.GetAccessTokenForRequestAsync()`. If the access token is still valid
it is returned immediately. If expired, `UserCredential` silently uses the stored refresh token to
obtain a new access token — no browser, no user interaction. If the refresh token itself is
revoked (user removed app access in Google settings), the connect attempt throws
`OperationCanceledException` or a custom auth exception; `ImapMailService` surfaces this as a
connection failure and the account shows as disconnected.

---

## Infrastructure changes

| What | Change |
|------|--------|
| `AuthType` enum | Add `OAuth2Google` |
| `FeatureFlag` enum | Add `GoogleAuth` |
| `ConfigModel` | Add `GoogleClientId`, `GoogleClientSecret` |
| `GoogleOAuthService.cs` | New service, new interface `IGoogleOAuthService` |
| `OAuthRouter.cs` | New class implementing `IOAuthService`, routes by `AuthType` |
| `App.xaml.cs` | Wire `GoogleOAuthService` and `OAuthRouter`; pass router as `IOAuthService` |
| `AccountEditorViewModel` | Add `SignInGoogleCommand`, `IsGoogleOAuth` property |
| `AddAccountViewModel` | Add Google option, `OnAuthTypeChanged` case for Google host auto-fill |
| `AddAccountDialog.xaml` | New "Google OAuth" combo item, "Sign in with Google" button panel |
| `ImapMailService.cs` | Extend OAuth guard: `OAuth2Microsoft or OAuth2Google` |
| `SmtpService.cs` | Same extend |
| `StubServices.cs` (tests) | Extend `StubOAuthService` to handle `OAuth2Google` without crashing |
| NuGet | Add `Google.Apis.Auth` |

No changes to:
- `OAuthService` (Microsoft MSAL — untouched)
- `CredentialService` (already stores arbitrary secrets by key)
- `BackendKind` (Google uses `ImapSmtp` backend, no new backend type)
- F6 ring (no new primary pane)
- `CommandRegistry` (no new commands)

---

## Announcements

| Trigger | Text | Category |
|---------|------|----------|
| Sign-in button activated | "Opening browser for Google sign-in…" | `Status` |
| Sign-in completes | "Signed in as {email}" (via StatusText binding, not programmatic announce) | n/a |
| Sign-in fails | "Sign-in failed: {reason}" (via StatusText binding) | n/a |
| Account added | "Account added." | `Result` |

Status and error text are surfaced via the existing `StatusText` binding on the Add Account
dialog, which is already a visible text block. No new `AccessibilityHelper.Announce` calls are
needed for the happy path. The failure path inherits the existing pattern.

---

## Out of scope

- **Gmail REST API**: not used; all access is IMAP/SMTP with OAuth tokens.
- **Google Workspace / organisational accounts**: the consent screen and scope (`https://mail.google.com/`) work for personal Gmail. Workspace accounts may need additional admin consent — defer.
- **Multiple Google accounts**: the existing multi-account architecture handles this; each account stores its own refresh token keyed by username.
- **Editing an existing account's auth method**: changing from password to Google OAuth on an existing account is not supported — delete and re-add (same restriction as Microsoft OAuth today).
- **Token revocation detection in UI**: if a refresh token is revoked, the account shows as disconnected with a generic connection error. A specific "re-authenticate" prompt is not in scope.
- **Linux / macOS**: `CredentialService` uses Windows Credential Manager. Not in scope.
