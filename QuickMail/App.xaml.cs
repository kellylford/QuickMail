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

        // /debug enables verbose debug logging to the log file.
        if (e.Args.Contains("/debug", StringComparer.OrdinalIgnoreCase))
        {
            LogService.DebugMode = true;
            LogService.Log("Debug mode enabled.");
        }

        try
        {
            var accountService    = new AccountService();
            var credentialService = new CredentialService();
            var oauthService      = new OAuthService();
            var configService     = new ConfigService();
            var imapService       = new ImapService(oauthService, configService);
            var smtpService       = new SmtpService(oauthService);

            var localStore = new LocalStoreService();
            localStore.Initialize();

            var contactService = new ContactService();
            var syncService = new SyncService(imapService, localStore, configService);

            var commandRegistry = new CommandRegistry();
            commandRegistry.ApplyUserOverrides(configService.Load().CustomHotkeys);

            var mainVm = new MainViewModel(
                imapService, accountService, credentialService, localStore, oauthService, syncService, configService, commandRegistry);
            mainVm.LoadAccountList();

            var mainWindow = new MainWindow(mainVm, smtpService, accountService, credentialService, imapService, oauthService, commandRegistry, contactService, configService, localStore);
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
}
