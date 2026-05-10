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

    public AddAccountViewModel(IImapService imap)
    {
        _imap = imap;
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
                ImapHost = ImapHost,
                ImapPort = ImapPort,
                ImapUseSsl = ImapUseSsl,
                ImapAcceptInvalidCert = ImapAcceptInvalidCert
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _imap.ConnectAsync(testAccount, Password, cts.Token);
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
        DisplayName = DisplayName,
        Username = Username,
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
