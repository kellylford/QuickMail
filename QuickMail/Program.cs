using System;
using System.Linq;
using System.Windows.Forms;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;

namespace QuickMail;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.ThreadException += (_, e) =>
            MessageBox.Show($"Unhandled error:\n\n{e.Exception.Message}\n\n{e.Exception.StackTrace}",
                "QuickMail Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            MessageBox.Show($"Fatal error:\n\n{e.ExceptionObject}",
                "QuickMail Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

        if (args.Contains("/debug", StringComparer.OrdinalIgnoreCase))
        {
            LogService.DebugMode = true;
            LogService.Log("Debug mode enabled.");
        }

        ApplicationConfiguration.Initialize();

        var accountService    = new AccountService();
        var credentialService = new CredentialService();
        var oauthService      = new OAuthService();
        var imapService       = new ImapService(oauthService);
        var smtpService       = new SmtpService(oauthService);
        var configService     = new ConfigService();

        var localStore = new LocalStoreService();
        localStore.Initialize();

        var syncService = new SyncService(imapService, localStore, configService);

        var commandRegistry = new CommandRegistry();
        commandRegistry.ApplyUserOverrides(configService.Load().CustomHotkeys);

        var mainVm = new MainViewModel(
            imapService, accountService, credentialService, localStore, syncService, configService, commandRegistry);
        mainVm.LoadAccountList();

        Application.Run(new MainForm(mainVm, smtpService, accountService, credentialService, imapService, oauthService, commandRegistry));
    }
}
