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

    public async Task<(IReadOnlyList<AdminSubscriptionRow> Items, int Total)> GetAdminDirectoryAsync(
        AdminSubscriptionFilter filter,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var query = ApplyAdminFilters(_dbSet.AsNoTracking(), filter);

        var total = await query.CountAsync(ct);

        // The sort is applied to the ENTITY, before the projection — which is what makes
        // projecting straight into a positional record safe here.
        //
        // Ordering a record projection by one of its OWN properties does not translate: EF cannot
        // map a constructor parameter back to the expression it came from. That defect shipped in
        // this very service, where usage-by-member returned 500 on every call it ever served.
        // Verified by translating each shape in isolation: the same projection without a trailing
        // OrderBy translates fine, and this has none.
        var rows = await ApplyAdminSort(query, filter.Sort)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new AdminSubscriptionRow(
                s.Id,
                s.WorkspaceId,
                s.Status,
                s.ServiceState,
                s.SuspendedReason,
                s.Plan.Name,
                s.Plan.Slug,
                s.Plan.Tier,
                s.Plan.BillingCycle,
                s.Plan.Price,
                s.Plan.Currency,
                s.ContractPriceVnd,
                s.CreditsRemaining,
                s.CreditsUsedThisCycle,
                s.CurrentPeriodStart,
                s.CurrentPeriodEnd,
                s.AutoRenew,
                s.TrialEndsAt,
                s.CancelledAt,
                s.CreatedAt))
            .ToListAsync(ct);

        return (rows, total);
    }

    public async Task<IReadOnlyList<AdminSubscriptionRow>> GetActiveForRevenueAsync(
        CancellationToken ct = default)
    {
        var rows = await _dbSet
            .AsNoTracking()
            .Where(s =>
                s.DeletedAt == null
                && s.Status == SubscriptionConstants.SubscriptionStatuses.Active)
            .Select(s => new AdminSubscriptionRow(
                s.Id,
                s.WorkspaceId,
                s.Status,
                s.ServiceState,
                s.SuspendedReason,
                s.Plan.Name,
                s.Plan.Slug,
                s.Plan.Tier,
                s.Plan.BillingCycle,
                s.Plan.Price,
                s.Plan.Currency,
                s.ContractPriceVnd,
                s.CreditsRemaining,
                s.CreditsUsedThisCycle,
                s.CurrentPeriodStart,
                s.CurrentPeriodEnd,
                s.AutoRenew,
                s.TrialEndsAt,
                s.CancelledAt,
                s.CreatedAt))
            .ToListAsync(ct);

        return rows;
    }

    private static IQueryable<Subscription> ApplyAdminFilters(
        IQueryable<Subscription> query,
        AdminSubscriptionFilter filter)
    {
        // A soft-deleted subscription is not a state anyone can act on, and counting it would make
        // every figure here disagree with billing's own.
        query = query.Where(s => s.DeletedAt == null);

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            var status = filter.Status;
            query = query.Where(s => s.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(filter.PlanSlug))
        {
            var slug = filter.PlanSlug;
            query = query.Where(s => s.Plan.Slug == slug);
        }

        return query;
    }

    private static IQueryable<Subscription> ApplyAdminSort(IQueryable<Subscription> query, string sort)
        => sort switch
        {
            "period_end_desc" => query.OrderByDescending(s => s.CurrentPeriodEnd),
            "created_desc" => query.OrderByDescending(s => s.CreatedAt),
            "created_asc" => query.OrderBy(s => s.CreatedAt),
            "credits_asc" => query.OrderBy(s => s.CreditsRemaining),
            // Soonest renewal first: the default, because the question this screen answers is
            // "what needs attention", and what needs attention is what runs out next.
            _ => query.OrderBy(s => s.CurrentPeriodEnd),
        };

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
