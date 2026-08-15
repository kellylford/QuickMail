using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class AddAccountViewModel : AccountEditorViewModel, IDisposable
{
    private readonly IFeatureGate _gate;
    private readonly IAutoDiscoverService? _autoDiscover;

    /// <summary>
    /// Cancels a superseded lookup. A user types over their address faster than a network round trip
    /// completes, so without this a stale result could land on top of a fresher one.
    /// </summary>
    private CancellationTokenSource? _discoverCts;

    private bool _disposed;

    public AddAccountViewModel(
        IFeatureGate gate,
        IMailService mailService,
        IOAuthService oauth,
        IProviderCatalog catalog,
        IAutoDiscoverService? autoDiscover = null,
        ISendMailService? sendMail = null)
        : base(mailService, oauth, catalog, sendMail)
    {
        _gate = gate;
        _autoDiscover = autoDiscover;

        var backends = new List<BackendKindOption>
        {
            new(BackendKind.ImapSmtp, "Standard IMAP/SMTP"),
        };
        if (gate.IsEnabled(FeatureFlag.GraphBackend))
            backends.Add(new(BackendKind.MicrosoftGraph, "Microsoft 365 (Graph)"));
        if (gate.IsEnabled(FeatureFlag.Pop3Backend))
            backends.Add(new(BackendKind.Pop3Smtp, "POP3/SMTP"));

        _allBackends = backends;
        _selectedBackend = backends[0];

        // The Google sign-in provider entry only exists for users who opted in. Everyone else is
        // creating a Gmail account with an app password, which the plain Gmail entry already does.
        if (gate.IsEnabled(FeatureFlag.GoogleAuth)) EnsureGoogleSignInListed();
        // SelectedProvider already starts on the catalog's "Other" entry (set by the base
        // constructor). Typing an address whose domain is in the catalog moves the picker for the user.
    }

    // ── Provider selection ───────────────────────────────────────────────────────

    /// <summary>
    /// Raised after the provider changes so the View can announce the consequence — including any
    /// app-password requirement, which belongs in an announcement rather than baked into a control's
    /// AutomationProperties.Name.
    /// </summary>
    public event Action<MailProvider>? ProviderApplied;

    /// <summary>
    /// True while this VM is assigning the provider because the typed address matched the catalog,
    /// as opposed to the user picking one or a lookup finding one. Only a match-driven selection may
    /// be undone by <see cref="ResetToUnknownProvider"/> — undoing a deliberate pick would fight the
    /// user, e.g. choosing Outlook.com / Microsoft 365 and then typing a work address.
    /// </summary>
    private bool _assigningFromUsernameMatch;

    private bool _providerCameFromUsernameMatch;

    protected override void OnSelectedProviderChangedInternal(MailProvider? value)
    {
        _providerCameFromUsernameMatch = _assigningFromUsernameMatch;
        if (value is null) return;

        ApplyProvider(value);
        // Picking a provider is itself a commit point: the address is whatever is already in the
        // box, finished or not touched at all, and choosing Microsoft with a work address already
        // typed must land on Graph without waiting for the field to be revisited.
        ChooseBackendForMicrosoftAccount();
        SyncBackendOptionToBackendKind();
        // Declared on this class, so the base class's [NotifyPropertyChangedFor] can't cover it.
        // AvailableBackends is provider-dependent (Graph is Microsoft-only), so the combo refreshes
        // with the provider.
        OnPropertyChanged(nameof(AvailableBackends));
        OnPropertyChanged(nameof(ShowConnectionMethod));

        // Deliberately NOT filling in the account name from the provider. Selecting a provider fires
        // this for every item the user passes through — arrowing Other -> Gmail -> Microsoft wrote
        // "Gmail" into the name box on the way past, and then declined to correct it because the box
        // was no longer blank, leaving a Microsoft account called Gmail. Writing into a field the
        // user is not looking at, as a side effect of changing a different one, is the whole
        // problem. It bought nothing either way: AccountModel.AccountLabel already falls back to the
        // email address when the name is blank.
        ProviderApplied?.Invoke(value);
    }

    // ── Connection method (Advanced settings) ────────────────────────────────────

    private readonly List<BackendKindOption> _allBackends;

    /// <summary>
    /// Connection methods offered, derived from the feature gate AND the selected provider: Graph
    /// is the Microsoft 365 API, so it is only offered when the provider is Microsoft. Offering it
    /// for any provider let a Gmail account be saved bound to Microsoft OAuth with no hosts — a
    /// dead account nothing later corrects, since <see cref="IsReadyToSave"/> has nothing to check
    /// for Graph and ChooseBackendForMicrosoftAccount deliberately no-ops for other providers.
    /// </summary>
    public IReadOnlyList<BackendKindOption> AvailableBackends =>
        _allBackends.Where(b => b.Kind != BackendKind.MicrosoftGraph
                                || SelectedProvider?.Id == ProviderCatalog.MicrosoftId)
                    .ToList();

    /// <summary>
    /// The connection-method choice, shown inside Advanced settings.
    ///
    /// <para>IMAP versus Graph is a Microsoft-only question, so on its own it appears only for
    /// Microsoft accounts — every other provider has exactly one connection method, and asking would
    /// be noise. POP3 changes that: it is offered for any provider, so once the POP3 backend is
    /// enabled the combo is shown throughout. It is the only way to reach POP3, and a user who wants
    /// it usually has a provider QuickMail has never heard of.</para>
    /// </summary>
    public bool ShowConnectionMethod =>
        AvailableBackends.Count > 1
        && (_gate.IsEnabled(FeatureFlag.Pop3Backend) || SelectedProvider?.Id == ProviderCatalog.MicrosoftId);

    protected override bool IsGoogleAuthEnabled => _gate.IsEnabled(FeatureFlag.GoogleAuth);

    /// <summary>
    /// A hand-picked POP3 survives a provider change; see the base declaration for why POP3 and not
    /// Graph. <see cref="_backendUserChosen"/> is what makes it the user's choice rather than one of
    /// this VM's own inferences.
    /// </summary>
    protected override bool PreserveChosenBackend =>
        _backendUserChosen && BackendKind == BackendKind.Pop3Smtp;

    [ObservableProperty]
    private BackendKindOption _selectedBackend;

    /// <summary>
    /// True while this VM is assigning the connection method itself — following a provider, or
    /// inferring one from the address domain — as opposed to the user picking one in Advanced
    /// settings. Only the latter sets <see cref="_backendUserChosen"/>.
    /// </summary>
    private bool _assigningBackendInternally;

    /// <summary>
    /// Set once the user picks a connection method by hand. The mirror of
    /// <see cref="_providerCameFromUsernameMatch"/>, and for the same reason: without it, typing
    /// one more character of the address forced a work Microsoft account straight back onto Graph,
    /// undoing a deliberate choice the user could not even see happen — the combo lives inside the
    /// collapsed Advanced expander.
    /// </summary>
    private bool _backendUserChosen;

    /// <summary>Assigns the connection method without it counting as the user's own choice.</summary>
    private void SetBackendInternally(BackendKindOption option)
    {
        var wasAssigning = _assigningBackendInternally;
        _assigningBackendInternally = true;
        try { SelectedBackend = option; }
        finally { _assigningBackendInternally = wasAssigning; }
    }

    partial void OnSelectedBackendChanged(BackendKindOption value)
    {
        if (!_assigningBackendInternally) _backendUserChosen = true;

        // ORDER MATTERS: set BackendKind BEFORE AuthType. Assigning AuthType triggers
        // OnAuthTypeChangedInternal, which reads BackendKind. If AuthType were assigned first, a
        // Graph account could still pick up IMAP host defaults.
        BackendKind = value?.Kind ?? BackendKind.ImapSmtp;
        if (BackendKind == BackendKind.MicrosoftGraph)
        {
            // Graph accounts authenticate via OAuth and need no IMAP/SMTP host configuration.
            AuthType = AuthType.OAuth2Microsoft;
            ClearHostsForGraph();
        }
        else if (BackendKind == BackendKind.Pop3Smtp)
        {
            // The POP3 backend authenticates with a password; there is no OAuth path through it.
            // Coming from Graph, the host fields were cleared and have to be put back — the SMTP
            // half is still needed to send, and a provider's POP3 host to receive.
            AuthType = AuthType.Password;
            if (SelectedProvider is { IsOther: false } popProvider && !HostsUserEdited)
                ApplyProvider(popProvider, collapseAdvanced: false);
        }
        else if (SelectedProvider is { IsOther: false } provider && !HostsUserEdited)
        {
            // Switching back to IMAP restores the hosts the Graph branch cleared. collapseAdvanced
            // is false because this combo lives INSIDE Advanced settings — collapsing the expander
            // the user is standing in removes the focused control from the visual tree and strands
            // keyboard focus on the window, silently.
            ApplyProvider(provider, collapseAdvanced: false);
        }
    }

    /// <summary>Keeps the connection-method combo in step when BackendKind is set from a provider.</summary>
    private void SyncBackendOptionToBackendKind()
    {
        foreach (var option in AvailableBackends)
        {
            if (option.Kind != BackendKind || ReferenceEquals(option, SelectedBackend)) continue;
            SetBackendInternally(option);
            return;
        }
    }

    // ── Settings discovery ───────────────────────────────────────────────────────

    /// <summary>
    /// Raised when a lookup finishes: (found, message). The View announces the message as a Result
    /// and, when nothing was found, moves focus into Advanced settings — where the user now has to
    /// type.
    /// </summary>
    public event Action<bool, string>? DiscoveryCompleted;

    protected override void OnUsernameChangedInternal(string value)
    {
        MatchProviderFromUsername();
        // Deliberately NOT choosing the connection method here. This runs on every keystroke
        // (UsernameBox binds with UpdateSourceTrigger=PropertyChanged), and a half-typed address has
        // a half-typed domain: "kelly@outlook.com" passes through "kelly@o", which matches no
        // consumer domain and so read as a work tenant — the form switched to Graph and
        // ClearHostsForGraph blanked the hosts AND the password on the way past. The domain only
        // means anything once the address is finished, which is what CommitUsername marks.
    }

    /// <summary>
    /// The address is finished: the user left the field, or pressed the default button. This is
    /// where the domain is worth reading, and it is the same moment the settings lookup already
    /// waits for. Safe to call repeatedly — <see cref="ChooseBackendForMicrosoftAccount"/> is
    /// idempotent and no-ops for every provider but Microsoft.
    /// </summary>
    public void CommitUsername() => ChooseBackendForMicrosoftAccount();

    /// <summary>
    /// Chooses the connection method for a Microsoft account from what is known about the address.
    ///
    /// A work or school tenant must connect over Graph, and the reason is the scopes. BackendKind
    /// ImapSmtp asks for outlook.office.com/IMAP.AccessAsUser.All and SMTP.Send; Graph asks for
    /// graph.microsoft.com/.default, which resolves to exactly the permissions the app registration
    /// declares and a tenant admin has already consented to. Most tenants have not consented to the
    /// IMAP/SMTP pair — many disable IMAP entirely — so leaving a work account on the IMAP backend
    /// ends sign-in at "your administrator needs to make a change", for an account that signs in
    /// perfectly well over Graph.
    ///
    /// Consumer accounts are the opposite case: they have no admin-consent model, work fine over
    /// IMAP+OAuth, and on Graph they draw work scopes that under-deliver for them (#217, #233,
    /// #239). Two things identify one: a domain on the Microsoft catalog entry, and — the answer
    /// that survives a personal account on a vanity domain, where the domain guess is simply
    /// wrong — the tenant id MSAL reports at sign-in.
    ///
    /// Both directions are applied, not just the move to Graph. An address edited from a work
    /// domain back to outlook.com re-selects the same provider, so nothing else would put the IMAP
    /// hosts back.
    /// </summary>
    private void ChooseBackendForMicrosoftAccount()
    {
        if (SelectedProvider is not { } provider || provider.Id != ProviderCatalog.MicrosoftId) return;
        if (string.IsNullOrWhiteSpace(Username)) return;
        if (HostsUserEdited) return;   // the user has taken over the servers
        if (_backendUserChosen) return; // ...or the connection method itself

        // A consumer domain, or a sign-in that reported a personal account, means IMAP.
        var wantGraph = !provider.MatchesEmail(Username) && IsPersonalMicrosoftAccount != true;
        MoveToBackend(wantGraph ? BackendKind.MicrosoftGraph : BackendKind.ImapSmtp);
    }

    /// <summary>
    /// Sign-in answered the question the domain could only guess at. A personal Microsoft account
    /// on a vanity domain looks exactly like a work tenant from the address alone, so the domain
    /// guess had moved it to Graph; the tenant id from sign-in is ground truth, so put an
    /// <em>auto-inferred</em> Graph choice back on IMAP.
    ///
    /// But a connection method the user picked BY HAND in Advanced is honored — the same
    /// <see cref="_backendUserChosen"/> guard <see cref="ChooseBackendForMicrosoftAccount"/> already
    /// uses. Its absence here was the #527 bug: an explicit "Microsoft 365 (Graph)" pick for a
    /// personal account survived the address-commit step and was then silently reverted after sign-in,
    /// leaving no way to reach Graph for a personal account through the UI at all.
    /// </summary>
    protected override void OnMicrosoftSignInCompleted(bool isPersonalAccount)
    {
        if (!isPersonalAccount) return;
        if (SelectedProvider is not { } provider || provider.Id != ProviderCatalog.MicrosoftId) return;
        if (BackendKind != BackendKind.MicrosoftGraph) return;
        if (_backendUserChosen) return;   // the user deliberately chose Graph — don't override it (#527)

        MoveToBackend(BackendKind.ImapSmtp);
    }

    /// <summary>
    /// Selects the offered connection method of the given kind, if there is one and it is not
    /// already selected. Going back to ImapSmtp restores the provider's hosts, via
    /// <see cref="OnSelectedBackendChanged"/> — the fields Graph cleared must not be left blank.
    /// </summary>
    private void MoveToBackend(BackendKind kind)
    {
        var option = AvailableBackends.FirstOrDefault(b => b.Kind == kind);
        // No such option means the Graph feature gate is off. Nothing better is available, so the
        // account stays creatable on IMAP and the user finds out at sign-in as they did before.
        if (option is null || ReferenceEquals(option, SelectedBackend)) return;

        SetBackendInternally(option);
    }

    /// <summary>
    /// Matches the address against the built-in catalog without touching the network. Runs on every
    /// keystroke, so it must stay free.
    /// </summary>
    public void MatchProviderFromUsername()
    {
        if (HostsUserEdited) return; // never overwrite hosts the user typed

        // The selected provider already owns this domain, so there is nothing to correct. Without
        // this, picking "Gmail (sign in with Google)" and then typing the address threw the choice
        // away on the first keystroke: MatchByEmail answers with the plain Gmail entry for every
        // gmail.com address by design, so the two entries fought and the app-password one always
        // won — silently, and after the user had explicitly chosen otherwise.
        if (SelectedProvider?.MatchesEmail(Username) == true) return;

        var match = Catalog.MatchByEmail(Username);
        if (match is null)
        {
            // Only undo a provider the address itself selected. A provider the user picked, or one a
            // settings lookup found, stays — otherwise choosing Microsoft 365 and then typing a work
            // address would silently throw the choice away.
            if (!_providerCameFromUsernameMatch) return;
            // The address no longer matches. This is the correcting-a-typo case, and leaving the old
            // provider selected is how "kelly@gmail.com" edited into "kelly@theideaplace.net" ends up
            // saved with Gmail's servers — invisibly, because Advanced is collapsed, and with the
            // settings lookup skipped entirely since a provider was already chosen.
            ResetToUnknownProvider();
            return;
        }

        if (ReferenceEquals(match, SelectedProvider)) return;

        _assigningFromUsernameMatch = true;
        try { SelectedProvider = match; }
        finally { _assigningFromUsernameMatch = false; }
    }

    /// <summary>
    /// Looks up settings for an address the built-in catalog does not recognize. Safe to call on
    /// every focus change: it no-ops for a blank address, a known provider, or hand-edited hosts.
    /// </summary>
    // AllowConcurrentExecutions matters: by default AsyncRelayCommand reports CanExecute=false while
    // a run is in flight, so a second lookup — the user fixing their address and tabbing out while
    // the first is still going — was silently dropped, and the FIRST lookup's settings were then
    // applied to the corrected address. Letting both run, plus the address guard below, is what
    // makes the supersede logic reachable at all.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DiscoverSettingsAsync()
    {
        if (_autoDiscover is null || _disposed) return;
        if (string.IsNullOrWhiteSpace(Username)) return;
        if (HostsUserEdited) return;
        if (SelectedProvider is { IsOther: false }) return; // the catalog already answered this

        var domain = AutoDiscoverService.DomainOf(Username);
        if (domain is null) return;

        // Supersede any lookup still in flight for a previous address.
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _discoverCts, cts);
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        IsBusy = true;
        var lookingUpMessage = $"Looking up settings for {domain}…";
        StatusText = lookingUpMessage;
        try
        {
            var found = await _autoDiscover.DiscoverAsync(Username, cts.Token);

            // A newer lookup started while this one ran — its result wins, so drop this one.
            if (!ReferenceEquals(Volatile.Read(ref _discoverCts), cts)) return;

            // And the address itself must still be the one we looked up. Belt and braces against
            // applying one domain's servers to another domain's account.
            if (!string.Equals(AutoDiscoverService.DomainOf(Username), domain, StringComparison.OrdinalIgnoreCase))
            {
                // Don't leave "Looking up settings for <the old domain>…" sitting there forever.
                if (string.Equals(StatusText, lookingUpMessage, StringComparison.Ordinal))
                    StatusText = string.Empty;
                return;
            }

            if (found is null)
            {
                // Never a silent empty state: say what happened, name the routes that need no server
                // settings at all, and open the fields to be filled in.
                IsAdvancedExpanded = true;
                StatusText = $"No settings found for {domain}. If this is a work or school Microsoft 365 "
                           + "account, choose Outlook.com / Microsoft 365 as the provider and sign in. "
                           + "Otherwise enter your IMAP host under Advanced settings.";
                DiscoveryCompleted?.Invoke(false, StatusText);
            }
            else if (found.Source == DiscoverySource.DnsMailHost && ApplyDiscovered(found))
            {
                // Name the provider its DNS points at, and say what the evidence was — this came from
                // where the domain delivers its mail, not from anything the user typed.
                var provider = SelectedProvider?.DisplayName ?? found.DisplayName ?? domain;
                StatusText = $"{domain} delivers its mail to {provider}, according to its DNS records. "
                           + "Open Advanced settings if you would rather enter your own servers.";
                DiscoveryCompleted?.Invoke(true, StatusText);
            }
            else if (found.Source != DiscoverySource.DnsMailHost && ApplyDiscovered(found))
            {
                var label = string.IsNullOrWhiteSpace(found.DisplayName) ? domain : found.DisplayName;
                // Name the hosts, not just the provider. These servers are about to receive the
                // user's password and they arrived over the network, so they should not be applied
                // behind a collapsed expander without ever being stated.
                StatusText = $"Settings found for {label}: {found.ImapHost} and {found.SmtpHost}.";
                DiscoveryCompleted?.Invoke(true, StatusText);
            }
            else
            {
                // Settings WERE found, but the user typed their own hosts while the lookup ran and
                // those win. Saying "no settings found" here would be a lie.
                StatusText = $"Settings found for {domain}, but your own server settings were kept.";
                DiscoveryCompleted?.Invoke(true, StatusText);
            }
        }
        catch (Exception ex)
        {
            // DiscoverAsync already absorbs network failures, so reaching here is unexpected — still
            // surface it rather than leaving the dialog looking like nothing happened.
            IsAdvancedExpanded = true;
            StatusText = $"Couldn't look up settings for {domain}: {ex.Message}";
            DiscoveryCompleted?.Invoke(false, StatusText);
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
        // The NORMALIZED address where one can be parsed out, not the raw box: a pasted
        // "Kelly Ford <kelly@example.com>" or a trailing space parses fine in the validator and then
        // throws in MimeMessageBuilder on every send. Falls back to the raw value only on a path
        // that never reaches here with an unusable address — IsReadyToSave has already refused it.
        Username = EmailAddressValidator.TryNormalize(Username, out var address) ? address : Username,
        // Null rather than "" when unset, so accounts.json carries the field only for the accounts
        // that actually need it.
        LoginUsername = string.IsNullOrWhiteSpace(LoginUsername) ? null : LoginUsername.Trim(),
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
        // Persisted with the account: settings QuickMail supplied must keep requiring encryption
        // every time the account connects, not only on the day it was created.
        RequireStartTls = RequireStartTls,
        Signature = Signature,
        SyncContacts = SyncContacts && ShowContactSyncOption,
        SyncCalendar = SyncCalendar && ShowCalendarSyncOption,
    };

    /// <summary>
    /// Called from AddAccountDialog.OnClosed. Cancels before disposing so an in-flight lookup gets a
    /// clean OperationCanceledException rather than an ObjectDisposedException.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var cts = Interlocked.Exchange(ref _discoverCts, null);
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
