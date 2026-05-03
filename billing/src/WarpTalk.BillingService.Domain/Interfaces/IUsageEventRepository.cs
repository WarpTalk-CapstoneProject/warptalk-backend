// =======================================================
// IUsageEventRepository.cs
// =======================================================

using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface IUsageEventRepository
{
    Task AddAsync(UsageEvent entity, CancellationToken ct = default);

    Task<UsageEvent?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<bool> ExistsByIdempotencyKeyAsync(string key, CancellationToken ct = default);

    Task<IReadOnlyList<UsageEvent>> GetPendingAsync(int take, CancellationToken ct = default);

    Task UpdateAsync(UsageEvent entity, CancellationToken ct = default);
}