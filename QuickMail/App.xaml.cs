using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using System.Windows;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;

namespace QuickMail;

[SuppressMessage("Design", "CA1001", Justification = "Disposable fields are disposed in OnExit; WPF Application does not support IDisposable.")]
public partial class App : Application
{
    /// <summary>
    /// Debug screenshot capture (#175). Exposed so view code-behind (Settings
    /// composition, reading-pane render-complete) can reach the session service
    /// without threading it through every constructor; NullScreenshotCaptureService
    /// outside /debug.
    /// </summary>
    public IScreenshotCaptureService? ScreenshotCapture { get; private set; }

    /// <summary>Non-null only in --ui-probe automation mode (#180).</summary>
    public UiProbeOptions? UiProbe { get; private set; }

    /// <summary>
    /// The active profile. Exposed so Settings can clean profile-scoped
    /// artifacts (debug screenshots, #436) regardless of which capture service
    /// is wired — leftovers from an earlier /debug session must be deletable
    /// from a normal launch too.
    /// </summary>
    public ProfileContext? Profile { get; private set; }

    // Held so OnExit can dispose them.
    private GraphSendMailService? _graphSendMail;
    private ContactService? _contactService;
    private GooglePeopleClient? _googlePeopleClient;
    private GoogleCalendarClient? _googleCalendarClient;
    private CalDavCalendarClient? _calDavCalendarClient;
    private CardDavContactClient? _cardDavContactClient;
    private TemplateService? _templateService;
    private ChangeNotifierRouter? _changeNotifier;
    private GraphChangeNotifier? _graphNotifier;
    private ImapMailService? _imapBackend;
    private GraphMailService? _graphBackend;
    private Pop3MailService? _pop3Backend;
    private OutboxService? _outboxService;
    private UpdateCheckService? _updateCheckService;
    private ThemeService? _themeService;
    private BugReportService? _bugReportService;
    private WindowsToastNotificationService? _notificationService;
    private AutoDiscoverService? _autoDiscoverService;
    private ConnectionTruthProbe? _truthProbe;

    // Owned by Main (acquired before WPF starts, disposed after Run returns); OnStartup
    // wires its activation signal to the main window.
    private static SingleInstanceService? _singleInstance;

    // Explicit entry point (App.xaml compiles as Page; see csproj StartupObject). Velopack must
    // run before any WPF initialization: on install/update/uninstall its hooks handle the event
    // and exit the process, and on a normal launch after an update it finalizes the new version.
    [STAThread]
    public static void Main(string[] args)
    {
        Velopack.VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => LaunchUninstallDataPrompt())
            .Run();

        // One instance per profile (issue #240): with close-to-tray the process can be running
        // with no visible window, and relaunching from the Start menu must restore that window
        // rather than pile up processes sharing one SQLite store. When another instance owns
        // this profile, TryAcquire has already signaled it to come to the foreground, so this
        // launch simply ends. --help is exempt so usage is always available.
        if (!IsHelpRequest(args))
        {
            _singleInstance = SingleInstanceService.TryAcquire(args);
            if (_singleInstance is null) return;
        }

        using (_singleInstance)
        {
            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
    }

