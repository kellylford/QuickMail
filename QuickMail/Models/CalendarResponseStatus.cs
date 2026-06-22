namespace QuickMail.Models;

/// <summary>
/// The user's response to a calendar invitation.
/// Persisted in the CalendarEvent table so it survives beyond the reply email.
/// </summary>
public enum CalendarResponseStatus
{
    /// <summary>No response sent yet (invite received but not actioned).</summary>
    Pending = 0,

    /// <summary>User accepted the invite.</summary>
    Accepted = 1,

    /// <summary>User tentatively accepted the invite.</summary>
    Tentative = 2,

    /// <summary>User declined the invite.</summary>
    Declined = 3,
}