using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class CreditBalanceSnapshotRepository : GenericRepository<CreditBalanceSnapshot>, ICreditBalanceSnapshotRepository
{
    public CreditBalanceSnapshotRepository(BillingDbContext context) : base(context)
    {
    }

    public Task<CreditBalanceSnapshot?> GetLatestForSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        return _dbSet
            .Where(s => s.SubscriptionId == subscriptionId)
            .OrderByDescending(s => s.SnapshotAt)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
