using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Helpers;
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

    /// <summary>
    /// Bound to the optional "Login username" box in Advanced settings (#396). Empty means the
    /// server is logged into with the email address, which is the case for almost every account.
    /// </summary>
    [ObservableProperty] private string _loginUsername = string.Empty;

    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsICloudAccount))]
    [NotifyPropertyChangedFor(nameof(ShowContactSyncOption))]
    [NotifyPropertyChangedFor(nameof(ShowCalendarSyncOption))]
    private string _imapHost = string.Empty;
    [ObservableProperty] private int    _imapPort = 993;
    [ObservableProperty] private bool   _imapUseSsl = true;
    [ObservableProperty] private bool   _imapAcceptInvalidCert = false;
    [ObservableProperty] private string _pop3Host = string.Empty;
    [ObservableProperty] private int    _pop3Port = 995;
    [ObservableProperty] private bool   _pop3UseSsl = true;
    [ObservableProperty] private bool   _pop3AcceptInvalidCert = false;

    /// <summary>
    /// Bound to "Leave mail on the server" in the POP3 settings. On by default, because the
    /// alternative — the classic POP3 behavior — makes the local store the only copy of the user's
    /// mail from the moment it is collected.
    /// </summary>
    [ObservableProperty] private bool   _pop3LeaveMailOnServer = true;

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
    [NotifyPropertyChangedFor(nameof(ShowGoogleAuthOption))]
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

    /// <summary>
    /// Providers offered in the picker, in display order. Built from the catalog minus the Google
    /// sign-in entry, which <see cref="EnsureGoogleSignInListed"/> puts back for the users entitled
    /// to it. Observable rather than a plain list because that call can happen after the ComboBox
    /// has already bound.
    /// </summary>
    public ObservableCollection<MailProvider> Providers { get; }

    /// <summary>
    /// Adds "Gmail (sign in with Google)" to the picker, directly after plain Gmail so the two sit
    /// together. Idempotent. Called by the derived VMs when the GoogleAuth flag is on, and — in the
    /// Account Manager — when the account being edited already uses Google sign-in, so an account
    /// created before the flag was turned back off is never left with a blank provider box.
    /// </summary>
    protected void EnsureGoogleSignInListed()
    {
        var entry = Catalog.GmailGoogleSignIn;
        if (Providers.Contains(entry)) return;

        var afterGmail = Providers.ToList().FindIndex(p => p.Id == ProviderCatalog.GmailId) + 1;
        // FindIndex returns -1 for a catalog with no plain Gmail entry, which +1 turns into 0 — a
        // valid insert position, so the entry is still offered rather than silently dropped.
        Providers.Insert(afterGmail, entry);
    }

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

    /// <summary>
    /// Carried to <see cref="AccountModel.RequireStartTls"/>. True while the server settings are the
    /// ones QuickMail supplied — from the built-in catalog or from a discovery result — so a leg that
    /// is not implicit TLS must negotiate STARTTLS rather than silently fall back to plaintext.
    /// Cleared the moment the user edits a server field by hand, because from then on the settings
    /// are theirs and the permissive behavior is their call to make.
    /// </summary>
    public bool RequireStartTls { get; protected set; }

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
    /// <remarks>
    /// See <see cref="PreserveChosenBackend"/> for the one case where the provider's default backend
    /// is deliberately not applied.
    /// </remarks>
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
            if (!PreserveChosenBackend)
                BackendKind = provider.DefaultBackend;

            // POP3 authenticates with a password whatever the provider's default says: there is no
            // OAuth path through that backend, and taking the provider's OAuth default here would
            // leave the account unable to sign in at all.
            AuthType = BackendKind == BackendKind.Pop3Smtp ? AuthType.Password : provider.DefaultAuthType;

            if (BackendKind == BackendKind.MicrosoftGraph)
            {
                // Graph authenticates via OAuth and needs no host configuration at all.
                ImapHost = string.Empty;
                SmtpHost = string.Empty;
                // ClearPassword, not a bare assignment: a PasswordBox cannot be data-bound, so the
                // View only learns the password is gone from the PasswordCleared event. Assigning
                // the field directly cleared it silently AND made the ClearPassword call below a
                // no-op (it returns early on an already-empty password), so the box kept showing
                // dots for a password that no longer existed.
                ClearPassword();
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

                // POP3 fields are filled whichever backend is selected, so switching the connection
                // method to POP3 finds them already set. A provider with no POP3 service leaves the
                // host empty rather than guessing one — the user types it, and IsReadyToSave says so.
                Pop3Host = provider.Pop3Host ?? string.Empty;
                Pop3Port = provider.Pop3Port;
                Pop3UseSsl = provider.Pop3Port == 995;
                Pop3AcceptInvalidCert = false;
            }

            if (AuthType != AuthType.Password) ClearPassword();
        }
        finally
        {
            _applyingSettings = wasApplying;
        }

        // The settings came from the catalog, not the user, so a later match may still replace them —
        // and, for the same reason, encryption on them is required rather than merely attempted.
        // Every catalog provider genuinely offers implicit TLS or STARTTLS on the ports listed.
        HostsUserEdited = false;
        RequireStartTls = true;
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
            Pop3Host = string.Empty;
            Pop3Port = 995;
            Pop3UseSsl = true;
            Pop3AcceptInvalidCert = false;
            // The account name is never touched here — nothing writes it but the user.
        }
        finally
        {
            _applyingSettings = false;
        }

        HostsUserEdited = false;
        // Back to no known-good settings at all, so nothing is being required of a server yet.
        RequireStartTls = false;

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
        // Discovery names these hosts, not the user, so encryption on them is mandatory: a server
        // that offers no STARTTLS must fail the connection rather than take the password in the clear.
        RequireStartTls = settings.RequireStartTls;
        IsAdvancedExpanded = false;
        return true;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGraphBackend))]
    [NotifyPropertyChangedFor(nameof(IsImapBackend))]
    [NotifyPropertyChangedFor(nameof(IsPop3Backend))]
    [NotifyPropertyChangedFor(nameof(IncomingHostLabel))]
    [NotifyPropertyChangedFor(nameof(ShowServerSettings))]
    private BackendKind _backendKind = BackendKind.ImapSmtp;

    /// <summary>True when this account uses the Microsoft Graph backend (drives IMAP/SMTP field visibility).</summary>
    public bool IsGraphBackend => BackendKind == BackendKind.MicrosoftGraph;

    /// <summary>True when this account uses the standard IMAP/SMTP backend.</summary>
    public bool IsImapBackend => BackendKind == BackendKind.ImapSmtp;

    /// <summary>True when this account receives over POP3 (drives POP3 field visibility).</summary>
    public bool IsPop3Backend => BackendKind == BackendKind.Pop3Smtp;

    /// <summary>
    /// Names the incoming protocol in status text, so a failure says which server refused the
    /// connection rather than naming IMAP for an account that never speaks it.
    /// </summary>
    public string IncomingHostLabel => IsPop3Backend ? "POP3" : "IMAP";

    /// <summary>
    /// Whether the server-settings block is shown at all. Both host-based backends need it (each
    /// shows its own incoming section, and both send over SMTP); Graph has no hosts to configure.
    /// </summary>
    public bool ShowServerSettings => !IsGraphBackend;

    [ObservableProperty] private string _statusText = string.Empty;

    /// <summary>
    /// Which announcement category the View reads the accompanying <see cref="StatusText"/> under.
    ///
    /// Status suits background progress ("Testing connection…"); the outcome of a command the user
    /// just invoked — above all a refused save — must be a Result, or it is silent for anyone who
    /// has turned background-progress announcements off.
    ///
    /// Defaults back to Status after every raise, because these VMs assign StatusText directly in
    /// several dozen places and a category left latched to Result would re-classify all of them —
    /// announcing a settings lookup as an interrupting outcome. Same one-shot contract, and same
    /// reasoning, as <see cref="MainViewModel.StatusAnnouncementCategory"/>.
    /// </summary>
    public AnnouncementCategory StatusCategory { get; private set; } = AnnouncementCategory.Status;

    /// <summary>
    /// Sets <see cref="StatusText"/> as the outcome of a user action. The reset afterwards is safe
    /// because StatusText's PropertyChanged fires synchronously — the View has read the category
    /// before this method returns.
    /// </summary>
    public void SetStatusOutcome(string text)
    {
        StatusCategory = AnnouncementCategory.Result;
        // Cleared first so an identical message repeats. StatusText is an [ObservableProperty] with
        // an equality check, so pressing Save twice on the same unfixed field would otherwise raise
        // no notification the second time — the user presses the button and hears nothing, which is
        // the symptom this whole change exists to remove (#396). The empty value is not announced;
        // the View skips empty status text.
        StatusText = string.Empty;
        StatusText = text;
        StatusCategory = AnnouncementCategory.Status;
    }

    [ObservableProperty] private bool   _isBusy = false;

    public bool IsPasswordAuth => AuthType == AuthType.Password;
    public bool IsOAuth2       => AuthType == AuthType.OAuth2Microsoft;
    public bool IsGoogleOAuth  => AuthType == AuthType.OAuth2Google;

    /// <summary>
    /// Whether the GoogleAuth feature flag is on in this editor context. Derived classes override to
    /// reflect their feature-gate state; prefer <see cref="ShowGoogleAuthOption"/> for UI decisions.
    /// </summary>
    protected virtual bool IsGoogleAuthEnabled => false;

    /// <summary>
    /// Whether the Google OAuth item belongs in the Authentication combo.
    ///
    /// The second clause is what keeps a grandfathered account editable. With the flag off, an
    /// account saved as <see cref="AuthType.OAuth2Google"/> still sets AuthTypeIndex to 2 — and if
    /// the item at index 2 were collapsed, the Account Manager would show a blank Authentication
    /// box for an account that is authenticating perfectly well, with no way to tell what it uses.
    /// </summary>
    public bool ShowGoogleAuthOption => IsGoogleAuthEnabled || AuthType == AuthType.OAuth2Google;

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
        // Google sign-in is left out here and added back by EnsureGoogleSignInListed. Filtering at
        // construction rather than gating a binding keeps the entry out of the keyboard order and
        // out of the accessibility tree entirely — a Collapsed ComboBoxItem is still an item the
        // arrow keys can land on in some templates, and "offered but unusable" is exactly what this
        // change exists to prevent.
        Providers = new ObservableCollection<MailProvider>(
            catalog.All.Where(p => p.Id != ProviderCatalog.GmailOAuthId));
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
    partial void OnPop3HostChanged(string value) => NoteHostEdit();
    partial void OnPop3PortChanged(int value) => NoteHostEdit();
    partial void OnPop3UseSslChanged(bool value) => NoteHostEdit();

    private void NoteHostEdit()
    {
        if (_applyingSettings) return;
        HostsUserEdited = true;
        // These are now the user's settings, not ones QuickMail chose for them, so drop the
        // requirement that a non-implicit-TLS leg must negotiate STARTTLS. Someone deliberately
        // pointing QuickMail at their own server keeps the permissive behavior they had before.
        RequireStartTls = false;
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
    /// <summary>
    /// True when the connection method the user picked outranks the provider's default, so
    /// <see cref="ApplyProvider"/> leaves <see cref="BackendKind"/> alone.
    ///
    /// <para>Only POP3 qualifies, and the asymmetry is the point. Graph is a property of the
    /// PROVIDER — only Microsoft accounts have it — so a provider change must be free to move an
    /// account off it. POP3 is a property of how the user wants to connect, and any provider may
    /// offer it; without this, choosing POP3 in Advanced settings and then finishing the email
    /// address put the account silently back on IMAP, because typing the address matches a provider
    /// and re-applies its default. The user could not see it happen — the combo is inside a
    /// collapsed expander.</para>
    /// </summary>
    protected virtual bool PreserveChosenBackend => false;

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
    /// Called after a Microsoft sign-in succeeds, with the answer MSAL's tenant id gives for
    /// "is this a personal Microsoft account?" (#233). It is the only reliable source for that:
    /// guessing from the domain is exactly what fails for a personal account on a vanity domain.
    /// AddAccountViewModel overrides it to put such an account back on the IMAP backend, which the
    /// domain guess had moved to Graph. Sign-in happens before the account is created, so the
    /// correction lands in time.
    /// </summary>
    protected virtual void OnMicrosoftSignInCompleted(bool isPersonalAccount) { }

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
            // AFTER Username, because assigning it re-runs the derived VM's address handling.
            OnMicrosoftSignInCompleted(result.IsPersonalMicrosoftAccount);
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
    /// Whether <see cref="Username"/> can serve as this account's own email address.
    ///
    /// Checked when the account is saved, not when a message is sent from it. This field becomes
    /// the From header — and so the SMTP envelope sender — on everything the account sends, so a
    /// login name typed here by someone whose server wants one surfaced days later as a rejected
    /// send with nothing pointing back at this box (#396). Advanced settings now has a separate
    /// Login username field for that case, which the message names.
    ///
    /// Shared by both editors so Add Account and Manage Accounts refuse the same input in the same
    /// words.
    /// </summary>
    /// <param name="error">Message to show when the address cannot be used.</param>
    /// <param name="normalized">
    /// The address stripped to its bare addr-spec — no display name, no angle brackets, no
    /// surrounding whitespace. This, not the raw field, is what gets saved: MimeMessageBuilder's
    /// MailboxAddress constructor throws on every one of those forms, so storing what the user
    /// typed would move the failure from a refused save to a rejected send.
    /// </param>
    protected bool IsEmailAddressUsable(out string error, out string normalized)
    {
        if (EmailAddressValidator.TryNormalize(Username, out normalized))
        {
            error = string.Empty;
            return true;
        }

        error = $"\"{Username}\" is not an email address. Enter the full address, including the "
              + "domain. If your mail server logs in under a different name, put that in "
              + "Advanced settings, Login username.";
        return false;
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

        if (!IsEmailAddressUsable(out error, out _)) return false;

        // Graph carries no host configuration at all; sign-in is what proves the account.
        if (IsGraphBackend) return true;

        if (IsPop3Backend)
        {
            if (string.IsNullOrWhiteSpace(Pop3Host) || string.IsNullOrWhiteSpace(SmtpHost))
            {
                error = "No server settings for this address. Enter the POP3 and SMTP hosts under Advanced settings.";
                return false;
            }
        }
        else if (string.IsNullOrWhiteSpace(ImapHost) || string.IsNullOrWhiteSpace(SmtpHost))
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
        // The probe must log in under the same name the saved account will, or a passing test says
        // nothing about whether the real credentials work.
        LoginUsername = LoginUsername,
        AuthType = AuthType,
        BackendKind = BackendKind,
        ProviderId = SelectedProvider?.Id,
        IsPersonalMicrosoftAccount = IsPersonalMicrosoftAccount,
        ImapHost = ImapHost,
        ImapPort = ImapPort,
        ImapUseSsl = ImapUseSsl,
        ImapAcceptInvalidCert = ImapAcceptInvalidCert,
        Pop3Host = Pop3Host,
        Pop3Port = Pop3Port,
        Pop3UseSsl = Pop3UseSsl,
        Pop3AcceptInvalidCert = Pop3AcceptInvalidCert,
        Pop3LeaveMailOnServer = Pop3LeaveMailOnServer,
        SmtpHost = SmtpHost,
        SmtpPort = SmtpPort,
        SmtpUseSsl = SmtpUseSsl,
        SmtpAcceptInvalidCert = SmtpAcceptInvalidCert,
        // The probe must connect exactly as the saved account will, or "Test Connection: OK" is a
        // claim about a different connection than the one the password will be sent over.
        RequireStartTls = RequireStartTls,
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
                StatusText = "Microsoft 365 connection successful.";
            }
            catch (Exception ex)
            {
                StatusText = $"Microsoft 365 connection failed: {ex.Message}";
            }
            finally
            {
                await ReleaseProbeAsync(graphAccount.Id);
                IsBusy = false;
            }
            return;
        }

        // The incoming host the probe will actually dial — naming IMAP for a POP3 account would send
        // the user to fill in a box that is not even shown.
        var incomingHost = IsPop3Backend ? Pop3Host : ImapHost;
        if (string.IsNullOrWhiteSpace(incomingHost) || string.IsNullOrWhiteSpace(Username))
        {
            StatusText = $"Fill in {IncomingHostLabel} host and username first.";
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
            var incomingResult = await ProbeAsync(ct => MailService.ConnectAsync(account, pwd, ct));

            var smtpResult = string.IsNullOrWhiteSpace(SmtpHost)
                ? "SMTP not configured."
                : SendMailService is null
                    ? "SMTP not checked."
                    : await ProbeAsync(ct => SendMailService.VerifyAsync(account, pwd, ct));

            StatusText = $"{IncomingHostLabel}: {incomingResult} SMTP: {smtpResult}";
        }
        finally
        {
            await ReleaseProbeAsync(account.Id);
            IsBusy = false;
        }
    }

    /// <summary>
    /// Ends a probe's connection, in a finally rather than on the success path.
    ///
    /// MailServiceRouter binds an account to a backend when it connects, so the Disconnect that
    /// follows — which carries only a Guid — reaches the same backend. Releasing that binding is
    /// the Disconnect's job, and <see cref="BuildProbeAccount"/> mints a fresh Guid for every
    /// probe: a Test Connection that FAILED to connect and therefore skipped the disconnect left an
    /// entry behind for the life of the process, once per press of the button.
    /// </summary>
    private async Task ReleaseProbeAsync(Guid probeId)
    {
        try
        {
            using var cts = new CancellationTokenSource(ProbeTimeout);
            await MailService.DisconnectAsync(probeId, cts.Token);
        }
        catch (Exception ex)
        {
            // Not surfaced: the probe is finished either way and there is nothing for the user to
            // act on. Logged rather than swallowed silently.
            LogService.Log($"AccountEditor: probe disconnect failed — {ex.Message}");
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
