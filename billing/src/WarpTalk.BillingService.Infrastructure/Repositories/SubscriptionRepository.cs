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
                .SetProperty(s => s.Status, SubscriptionConstants.SubscriptionStatuses.Cancelled)
                .SetProperty(s => s.UpdatedAt, DateTime.UtcNow),
                cancellationToken);
    }

    public async Task<PagedResult<Subscription>> GetPageAsync(PageRequest page, CancellationToken cancellationToken = default)
    {
        var normalized = RepositoryPaging.Normalize(page);
        var total = await _dbSet.CountAsync(cancellationToken);
        var items = await _dbSet
            .OrderByDescending(s => s.CreatedAt)
            .Skip(normalized.Skip)
            .Take(normalized.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<Subscription>(items, total, normalized.PageNumber, normalized.PageSize);
    }

    public async Task<IReadOnlyList<Subscription>> GetActiveSubscriptionsAsync(CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Where(s => s.IsActive && s.DeletedAt == null)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Subscription>> GetDueForRenewalAsync(
        DateTime renewalThreshold,
        DateTime lowerBound,
        CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Include(s => s.Plan)
            .Where(s =>
                s.IsActive &&
                s.DeletedAt == null &&
                s.AutoRenew &&
                s.Status == SubscriptionConstants.SubscriptionStatuses.Active &&
                s.CurrentPeriodEnd <= renewalThreshold &&
                s.CurrentPeriodEnd > lowerBound)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Subscription>> GetExpiredActiveSubscriptionsAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Where(s => s.IsActive && s.DeletedAt == null && s.CurrentPeriodEnd < now)
            .ToListAsync(cancellationToken);
    }

    public async Task<Subscription?> GetActiveByWorkspaceIdAsync(
        Guid workspaceId,
        bool includePlan = true,
        bool requireActivePeriod = false,
        CancellationToken cancellationToken = default)
    {
        var query = _dbSet.AsQueryable();

        if (includePlan)
        {
            query = query.Include(s => s.Plan);
        }

        return await query
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(s =>
                s.WorkspaceId == workspaceId &&
                s.IsActive &&
                s.DeletedAt == null &&
                (!requireActivePeriod || s.CurrentPeriodEnd >= DateTime.UtcNow), cancellationToken);
    }
}
