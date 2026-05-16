using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class AddAccountViewModel : ObservableObject
{
    private readonly IImapService _imap;
    private readonly IOAuthService _oauth;

    [ObservableProperty] private string _accountName = string.Empty;
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _imapHost = string.Empty;
    [ObservableProperty] private int _imapPort = 993;
    [ObservableProperty] private bool _imapUseSsl = true;
    [ObservableProperty] private bool _imapAcceptInvalidCert = false;
    [ObservableProperty] private string _smtpHost = string.Empty;
    [ObservableProperty] private int _smtpPort = 587;
    [ObservableProperty] private bool _smtpUseSsl = false;
    [ObservableProperty] private bool _smtpAcceptInvalidCert = false;

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPasswordAuth))]
    [NotifyPropertyChangedFor(nameof(IsOAuth2))]
    [NotifyPropertyChangedFor(nameof(AuthTypeIndex))]
    private AuthType _authType = AuthType.Password;

    public bool IsPasswordAuth => AuthType == AuthType.Password;
    public bool IsOAuth2       => AuthType == AuthType.OAuth2Microsoft;

    public int AuthTypeIndex
    {
        get => AuthType == AuthType.Password ? 0 : 1;
        set => AuthType = value == 0 ? AuthType.Password : AuthType.OAuth2Microsoft;
    }

    public AddAccountViewModel(IImapService imap, IOAuthService oauth)
    {
        _imap  = imap;
        _oauth = oauth;
    }

    partial void OnAuthTypeChanged(AuthType value)
    {
        if (value == AuthType.OAuth2Microsoft)
        {
            // Personal Outlook.com IMAP/SMTP server settings
            ImapHost             = "outlook.office365.com";
            ImapPort             = 993;
            ImapUseSsl           = true;
            ImapAcceptInvalidCert = false;
            SmtpHost             = "smtp-mail.outlook.com";
            SmtpPort             = 587;
            SmtpUseSsl           = false;
            SmtpAcceptInvalidCert = false;
            Password             = string.Empty;
        }
    }

    [RelayCommand]
    private async Task SignInMicrosoftAsync()
    {
        IsBusy = true;
        StatusText = "Opening browser for Microsoft sign-in…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var tempAccount = new AccountModel { Username = Username, AuthType = AuthType.OAuth2Microsoft };
            var result = await _oauth.SignInInteractiveAsync(tempAccount, cts.Token);
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
        if (string.IsNullOrWhiteSpace(ImapHost) || string.IsNullOrWhiteSpace(Username))
        {
            StatusText = "Fill in IMAP host and username first.";
            return;
        }

        IsBusy = true;
        StatusText = "Testing connection…";
        try
        {
            var testAccount = new AccountModel
            {
                Id = Guid.NewGuid(),
                Username = Username,
                AuthType = AuthType,
                ImapHost = ImapHost,
                ImapPort = ImapPort,
                ImapUseSsl = ImapUseSsl,
                ImapAcceptInvalidCert = ImapAcceptInvalidCert
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var pwd = IsPasswordAuth ? Password : null;
            await _imap.ConnectAsync(testAccount, pwd, cts.Token);
            await _imap.DisconnectAsync(testAccount.Id, cts.Token);
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

    public AccountModel ToAccountModel() => new()
    {
        AccountName = AccountName,
        DisplayName = DisplayName,
        Username = Username,
        AuthType = AuthType,
        ImapHost = ImapHost,
        ImapPort = ImapPort,
        ImapUseSsl = ImapUseSsl,
        ImapAcceptInvalidCert = ImapAcceptInvalidCert,
        SmtpHost = SmtpHost,
        SmtpPort = SmtpPort,
        SmtpUseSsl = SmtpUseSsl,
        SmtpAcceptInvalidCert = SmtpAcceptInvalidCert,
    };
}
