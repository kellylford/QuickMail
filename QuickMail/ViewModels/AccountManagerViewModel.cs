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
    private readonly IGraphCalendarSyncService? _graphCalendarSync;

    [ObservableProperty]
    private ObservableCollection<AccountModel> _accounts = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    [NotifyPropertyChangedFor(nameof(CanSyncContacts))]
    [NotifyPropertyChangedFor(nameof(CanSyncCalendar))]
    private AccountModel? _selectedAccount;

    public bool IsEditing => SelectedAccount != null;

    /// <summary>
    /// Contact sync (issue #256) is offered for Microsoft and Google (OAuth contact APIs), plus
    /// iCloud accounts (CardDAV via the account's app-specific password) — a superset matching
    /// calendar sync. Other password/IMAP accounts show no checkbox.
    /// </summary>
    public bool CanSyncContacts =>
        _contactSync != null && SelectedAccount is { } acct &&
        (acct.AuthType is AuthType.OAuth2Microsoft or AuthType.OAuth2Google
         || acct.ImapHost.Equals("imap.mail.me.com", StringComparison.OrdinalIgnoreCase));

    // SyncContacts / SyncCalendar are inherited from AccountEditorViewModel (shared with Add Account).

    /// <summary>
    /// Calendar sync (#282) is offered for a superset of contact sync: Microsoft and Google (calendar
    /// API), plus iCloud accounts (CalDAV via the account's app-specific password). Other password/IMAP
    /// accounts show no checkbox.
    /// </summary>
    public bool CanSyncCalendar =>
        _graphCalendarSync != null && SelectedAccount is { } acct &&
        (acct.AuthType is AuthType.OAuth2Microsoft or AuthType.OAuth2Google
         || acct.ImapHost.Equals("imap.mail.me.com", StringComparison.OrdinalIgnoreCase));

    public override bool ShowGoogleAuthOption => _featureGate.IsEnabled(FeatureFlag.GoogleAuth);

    public AccountManagerViewModel(
        IAccountService accountService,
        ICredentialService credentials,
        IMailService imap,
        IOAuthService oauth,
        ILocalStoreService localStore,
        IConfigService configService,
        IFeatureGate featureGate,
        IContactSyncService? contactSync = null,
        IGraphCalendarSyncService? graphCalendarSync = null)
        : base(imap, oauth)
    {
        _accountService = accountService;
        _credentials    = credentials;
        _oauth          = oauth;
        _localStore     = localStore;
        _configService  = configService;
        _featureGate    = featureGate;
        _contactSync    = contactSync;
        _graphCalendarSync = graphCalendarSync;
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
        SyncCalendar = value.SyncCalendar;
        StatusText = string.Empty;
    }

    /// <summary>
    /// Applies a contact-sync toggle immediately (issue #256) — there is no Save step for it.
    /// Enabling requests read-only contact consent, persists the flag, and pulls an initial snapshot;
    /// disabling purges the account's synced contacts. On failure the checkbox is reverted. Called
    /// from the checkbox's Click handler (Click fires only on real user interaction, never on the
    /// programmatic assignment in <see cref="OnSelectedAccountChanged"/>). Returns without side
    /// effects if there is no selected account. <paramref name="enabled"/> is the new checkbox state.
    /// </summary>
    public async Task SetContactSyncAsync(bool enabled)
    {
        if (SelectedAccount is not { } account) return;

        try
        {
            if (enabled)
            {
                if (!CanSyncContacts) return; // box is hidden for these accounts; defensive
                account.SyncContacts = true;
                // Both OAuth providers need an explicit contact-scope consent here: unlike calendar
                // (Google's calendar scope rides along with mail sign-in), the Google CONTACTS scope
                // is NOT in the base sign-in, so toggling contacts on for a Google account must
                // request it (RequestContactsConsentAsync re-authorizes for Google, acquires the
                // Graph scope for Microsoft). iCloud uses the account's app-specific password over
                // CardDAV — no OAuth, no prompt.
                if (account.AuthType is AuthType.OAuth2Microsoft or AuthType.OAuth2Google)
                {
                    StatusText = "Requesting permission to read your contacts…";
                    await _oauth.RequestContactsConsentAsync(account);
                }
                _accountService.SaveAccounts([.. Accounts]);
                // Pull an initial snapshot so contacts appear without waiting for the next launch.
                _contactSync?.SyncAccountAsync(account).LogFaults("contact sync after enable");
                StatusText = "Contact sync enabled — new contact data will be available in QuickMail.";
            }
            else
            {
                account.SyncContacts = false;
                _accountService.SaveAccounts([.. Accounts]);
                if (_contactSync != null)
                    await _contactSync.RemoveAccountContactsAsync(account.Id);
                StatusText = "Contact sync disabled.";
            }
        }
        catch (Exception ex)
        {
            // Consent declined or failed — revert the checkbox and leave sync off so we don't retry
            // against a missing grant.
            account.SyncContacts = false;
            SyncContacts = false;
            _accountService.SaveAccounts([.. Accounts]);
            StatusText = $"Contact sync not enabled: {ex.Message}";
            LogService.Log($"AccountManager: contact-sync enable failed for {account.AccountLabel} — {ex.Message}");
        }
    }

    /// <summary>
    /// Applies a calendar-sync toggle immediately (#282) — no Save step, mirroring contact sync.
    /// Enabling requests calendar consent where needed (Microsoft; Google/iCloud need none), persists
    /// the flag, and pulls an initial snapshot; disabling removes that account's synced calendar rows.
    /// On failure the checkbox reverts. Called from the checkbox's Click handler (never fires on the
    /// programmatic assignment in <see cref="OnSelectedAccountChanged"/>).
    /// </summary>
    public async Task SetCalendarSyncAsync(bool enabled)
    {
        if (SelectedAccount is not { } account) return;

        try
        {
            if (enabled)
            {
                if (!CanSyncCalendar) return; // box is hidden for these accounts; defensive
                account.SyncCalendar = true;
                // Only Microsoft needs an explicit calendar-scope consent; Google's is granted at
                // mail sign-in and iCloud uses the account's app-specific password — neither prompts,
                // so don't show a "requesting permission" message for them.
                if (account.AuthType == AuthType.OAuth2Microsoft)
                {
                    StatusText = "Requesting permission to read your calendar…";
                    await _oauth.RequestCalendarConsentAsync(account);
                }
                _accountService.SaveAccounts([.. Accounts]);
                // Pull an initial snapshot so events appear without waiting for the next sync pass.
                _graphCalendarSync?.SyncAccountCalendarAsync(account).LogFaults("calendar sync after enable");
                StatusText = "Calendar sync enabled — this account's events will appear in the Calendar.";
            }
            else
            {
                account.SyncCalendar = false;
                _accountService.SaveAccounts([.. Accounts]);
                if (_graphCalendarSync != null)
                    await _graphCalendarSync.RemoveAccountCalendarAsync(account.Id);
                StatusText = "Calendar sync disabled.";
            }
        }
        catch (Exception ex)
        {
            // Consent declined or failed — revert the checkbox and leave sync off so we don't retry
            // against a missing grant.
            account.SyncCalendar = false;
            SyncCalendar = false;
            _accountService.SaveAccounts([.. Accounts]);
            StatusText = $"Calendar sync not enabled: {ex.Message}";
            LogService.Log($"AccountManager: calendar-sync enable failed for {account.AccountLabel} — {ex.Message}");
        }
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

        // If the user checked "sync contacts" while adding the account (issue #256), finish enabling
        // it: Google already granted contacts during sign-in, Microsoft needs the read-only contact
        // scope granted now (silent when the app registration declares it). Then pull the first batch.
        if (account.SyncContacts && _contactSync != null && _contactSync.CanSync(account))
            _ = FinishNewAccountContactSyncAsync(account);

        // Same for calendar (#282): Microsoft needs a calendar-scope grant now; Google already has it
        // from sign-in; iCloud uses the app-specific password just saved above. Then pull the first batch.
        if (account.SyncCalendar && _graphCalendarSync != null)
            _ = FinishNewAccountCalendarSyncAsync(account);
    }

    private async Task FinishNewAccountContactSyncAsync(AccountModel account)
    {
        try
        {
            if (account.AuthType == AuthType.OAuth2Microsoft)
                await _oauth.RequestContactsConsentAsync(account);
            _contactSync!.SyncAccountAsync(account).LogFaults("initial contact sync for new account");
            StatusText = "Account added. Contact sync enabled — new contact data will be available in QuickMail.";
        }
        catch (Exception ex)
        {
            account.SyncContacts = false;
            _accountService.SaveAccounts([.. Accounts]);
            StatusText = $"Account added, but contact sync couldn't be enabled: {ex.Message}";
            LogService.Log($"AccountManager: new-account contact sync failed for {account.AccountLabel} — {ex.Message}");
        }
    }

    private async Task FinishNewAccountCalendarSyncAsync(AccountModel account)
    {
        try
        {
            if (account.AuthType == AuthType.OAuth2Microsoft)
                await _oauth.RequestCalendarConsentAsync(account);
            _graphCalendarSync!.SyncAccountCalendarAsync(account).LogFaults("initial calendar sync for new account");
            StatusText = "Account added. Calendar sync enabled — this account's events will appear in the Calendar.";
        }
        catch (Exception ex)
        {
            account.SyncCalendar = false;
            _accountService.SaveAccounts([.. Accounts]);
            StatusText = $"Account added, but calendar sync couldn't be enabled: {ex.Message}";
            LogService.Log($"AccountManager: new-account calendar sync failed for {account.AccountLabel} — {ex.Message}");
        }
    }

    [RelayCommand]
    private void SaveAccount()
    {
        if (SelectedAccount == null) return;
        var account = SelectedAccount;

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
        // SyncContacts is NOT touched here — the checkbox applies itself immediately via
        // OnSyncContactsChanged (consent + persist), so Save never enables/disables it.

        if (AuthType == AuthType.Password && !string.IsNullOrEmpty(Password))
            _credentials.SavePassword(account.Id, Password);

        _accountService.SaveAccounts([.. Accounts]);
        StatusText = "Account saved.";

        // Force list item refresh
        var idx = Accounts.IndexOf(account);
        if (idx >= 0)
        {
            Accounts.RemoveAt(idx);
            Accounts.Insert(idx, account);
            SelectedAccount = Accounts[idx];
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
