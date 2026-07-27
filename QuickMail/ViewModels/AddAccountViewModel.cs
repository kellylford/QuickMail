using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

        AvailableBackends = backends;
        _selectedBackend = backends[0];
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

    protected override void OnSelectedProviderChangedInternal(MailProvider? value)
    {
        if (value is null) return;

        ApplyProvider(value);
        SyncBackendOptionToBackendKind();
        // Declared on this class, so the base class's [NotifyPropertyChangedFor] can't cover it.
        OnPropertyChanged(nameof(ShowConnectionMethod));

        // Give the account a sensible name rather than leaving the user to invent one — but only
        // when blank, so a name they already typed is never clobbered.
        if (string.IsNullOrWhiteSpace(AccountName) && !value.IsOther)
            AccountName = value.DisplayName;

        ProviderApplied?.Invoke(value);
    }

    // ── Connection method (Advanced settings) ────────────────────────────────────

    /// <summary>Connection methods offered, derived from the feature gate.</summary>
    public IReadOnlyList<BackendKindOption> AvailableBackends { get; }

    /// <summary>
    /// The IMAP-versus-Graph choice, shown only for Microsoft accounts and only inside Advanced
    /// settings. Every other provider has exactly one connection method, so asking would be noise.
    /// </summary>
    public bool ShowConnectionMethod =>
        AvailableBackends.Count > 1 && SelectedProvider?.Id == ProviderCatalog.MicrosoftId;

    public override bool ShowGoogleAuthOption => _gate.IsEnabled(FeatureFlag.GoogleAuth);

    [ObservableProperty]
    private BackendKindOption _selectedBackend;

    partial void OnSelectedBackendChanged(BackendKindOption value)
    {
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
        else if (SelectedProvider is { IsOther: false } provider && !HostsUserEdited)
        {
            // Switching back to IMAP restores the hosts the Graph branch cleared.
            ApplyProvider(provider);
        }
    }

    /// <summary>Keeps the connection-method combo in step when BackendKind is set from a provider.</summary>
    private void SyncBackendOptionToBackendKind()
    {
        foreach (var option in AvailableBackends)
        {
            if (option.Kind != BackendKind || ReferenceEquals(option, SelectedBackend)) continue;
            SelectedBackend = option;
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

    protected override void OnUsernameChangedInternal(string value) => MatchProviderFromUsername();

    /// <summary>
    /// Matches the address against the built-in catalog without touching the network. Runs on every
    /// keystroke, so it must stay free.
    /// </summary>
    public void MatchProviderFromUsername()
    {
        if (HostsUserEdited) return; // never overwrite hosts the user typed
        var match = Catalog.MatchByEmail(Username);
        if (match is null || ReferenceEquals(match, SelectedProvider)) return;

        SelectedProvider = match;
    }

    /// <summary>
    /// Looks up settings for an address the built-in catalog does not recognize. Safe to call on
    /// every focus change: it no-ops for a blank address, a known provider, or hand-edited hosts.
    /// </summary>
    [RelayCommand]
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
        StatusText = $"Looking up settings for {domain}…";
        try
        {
            var found = await _autoDiscover.DiscoverAsync(Username, cts.Token);

            // A newer lookup started while this one ran — its result wins, so drop this one.
            if (!ReferenceEquals(Volatile.Read(ref _discoverCts), cts)) return;

            if (found is not null && ApplyDiscovered(found))
            {
                var label = string.IsNullOrWhiteSpace(found.DisplayName) ? domain : found.DisplayName;
                StatusText = $"Settings found for {label}.";
                DiscoveryCompleted?.Invoke(true, StatusText);
            }
            else
            {
                // Never a silent empty state: say what happened and open the fields to be filled in.
                IsAdvancedExpanded = true;
                StatusText = $"No settings found for {domain}. Advanced settings expanded — enter your IMAP host.";
                DiscoveryCompleted?.Invoke(false, StatusText);
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
