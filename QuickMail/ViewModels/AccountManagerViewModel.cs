using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class AccountManagerViewModel : AccountEditorViewModel
{
    private readonly IAccountService _accountService;
    private readonly ICredentialService _credentials;
    private readonly ILocalStoreService _localStore;
    private readonly IConfigService _configService;
    private readonly IOAuthService _oauth;
    private readonly IFeatureGate _featureGate;
    private readonly IContactSyncService? _contactSync;

    [ObservableProperty]
    private ObservableCollection<AccountModel> _accounts = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    [NotifyPropertyChangedFor(nameof(CanSyncContacts))]
    private AccountModel? _selectedAccount;

    public bool IsEditing => SelectedAccount != null;

    /// <summary>
    /// Contact sync (issue #256) is offered only for OAuth accounts — the only backends that expose
    /// a contact API. Password/IMAP accounts show no checkbox at all (spec §6 Path F).
    /// </summary>
    public bool CanSyncContacts =>
        _contactSync != null &&
        SelectedAccount is { AuthType: AuthType.OAuth2Microsoft or AuthType.OAuth2Google };

    /// <summary>Bound to the "Sync contacts from this account" checkbox; mirrors the selected account.</summary>
    [ObservableProperty]
    private bool _syncContacts;

    public override bool ShowGoogleAuthOption => _featureGate.IsEnabled(FeatureFlag.GoogleAuth);

    public AccountManagerViewModel(
        IAccountService accountService,
        ICredentialService credentials,
        IMailService imap,
        IOAuthService oauth,
        ILocalStoreService localStore,
        IConfigService configService,
        IFeatureGate featureGate,
        IContactSyncService? contactSync = null)
        : base(imap, oauth)
    {
        _accountService = accountService;
        _credentials    = credentials;
        _oauth          = oauth;
        _localStore     = localStore;
        _configService  = configService;
        _featureGate    = featureGate;
        _contactSync    = contactSync;
        Accounts = new ObservableCollection<AccountModel>(accountService.LoadAccounts());
    }

    partial void OnSelectedAccountChanged(AccountModel? value)
    {
        if (value == null) return;
        BackendKind = value.BackendKind; // drives IsGraphBackend/IsImapBackend → hides auth + IMAP/SMTP for Graph
        AccountName = value.AccountName;
        DisplayName = value.DisplayName;
        Username = value.Username;
        AuthType = value.AuthType;
        Password = value.AuthType == AuthType.Password
            ? (_credentials.GetPassword(value.Id) ?? string.Empty)
            : string.Empty;
        ImapHost = value.ImapHost;
        ImapPort = value.ImapPort;
        ImapUseSsl = value.ImapUseSsl;
        ImapAcceptInvalidCert = value.ImapAcceptInvalidCert;
        SmtpHost = value.SmtpHost;
        SmtpPort = value.SmtpPort;
        SmtpUseSsl = value.SmtpUseSsl;
        SmtpAcceptInvalidCert = value.SmtpAcceptInvalidCert;
        Signature = value.Signature;
        SyncContacts = value.SyncContacts;
        StatusText = string.Empty;
    }

    public AddAccountViewModel CreateAddAccountViewModel() => new(_featureGate, MailService, OAuthService);

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
    private async Task SaveAccountAsync()
    {
        if (SelectedAccount == null) return;
        var account = SelectedAccount;
        var wasSyncingContacts = account.SyncContacts;

        account.AccountName = AccountName;
        account.DisplayName = DisplayName;
        account.Username = Username;
        // Backfill the personal-account flag when this edit re-authed (SignInMicrosoftAsync set it);
        // leave it untouched otherwise so a plain field edit doesn't wipe a prior detection (#233).
        if (IsPersonalMicrosoftAccount.HasValue)
            account.IsPersonalMicrosoftAccount = IsPersonalMicrosoftAccount;
        account.AuthType = AuthType;
        account.ImapHost = ImapHost;
        account.ImapPort = ImapPort;
        account.ImapUseSsl = ImapUseSsl;
        account.ImapAcceptInvalidCert = ImapAcceptInvalidCert;
        account.SmtpHost = SmtpHost;
        account.SmtpPort = SmtpPort;
        account.SmtpUseSsl = SmtpUseSsl;
        account.SmtpAcceptInvalidCert = SmtpAcceptInvalidCert;
        account.Signature = Signature;
        // Never enable sync for a backend without a contact API, even if the checkbox state is stale.
        account.SyncContacts = SyncContacts && CanSyncContacts;

        if (AuthType == AuthType.Password && !string.IsNullOrEmpty(Password))
            _credentials.SavePassword(account.Id, Password);

        // Contact-sync transitions (issue #256): enabling asks for read-only contact consent and
        // pulls an initial snapshot; disabling purges the account's synced contacts. Both are
        // best-effort and never block the account save.
        StatusText = "Account saved.";
        if (account.SyncContacts && !wasSyncingContacts)
            await EnableContactSyncAsync(account);
        else if (!account.SyncContacts && wasSyncingContacts && _contactSync != null)
        {
            await _contactSync.RemoveAccountContactsAsync(account.Id);
            StatusText = "Account saved. Contact sync disabled.";
        }

        _accountService.SaveAccounts([.. Accounts]);

        // Force list item refresh
        var idx = Accounts.IndexOf(account);
        if (idx >= 0)
        {
            Accounts.RemoveAt(idx);
            Accounts.Insert(idx, account);
            SelectedAccount = Accounts[idx];
        }
    }

    private async Task EnableContactSyncAsync(AccountModel account)
    {
        try
        {
            StatusText = "Requesting permission to read your contacts…";
            await _oauth.RequestContactsConsentAsync(account);
            // Pull an initial snapshot so contacts appear without waiting for the next launch.
            _contactSync?.SyncAccountAsync(account).LogFaults("contact sync after enable");
            StatusText = "Account saved. Contact sync enabled — new contact data will be available in QuickMail.";
        }
        catch (Exception ex)
        {
            // Consent declined or failed — leave sync off so we don't retry against a missing grant.
            account.SyncContacts = false;
            SyncContacts = false;
            StatusText = $"Account saved. Contact sync not enabled: {ex.Message}";
            LogService.Log($"AccountManager: contact-sync consent failed for {account.AccountLabel} — {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteAccountAsync()
    {
        if (SelectedAccount == null) return;
        var account = SelectedAccount;

        _credentials.DeletePassword(account.Id);
        Accounts.Remove(account);
        SelectedAccount = null;
        _accountService.SaveAccounts([.. Accounts]);

        var config = _configService.Load();
        if (config.Accounts.Remove(account.Id))
            _configService.Save(config);

        StatusText = "Account deleted. Cleaning up…";

        try   { await _localStore.DeleteAccountDataAsync(account.Id); }
        catch (Exception ex) { LogService.Log($"AccountManager.DeleteAccount: failed to purge mail.db — {ex.Message}"); }

        if (account.AuthType is AuthType.OAuth2Microsoft or AuthType.OAuth2Google)
        {
            try   { await _oauth.SignOutAsync(account); }
            catch (Exception ex) { LogService.Log($"AccountManager.DeleteAccount: failed OAuth sign-out — {ex.Message}"); }
        }

        // Purge this account's synced contacts (issue #256) so they don't linger after deletion.
        if (_contactSync != null)
        {
            try   { await _contactSync.RemoveAccountContactsAsync(account.Id); }
            catch (Exception ex) { LogService.Log($"AccountManager.DeleteAccount: failed to remove synced contacts — {ex.Message}"); }
        }

        StatusText = "Account deleted.";
    }

    [RelayCommand]
    private void SetDefault(AccountModel? account)
    {
        if (account == null) return;
        _accountService.SetDefaultAccount(account.Id);
        foreach (var a in Accounts)
            a.IsDefault = a.Id == account.Id;
        // Rebuild the collection so item templates re-evaluate IsDefault bindings.
        var selectedId = SelectedAccount?.Id;
        Accounts = new ObservableCollection<AccountModel>([.. Accounts]);
        SelectedAccount = selectedId.HasValue
            ? Accounts.FirstOrDefault(a => a.Id == selectedId.Value)
            : null;
        StatusText = $"{account.AccountLabel} set as default.";
    }
}
