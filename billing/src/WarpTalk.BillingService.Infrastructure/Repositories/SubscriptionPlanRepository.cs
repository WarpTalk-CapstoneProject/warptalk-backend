using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
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

    public async Task<SubscriptionPlan?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.SubscriptionPlans
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    public async Task<IEnumerable<SubscriptionPlan>> GetAllActiveAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.SubscriptionPlans
            .Where(p => p.IsActive)
            .ToListAsync(cancellationToken);
    }
}
