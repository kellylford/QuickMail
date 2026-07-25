namespace QuickMail.Models;

/// <summary>
/// Feature-gate keys. Adding a new gate is one enum value here plus an entry
/// in ConfigFeatureGate.Defaults.
/// </summary>
public enum FeatureFlag
{
    /// <summary>
    /// Enables Microsoft Graph as a mail-backend option in the Add Account dialog.
    /// Default: true (on by default from v0.8.35). Disable in config.ini with
    /// GraphBackend=false under [features], or at launch with --no-feature GraphBackend.
    /// </summary>
    GraphBackend,

    /// <summary>
    /// Shows the Google OAuth (Gmail) option in Add Account and Account Manager dialogs.
    /// Default: true. Disable in config.ini with GoogleAuth=false under [features].
    /// </summary>
    GoogleAuth,

    /// <summary>
    /// Shows server-side (Exchange/Graph) Inbox rules in the Rules Manager for Microsoft 365
    /// accounts (#333). Default: false while the feature is built out — enable at launch with
    /// --feature ServerRules or in config.ini with ServerRules=true under [features]. Flip the
    /// default to true via a joint-decision PR once create/edit/delete and the unified per-account
    /// window are complete.
    /// </summary>
    ServerRules,
}
