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

        // An edit reaches the people still invited with the update, and the people it removed with
        // a cancellation. A create has nobody to cancel.
        var invited = saved.Attendees;
        var removed = isNew
            ? []
            : saved.PreviousAttendees.Where(p => !invited.Any(a => a.IsSameAddress(p.Email))).ToList();
        var outcome = new InvitationOutcome(invited.Count, removed.Count);

        try
        {
            if (await _calendarSync.ServerSendsInvitationsAsync(account, ct))
                return outcome;

            var password = _credentials.GetPassword(account.Id);
            if (invited.Count > 0)
            {
                var updated = !isNew && saved.PreviousAttendees.Count > 0;
                await SendAsync(account, password, saved, "REQUEST", invited,
                                (updated ? "Updated invitation: " : "Invitation: ") + Describe(saved), ct);
            }
            if (removed.Count > 0)
                await SendAsync(account, password, saved, "CANCEL", removed, "Cancelled: " + Describe(saved), ct);
            return outcome;
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
        if (!deleted.HasAttendees || !GraphCalendarSyncService.NotifiesAttendees(account, deleted))
            return InvitationOutcome.None;

        var outcome = new InvitationOutcome(0, deleted.Attendees.Count);
        try
        {
            if (await _calendarSync.ServerSendsInvitationsAsync(account, ct))
                return outcome;

            // The cancellation must be a newer version than the invitation the attendees hold.
            deleted.Sequence = IcsModel.NextSequence(deleted.Sequence);
            await SendAsync(account, _credentials.GetPassword(account.Id), deleted, "CANCEL", deleted.Attendees,
                            "Cancelled: " + Describe(deleted), ct);
            return outcome;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogService.Log($"CalendarInvitations: could not send the cancellation for {account.AccountLabel}", ex);
            return outcome with { Error = ex.Message };
        }
    }

    private Task SendAsync(AccountModel account, string? password, CalendarEvent evt, string method,
                           IReadOnlyList<EventAttendee> recipients, string subject, CancellationToken ct)
    {
        var ics = IcsModel.ExportScheduling(evt, method, recipients);
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
