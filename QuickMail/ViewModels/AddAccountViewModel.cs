using System;
using CommunityToolkit.Mvvm.ComponentModel;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class AddAccountViewModel : AccountEditorViewModel
{
    public AddAccountViewModel(IMailService mailService, IOAuthService oauth)
        : base(mailService, oauth) { }

    protected override void OnAuthTypeChangedInternal(AuthType value)
    {
        if (value == AuthType.OAuth2Microsoft)
        {
            // Personal Outlook.com IMAP/SMTP server settings
            ImapHost             = "outlook.office365.com";
            ImapPort             = 993;
            ImapUseSsl           = true;
            ImapAcceptInvalidCert = false;
            SmtpHost             = "smtp-mail.outlook.com";
            SmtpPort             = 587;
            SmtpUseSsl           = false;
            SmtpAcceptInvalidCert = false;
            Password             = string.Empty;
        }
    }

    public AccountModel ToAccountModel() => new()
    {
        AccountName = AccountName,
        DisplayName = DisplayName,
        Username = Username,
        AuthType = AuthType,
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
}
