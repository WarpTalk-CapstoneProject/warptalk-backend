using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IBillingPolicyRepository
{

    Task<decimal> ReadPolicyValueAsync(string key, decimal seedValue, CancellationToken cancellationToken = default);
    Task UpsertPolicyValueAsync(string key, decimal value, CancellationToken cancellationToken = default);
}
