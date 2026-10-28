using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// What happened to a meeting's invitations after a save or delete (issue #866), for the
/// announcement. <paramref name="Invited"/> people were sent the invitation or its update and
/// <paramref name="Cancelled"/> people a cancellation. <paramref name="Error"/> is set when
/// QuickMail had to send them itself and could not; the appointment is saved either way.
/// </summary>
public sealed record InvitationOutcome(int Invited, int Cancelled, string? Error = null)
{
    public static readonly InvitationOutcome None = new(0, 0);

    /// <summary>True when nobody was, or was meant to be, told anything.</summary>
    public bool IsNone => Invited == 0 && Cancelled == 0 && Error is null;
}

/// <summary>
/// Gets a meeting's invitations to its attendees after the appointment has been written to the
/// account's calendar (issue #866). Where the calendar server delivers them itself — Microsoft,
/// Google, an iCloud server that schedules — this only reports who it reached. Where it does not,
/// it emails them from the account: a REQUEST to everyone invited, and a CANCEL to anyone the edit
/// removed or the delete uninvited.
/// </summary>
public interface ICalendarInvitationService
{
    /// <summary>
    /// After a create (<paramref name="isNew"/>) or an update of <paramref name="saved"/>, whose
    /// <see cref="CalendarEvent.Attendees"/> are the people invited now and whose
    /// <see cref="CalendarEvent.PreviousAttendees"/> are the people invited before the edit.
    /// Never throws.
    /// </summary>
    Task<InvitationOutcome> AfterSaveAsync(AccountModel account, CalendarEvent saved, bool isNew,
                                           CancellationToken ct = default);

    /// <summary>After <paramref name="deleted"/> was removed from the calendar. Never throws.</summary>
    Task<InvitationOutcome> AfterDeleteAsync(AccountModel account, CalendarEvent deleted,
                                             CancellationToken ct = default);
}
