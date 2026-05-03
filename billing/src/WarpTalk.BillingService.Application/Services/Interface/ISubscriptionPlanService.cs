using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Services.Interface;

public interface ISubscriptionPlanService
{
    Task<SubscriptionPlan?> GetByIdAsync(Guid planId, CancellationToken ct = default);
    Task<IReadOnlyList<SubscriptionPlan>> GetAllActiveAsync(CancellationToken ct = default);
    Task<bool> ExistsAsync(Guid planId, CancellationToken ct = default);
}