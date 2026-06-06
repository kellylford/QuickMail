namespace QuickMail.Models;

/// <summary>
/// Which protocol stack this account uses. Selected when the account is added
/// and fixed for its lifetime. To switch, delete and re-add the account.
/// </summary>
public enum BackendKind
{
    /// <summary>Standard IMAP for receive + SMTP for send (default for all existing accounts).</summary>
    ImapSmtp,

    /// <summary>Microsoft Graph for receive + send. Used for M365 / Outlook.com.</summary>
    MicrosoftGraph,
}
