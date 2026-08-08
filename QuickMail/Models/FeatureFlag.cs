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
    /// Default: false as of v0.8.37. QuickMail's Google OAuth client stopped accepting new
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
    /// accounts (#333). Default: true — server-rule list/create/edit/delete/reorder and the unified
    /// per-account rules window are complete. Rules the API can't fully round-trip are surfaced
    /// read-only (see GraphServerRuleService.ListAsync). Turn the surface off again with
    /// ServerRules=false under [features] in config.ini, or --no-feature ServerRules at launch.
    /// </summary>
    ServerRules,

    /// <summary>
    /// Enables shared mailboxes (#31): the "Add shared…" button in the Account Manager, which is the
    /// sole path that can create a shared <see cref="QuickMail.Models.AccountModel"/> (IsShared). This
    /// gates the whole feature, because everything downstream — the shared tree node, aggregate
    /// exclusion, the connect-skip, cascade removal — is data-driven off a shared account that only the
    /// button can produce; with no shared account, every IsShared branch is a no-op.
    ///
    /// Default: false while the feature is built across multiple PRs (PR 1 is the linked-account model
    /// and manual add only — no backend access yet, so a shared node has no folders). Turn it on to
    /// test with SharedMailboxes=true under [features] in config.ini, or --feature SharedMailboxes at
    /// launch. Flips to true by default once the feature is complete.
    /// </summary>
    SharedMailboxes,
}
