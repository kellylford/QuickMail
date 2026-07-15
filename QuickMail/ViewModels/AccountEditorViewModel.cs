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
    private AuthType _authType = AuthType.Password;

    /// <summary>
    /// Bound to the "Sync contacts from this account" checkbox (issue #256). In the Add Account
    /// dialog it can be checked before sign-in so contact permission is requested as part of the same
    /// sign-in (Google) or granted right after account creation (Microsoft).
    /// </summary>
    [ObservableProperty]
    private bool _syncContacts;

    /// <summary>Contact sync is only offered for OAuth accounts — the backends with a contact API.</summary>
    public bool ShowContactSyncOption => IsOAuth2 || IsGoogleOAuth;

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

    [RelayCommand]
    private async Task SignInMicrosoftAsync()
    {
        IsBusy = true;
        StatusText = "Opening browser for Microsoft sign-in…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var tempAccount = new AccountModel { Username = Username, AuthType = AuthType.OAuth2Microsoft, BackendKind = BackendKind };
            var result = SyncContacts
                ? await OAuthService.SignInInteractiveWithContactsAsync(tempAccount, cts.Token)
                : await OAuthService.SignInInteractiveAsync(tempAccount, cts.Token);
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
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var tempAccount = new AccountModel { Username = Username, AuthType = AuthType.OAuth2Google, BackendKind = BackendKind.ImapSmtp };
            // When contact sync is requested, Google grants mail + contacts in this single consent.
            var result = SyncContacts
                ? await OAuthService.SignInInteractiveWithContactsAsync(tempAccount, cts.Token)
                : await OAuthService.SignInInteractiveAsync(tempAccount, cts.Token);
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
