using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface IIdempotencyRepository : IGenericRepository<IdempotencyRecord>
{
    Task<IdempotencyRecord?> GetAsync(string key, string operation, CancellationToken ct = default);
}