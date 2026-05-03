using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface ISubscriptionPlanRepository
{
    Task<SubscriptionPlan?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<SubscriptionPlan?> GetByTypeAsync(PlanType type, CancellationToken ct = default);

    Task<IReadOnlyList<SubscriptionPlan>> GetAllActiveAsync(CancellationToken ct = default);

    Task AddAsync(SubscriptionPlan entity, CancellationToken ct = default);

    Task UpdateAsync(SubscriptionPlan entity, CancellationToken ct = default);
}