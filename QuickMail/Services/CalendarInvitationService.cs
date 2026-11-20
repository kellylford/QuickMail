using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <inheritdoc cref="ICalendarInvitationService"/>
public sealed class CalendarInvitationService : ICalendarInvitationService
{
    private readonly IGraphCalendarSyncService _calendarSync;
    private readonly ISendMailService _mail;
    private readonly ICredentialService _credentials;

    public CalendarInvitationService(IGraphCalendarSyncService calendarSync, ISendMailService mail,
                                     ICredentialService credentials)
    {
        _calendarSync = calendarSync;
        _mail = mail;
        _credentials = credentials;
    }

    public async Task<InvitationOutcome> AfterSaveAsync(AccountModel account, CalendarEvent saved, bool isNew,
                                                        CancellationToken ct = default)
    {
        if (!GraphCalendarSyncService.NotifiesAttendees(account, saved))
            return InvitationOutcome.None;

        // People only: a room is booked by the meeting, and its booking is the server's business.
        // An edit invites the people it added, sends the change to the people already invited, and
        // tells the people it removed that they are no longer expected.
        var people = EventAttendees.People(saved.Attendees);
        var before = isNew ? [] : EventAttendees.People(saved.PreviousAttendees);
        var added = people.Where(a => !before.Any(p => p.IsSameAddress(a.Email))).ToList();
        var kept = people.Where(a => before.Any(p => p.IsSameAddress(a.Email))).ToList();
        var removed = before.Where(p => !people.Any(a => a.IsSameAddress(p.Email))).ToList();
        var outcome = new InvitationOutcome(added.Count, kept.Count, removed.Count);
        if (outcome.IsNone) return outcome;

        return await TrySendAsync(account, outcome, async (password, token) =>
        {
            if (added.Count > 0)
                await SendAsync(account, password, saved, "REQUEST", added, "Invitation: " + Describe(saved), false, token);
            if (kept.Count > 0)
                await SendAsync(account, password, saved, "REQUEST", kept, "Updated invitation: " + Describe(saved), false, token);
            if (removed.Count > 0)
                await SendAsync(account, password, saved, "CANCEL", removed, "Cancelled: " + Describe(saved), false, token);
        }, ct);
    }

