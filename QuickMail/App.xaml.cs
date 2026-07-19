using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using System.Windows;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;

namespace QuickMail;

[SuppressMessage("Design", "CA1001", Justification = "Disposable fields are disposed in OnExit; WPF Application does not support IDisposable.")]
public partial class App : Application
{
    // Held so OnExit can dispose them.
    private GraphSendMailService? _graphSendMail;
    private ContactService? _contactService;
    private GooglePeopleClient? _googlePeopleClient;
    private GoogleCalendarClient? _googleCalendarClient;
    private CalDavCalendarClient? _calDavCalendarClient;
    private TemplateService? _templateService;
    private ChangeNotifierRouter? _changeNotifier;
    private GraphChangeNotifier? _graphNotifier;
    private ImapMailService? _imapBackend;
    private GraphMailService? _graphBackend;
    private UpdateCheckService? _updateCheckService;
    private ThemeService? _themeService;
    private BugReportService? _bugReportService;
    private WindowsToastNotificationService? _notificationService;

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

        LogService.Configure(profile.ProfileDir);

        // /debug enables verbose debug logging to the log file.
        if (e.Args.Contains("/debug", StringComparer.OrdinalIgnoreCase))
        {
            LogService.DebugMode = true;
            LogService.Log("Debug mode enabled.");
        }

        var onlineMode = e.Args.Contains("--online", StringComparer.OrdinalIgnoreCase);
        if (onlineMode)
            LogService.Log("Online mode enabled — SQLite cache bypassed.");

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
            var msOAuthService    = new OAuthService(profile);
            var googleOAuth       = new GoogleOAuthService(credentialService);
            var oauthService      = new OAuthRouter(msOAuthService, googleOAuth);
            _imapBackend          = new ImapMailService(oauthService, configService);
            var imapBackend       = _imapBackend;
            _graphBackend         = new GraphMailService(msOAuthService, configService);
            var graphBackend      = _graphBackend;
            _graphSendMail        = new GraphSendMailService(msOAuthService);
            var smtpService       = new SmtpService(oauthService, _graphSendMail);

            // Per-account mail backend router. Each account is registered to the backend its
            // BackendKind selects (IMAP by default, Graph for Microsoft 365 accounts).
            var mailRouter = new MailServiceRouter(new IMailService[] { imapBackend, graphBackend });
            IMailService BackendFor(AccountModel a)
                => a.BackendKind == BackendKind.MicrosoftGraph ? graphBackend : imapBackend;

            var localStore = new LocalStoreService(profile);
            if (!onlineMode)
                localStore.Initialize();

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

            _contactService = new ContactService(profile);
            var contactService = _contactService;
            _templateService = new TemplateService(profile);
            var templateService = _templateService;
            var ruleService = new RuleService(mailRouter, localStore, profile.ProfileDir);
            var syncService = new SyncService(mailRouter, localStore, configService, ruleService);

            // Contact sync (issue #256): Graph source reuses the Graph backend's client; Google source
            // gets its own People API client (owns an HttpClient → disposed in OnExit).
            var graphContactSource  = new GraphContactSource(graphBackend.Client);
            _googlePeopleClient     = new GooglePeopleClient(googleOAuth);
            var googleContactSource = new GoogleContactSource(_googlePeopleClient);
            var contactSyncService  = new ContactSyncService(accountService, contactService, graphContactSource, googleContactSource);

            var startupCfg = configService.Load();
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
            var flagService = new FlagService(profile, configService, localStore, mailRouter);
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
            var mainVm = new MainViewModel(
                mailRouter, accountService, credentialService, localStore, oauthService, syncService, configService, commandRegistry, viewService, ruleService, smtpService,
                onlineMode: onlineMode, flagService: flagService, calendarService: calendarService, changeNotifier: _changeNotifier, updateCheckService: _updateCheckService,
                themeService: themeService, notificationService: _notificationService, contactSyncService: contactSyncService,
                graphCalendarSyncService: graphCalendarSync);
            mainVm.RegisterAccountBackend = a => mailRouter.RegisterAccount(a.Id, BackendFor(a));
            mainVm.LoadAccountList(accounts);

            var mainWindow = new MainWindow(mainVm, smtpService, accountService, credentialService, mailRouter, oauthService, commandRegistry, contactService, configService, localStore, viewService, ruleService, templateService, featureGate, flagService, customDictionary, themeService, _bugReportService, _notificationService, contactSyncService, graphCalendarSync);

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
        _imapBackend?.Dispose();    // closes connection pools (StopWatchers already ran, and is idempotent)
        _graphBackend?.Dispose();   // releases GraphClient/HttpClient; after the notifiers, which poll through its client
        _graphSendMail?.Dispose();
        _googlePeopleClient?.Dispose();
        _googleCalendarClient?.Dispose();
        _calDavCalendarClient?.Dispose();
        _contactService?.Dispose();
        _templateService?.Dispose();
        _updateCheckService?.Dispose();
        _bugReportService?.Dispose();
        _themeService?.Dispose();   // unsubscribes SystemParameters/SystemEvents static events
        _notificationService?.Dispose(); // unhooks the toast-activation static event
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
