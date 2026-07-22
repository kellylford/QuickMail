using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>
/// Base class for account-editor ViewModels, containing the shared form fields,
/// auth-type helpers, and OAuth / connection-test commands.
/// </summary>
public abstract partial class AccountEditorViewModel : ObservableObject
{
    protected IMailService MailService { get; }
    protected IOAuthService OAuthService { get; }

    [ObservableProperty] private string _accountName = string.Empty;
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsICloudAccount))]
    [NotifyPropertyChangedFor(nameof(ShowContactSyncOption))]
    [NotifyPropertyChangedFor(nameof(ShowCalendarSyncOption))]
    private string _imapHost = string.Empty;
    [ObservableProperty] private int    _imapPort = 993;
    [ObservableProperty] private bool   _imapUseSsl = true;
    [ObservableProperty] private bool   _imapAcceptInvalidCert = false;
    [ObservableProperty] private string _smtpHost = string.Empty;
    [ObservableProperty] private int    _smtpPort = 587;
    [ObservableProperty] private bool   _smtpUseSsl = false;
    [ObservableProperty] private bool   _smtpAcceptInvalidCert = false;
    [ObservableProperty] private string _signature = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPasswordAuth))]
    [NotifyPropertyChangedFor(nameof(IsOAuth2))]
    [NotifyPropertyChangedFor(nameof(IsGoogleOAuth))]
    [NotifyPropertyChangedFor(nameof(AuthTypeIndex))]
    [NotifyPropertyChangedFor(nameof(ShowContactSyncOption))]
    [NotifyPropertyChangedFor(nameof(ShowCalendarSyncOption))]
    private AuthType _authType = AuthType.Password;

    /// <summary>
    /// Bound to the "Sync contacts from this account" checkbox (issue #256). In the Add Account
    /// dialog it can be checked before sign-in so contact permission is requested as part of the same
    /// sign-in (Google) or granted right after account creation (Microsoft).
    /// </summary>
    [ObservableProperty]
    private bool _syncContacts;

    /// <summary>
    /// Contact sync is offered for Microsoft and Google (OAuth contact APIs) plus iCloud (CardDAV
    /// via the account's app-specific password) — a superset matching calendar sync.
    /// </summary>
    public bool ShowContactSyncOption => IsOAuth2 || IsGoogleOAuth || IsICloudAccount;

    /// <summary>
    /// Bound to the "Sync calendar from this account" checkbox (#282). Like contact sync it can be
    /// checked before sign-in. Offered for a superset of contact sync: Microsoft and Google
    /// (calendar API) plus iCloud (CalDAV via the account's app-specific password).
    /// </summary>
    [ObservableProperty]
    private bool _syncCalendar;

    /// <summary>Calendar sync is offered for Microsoft, Google, and iCloud accounts.</summary>
    public bool ShowCalendarSyncOption => IsOAuth2 || IsGoogleOAuth || IsICloudAccount;

    /// <summary>True when the IMAP host matches iCloud — drives the app-specific password hint.</summary>
    public bool IsICloudAccount => ImapHost.Equals("imap.mail.me.com", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGraphBackend))]
    [NotifyPropertyChangedFor(nameof(IsImapBackend))]
    private BackendKind _backendKind = BackendKind.ImapSmtp;

    /// <summary>True when this account uses the Microsoft Graph backend (drives IMAP/SMTP field visibility).</summary>
    public bool IsGraphBackend => BackendKind == BackendKind.MicrosoftGraph;

    /// <summary>True when this account uses the standard IMAP/SMTP backend.</summary>
    public bool IsImapBackend => BackendKind == BackendKind.ImapSmtp;

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool   _isBusy = false;

    public bool IsPasswordAuth => AuthType == AuthType.Password;
    public bool IsOAuth2       => AuthType == AuthType.OAuth2Microsoft;
    public bool IsGoogleOAuth  => AuthType == AuthType.OAuth2Google;

    /// <summary>
    /// Whether the Google OAuth option is available in this editor context.
    /// Derived classes override to reflect their feature-gate state.
    /// </summary>
    public virtual bool ShowGoogleAuthOption => false;

    public int AuthTypeIndex
    {
        get => AuthType switch
        {
            AuthType.OAuth2Microsoft => 1,
            AuthType.OAuth2Google    => 2,
            _                        => 0,
        };
        set => AuthType = value switch
        {
            1 => AuthType.OAuth2Microsoft,
            2 => AuthType.OAuth2Google,
            _ => AuthType.Password,
        };
    }

    protected AccountEditorViewModel(IMailService mailService, IOAuthService oauth)
    {
        MailService = mailService;
        OAuthService = oauth;
    }

    /// <summary>
    /// Called when <see cref="AuthType"/> changes. Override in derived classes
    /// to react (e.g. auto-fill server settings for OAuth).
    /// </summary>
    protected virtual void OnAuthTypeChangedInternal(AuthType value) { }

    partial void OnAuthTypeChanged(AuthType value) => OnAuthTypeChangedInternal(value);

    /// <summary>
    /// Detected from the token at Microsoft sign-in (null until signed in). Carried to
    /// <see cref="AccountModel.IsPersonalMicrosoftAccount"/> by the derived VM's ToAccountModel so
    /// scope selection is correct even for personal accounts on custom domains (#233).
    /// </summary>
    public bool? IsPersonalMicrosoftAccount { get; private set; }

    /// <summary>
    /// Raised when interactive sign-in completed as a DIFFERENT identity than the one entered (#202) —
    /// typically an administrator signing in to grant consent in an admin-approval tenant. The View
    /// surfaces a focus-grabbing warning in response; the account is deliberately NOT rebound to the
    /// signed-in identity. Args are (enteredUsername, actualSignedInUsername).
    /// </summary>
    public event Action<string, string>? SignInIdentityMismatch;

    /// <summary>
    /// True when a non-empty entered username differs (case-insensitively) from the username that
    /// actually signed in — the wrong-identity case #202 guards against. Raises
    /// <see cref="SignInIdentityMismatch"/> as a side effect so the View can warn. An empty entered
    /// username (the user let the provider choose the account) is never treated as a mismatch.
    /// </summary>
    private bool IsSignInIdentityMismatch(string entered, string actual)
    {
        if (string.IsNullOrWhiteSpace(entered)) return false;
        if (string.Equals(entered.Trim(), actual, StringComparison.OrdinalIgnoreCase)) return false;
        SignInIdentityMismatch?.Invoke(entered.Trim(), actual);
        return true;
    }

    [RelayCommand]
    private async Task SignInMicrosoftAsync()
    {
        IsBusy = true;
        StatusText = "Opening browser for Microsoft sign-in…";
        try
        {
            // #203: no app-imposed timeout. Sign-in renders in the embedded window the user can close
            // to cancel (MSAL treats the close as a cancellation), so there is no reason to force-cancel
            // after a few minutes — admin-consent tenants and screen-reader navigation legitimately take
            // longer than the old 3-minute cutoff, which tore the window down mid-sign-in.
            var entered = Username;
            var tempAccount = new AccountModel { Username = Username, AuthType = AuthType.OAuth2Microsoft, BackendKind = BackendKind };
            var result = SyncContacts
                ? await OAuthService.SignInInteractiveWithContactsAsync(tempAccount, CancellationToken.None)
                : await OAuthService.SignInInteractiveAsync(tempAccount, CancellationToken.None);

            // #202: guard against a DIFFERENT identity completing sign-in than the one entered —
            // typically an admin signing in to approve consent in an admin-approval tenant. Adopting
            // that identity silently rebinds the account to the admin's mailbox (often not REST-enabled)
            // and loses the intended user. Keep the entered username and warn instead of overwriting.
            if (IsSignInIdentityMismatch(entered, result.Username))
            {
                StatusText = $"Signed in as {result.Username}, not {entered}. Account unchanged — please sign in as {entered}.";
                return;
            }

            Username = result.Username;
            IsPersonalMicrosoftAccount = result.IsPersonalMicrosoftAccount;
            StatusText = $"Signed in as {result.Username}";
        }
        catch (Exception ex)
        {
            StatusText = $"Sign-in failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SignInGoogleAsync()
    {
        IsBusy = true;
        StatusText = "Opening browser for Google sign-in…";
        try
        {
            // #203: no app-imposed timeout — the user cancels by closing the sign-in window.
            var entered = Username;
            var tempAccount = new AccountModel { Username = Username, AuthType = AuthType.OAuth2Google, BackendKind = BackendKind.ImapSmtp };
            // When contact sync is requested, Google grants mail + contacts in this single consent.
            var result = SyncContacts
                ? await OAuthService.SignInInteractiveWithContactsAsync(tempAccount, CancellationToken.None)
                : await OAuthService.SignInInteractiveAsync(tempAccount, CancellationToken.None);

            // #202: same wrong-identity guard as the Microsoft path — never silently adopt a different
            // account than the one entered.
            if (IsSignInIdentityMismatch(entered, result.Username))
            {
                StatusText = $"Signed in as {result.Username}, not {entered}. Account unchanged — please sign in as {entered}.";
                return;
            }

            Username = result.Username;
            StatusText = $"Signed in as {result.Username}";
        }
        catch (Exception ex)
        {
            StatusText = $"Sign-in failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        // Branch on backend before the IMAP-specific validation. Graph accounts have no IMAP
        // host/port; their connectivity probe (GET /me) lands with the Graph backend in PR 4.
        if (IsGraphBackend)
        {
            // A Graph account is verified by the OAuth sign-in itself (it acquires a Graph token and
            // populates the username from /me). There is no separate host/port to probe.
            StatusText = "For Microsoft 365, use Sign in with Microsoft to verify access.";
            return;
        }

        if (IsGoogleOAuth)
        {
            StatusText = "For Gmail, use Sign in with Google to verify access.";
            return;
        }

        if (string.IsNullOrWhiteSpace(ImapHost) || string.IsNullOrWhiteSpace(Username))
        {
            StatusText = "Fill in IMAP host and username first.";
            return;
        }

        IsBusy = true;
        StatusText = "Testing connection…";
        var testAccountId = Guid.NewGuid();
        try
        {
            var testAccount = new AccountModel
            {
                Id = testAccountId,
                AccountName = $"Test ({Username})",
                DisplayName = Username,
                Username = Username,
                AuthType = AuthType,
                BackendKind = BackendKind,
                ImapHost = ImapHost,
                ImapPort = ImapPort,
                ImapUseSsl = ImapUseSsl,
                ImapAcceptInvalidCert = ImapAcceptInvalidCert,
                SmtpHost = SmtpHost,
                SmtpPort = SmtpPort,
                SmtpUseSsl = SmtpUseSsl,
                SmtpAcceptInvalidCert = SmtpAcceptInvalidCert,
                Signature = Signature
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var pwd = IsPasswordAuth ? Password : null;
            await MailService.ConnectAsync(testAccount, pwd, cts.Token);
            await MailService.DisconnectAsync(testAccountId, cts.Token);
            StatusText = "Connection successful!";
        }
        catch (Exception ex)
        {
            StatusText = $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
