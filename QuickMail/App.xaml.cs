using System.Threading.Tasks;
using System.Windows;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;

namespace QuickMail;

public partial class App : Application
{
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
            var oauthService      = new OAuthService(profile);
            var configService     = new ConfigService(profile);
            var mailService       = new ImapMailService(oauthService, configService);
            var smtpService       = new SmtpService(oauthService);

            var localStore = new LocalStoreService(profile);
            if (!onlineMode)
                localStore.Initialize();

            var contactService = new ContactService(profile);
            var templateService = new TemplateService(profile);
            var ruleService = new RuleService(mailService, localStore, profile.ProfileDir);
            var syncService = new SyncService(mailService, localStore, configService, ruleService);

            var startupCfg = configService.Load();
            Views.AccessibilityHelper.Configure(startupCfg);
            LogService.Format = startupCfg.LogFormat;

            var commandRegistry = new CommandRegistry();
            commandRegistry.ApplyUserOverrides(startupCfg.CustomHotkeys);

            var viewService = new ViewService(profile);

            var mainVm = new MainViewModel(
                mailService, accountService, credentialService, localStore, oauthService, syncService, configService, commandRegistry, viewService, ruleService, smtpService,
                onlineMode: onlineMode);
            mainVm.LoadAccountList();

            var mainWindow = new MainWindow(mainVm, smtpService, accountService, credentialService, mailService, oauthService, commandRegistry, contactService, configService, localStore, viewService, ruleService, templateService);
            mainWindow.Show();
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

    /// <summary>
    /// Parses --profileDir from args, validates the path, and returns a ProfileContext.
    /// Returns null (and shows an error dialog) if the path is unusable.
    /// </summary>
    private static bool IsHelpRequest(string[] args)
    {
        var helpFlags = new[] { "--help", "-help", "-h", "/?" };
        foreach (var arg in args)
            foreach (var flag in helpFlags)
                if (arg.Equals(flag, StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

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
