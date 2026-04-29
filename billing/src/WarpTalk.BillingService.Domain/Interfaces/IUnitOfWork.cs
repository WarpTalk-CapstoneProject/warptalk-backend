using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
