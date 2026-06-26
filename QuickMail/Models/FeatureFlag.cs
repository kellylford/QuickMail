namespace QuickMail.Models;

/// <summary>
/// Feature-gate keys. Adding a new gate is one enum value here plus an entry
/// in ConfigFeatureGate.Defaults.
/// </summary>
public enum FeatureFlag
{
    /// <summary>
    /// Enables Microsoft Graph as a mail-backend option in the Add Account dialog.
    /// Default: false. Flip the default to true via a future joint-decision PR.
    /// </summary>
    GraphBackend,

    /// <summary>
    /// Shows the Google OAuth (Gmail) option in Add Account and Account Manager dialogs.
    /// Default: true. Disable in config.ini with GoogleAuth=false under [features].
    /// </summary>
    GoogleAuth,

    /// <summary>
    /// Enables POP3/SMTP as a backend option in the Add Account dialog.
    /// Default: false. Enable in config.ini with Pop3Backend=true under [features].
    /// </summary>
    Pop3Backend,
}
