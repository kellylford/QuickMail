using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class AddAccountViewModel : AccountEditorViewModel
{
    private readonly IFeatureGate _gate;

    public AddAccountViewModel(IFeatureGate gate, IMailService mailService, IOAuthService oauth)
        : base(mailService, oauth)
    {
        _gate = gate;

        var backends = new List<BackendKindOption>
        {
            new(BackendKind.ImapSmtp, "Standard IMAP/SMTP"),
        };
        if (gate.IsEnabled(FeatureFlag.GraphBackend))
            backends.Add(new(BackendKind.MicrosoftGraph, "Microsoft 365 / Outlook.com"));

        AvailableBackends = backends;
        _selectedBackend = backends[0];

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(Username)) return;

            if (ShowGoogleAuthOption && AuthType != AuthType.OAuth2Google &&
                (Username.EndsWith("@gmail.com", StringComparison.OrdinalIgnoreCase) ||
                 Username.EndsWith("@googlemail.com", StringComparison.OrdinalIgnoreCase)))
            {
                AuthType = AuthType.OAuth2Google;
                return;
            }

            if (ImapHost != "imap.mail.me.com" &&
                (Username.EndsWith("@icloud.com", StringComparison.OrdinalIgnoreCase) ||
                 Username.EndsWith("@me.com", StringComparison.OrdinalIgnoreCase) ||
                 Username.EndsWith("@mac.com", StringComparison.OrdinalIgnoreCase)))
            {
                ImapHost              = "imap.mail.me.com";
                ImapPort              = 993;
                ImapUseSsl            = true;
                ImapAcceptInvalidCert = false;
                SmtpHost              = "smtp.mail.me.com";
                SmtpPort              = 587;
                SmtpUseSsl            = false;
                SmtpAcceptInvalidCert = false;
            }
        };
    }

    /// <summary>Backend options offered in the dialog, derived from the feature gate.</summary>
    public IReadOnlyList<BackendKindOption> AvailableBackends { get; }

    /// <summary>
    /// True when more than one backend is available — the dialog renders a combo box. When only
    /// one option exists (the default), it renders a static label to reduce clutter.
    /// </summary>
    public bool ShowBackendPicker => AvailableBackends.Count > 1;

    public override bool ShowGoogleAuthOption => _gate.IsEnabled(FeatureFlag.GoogleAuth);

    [ObservableProperty]
    private BackendKindOption _selectedBackend;

    partial void OnSelectedBackendChanged(BackendKindOption value)
    {
        // ORDER MATTERS: set BackendKind BEFORE AuthType. Assigning AuthType below triggers
        // OnAuthTypeChangedInternal, which reads BackendKind to decide whether to auto-fill the
        // Outlook.com IMAP/SMTP hosts. If AuthType were assigned first, a Graph account would
        // wrongly receive IMAP host defaults.
        BackendKind = value?.Kind ?? BackendKind.ImapSmtp;
        if (BackendKind == BackendKind.MicrosoftGraph)
        {
            // Graph accounts authenticate via OAuth and need no IMAP/SMTP host configuration.
            AuthType = AuthType.OAuth2Microsoft;
            ImapHost = string.Empty;
            SmtpHost = string.Empty;
            Password = string.Empty;
        }
    }

    protected override void OnAuthTypeChangedInternal(AuthType value)
    {
        // Auto-fill server settings for OAuth providers.
        if (value == AuthType.OAuth2Microsoft && BackendKind == BackendKind.ImapSmtp)
        {
            ImapHost              = "outlook.office365.com";
            ImapPort              = 993;
            ImapUseSsl            = true;
            ImapAcceptInvalidCert = false;
            SmtpHost              = "smtp-mail.outlook.com";
            SmtpPort              = 587;
            SmtpUseSsl            = false;
            SmtpAcceptInvalidCert = false;
            Password              = string.Empty;
        }
        else if (value == AuthType.OAuth2Google)
        {
            ImapHost              = "imap.gmail.com";
            ImapPort              = 993;
            ImapUseSsl            = true;
            ImapAcceptInvalidCert = false;
            SmtpHost              = "smtp.gmail.com";
            SmtpPort              = 587;
            SmtpUseSsl            = false;
            SmtpAcceptInvalidCert = false;
            Password              = string.Empty;
        }
    }

    public AccountModel ToAccountModel() => new()
    {
        AccountName = AccountName,
        DisplayName = DisplayName,
        Username = Username,
        AuthType = AuthType,
        BackendKind = BackendKind,
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
