using WarpTalk.BillingService.Domain.Constants;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Domain.Services;

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

    /// <summary>
    /// The directory's WHERE clause. Public so tests can run it over plain rows and ask Npgsql to
    /// translate it (ToQueryString) without a database.
    /// </summary>
    public static IQueryable<Subscription> ApplyAdminFilters(
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

        if (!string.IsNullOrWhiteSpace(filter.ServiceState))
        {
            var serviceState = filter.ServiceState;
            query = query.Where(s => s.ServiceState == serviceState);
        }

        if (!string.IsNullOrWhiteSpace(filter.BillingCycle))
        {
            var cycle = filter.BillingCycle;
            query = query.Where(s => s.Plan.BillingCycle == cycle);
        }

        if (filter.AutoRenew is { } autoRenew)
            query = query.Where(s => s.AutoRenew == autoRenew);

        if (filter.WorkspaceId is { } workspaceId)
            query = query.Where(s => s.WorkspaceId == workspaceId);

        // Half-open: inclusive from, exclusive to.
        if (filter.PeriodEndFrom is { } periodEndFrom)
            query = query.Where(s => s.CurrentPeriodEnd >= periodEndFrom);

        if (filter.PeriodEndTo is { } periodEndTo)
            query = query.Where(s => s.CurrentPeriodEnd < periodEndTo);

        return query;
    }

    /// <summary>The directory's ORDER BY. Public for the same reason as <see cref="ApplyAdminFilters"/>.</summary>
    public static IQueryable<Subscription> ApplyAdminSort(IQueryable<Subscription> query, string sort)
        => sort switch
        {
            "period_end_desc" => query.OrderByDescending(s => s.CurrentPeriodEnd),
            "created_desc" => query.OrderByDescending(s => s.CreatedAt),
            "created_asc" => query.OrderBy(s => s.CreatedAt),
            "credits_asc" => query.OrderBy(s => s.CreditsRemaining),
            "credits_desc" => query.OrderByDescending(s => s.CreditsRemaining),
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
        // #466: invoice rows only. A card customer's renewal is Stripe's to charge and grant.
        return await _dbSet
            .Include(s => s.Plan)
            .Where(SubscriptionOwnership.DueForCycleClose(renewalThreshold, lowerBound))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Subscription>> GetExpiredActiveSubscriptionsAsync(
        DateTime now,
        TimeSpan renewalLookback,
        TimeSpan stripeSafetyMargin,
        CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Where(SubscriptionOwnership.DueForExpiry(now, renewalLookback, stripeSafetyMargin))
            .ToListAsync(cancellationToken);
    }

    public async Task<Subscription?> GetByStripeSubscriptionIdAsync(
        string stripeSubscriptionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stripeSubscriptionId))
        {
            return null;
        }

        return await _dbSet
            .Include(s => s.Plan)
            .FirstOrDefaultAsync(s => s.StripeSubscriptionId == stripeSubscriptionId, cancellationToken);
    }

    public async Task<IReadOnlyList<Subscription>> GetUnlinkedCardSubscriptionsAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Where(s => s.IsActive
                        && s.DeletedAt == null
                        && s.RenewalMode == SubscriptionConstants.RenewalModes.None
                        && s.StripeSubscriptionId == null)
            .OrderBy(s => s.CurrentPeriodEnd)
            .Take(limit)
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

    // ── Admin Insights (2026-09-17) ──────────────────────────────────────────

    public async Task<SubscriptionFlowCounts> GetSubscriptionFlowCountsAsync(
        DateTime from, DateTime to, DateTime now, CancellationToken ct = default)
    {
        var payments = _context.Payments.IgnoreQueryFilters();
        var everySubscription = _dbSet.IgnoreQueryFilters();

        // Soft-deleted subscriptions are excluded by the query filter: a deleted row is not a
        // customer that joined or left.
        var lifecycle = _dbSet.AsNoTracking().Select(s => new
        {
            s.Id,
            s.WorkspaceId,
            s.CreatedAt,
            s.TrialEndsAt,
            Start = s.ContractPriceVnd != null
                ? (DateTime?)s.CreatedAt
                : payments
                    .Where(p => p.SubscriptionId == s.Id && p.Status == PaymentConstants.PaymentStatuses.Paid)
                    .Min(p => (DateTime?)(p.PaidAt ?? p.UpdatedAt)),
            // cancel-at-period-end (SubscriptionMapper.Cancel) never stamps cancelled_at, and
            // updated_at moves on every usage charge, so the end of the paid period is the only
            // stable date for it — and it is also when the revenue actually stops.
            End = s.Status == SubscriptionConstants.SubscriptionStatuses.Cancelled
                  || s.Status == SubscriptionConstants.SubscriptionStatuses.Expired
                ? (DateTime?)(s.CancelledAt ?? s.CurrentPeriodEnd)
                : null,
        });

        var cancelledBefore = to < now ? to : now;

        var newSubscriptions = await lifecycle.CountAsync(x => x.Start >= from && x.Start < to, ct);
        var trialsStarted = await lifecycle.CountAsync(
            x => x.Start == null && x.TrialEndsAt != null && x.CreatedAt >= from && x.CreatedAt < to, ct);
        var cancelled = await lifecycle.CountAsync(
            x => x.Start != null
                 && x.End >= from
                 && x.End < cancelledBefore
                 && !everySubscription.Any(o =>
                     o.WorkspaceId == x.WorkspaceId
                     && o.Id != x.Id
                     && o.CreatedAt >= x.End!.Value.AddHours(-1)
                     && o.CreatedAt <= x.End!.Value.AddHours(1)),
            ct);
        var activeAtStart = await lifecycle.CountAsync(
            x => x.Start != null && x.Start < from && (x.End == null || x.End >= from), ct);

        return new SubscriptionFlowCounts(newSubscriptions, trialsStarted, cancelled, activeAtStart);
    }

    public async Task<IReadOnlyList<EndingSoonSubscriptionRow>> GetEndingSoonAsync(
        DateTime now, DateTime until, int take, CancellationToken ct = default)
    {
        var rows = await _dbSet
            .AsNoTracking()
            .Where(s =>
                s.IsActive
                && s.CurrentPeriodEnd > now
                && s.CurrentPeriodEnd <= until
                // A trial's end is reported as trialsEndingThisWeek, not as a renewal.
                && !(s.TrialEndsAt != null && s.TrialEndsAt > now))
            .OrderBy(s => s.CurrentPeriodEnd)
            .ThenBy(s => s.Id)
            .Take(take)
            .Select(s => new { s.WorkspaceId, PlanName = s.Plan.Name, s.CurrentPeriodEnd, s.AutoRenew, s.Status })
            .ToListAsync(ct);

        return rows
            .Select(r => new EndingSoonSubscriptionRow(r.WorkspaceId, r.PlanName, r.CurrentPeriodEnd, r.AutoRenew, r.Status))
            .ToList();
    }

    public async Task<IReadOnlyList<InboxSubscriptionRow>> GetNeedingAttentionAsync(
        DateTime now, DateTime until, int take, CancellationToken ct = default)
    {
        var rows = await _dbSet
            .AsNoTracking()
            .Where(s => s.IsActive && (
                (s.TrialEndsAt != null && s.TrialEndsAt > now && s.TrialEndsAt <= until)
                || (!s.AutoRenew && s.CurrentPeriodEnd > now && s.CurrentPeriodEnd <= until)
                || s.ServiceState == SubscriptionConstants.ServiceStates.Suspended))
            .OrderBy(s => s.TrialEndsAt ?? s.CurrentPeriodEnd)
            .Take(take)
            .Select(s => new
            {
                s.Id, s.WorkspaceId, PlanName = s.Plan.Name, s.TrialEndsAt, s.CurrentPeriodEnd, s.AutoRenew,
                s.ServiceState, s.SuspendedReason, s.UpdatedAt,
            })
            .ToListAsync(ct);

        return rows
            .Select(r => new InboxSubscriptionRow(
                r.Id, r.WorkspaceId, r.PlanName, r.TrialEndsAt, r.CurrentPeriodEnd, r.AutoRenew, r.ServiceState, r.SuspendedReason, r.UpdatedAt))
            .ToList();
    }
}
