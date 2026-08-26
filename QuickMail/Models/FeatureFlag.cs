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
    /// Makes Microsoft 365 (Graph) the default connection method for a NEW Microsoft account —
    /// personal Outlook.com included, not only work/school — instead of IMAP. IMAP/SMTP stays
    /// selectable under Advanced → Connection method, and a hand-pick there is always honored.
    /// Effective only when <see cref="GraphBackend"/> is also on (Graph must be an offered backend);
    /// "Graph is offered" vs "Graph is the default" are the two distinct knobs.
    ///
    /// Default: false. Ships off so the broad release keeps today's behavior (personal → IMAP);
    /// testers turn it on, and the default is flipped in a later release once personal-Graph has
    /// mileage (#529 step 3). Only the DEFAULT for new accounts changes — existing accounts are not
    /// touched. Turn it on with MicrosoftGraphDefault=true under [features] in config.ini, or
    /// --feature MicrosoftGraphDefault at launch.
    /// </summary>
    MicrosoftGraphDefault,

    /// <summary>
    /// Offers the opt-in "Convert to Microsoft 365 (Graph)" command in the Account Manager, which moves
    /// an existing Microsoft IMAP account onto the Graph backend in place (#529 step 4). Effective only
    /// when <see cref="GraphBackend"/> is also on. Nothing converts automatically; the command is per
    /// account and asks first.
    ///
    /// Default: false. Ships off while the convert is exercised by testers — it purges and re-downloads
    /// the account's local cache, so it stays opt-in until it has real mileage. Turn it on with
    /// MicrosoftGraphMigration=true under [features] in config.ini, or --feature MicrosoftGraphMigration
    /// at launch.
    /// </summary>
    MicrosoftGraphMigration,

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
    /// Enables shared mailboxes (#31): the "Add shared…" button in the Account Manager, which is the
    /// sole path that can create a shared <see cref="QuickMail.Models.AccountModel"/> (IsShared). This
    /// gates the whole feature, because everything downstream — the shared tree node, aggregate
    /// exclusion, the connect-skip, cascade removal — is data-driven off a shared account that only the
    /// button can produce; with no shared account, every IsShared branch is a no-op.
    ///
    /// Default: true as of 0.8.42 — the feature is complete across its PRs (linked-account model,
    /// reading and send-as, per-account notification opt-in, and parent-account consent). Set
    /// SharedMailboxes=false under [features] in config.ini, or --no-feature SharedMailboxes at launch,
    /// to hide it again.
    /// </summary>
    SharedMailboxes,

    /// <summary>
    /// Offers POP3/SMTP as a connection method in the Add Account dialog (#128).
    ///
    /// Only the OFFER is gated, exactly as with <see cref="GoogleAuth"/>: an account already saved
    /// with <see cref="QuickMail.Models.BackendKind.Pop3Smtp"/> connects, syncs and sends normally
    /// whatever this is set to, and the Account Manager still shows its POP3 server settings. Turning
    /// the flag off after adding an account therefore hides the option without stranding the mail.
    ///
    /// Default: false. POP3 puts the only copy of a message in the local store, so it stays opt-in
    /// until it has run against real servers. Turn it on with Pop3Backend=true under [features] in
    /// config.ini, or --feature Pop3Backend at launch.
    /// </summary>
    Pop3Backend,
}
