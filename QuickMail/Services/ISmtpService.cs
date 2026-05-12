using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public interface ISmtpService
{
    Task SendAsync(ComposeModel compose, AccountModel account, string? password, CancellationToken ct = default);
}
