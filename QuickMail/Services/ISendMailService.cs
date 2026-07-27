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

    /// <summary>
    /// Proves the outgoing settings work: connects, authenticates, disconnects. Sends nothing.
    /// Throws on failure so the caller can report the server's own message.
    ///
    /// This exists because Test Connection used to probe IMAP only, which meant an account could be
    /// added, appear healthy, and then fail on the first send. It matters more now that servers can
    /// come from auto-discovery rather than from the user.
    /// </summary>
    Task VerifyAsync(AccountModel account, string? password, CancellationToken ct = default);
}
