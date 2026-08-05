namespace QuickMail.Models;

/// <summary>
/// A user's answer to a meeting invitation, as the UI expresses it. The ICS PARTSTAT value and the
/// wording of the confirmation are derived from this in the ViewModel, so no View has to carry
/// protocol constants or user-facing text.
/// </summary>
public enum InviteResponse
{
    Accept,
    Tentative,
    Decline,
}
