using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public interface ISendMailService
{
    Task SendAsync(ComposeModel compose, AccountModel account, string? password, CancellationToken ct = default);

    /// <summary>
    /// Sends an ICS calendar reply (accept/decline/tentative) to the event organizer.
    /// The <paramref name="icsReplyContent"/> is a full iCalendar REPLY payload.
    /// </summary>
    Task SendIcsReplyAsync(string icsReplyContent, AccountModel account, string? password,
        string organizerEmail, CancellationToken ct = default);
}
