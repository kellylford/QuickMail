namespace QuickMail.Helpers;

/// <summary>
/// Focus hints for the account form fields that Add Account and Account Manager both show.
///
/// <para>The two dialogs render the same field stack and so want the same guidance, but each keeps
/// its own <c>HintFor</c> — the lookup is by control reference, which is per-window. Only the strings
/// are shared, and they are shared here because a hint that drifts between the two dialogs describes
/// the same control two different ways depending on which window the user opened it from.</para>
///
/// <para>Hints, not <c>AutomationProperties.Name</c>: a name must stay a short label, and delivering
/// guidance as an <c>AnnouncementCategory.Hint</c> is what keeps the user's announcement preference
/// in charge of whether they hear it. See the Accessibility Checklist in CLAUDE.md.</para>
/// </summary>
internal static class AccountFieldHints
{
    public const string AccountName =
        "Leave blank to use your email address.";

    public const string Password =
        "Stored in Windows Credential Manager.";

    public const string LoginUsername =
        "Leave blank unless your mail server logs in under a different name than your email address.";

    public const string SyncCalendar =
        "Shows this account's calendar in the Calendar view.";

    /// <summary>The ports are in the checkbox's visible Content, but an explicit
    /// <c>AutomationProperties.Name</c> OVERRIDES that text — so without this the port guidance would
    /// exist for sighted users only.</summary>
    public const string SmtpImplicitSsl =
        "Checked uses port 465. Cleared uses STARTTLS on port 587.";

    /// <summary>POP3 hands the local store the only copy of a message once the server drops it, and
    /// that is what this checkbox decides — the consequence is the point of the control, so it is
    /// spelled out rather than left to a label that must stay short.</summary>
    public const string Pop3LeaveOnServer =
        "Cleared, mail is removed from the server once QuickMail has downloaded it, " +
        "so this computer holds the only copy.";

    public const string Signature =
        "Added to the end of new messages, replies, and forwards.";
}
