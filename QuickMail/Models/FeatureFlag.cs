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
    /// Offers Google sign-in for Gmail accounts: the "Gmail (sign in with Google)" provider entry
    /// and the Google OAuth item in the Authentication combo.
    ///
    /// Default: false as of v0.8.38. QuickMail's Google OAuth client stopped accepting new
    /// authorizations (#369, #226), so for almost everyone the option can only end in a failed
    /// sign-in — an app password is the path that works. The users authorized before it closed keep
    /// working, and turn this on to get the option back: Settings, Advanced, "Sign in with Google
    /// for Gmail accounts", or GoogleAuth=true under [features] in config.ini, or --feature
    /// GoogleAuth at launch.
    ///
    /// Only the OFFER is gated. An account already saved with Google OAuth authenticates normally
    /// whatever this is set to, and its Authentication combo still shows the option it is using —
    /// see AccountEditorViewModel.ShowGoogleAuthOption.
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
