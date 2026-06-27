using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public interface IUpdateCheckService
{
    Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default);
}
