using WarpTalk.BillingService.Domain.Constants;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;

using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class SubscriptionRepository : GenericRepository<Subscription>, ISubscriptionRepository
{
    public SubscriptionRepository(BillingDbContext context) : base(context)
    {
    }

    public async Task DeactivateOtherActiveSubscriptionsAsync(Guid userId, Guid excludeSubscriptionId, CancellationToken cancellationToken)
    {
        await _dbSet
            .Where(s => s.UserId == userId && s.IsActive && s.Id != excludeSubscriptionId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.AutoRenew, false)
                .SetProperty(s => s.Status, BillingConstants.SubscriptionStatuses.Cancelled)
                .SetProperty(s => s.UpdatedAt, DateTime.UtcNow),
                cancellationToken);
    }
}