    /// <summary>
    /// How long QuickMail waits on the mail server for an invitation before reporting it unsent.
    /// The announcement waits on this, so a stalled server must not hold the user's feedback for
    /// as long as the mail library would.
    /// </summary>
    internal static TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Sends through <paramref name="send"/> unless the server already does, and turns a failure
    /// into the outcome's <see cref="InvitationOutcome.Error"/>. Only the caller's own cancellation
    /// escapes; running out of time is a failure to report like any other.
    /// </summary>
    private async Task<InvitationOutcome> TrySendAsync(AccountModel account, InvitationOutcome outcome,
        Func<string?, CancellationToken, Task> send, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SendTimeout);
        try
        {
            if (await _calendarSync.ServerSendsInvitationsAsync(account, timeout.Token))
                return outcome;
            await send(_credentials.GetPassword(account.Id), timeout.Token);
            return outcome;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LogService.Log($"CalendarInvitations: timed out sending for {account.AccountLabel}");
            return outcome with { Error = "the mail server did not answer in time." };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogService.Log($"CalendarInvitations: could not send for {account.AccountLabel}", ex);
            return outcome with { Error = ex.Message };
        }
    }

    public async Task<InvitationOutcome> AfterDeleteAsync(AccountModel account, CalendarEvent deleted,
                                                          CancellationToken ct = default)
    {
        var people = EventAttendees.People(deleted.Attendees);
        if (people.Count == 0 || !GraphCalendarSyncService.NotifiesAttendees(account, deleted))
            return InvitationOutcome.None;

        return await TrySendAsync(account, new InvitationOutcome(0, 0, people.Count), (password, token) =>
        {
            // The cancellation must be a newer version than the invitation the attendees hold.
            deleted.Sequence = IcsModel.NextSequence(deleted.Sequence);
            return SendAsync(account, password, deleted, "CANCEL", people, "Cancelled: " + Describe(deleted),
                             wholeEvent: true, token);
        }, ct);
    }

    private Task SendAsync(AccountModel account, string? password, CalendarEvent evt, string method,
                           IReadOnlyList<EventAttendee> recipients, string subject, bool wholeEvent,
                           CancellationToken ct)
    {
        // A meeting saved before anyone recorded its organizer is still the user's (that is why it
        // reached here): name them, or the message carries neither ORGANIZER nor ATTENDEE.
        if (string.IsNullOrWhiteSpace(evt.Organizer))
        {
            evt.Organizer = account.Username;
            evt.OrganizerName = account.SenderDisplayName;
        }
        var ics = IcsModel.ExportScheduling(evt, method, recipients, wholeEvent);
        var message = MimeMessageBuilder.BuildInvitation(account, ics, method, subject,
                                                         PlainText(evt, method), recipients);
        return _mail.SendPreparedAsync(message, account, password, ct);
    }

    /// <summary>"Team lunch, Tuesday, November 3, 2026 at 12:00 PM" — the subject's tail.</summary>
    internal static string Describe(CalendarEvent evt)
    {
        var title = string.IsNullOrWhiteSpace(evt.Summary) ? "Appointment" : evt.Summary.Trim();
        if (!evt.StartTime.HasValue) return title;
        return evt.IsAllDay
            ? $"{title}, {evt.StartTime.Value:dddd, MMMM d, yyyy}"
            : $"{title}, {evt.StartTime.Value:dddd, MMMM d, yyyy 'at' t}";
    }

    /// <summary>
    /// The readable part of the message, for a mail program that does not understand invitations
    /// and for anyone reading it as mail: what, when, where, who, and the notes.
    /// </summary>
    internal static string PlainText(CalendarEvent evt, string method)
    {
        var sb = new StringBuilder();
        if (string.Equals(method, "CANCEL", StringComparison.OrdinalIgnoreCase))
            sb.Append("This meeting has been cancelled.\n\n");
        sb.Append(string.IsNullOrWhiteSpace(evt.Summary) ? "Appointment" : evt.Summary.Trim()).Append('\n');
        if (evt.StartTime is { } start)
        {
            sb.Append("When: ");
            if (evt.IsAllDay)
            {
                sb.Append(start.ToString("dddd, MMMM d, yyyy")).Append(" (all day)");
                if (evt.EndTime is { } lastDay && lastDay.Date > start.Date)
                    sb.Append(" to ").Append(lastDay.ToString("dddd, MMMM d, yyyy"));
            }
            else
            {
                sb.Append(start.ToString("dddd, MMMM d, yyyy 'at' t"));
                if (evt.EndTime is { } end)
                    sb.Append(" to ").Append(end.Date == start.Date
                        ? end.ToString("t")
                        : end.ToString("dddd, MMMM d, yyyy 'at' t"));
                sb.Append(" (").Append(TimeZoneInfo.Local.StandardName).Append(')');
            }
            sb.Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(evt.Location))
            sb.Append("Location: ").Append(evt.Location.Trim()).Append('\n');
        var organizer = string.IsNullOrWhiteSpace(evt.OrganizerName) ? evt.Organizer : evt.OrganizerName;
        if (!string.IsNullOrWhiteSpace(organizer))
            sb.Append("Organizer: ").Append(organizer.Trim()).Append('\n');
        if (evt.HasAttendees)
            sb.Append("Invited: ").Append(string.Join(", ", evt.Attendees.Select(a => a.DisplayName))).Append('\n');
        if (!string.IsNullOrWhiteSpace(evt.Description))
            sb.Append('\n').Append(evt.Description.Trim()).Append('\n');
        return sb.ToString();
    }
}
