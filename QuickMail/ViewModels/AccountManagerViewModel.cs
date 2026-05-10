using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class AccountManagerViewModel : ObservableObject
{
    private readonly IAccountService _accountService;
    private readonly ICredentialService _credentials;
    private readonly IImapService _imap;

    [ObservableProperty]
    private ObservableCollection<AccountModel> _accounts = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    private AccountModel? _selectedAccount;

    // Editing form fields (bound to a working copy, not directly to SelectedAccount)
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

    public bool IsEditing => SelectedAccount != null;

    public AccountManagerViewModel(IAccountService accountService, ICredentialService credentials, IImapService imap)
    {
        _accountService = accountService;
        _credentials = credentials;
        _imap = imap;
        Accounts = new ObservableCollection<AccountModel>(accountService.LoadAccounts());
    }

    partial void OnSelectedAccountChanged(AccountModel? value)
    {
        if (value == null) return;
        DisplayName = value.DisplayName;
        Username = value.Username;
        Password = _credentials.GetPassword(value.Id) ?? string.Empty;
        ImapHost = value.ImapHost;
        ImapPort = value.ImapPort;
        ImapUseSsl = value.ImapUseSsl;
        ImapAcceptInvalidCert = value.ImapAcceptInvalidCert;
        SmtpHost = value.SmtpHost;
        SmtpPort = value.SmtpPort;
        SmtpUseSsl = value.SmtpUseSsl;
        SmtpAcceptInvalidCert = value.SmtpAcceptInvalidCert;
        StatusText = string.Empty;
    }

    public AddAccountViewModel CreateAddAccountViewModel() => new(_imap);

    public void CommitNewAccount(AccountModel account, string password)
    {
        if (!string.IsNullOrEmpty(password))
            _credentials.SavePassword(account.Id, password);
        Accounts.Add(account);
        _accountService.SaveAccounts([.. Accounts]);
        SelectedAccount = account;
        StatusText = "Account added.";
    }

    [RelayCommand]
    private void SaveAccount()
    {
        if (SelectedAccount == null) return;

        SelectedAccount.DisplayName = DisplayName;
        SelectedAccount.Username = Username;
        SelectedAccount.ImapHost = ImapHost;
        SelectedAccount.ImapPort = ImapPort;
        SelectedAccount.ImapUseSsl = ImapUseSsl;
        SelectedAccount.ImapAcceptInvalidCert = ImapAcceptInvalidCert;
        SelectedAccount.SmtpHost = SmtpHost;
        SelectedAccount.SmtpPort = SmtpPort;
        SelectedAccount.SmtpUseSsl = SmtpUseSsl;
        SelectedAccount.SmtpAcceptInvalidCert = SmtpAcceptInvalidCert;

        if (!string.IsNullOrEmpty(Password))
            _credentials.SavePassword(SelectedAccount.Id, Password);

        _accountService.SaveAccounts([.. Accounts]);
        StatusText = "Account saved.";

        // Force list item refresh
        var idx = Accounts.IndexOf(SelectedAccount);
        if (idx >= 0)
        {
            Accounts.RemoveAt(idx);
            Accounts.Insert(idx, SelectedAccount);
            SelectedAccount = Accounts[idx];
        }
    }

    [RelayCommand]
    private void DeleteAccount()
    {
        if (SelectedAccount == null) return;
        _credentials.DeletePassword(SelectedAccount.Id);
        Accounts.Remove(SelectedAccount);
        SelectedAccount = null;
        _accountService.SaveAccounts([.. Accounts]);
        StatusText = "Account deleted.";
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
                Id = SelectedAccount?.Id ?? Guid.NewGuid(),
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
}
