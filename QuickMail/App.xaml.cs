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

        var accountService    = new AccountService();
        var credentialService = new CredentialService();
        var imapService       = new ImapService();
        var smtpService       = new SmtpService();
        var configService     = new ConfigService();

        var localStore = new LocalStoreService();
        localStore.Initialize();

        var syncService = new SyncService(imapService, localStore, configService);

        var mainVm = new MainViewModel(
            imapService, accountService, credentialService, localStore, syncService, configService);
        mainVm.LoadAccountList();

        var mainWindow = new MainWindow(mainVm, smtpService, accountService, credentialService, imapService);
        mainWindow.Show();
    }
}