    // Uninstall-time offer to remove user data, mirroring the old installer's prompt.
    // Update.exe kills hook processes after ~30 seconds — far too short to leave a question
    // pending — so the prompt runs in a detached PowerShell process that outlives the
    // uninstall. The default answer keeps everything; only an explicit Yes deletes the
    // default profile (%APPDATA%\QuickMail) and QuickMail entries in Windows Credential
    // Manager. Custom --profileDir locations are never touched. The whole mechanism is
    // best-effort: on script-restricted machines (AppLocker, Constrained Language Mode)
    // the prompt may never appear, in which case data is kept — the safe default.
    // Diagnostics go to %TEMP%\quickmail-uninstall.log: LogService is not configured in
    // the hook context (OnStartup never runs), so it cannot be used here.
    private static void LaunchUninstallDataPrompt()
    {
        var diagLog = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "quickmail-uninstall.log");
        try
        {
            var dataDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickMail");
            if (!System.IO.Directory.Exists(dataDir)) return;

            var script = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "quickmail-uninstall-prompt.ps1");
            System.IO.File.WriteAllText(script, """
                $log = Join-Path $env:TEMP 'quickmail-uninstall.log'
                Add-Content -Path $log -Value "$(Get-Date -Format s) prompt script started"
                Add-Type -AssemblyName System.Windows.Forms
                $dir = Join-Path $env:APPDATA 'QuickMail'
                if (-not (Test-Path $dir)) { exit }
                $msg = "QuickMail has been uninstalled.`n`n" +
                       "Do you also want to remove your QuickMail data? This permanently deletes all accounts, settings, contacts, rules, templates, saved views, and cached mail stored under:`n$dir`n`n" +
                       "It also removes QuickMail's saved passwords and sign-ins from Windows Credential Manager.`n`n" +
                       "Choose No to keep everything, so a future install picks up exactly where you left off."
                $owner = New-Object System.Windows.Forms.Form -Property @{ TopMost = $true }
                $r = [System.Windows.Forms.MessageBox]::Show($owner, $msg, 'QuickMail Uninstall',
                    [System.Windows.Forms.MessageBoxButtons]::YesNo,
                    [System.Windows.Forms.MessageBoxIcon]::Question,
                    [System.Windows.Forms.MessageBoxDefaultButton]::Button2)
                Add-Content -Path $log -Value "$(Get-Date -Format s) user answered: $r"
                if ($r -eq [System.Windows.Forms.DialogResult]::Yes) {
                    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
                    (cmdkey /list) | ForEach-Object {
                        if ($_ -match 'target=(QuickMail\S*)') { cmdkey /delete:$($Matches[1]) | Out-Null }
                    }
                    Add-Content -Path $log -Value "$(Get-Date -Format s) data removal completed"
                }
                Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
                """);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            System.IO.File.AppendAllText(diagLog, $"{DateTime.Now:s} uninstall hook: prompt process launched\r\n");
        }
        catch (Exception ex)
        {
            // The uninstall itself must never fail or stall because of this prompt.
#pragma warning disable RCS1075 // this IS the last-resort diagnostics writer — a failure to write the diagnostic has no further channel and must not escape into the uninstall hook
            try { System.IO.File.AppendAllText(diagLog, $"{DateTime.Now:s} uninstall hook failed: {ex}\r\n"); }
            catch (Exception) { }
#pragma warning restore RCS1075
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --help / -h / /? — show usage and exit before anything else.
        if (IsHelpRequest(e.Args))
        {
            MessageBox.Show(
                "Usage: QuickMail.exe [options]\n\n" +
                "Options:\n" +
                "  --profileDir <path>   Store all data in <path> instead of the default\n" +
                "                        %AppData%\\QuickMail directory. The directory is\n" +
                "                        created if it does not already exist.\n\n" +
                "  --online              Run in fully online mode: fetch everything live from\n" +
                "                        IMAP on every folder selection. Nothing is read from\n" +
                "                        or written to the local SQLite cache.\n\n" +
                "  --updateFeed <path>   Check for updates in <path> (a folder or URL of\n" +
                "                        Velopack packages) instead of GitHub Releases.\n" +
                "                        For testing update delivery.\n\n" +
                "  --help                Show this message and exit.\n\n" +
                "  /debug                Write verbose debug output to quickmail.log.",
                "QuickMail",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Resolve the profile directory first so all logging goes to the right place.
        var profile = ResolveProfile(e.Args);
        if (profile is null)
        {
            Shutdown();
            return;
        }
        Profile = profile;

        LogService.Configure(profile.ProfileDir);

        // Point the journal at the profile now; whether it actually records is decided below from
        // the ConnectionDiagnostics setting (off by default).
        ConnectionJournal.Configure(profile.ProfileDir);

        // /debug enables verbose debug logging to the log file.
        if (e.Args.Contains("/debug", StringComparer.OrdinalIgnoreCase))
        {
            LogService.DebugMode = true;
            LogService.Log("Debug mode enabled.");
        }

        // --ui-probe (#180): automation launch mode — implies /debug, forces the
        // app offline, drives to a surface, captures, exits. Never user-reachable.
        UiProbe = UiProbeOptions.Parse(e.Args, out var probeError);
        if (probeError != null)
        {
            LogService.Log($"ui-probe: {probeError}");
            Shutdown(64); // EX_USAGE — the orchestrator must see a hard failure
            return;
        }
        if (UiProbe != null)
        {
            LogService.DebugMode = true;
            LogService.Log($"ui-probe mode: surfaces=[{string.Join(";", UiProbe.Surfaces)}] theme={UiProbe.ThemeId ?? "(configured)"} scale={UiProbe.TextScale?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(configured)"}");
        }

        var onlineMode = e.Args.Contains("--online", StringComparer.OrdinalIgnoreCase);
        if (onlineMode && UiProbe != null)
        {
            LogService.Debug("ui-probe forces offline; --online ignored.");
            onlineMode = false;
        }
        if (onlineMode)
            LogService.Log("Online mode enabled — SQLite cache bypassed.");

        // Left/Right/Home/End through a wrapped tab strip (#528). A class handler so a window
        // with tabs added later cannot be left out.
        TabStripNavigation.Install();

        // Debug screenshot capture (#175): the real engine exists only under /debug;
        // otherwise a null object keeps the feature structurally unreachable. One
        // class handler covers all Window subclasses — no per-window edits.
        ScreenshotCapture = LogService.DebugMode
            ? new ScreenshotCaptureService(profile)
            : new NullScreenshotCaptureService();
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) =>
            {
                if (s is Window w) ScreenshotCapture?.OnWindowLoaded(w);
            }));

        // Install global exception handlers BEFORE anything else so an exception
        // in startup wiring or any background task is captured in the log instead
        // of disappearing with the process. (review §1.2)
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Sweep stale temp attachments (review §3.2). %TEMP%\QuickMail accumulated every
        // attachment ever opened — gigabytes over a year of use. Each attachment now
        // lives in its own Guid subfolder; delete subfolders older than 24h.
        _ = Task.Run(CleanupStaleTempAttachments);

        try
        {
            var accountService    = new AccountService(profile);
            var credentialService = new CredentialService();
            var configService     = new ConfigService(profile);
            // Provider presets + settings discovery for the Add Account dialog. The catalog is a
            // pure lookup table; the discovery service owns an HttpClient, so it is disposed in OnExit.
            var providerCatalog   = new ProviderCatalog();
            _autoDiscoverService  = new AutoDiscoverService(providerCatalog, configService);
            var msOAuthService    = new OAuthService(profile);
            var googleOAuth       = new GoogleOAuthService(credentialService);
            var oauthService      = new OAuthRouter(msOAuthService, googleOAuth);
            _imapBackend          = new ImapMailService(oauthService, configService);
            var imapBackend       = _imapBackend;
            _graphBackend         = new GraphMailService(msOAuthService, configService);
            var graphBackend      = _graphBackend;
            _graphSendMail        = new GraphSendMailService(msOAuthService);
            var smtpService       = new SmtpService(oauthService, _graphSendMail);

            // The local store is built before the backends because the POP3 backend reads and writes
            // it directly: a POP3 mailbox has no server-side state to consult, so the store is not a
            // cache of the account but the account itself.
            var localStore = new LocalStoreService(profile);
            if (!onlineMode)
                localStore.Initialize();

            _pop3Backend          = new Pop3MailService(localStore, onlineMode);
            var pop3Backend       = _pop3Backend;

            // Per-account mail backend router. Each account is registered to the backend its
            // BackendKind selects (IMAP by default, Graph for Microsoft 365 accounts, POP3 for
            // POP3/SMTP accounts).
            IMailService BackendFor(AccountModel a) => a.BackendKind switch
            {
                BackendKind.MicrosoftGraph => graphBackend,
                BackendKind.Pop3Smtp       => pop3Backend,
                _                          => imapBackend,
            };
            // BackendFor is also handed to the router so an account it has never been told about —
            // the throwaway probe account Test Connection builds, for instance — is routed by its
            // BackendKind rather than defaulting to IMAP.
            var mailRouter = new MailServiceRouter(new IMailService[] { imapBackend, graphBackend, pop3Backend }, BackendFor);

            // ui-probe (#180 Decision D): network hard-off at the DI root. EVERY
            // consumer of the mail/send/oauth services gets the offline no-op —
            // including RuleService (whose rules-apply path runs when the rules
            // window closes) and SyncService — so the probe is structurally
            // incapable of connecting, syncing, or sending, not merely unlikely to.
            var probeMode = UiProbe != null;
            IMailService effectiveMail = probeMode ? new ProbeOfflineMailService() : mailRouter;
            ISendMailService effectiveSmtp = probeMode ? new ProbeOfflineSendMailService() : smtpService;
            IOAuthService effectiveOAuth = probeMode ? new ProbeOfflineOAuthService() : oauthService;

            // Change-notification router (new-mail + reachability). IMAP's strategy is a held IDLE
            // connection, implemented by ImapMailService itself because it is bound to the IMAP
            // connection lifecycle. Graph uses delta polling, which needs the local store for its
            // delta cursor — hence wired after localStore. Each notifier filters to its own accounts.
            _graphNotifier  = new GraphChangeNotifier(graphBackend.Client, localStore, configService);
            _changeNotifier = new ChangeNotifierRouter(new IChangeNotifier[] { imapBackend, _graphNotifier });

            // Load accounts once — after the store is initialized — and reuse the list for the VM.
            // Router registration runs via mainVm.RegisterAccountBackend (set below), which also
            // covers accounts added at runtime through RefreshAccountList.
            var accounts = accountService.LoadAccounts();

            // One-time immutable-id cache rebuild (#366): clear cached mail for Graph accounts so the
            // next sync repopulates with immutable ids (mutable and immutable ids must not be mixed).
            // Graph-scoped so IMAP bodies — and the IMAP calendar-invite source links that depend on
            // them — survive; a marker file gates it to run exactly once. The VM announces the
            // resulting one-time re-sync. Skipped in --online mode (no local store).
            bool immutableIdRebuilt = false;
            var rebuiltGraphAccountIds = new List<Guid>(); // seeded to SyncService below so the first
                                                           // post-wipe sync doesn't re-run rules (#366/N5)
            if (!onlineMode && !probeMode) // never touch a --ui-probe fixture profile (review nit)
            {
                var rebuildMarker = System.IO.Path.Combine(profile.ProfileDir, ".immutable-id-rebuilt");
                if (!System.IO.File.Exists(rebuildMarker))
                {
                    var graphIds = new List<Guid>();
                    foreach (var a in accounts)
                        if (a.BackendKind == BackendKind.MicrosoftGraph) graphIds.Add(a.Id);

                    if (graphIds.Count == 0)
                    {
                        // No Graph accounts → nothing to rebuild. Mark done so we don't re-scan every
                        // launch; a Graph account added later starts fresh with immutable ids anyway.
                        try { System.IO.File.WriteAllText(rebuildMarker, DateTime.UtcNow.ToString("o")); } catch { }
                    }
                    else
                    {
                        // Isolate the clear so a failure (locked/corrupt SQLite, disk full) can NEVER
                        // crash startup: the outer OnStartup catch rethrows, so an unguarded throw here
                        // would kill the process before the marker is written — a permanent startup
                        // crash-loop the user could only escape by deleting the profile. On failure we
                        // log and leave the marker unwritten so the next launch retries; only a
                        // successful clear marks it done and announces the one-time re-sync.
                        try
                        {
                            localStore.ClearCachedMailAsync(graphIds).GetAwaiter().GetResult();
                            immutableIdRebuilt = true;
                            rebuiltGraphAccountIds = graphIds;
                            System.IO.File.WriteAllText(rebuildMarker, DateTime.UtcNow.ToString("o"));
                        }
                        catch (Exception rebuildEx)
                        {
                            LogService.Log("Immutable-id cache rebuild failed; will retry next launch.", rebuildEx);
                        }
                    }
                }
            }

            // Before anything connects: an account hand-configured before the provider catalog
            // existed can be pointed at one of our hosts with the wrong encryption mode, which fails
            // every send with an error about the socket rather than the setting; and one holding a
            // login name where its email address belongs needs that login preserved before the user
            // is asked to correct the address (#396). Persisted right away so both survive the
            // session that made them.
            var repaired = AccountStartupRepair.Apply(accounts, providerCatalog);
            if (repaired.Count > 0)
            {
                accountService.SaveAccounts(accounts);
                LogService.Log($"AccountStartupRepair: repaired {repaired.Count} account(s).");
            }

            _contactService = new ContactService(profile);
            var contactService = _contactService;
            _templateService = new TemplateService(profile);
            var templateService = _templateService;
            // accountService drives the one-time "All accounts" → per-account rule migration (#333 D1).
            var ruleService = new RuleService(effectiveMail, localStore, profile.ProfileDir, accountService);
            // Server-side (Exchange/Graph) Inbox rules — read/manage a Graph account's messageRules.
            // Reuses the shared GraphClient (no own disposables), so no disposal wiring needed.
            var serverRuleService = new GraphServerRuleService(accountService, graphBackend.Client);
            var syncService = new SyncService(effectiveMail, localStore, configService, ruleService, probeMode: probeMode);
            // The Outbox (#637): mail written while the server could not be reached. Drains through the
            // same router and send service as a live send, so a queued message leaves exactly as an
            // online one would have. Connectivity arrives with PR 2; until then it drains on startup,
            // on the fallback sync tick, and on Send Outbox Now.
            _outboxService = new OutboxService(localStore, effectiveMail, effectiveSmtp, accountService, credentialService, connectivity: null, onlineMode: onlineMode);
            var outboxService = _outboxService;
            // The one-time immutable-id wipe emptied these accounts' store, so their first re-sync would
            // read old mail as new and re-run rules over it on upgrade day. Baseline it (#366/N5).
            if (immutableIdRebuilt) syncService.SeedRebuildBaseline(rebuiltGraphAccountIds);

            // #529 step 4: an IMAP→Graph convert purges the account's store before it re-syncs. If the
            // app closed (or crashed) between the purge and the first baselined sync, the store is empty
            // and the in-memory baseline is gone, so the first re-sync would read all pre-existing mail as
            // new and re-fire client rules over it (the #454 failure). The persisted GraphConversionPending
            // marker survives that, so seed the baseline for every account still mid-convert — before any
            // sync runs (the sync service is built above; StartBackgroundSyncAsync runs later).
            var convertingAccountIds = accounts.Where(a => a.GraphConversionPending).Select(a => a.Id).ToList();
            if (convertingAccountIds.Count > 0) syncService.SeedRebuildBaseline(convertingAccountIds);

            // Contact sync (issue #256): Graph source reuses the Graph backend's client; Google source
            // gets its own People API client (owns an HttpClient → disposed in OnExit).
            var graphContactSource  = new GraphContactSource(graphBackend.Client);
            _googlePeopleClient     = new GooglePeopleClient(googleOAuth);
            var googleContactSource = new GoogleContactSource(_googlePeopleClient);
            // iCloud contacts run per-account over CardDAV: for each account the user opted into whose
            // IMAP host is imap.mail.me.com, the source discovers/fetches its address book using the
            // account's own app-specific password (no separate credential, no OAuth).
            _cardDavContactClient   = new CardDavContactClient();
            var iCloudContactSource = new ICloudContactSource(_cardDavContactClient, credentialService);
            var contactSyncService  = new ContactSyncService(accountService, contactService, graphContactSource, googleContactSource, iCloudContactSource);

            var startupCfg = configService.Load();
            // ui-probe (#180): theme/scale land in the loaded config BEFORE
            // ThemeService.Initialize so the first render is already correct;
            // ThemeService never persists, so config.ini is untouched.
            if (UiProbe is { } probeOpts)
            {
                if (probeOpts.ThemeId != null) startupCfg.AppearanceThemeId = probeOpts.ThemeId;
                if (probeOpts.TextScale != null) startupCfg.AppearanceTextScale = probeOpts.TextScale.Value;
            }
            Views.AccessibilityHelper.Configure(startupCfg);
            LogService.Format  = startupCfg.LogFormat;
            LogService.Enabled = startupCfg.EnableLogging;

            // Theme tokens must be published before the first window parses so every
            // Theme.* DynamicResource resolves on first render.
            _themeService = new ThemeService(new ThemeStore(profile));
            _themeService.Initialize(startupCfg);
            var themeService = _themeService;

            // Feature gate: CLI --feature/--no-feature > config.ini [features] section > built-in defaults.
            var (enableFlags, disableFlags) = ParseFeatureFlags(e.Args);
            var featureGate = new ConfigFeatureGate(startupCfg, enableFlags, disableFlags);

            var commandRegistry = new CommandRegistry();
            commandRegistry.ApplyUserOverrides(startupCfg.CustomHotkeys);

            var viewService = new ViewService(profile);

            // One-time: convert an old "default view (applied on startup)" into the startup folder
            // setting that replaced it (#516). No-ops once a startup folder is configured, and
            // rewrites views.json so the retired IsDefault flag cannot come back.
            StartupFolderMigration.Run(profile, startupCfg, configService, viewService);

            var folderViewState = new FolderViewStateService(profile);
            var watchService = new WatchService(profile);
            var rowLayoutService = new RowLayoutService(profile, configService);
            var flagService = new FlagService(profile, configService, localStore, effectiveMail);
            var customDictionary = new CustomDictionaryService(profile);

            // Calendar service: harvests events from the local message cache.
            var calendarProvider = new LocalCacheCalendarProvider(localStore);
            var calendarService = new CalendarService(calendarProvider);

            // Calendar sync (read-down v1): pulls each server-backed account's primary calendar
            // into the local store — Microsoft via the Graph backend's client (owned + disposed
            // with the backend) and Google via its own Calendar API client (owns an HttpClient →
            // disposed in OnExit, like the People client). The sync timer and its CTS live in
            // MainViewModel (disposed in MainViewModel.Dispose).
            _googleCalendarClient = new GoogleCalendarClient(googleOAuth);
            // iCloud CalDAV runs per-account (#282): for each account the user opted into whose IMAP
            // host is imap.mail.me.com, the sync service discovers/fetches its calendar using the
            // account's own app-specific password from Windows Credential Manager.
            _calDavCalendarClient = new CalDavCalendarClient();
            var graphCalendarSync = new GraphCalendarSyncService(accountService, localStore, graphBackend.Client,
                                                                 _googleCalendarClient,
                                                                 _calDavCalendarClient, credentialService);

            _updateCheckService = new UpdateCheckService(configService, ParseUpdateFeed(e.Args));
            _bugReportService   = new BugReportService(credentialService);
            _notificationService = new WindowsToastNotificationService();
            // Answers the question the app cannot answer about itself: when an account shows as
            // disconnected, is it actually unreachable? Probes on a connection that shares nothing
            // with the pools or watchers. Label resolution reads the live account list.
            // Through the ROUTER, not the IMAP backend: each account must be probed by the backend
            // that actually owns it. Probing a Graph account with the IMAP backend is what produced
            // the first live false alarm.
            _truthProbe = probeMode ? null : new ConnectionTruthProbe(
                mailRouter,
                id => accounts.FirstOrDefault(a => a.Id == id)?.AccountLabel ?? id.ToString());

            var mainVm = new MainViewModel(
                effectiveMail, accountService, credentialService, localStore, effectiveOAuth, syncService, configService, commandRegistry, viewService, ruleService, effectiveSmtp,
                onlineMode: onlineMode, flagService: flagService, calendarService: calendarService,
                changeNotifier: probeMode ? null : _changeNotifier,
                updateCheckService: probeMode ? null : _updateCheckService,
                screenshotCapture: ScreenshotCapture,
                themeService: themeService, notificationService: _notificationService,
                contactSyncService: probeMode ? null : contactSyncService,
                graphCalendarSyncService: probeMode ? null : graphCalendarSync,
                truthProbe: probeMode ? null : _truthProbe,
                rowLayoutService: rowLayoutService,
                watchService: watchService,
                folderViewState: folderViewState);
            mainVm.RegisterAccountBackend = a => { if (!probeMode) mailRouter.RegisterAccount(a.Id, BackendFor(a)); };
            // #31: a credential-less shared mailbox borrows its parent account's token. The resolver runs
            // on background sweep threads, so it goes through ResolveAccountById — a thread-safe snapshot
            // of the account list — never the UI-thread-owned Accounts collection directly.
            msOAuthService.ResolveAccount = mainVm.ResolveAccountById;
            mainVm.ImmutableIdRebuildAnnouncePending = immutableIdRebuilt;   // #366 one-time re-sync notice
            // Registers/unregisters the Help command and shows or hides the menu item, and sets
            // ConnectionJournal.Enabled — so nothing records until the user opts in.
            mainVm.ApplyConnectionDiagnosticsSetting(startupCfg.ConnectionDiagnostics);
            mainVm.LoadAccountList(accounts);

            var mainWindow = new MainWindow(mainVm, effectiveSmtp, accountService, credentialService, effectiveMail, effectiveOAuth, commandRegistry, contactService, configService, localStore, viewService, ruleService, templateService, featureGate, flagService, customDictionary, themeService, _bugReportService, _notificationService, contactSyncService, graphCalendarSync, serverRuleService, providerCatalog, _autoDiscoverService, _truthProbe, rowLayoutService, watchService, outboxService);

            // Clicking a new-mail toast brings QuickMail to the foreground and opens the referenced
            // message. OnActivated may fire on a background thread, so marshal to the UI thread first.
            _notificationService.Activated += act =>
                mainWindow.Dispatcher.BeginInvoke(() => mainWindow.HandleNotificationActivation(act));

            mainWindow.Show();

            // A second launch of the same profile signals this handle instead of starting
            // another process; restore the window (and drop the tray icon) exactly as the
            // tray icon's Open action would. The signal arrives on a thread-pool thread.
            _singleInstance?.ListenForActivation(() =>
                mainWindow.Dispatcher.BeginInvoke(() => mainWindow.RestoreFromTray()));
        }
        catch (Exception ex)
        {
            // Log the exception chain before WER kills the process so the cause
            // survives in %APPDATA%\QuickMail\quickmail.log.
            for (var cur = ex; cur != null; cur = cur.InnerException)
                LogService.Log("Startup", cur);
            throw;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _changeNotifier?.Dispose(); // stops all watchers (IDLE + Graph poll) + severs the event chain
        _graphNotifier?.Dispose();  // disposes the Graph poll CTS (StopWatchers already ran; idempotent)
        _outboxService?.Dispose();  // cancels an in-flight drain before the pools it sends through go away
        _imapBackend?.Dispose();    // closes connection pools (StopWatchers already ran, and is idempotent)
        _graphBackend?.Dispose();   // releases GraphClient/HttpClient; after the notifiers, which poll through its client
        _pop3Backend?.Dispose();    // releases the per-account session locks (POP3 holds no open connection)
        _graphSendMail?.Dispose();
        _googlePeopleClient?.Dispose();
        _googleCalendarClient?.Dispose();
        _calDavCalendarClient?.Dispose();
        _cardDavContactClient?.Dispose();
        _contactService?.Dispose();
        _templateService?.Dispose();
        _updateCheckService?.Dispose();
        _bugReportService?.Dispose();
        _themeService?.Dispose();   // unsubscribes SystemParameters/SystemEvents static events
        _notificationService?.Dispose(); // unhooks the toast-activation static event
        _autoDiscoverService?.Dispose(); // releases the autoconfig HttpClient
        _truthProbe?.Dispose();     // cancels in-flight probes before releasing their token source
        ScreenshotCapture?.Dispose(); // flushes any in-flight PNG save (best effort, bounded)
        base.OnExit(e);
    }

    /// <summary>
    /// Parses repeated <c>--feature &lt;Name&gt;</c> (force-on) and <c>--no-feature &lt;Name&gt;</c>
    /// (force-off) CLI flags. CLI flags are the highest-precedence feature-gate source; for a given
    /// flag an explicit <c>--no-feature</c> wins over <c>--feature</c>.
    /// </summary>
    private static (List<string> Enable, List<string> Disable) ParseFeatureFlags(string[] args)
    {
        var enable = new List<string>();
        var disable = new List<string>();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--feature", StringComparison.OrdinalIgnoreCase))
                enable.Add(args[i + 1]);
            else if (args[i].Equals("--no-feature", StringComparison.OrdinalIgnoreCase))
                disable.Add(args[i + 1]);
        }
        return (enable, disable);
    }

    private static bool IsHelpRequest(string[] args)
    {
        var helpFlags = new[] { "--help", "-help", "-h", "/?" };
        foreach (var arg in args)
            foreach (var flag in helpFlags)
                if (arg.Equals(flag, StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    /// <summary>
    /// Parses --updateFeed from args: a local folder or URL holding Velopack packages,
    /// overriding the GitHub Releases source. Lets the full update cycle (check, download,
    /// apply on relaunch) be tested against local vpk pack output without publishing.
    /// </summary>
    private static string? ParseUpdateFeed(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--updateFeed", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    /// <summary>
    /// Parses --profileDir from args, validates the path, and returns a ProfileContext.
    /// Returns null (and shows an error dialog) if the path is unusable.
    /// </summary>
    private static ProfileContext? ResolveProfile(string[] args)
    {
        var rawDir = ProfileContext.ParseProfileDir(args);
        if (rawDir is null)
            return ProfileContext.Default();

        var profile = ProfileContext.TryCreate(rawDir, out var error);
        if (profile is null)
            ShowProfileError(rawDir, error!);

        return profile;
    }

    private static void ShowProfileError(string dir, string reason)
    {
        MessageBox.Show(
            $"Cannot use profile directory:\n  {dir}\n\n{reason}\n\nCheck the --profileDir argument and try again.",
            "QuickMail — Invalid Profile Directory",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static void OnDispatcherUnhandledException(
        object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        for (var cur = e.Exception; cur != null; cur = cur.InnerException)
            LogService.Log("Dispatcher", cur);

        // ui-probe (#180): unattended — a modal error box would park the run on an
        // invisible dialog until the orchestrator's kill timeout and destroy the
        // diagnostic exit code. Log (done above) and exit distinctly instead.
        if ((Current as App)?.UiProbe != null)
        {
            e.Handled = true;
            Current!.Shutdown(3);
            return;
        }

        // Keep the process alive so the user isn't left staring at a vanished window.
        // The log captures the cause; the next user action will either succeed or
        // fault again, by which point we want it diagnosed rather than swallowed.
        try
        {
            MessageBox.Show(
                $"An unexpected error occurred and was logged.\n\n{e.Exception.GetType().Name}: {e.Exception.Message}",
                "QuickMail",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch { /* MessageBox itself can fail in extreme cases — swallow. */ }

        e.Handled = true;
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Non-recoverable: the runtime is tearing down. Just log every frame we can.
        if (e.ExceptionObject is Exception ex)
            for (var cur = ex; cur != null; cur = cur.InnerException)
                LogService.Log("AppDomain", cur);
        else
            LogService.Log($"AppDomain: non-Exception unhandled object: {e.ExceptionObject}");
    }

    private static void CleanupStaleTempAttachments()
    {
        try
        {
            var tempRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "QuickMail");
            if (!System.IO.Directory.Exists(tempRoot)) return;

            var cutoff = DateTime.UtcNow.AddHours(-24);

            // Each attachment lives under a Guid subdir.  An external process might still
            // hold the file open from a previous session; let those failures slide.
            foreach (var dir in System.IO.Directory.EnumerateDirectories(tempRoot))
            {
                try
                {
                    if (System.IO.Directory.GetLastWriteTimeUtc(dir) < cutoff)
                        System.IO.Directory.Delete(dir, recursive: true);
                }
                catch (Exception ex)
                {
                    LogService.Debug($"Temp-cleanup: could not delete {dir}: {ex.Message}");
                }
            }

            // Sweep loose files at the root (older code wrote attachments directly there
            // without a Guid subfolder — clean those up too).
            foreach (var file in System.IO.Directory.EnumerateFiles(tempRoot))
            {
                try
                {
                    if (System.IO.File.GetLastWriteTimeUtc(file) < cutoff)
                        System.IO.File.Delete(file);
                }
                catch (Exception ex)
                {
                    LogService.Debug($"Temp-cleanup: could not delete {file}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Log("CleanupStaleTempAttachments", ex);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        for (var cur = e.Exception as Exception; cur != null; cur = cur.InnerException)
            LogService.Log("UnobservedTask", cur);

        // Mark as observed so the GC finaliser doesn't crash the process on .NET <4.5
        // semantics or on a future hardening change.
        e.SetObserved();
    }
}
