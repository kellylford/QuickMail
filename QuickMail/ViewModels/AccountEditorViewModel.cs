using System;
using System.Collections.Generic;
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
    protected IProviderCatalog Catalog { get; }

    /// <summary>
    /// Used only to verify outgoing settings from Test Connection. Optional: when absent the SMTP
    /// leg reports "not checked" rather than silently claiming success.
    /// </summary>
    protected ISendMailService? SendMailService { get; }

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
    [NotifyPropertyChangedFor(nameof(ShowAppPasswordHint))]
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

    /// <summary>
    /// True when this is an iCloud account — drives contact/calendar sync eligibility. Checks the
    /// selected provider first, and still falls back to the IMAP host so a user who picked "Other"
    /// and typed the iCloud host by hand keeps the same options they had before the provider picker
    /// existed.
    /// </summary>
    public bool IsICloudAccount =>
        SelectedProvider?.Id == ProviderCatalog.ICloudId
        || string.Equals(ImapHost, Catalog.ById(ProviderCatalog.ICloudId)?.ImapHost,
                         StringComparison.OrdinalIgnoreCase);

    // ── Provider selection and progressive disclosure ────────────────────────────

    /// <summary>Providers offered in the picker, in display order.</summary>
    public IReadOnlyList<MailProvider> Providers => Catalog.All;

    /// <summary>
    /// The provider this account is being created from or was created with. Selecting one fills
    /// every server field, which is what keeps the Advanced settings expander closed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsICloudAccount))]
    [NotifyPropertyChangedFor(nameof(ShowContactSyncOption))]
    [NotifyPropertyChangedFor(nameof(ShowCalendarSyncOption))]
    [NotifyPropertyChangedFor(nameof(AppPasswordHint))]
    [NotifyPropertyChangedFor(nameof(AppPasswordUrl))]
    [NotifyPropertyChangedFor(nameof(ShowAppPasswordHint))]
    private MailProvider? _selectedProvider;

    /// <summary>
    /// Whether the Advanced settings expander is open. Collapsed for a recognized provider, opened
    /// when the user picks "Other" or discovery comes back empty and they must type settings in.
    /// </summary>
    [ObservableProperty]
    private bool _isAdvancedExpanded;

    /// <summary>
    /// Set once the user edits a server field by hand. Guards their edits: a later provider match or
    /// discovery result must not silently overwrite hosts they deliberately typed.
    /// </summary>
    [ObservableProperty]
    private bool _hostsUserEdited;

    /// <summary>Warning shown above the password box for providers that need an app-specific password.</summary>
    public string? AppPasswordHint => SelectedProvider?.AppPasswordHint;

    /// <summary>Where to generate that app password. Rendered as a link beside the hint.</summary>
    public string? AppPasswordUrl => SelectedProvider?.AppPasswordUrl;

    /// <summary>
    /// The hint only makes sense when a password is actually being entered — an OAuth account has no
    /// password box to warn about.
    /// </summary>
    public bool ShowAppPasswordHint => IsPasswordAuth && SelectedProvider?.RequiresAppPassword == true;

    /// <summary>
    /// Applies a provider's known settings to the form. Returns false without touching anything for
    /// the "Other" entry, which by definition has no settings to apply.
    /// </summary>
    /// <param name="collapseAdvanced">
    /// False when the user is already inside Advanced settings. Collapsing an expander that
    /// currently holds keyboard focus removes the focused element from the visual tree and strands
    /// focus on the window with no announcement.
    /// </param>
    protected bool ApplyProvider(MailProvider? provider, bool collapseAdvanced = true)
    {
        if (provider is null) return false;

        if (provider.IsOther)
        {
            // Nothing to fill in — open Advanced so the user can enter servers themselves. Their
            // existing field values are deliberately left alone.
            IsAdvancedExpanded = true;
            return false;
        }

        var wasApplying = _applyingSettings;
        _applyingSettings = true;
        try
        {
            // ORDER MATTERS: BackendKind is assigned before AuthType because OnAuthTypeChangedInternal
            // reads BackendKind. This is the same ordering hazard the old backend combo documented.
            BackendKind = provider.DefaultBackend;
            AuthType = provider.DefaultAuthType;

            if (BackendKind == BackendKind.MicrosoftGraph)
            {
                // Graph authenticates via OAuth and needs no host configuration at all.
                ImapHost = string.Empty;
                SmtpHost = string.Empty;
                Password = string.Empty;
            }
            else
            {
                ImapHost = provider.ImapHost;
                ImapPort = provider.ImapPort;
                ImapUseSsl = provider.ImapUseSsl;
                ImapAcceptInvalidCert = false;
                SmtpHost = provider.SmtpHost;
                SmtpPort = provider.SmtpPort;
                SmtpUseSsl = provider.SmtpUseSsl;
                SmtpAcceptInvalidCert = false;
            }

            if (AuthType != AuthType.Password) ClearPassword();
        }
        finally
        {
            _applyingSettings = wasApplying;
        }

        // The settings came from the catalog, not the user, so a later match may still replace them.
        HostsUserEdited = false;
        if (collapseAdvanced) IsAdvancedExpanded = false;
        return true;
    }

    /// <summary>
    /// The address no longer matches any known provider — because the user is editing it, or fixing
    /// a typo. Drops back to "Other" and clears the settings the previous provider filled in, so a
    /// corrected address can never be saved against the old provider's servers. Silent: no expander
    /// change, because this fires on keystrokes.
    /// </summary>
    protected void ResetToUnknownProvider()
    {
        var previous = SelectedProvider;
        if (previous is null || previous.IsOther) return;

        _applyingSettings = true;
        try
        {
            ImapHost = string.Empty;
            ImapPort = 993;
            ImapUseSsl = true;
            ImapAcceptInvalidCert = false;
            SmtpHost = string.Empty;
            SmtpPort = 587;
            SmtpUseSsl = false;
            SmtpAcceptInvalidCert = false;
            // The account name is never touched here — nothing writes it but the user.
        }
        finally
        {
            _applyingSettings = false;
        }

        HostsUserEdited = false;

        // Assigning the provider runs the normal "user picked Other" path, which opens Advanced
        // settings. That is right when the user chose Other deliberately and wrong here — this fires
        // on every keystroke of an unmatched address, and a pile of new fields appearing mid-word is
        // hostile, especially with a screen reader. Put the expander back the way the user had it.
        var wasExpanded = IsAdvancedExpanded;
        SelectedProvider = Catalog.Other;
        IsAdvancedExpanded = wasExpanded;
    }

    /// <summary>
    /// Applies settings found by <see cref="IAutoDiscoverService"/>. Refuses to overwrite hosts the
    /// user typed themselves. Returns false when the result was declined for that reason.
    /// </summary>
    protected bool ApplyDiscovered(DiscoveredSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (HostsUserEdited) return false;

        // A catalog-backed result carries a provider, which brings its auth type and app-password
        // hint along. A network-discovered one does not, so it stays on "Other" with password auth.
        var provider = Catalog.ById(settings.ProviderId);
        if (provider is not null)
        {
            // Assign and stop. Assigning runs OnSelectedProviderChangedInternal, which applies the
            // provider AND any backend preference the derived VM layers on top — a work-or-school
            // Microsoft tenant moving to Graph, for instance. Calling ApplyProvider again here would
            // re-apply the provider's DEFAULT backend and quietly undo that.
            if (!ReferenceEquals(provider, SelectedProvider))
            {
                SelectedProvider = provider;
                return true;
            }

            // Already selected, so no change notification will fire — apply it directly.
            return ApplyProvider(provider);
        }

        var wasApplying = _applyingSettings;
        _applyingSettings = true;
        try
        {
            ImapHost = settings.ImapHost;
            ImapPort = settings.ImapPort;
            ImapUseSsl = settings.ImapUseSsl;
            ImapAcceptInvalidCert = false;
            SmtpHost = settings.SmtpHost;
            SmtpPort = settings.SmtpPort;
            SmtpUseSsl = settings.SmtpUseSsl;
            SmtpAcceptInvalidCert = false;
        }
        finally
        {
            _applyingSettings = wasApplying;
        }

        HostsUserEdited = false;
        IsAdvancedExpanded = false;
        return true;
    }

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

    protected AccountEditorViewModel(
        IMailService mailService,
        IOAuthService oauth,
        IProviderCatalog catalog,
        ISendMailService? sendMail = null)
    {
        MailService = mailService;
        OAuthService = oauth;
        Catalog = catalog;
        SendMailService = sendMail;
        // Assign the backing field, not the property: going through the setter would fire
        // OnSelectedProviderChangedInternal, whose "Other" branch expands Advanced settings — so a
        // brand-new dialog would open with every server field showing, which is the opposite of the
        // point. Derived VMs move the selection once they know the account.
        _selectedProvider = catalog.Other;
    }

    /// <summary>
    /// Clears the host fields for a backend that has no use for them (Graph). Wrapped so the clear is
    /// not mistaken for the user hand-editing a host — otherwise switching to Graph and back would
    /// leave the fields permanently blank, because the restore path refuses to overwrite user edits.
    /// </summary>
    protected void ClearHostsForGraph()
    {
        var wasApplying = _applyingSettings;
        _applyingSettings = true;
        try
        {
            ImapHost = string.Empty;
            SmtpHost = string.Empty;
            ClearPassword();
        }
        finally
        {
            _applyingSettings = wasApplying;
        }
    }

    /// <summary>
    /// Raised when the ViewModel clears the password by itself — switching to an OAuth provider, for
    /// instance. A <c>PasswordBox</c> cannot be data-bound, so the View has to be told, or it keeps
    /// showing dots for a password that no longer exists and the account is saved without one.
    /// </summary>
    public event Action? PasswordCleared;

    private void ClearPassword()
    {
        if (Password.Length == 0) return;
        Password = string.Empty;
        PasswordCleared?.Invoke();
    }

    /// <summary>
    /// True while <see cref="ApplyProvider"/> / <see cref="ApplyDiscovered"/> are writing the form,
    /// so those writes are not mistaken for the user hand-editing a host.
    /// </summary>
    private bool _applyingSettings;

    // Every server field counts, not just the host names: a user who changes only the port or turns
    // SSL off has made a deliberate choice, and a later provider match must not silently undo it.
    partial void OnImapHostChanged(string value) => NoteHostEdit();
    partial void OnSmtpHostChanged(string value) => NoteHostEdit();
    partial void OnImapPortChanged(int value) => NoteHostEdit();
    partial void OnSmtpPortChanged(int value) => NoteHostEdit();
    partial void OnImapUseSslChanged(bool value) => NoteHostEdit();
    partial void OnSmtpUseSslChanged(bool value) => NoteHostEdit();

    private void NoteHostEdit()
    {
        if (!_applyingSettings) HostsUserEdited = true;
    }

    /// <summary>
    /// Called when <see cref="AuthType"/> changes. Override in derived classes
    /// to react (e.g. auto-fill server settings for OAuth).
    /// </summary>
    protected virtual void OnAuthTypeChangedInternal(AuthType value) { }

    partial void OnAuthTypeChanged(AuthType value) => OnAuthTypeChangedInternal(value);

    /// <summary>
    /// Called when <see cref="SelectedProvider"/> changes. Overridden in AddAccountViewModel to apply
    /// the provider's settings; the Manager dialog only reflects the resolved provider, so its base
    /// implementation does nothing. Uses the same *Internal indirection as
    /// <see cref="OnAuthTypeChangedInternal"/> because the generated partial method belongs to this
    /// class and cannot be implemented by a derived one.
    /// </summary>
    protected virtual void OnSelectedProviderChangedInternal(MailProvider? value) { }

    partial void OnSelectedProviderChanged(MailProvider? value) => OnSelectedProviderChangedInternal(value);

    /// <summary>
    /// Called when <see cref="Username"/> changes. AddAccountViewModel overrides it to match the
    /// address against the built-in provider table — an offline lookup, so it is safe to run on
    /// every keystroke. Deliberately in the ViewModel rather than a View text-changed handler: the
    /// provider must follow the address whoever sets it, including from a test.
    /// </summary>
    protected virtual void OnUsernameChangedInternal(string value) { }

    partial void OnUsernameChanged(string value) => OnUsernameChangedInternal(value);

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

    /// <summary>
    /// Checks the form is complete enough to save. Matters more than it used to: the server fields
    /// now live behind a collapsed expander, so without this a user whose settings lookup found
    /// nothing — or who pressed the default Add button straight from the address field, before the
    /// lookup ever ran — would save an account with a blank IMAP host and never see why it failed.
    /// </summary>
    /// <param name="error">Message to show, and the reason to open Advanced settings.</param>
    public bool IsReadyToSave(out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(Username))
        {
            error = "Enter your email address.";
            return false;
        }

        // Graph carries no host configuration at all; sign-in is what proves the account.
        if (IsGraphBackend) return true;

        if (string.IsNullOrWhiteSpace(ImapHost) || string.IsNullOrWhiteSpace(SmtpHost))
        {
            error = "No server settings for this address. Enter the IMAP and SMTP hosts under Advanced settings.";
            return false;
        }

        if (IsPasswordAuth && string.IsNullOrEmpty(Password))
        {
            error = SelectedProvider?.RequiresAppPassword == true
                ? $"Enter your {SelectedProvider.DisplayName} app password."
                : "Enter your password.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Builds a throwaway account carrying the form's current values, for a connectivity probe.
    /// Its Id is fresh so the probe never disturbs the real account's connection pool.
    /// </summary>
    private AccountModel BuildProbeAccount() => new()
    {
        Id = Guid.NewGuid(),
        AccountName = $"Test ({Username})",
        DisplayName = Username,
        Username = Username,
        AuthType = AuthType,
        BackendKind = BackendKind,
        ProviderId = SelectedProvider?.Id,
        IsPersonalMicrosoftAccount = IsPersonalMicrosoftAccount,
        ImapHost = ImapHost,
        ImapPort = ImapPort,
        ImapUseSsl = ImapUseSsl,
        ImapAcceptInvalidCert = ImapAcceptInvalidCert,
        SmtpHost = SmtpHost,
        SmtpPort = SmtpPort,
        SmtpUseSsl = SmtpUseSsl,
        SmtpAcceptInvalidCert = SmtpAcceptInvalidCert,
        Signature = Signature,
    };

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        // Graph has no host/port of its own: the backend's GET /me probe IS the connectivity check,
        // so run it rather than telling the user to go press a different button.
        if (IsGraphBackend)
        {
            if (string.IsNullOrWhiteSpace(Username))
            {
                StatusText = "Sign in with Microsoft first, so there is an account to test.";
                return;
            }

            IsBusy = true;
            StatusText = "Testing connection…";
            var graphAccount = BuildProbeAccount();
            try
            {
                using var graphCts = new CancellationTokenSource(ProbeTimeout);
                await MailService.ConnectAsync(graphAccount, null, graphCts.Token);
                await MailService.DisconnectAsync(graphAccount.Id, graphCts.Token);
                StatusText = "Microsoft 365 connection successful.";
            }
            catch (Exception ex)
            {
                StatusText = $"Microsoft 365 connection failed: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(ImapHost) || string.IsNullOrWhiteSpace(Username))
        {
            StatusText = "Fill in IMAP host and username first.";
            return;
        }

        IsBusy = true;
        StatusText = "Testing connection…";
        var account = BuildProbeAccount();
        var pwd = IsPasswordAuth ? Password : null;
        try
        {
            // Two independent legs, reported independently. Incoming can be perfect while outgoing
            // is wrong — that combination used to pass Test Connection and then fail on first send,
            // because only IMAP was ever probed.
            var imapResult = await ProbeAsync(async ct =>
            {
                await MailService.ConnectAsync(account, pwd, ct);
                await MailService.DisconnectAsync(account.Id, ct);
            });

            var smtpResult = string.IsNullOrWhiteSpace(SmtpHost)
                ? "SMTP not configured."
                : SendMailService is null
                    ? "SMTP not checked."
                    : await ProbeAsync(ct => SendMailService.VerifyAsync(account, pwd, ct));

            StatusText = $"IMAP: {imapResult} SMTP: {smtpResult}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs one connectivity probe and turns it into a short report line. Failures are reported, not
    /// thrown, so a broken SMTP host still lets the IMAP result through.
    /// </summary>
    private static async Task<string> ProbeAsync(Func<CancellationToken, Task> probe)
    {
        try
        {
            using var cts = new CancellationTokenSource(ProbeTimeout);
            await probe(cts.Token);
            return "OK.";
        }
        catch (OperationCanceledException)
        {
            return "timed out.";
        }
        catch (Exception ex)
        {
            return $"failed — {ex.Message}";
        }
    }
}
