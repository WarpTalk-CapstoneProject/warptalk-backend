// =======================================================
// SubscriptionPlanRepository.cs
// =======================================================

using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;
public class SubscriptionPlanRepository : ISubscriptionPlanRepository
{
    private readonly BillingDbContext _dbContext;

    public SubscriptionPlanRepository(BillingDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<SubscriptionPlan?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _dbContext.SubscriptionPlans
            .FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<SubscriptionPlan?> GetByTypeAsync(PlanType type, CancellationToken ct = default)
    {
        return await _dbContext.SubscriptionPlans
            .FirstOrDefaultAsync(x => x.Type == type && x.IsActive, ct);
    }

    public async Task<IReadOnlyList<SubscriptionPlan>> GetAllActiveAsync(CancellationToken ct = default)
    {
        return await _dbContext.SubscriptionPlans
            .Where(x => x.IsActive)
            .ToListAsync(ct);
    }

    public async Task AddAsync(SubscriptionPlan entity, CancellationToken ct = default)
    {
        await _dbContext.SubscriptionPlans.AddAsync(entity, ct);
    }

    public Task UpdateAsync(SubscriptionPlan entity, CancellationToken ct = default)
    {
        _dbContext.SubscriptionPlans.Update(entity);
        return Task.CompletedTask;
    }
}