using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IIdempotencyService
{
    Task<string?> GetResponseJsonAsync(IdempotencyKey key, CancellationToken cancellationToken = default);

    Task StoreResponseJsonAsync(
        IdempotencyKey key,
        string responseJson,
        Guid? workspaceId = null,
        CancellationToken cancellationToken = default);
}
