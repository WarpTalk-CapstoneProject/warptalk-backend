using WarpTalk.BillingService.Domain.Constants;
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

public class CreditTransactionRepository : GenericRepository<CreditTransaction>, ICreditTransactionRepository
{
    public CreditTransactionRepository(BillingDbContext context) : base(context)
    {
    }

    public async Task<PagedResult<CreditTransaction>> GetHistoryPageAsync(CreditTransactionHistoryFilter filter, CancellationToken cancellationToken = default)
    {
        var normalized = RepositoryPaging.Normalize(filter.Page);
        var filtered = ApplyHistoryFilters(_dbSet.Include(t => t.Subscription), filter);

        var total = await filtered.CountAsync(cancellationToken);
        var items = await filtered
            .OrderByDescending(t => t.CreatedAt)
            .Skip(normalized.Skip)
            .Take(normalized.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<CreditTransaction>(items, total, normalized.PageNumber, normalized.PageSize);
    }

    public Task<CreditTransaction?> GetLatestBeforeAsync(Guid subscriptionId, DateTime before, CancellationToken cancellationToken = default)
    {
        return _dbSet
            .Where(tx => tx.SubscriptionId == subscriptionId && tx.CreatedAt < before)
            .OrderByDescending(tx => tx.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static IQueryable<CreditTransaction> ApplyHistoryFilters(
        IQueryable<CreditTransaction> source,
        CreditTransactionHistoryFilter filter)
    {
        var filtered = source;

        if (filter.SubscriptionIds is { Count: > 0 })
            filtered = filtered.Where(t => filter.SubscriptionIds.Contains(t.SubscriptionId));

        if (filter.WorkspaceId.HasValue)
            filtered = filtered.Where(t => t.WorkspaceId == filter.WorkspaceId.Value);

        if (!string.IsNullOrEmpty(filter.Type))
            filtered = filtered.Where(t => t.Type == filter.Type);

        if (filter.FromDate.HasValue)
            filtered = filtered.Where(t => t.CreatedAt >= filter.FromDate.Value);

        if (filter.ToDate.HasValue)
            filtered = filtered.Where(t => t.CreatedAt <= filter.ToDate.Value);

        if (filter.MinAmount.HasValue)
            filtered = filtered.Where(t => Math.Abs(t.Amount) >= filter.MinAmount.Value);

        if (filter.MaxAmount.HasValue)
            filtered = filtered.Where(t => Math.Abs(t.Amount) <= filter.MaxAmount.Value);

        return filtered;
    }

    // ── Admin Insights (2026-09-17) ──────────────────────────────────────────
    //
    // Consume rows only. Soft-delete query filters are ignored: usage on a since-deleted
    // subscription was still consumed (and still cost the provider).

    public async Task<ConsumptionTotals> GetConsumptionTotalsAsync(
        DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        var rows =
            from t in ConsumeIn(@from, to)
            join u in _context.UsageRecords.IgnoreQueryFilters() on t.UsageRecordId equals (Guid?)u.Id into usage
            from u in usage.DefaultIfEmpty()
            join r in _context.UsageRateCards on t.PricingRateCardId equals (Guid?)r.Id into cards
            from r in cards.DefaultIfEmpty()
            select new
            {
                // Same key as GetConsumedByChargeTypeAsync, so the coverage note and the
                // credits-by-type breakdown name usage the same way.
                Type = t.ChargeType != null && t.ChargeType != "" ? t.ChargeType : (u != null ? u.UsageType : "UNKNOWN"),
                Credits = -t.Amount,
                // The part of this charge that went below a zero balance. settle_usage_charge:
                // prev >= 0 → max(0, credits - prev); prev < 0 → credits. With
                // prev = balance_after - amount, both reduce to max(0, -after) - max(0, -prev).
                Overage = Math.Max(0, -t.BalanceAfter) - Math.Max(0, t.Amount - t.BalanceAfter),
                // The card the row was settled on — CRD or VND alike — must carry a cost in the
                // unit the usage was metered in. A card with no unit predates Phase 2 and is
                // taken at its word.
                Covered = u != null && r != null && r.ProviderUnitCost != null && (r.Unit == null || r.Unit == u.Unit),
                CostUsd = (decimal?)u!.Quantity * r!.ProviderUnitCost,
            };

        // One row per charge type; the grand totals are their sums.
        var byType = await rows
            .GroupBy(x => x.Type)
            .Select(g => new
            {
                Type = g.Key,
                Credits = g.Sum(x => (long)x.Credits),
                Transactions = g.Count(),
                Overage = g.Sum(x => (long)x.Overage),
                CoveredCredits = g.Sum(x => x.Covered ? (long)x.Credits : 0L),
                CoveredTransactions = g.Count(x => x.Covered),
                CostUsd = g.Sum(x => x.Covered ? x.CostUsd : 0m),
            })
            .ToListAsync(cancellationToken);

        return new ConsumptionTotals(
            byType.Sum(x => x.Credits),
            byType.Sum(x => x.Transactions),
            byType.Sum(x => x.Overage),
            byType.Sum(x => x.CoveredCredits),
            byType.Sum(x => x.CoveredTransactions),
            byType.Sum(x => x.CostUsd ?? 0m),
            byType
                .Select(x => new ChargeTypeCoverage(x.Type, x.Credits, x.CoveredCredits))
                .OrderByDescending(x => x.Credits)
                .ThenBy(x => x.ChargeType, StringComparer.Ordinal)
                .ToList());
    }

    public async Task<IReadOnlyList<DailyChargeTypeConsumption>> GetConsumptionByUtcDayAsync(
        DateTime from, DateTime to, IReadOnlyCollection<string> chargeTypes, CancellationToken cancellationToken = default)
    {
        var types = chargeTypes.ToArray();
        if (types.Length == 0) return Array.Empty<DailyChargeTypeConsumption>();

        // Same type key and coverage rule as GetConsumptionTotalsAsync, so the rows this returns are
        // exactly the part of those totals they describe.
        var rows =
            from t in ConsumeIn(@from, to)
            join u in _context.UsageRecords.IgnoreQueryFilters() on t.UsageRecordId equals (Guid?)u.Id into usage
            from u in usage.DefaultIfEmpty()
            join r in _context.UsageRateCards on t.PricingRateCardId equals (Guid?)r.Id into cards
            from r in cards.DefaultIfEmpty()
            select new
            {
                Type = t.ChargeType != null && t.ChargeType != "" ? t.ChargeType : (u != null ? u.UsageType : "UNKNOWN"),
                Day = t.CreatedAt.Date,
                Credits = -t.Amount,
                Covered = u != null && r != null && r.ProviderUnitCost != null && (r.Unit == null || r.Unit == u.Unit),
                CostUsd = (decimal?)u!.Quantity * r!.ProviderUnitCost,
            };

        var grouped = await rows
            .Where(x => types.Contains(x.Type))
            .GroupBy(x => new { x.Day, x.Type })
            .Select(g => new
            {
                g.Key.Day,
                g.Key.Type,
                Credits = g.Sum(x => (long)x.Credits),
                Transactions = g.Count(),
                CoveredCredits = g.Sum(x => x.Covered ? (long)x.Credits : 0L),
                CoveredTransactions = g.Count(x => x.Covered),
                CostUsd = g.Sum(x => x.Covered ? x.CostUsd : 0m),
            })
            .ToListAsync(cancellationToken);

        return grouped
            .Select(x => new DailyChargeTypeConsumption(
                DateOnly.FromDateTime(x.Day),
                x.Type,
                x.Credits,
                x.Transactions,
                x.CoveredCredits,
                x.CoveredTransactions,
                x.CostUsd ?? 0m))
            .OrderBy(x => x.UtcDate)
            .ThenBy(x => x.ChargeType, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<CreditsByChargeType>> GetConsumedByChargeTypeAsync(
        DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        var rows = await (
                from t in ConsumeIn(@from, to)
                join u in _context.UsageRecords.IgnoreQueryFilters() on t.UsageRecordId equals (Guid?)u.Id into usage
                from u in usage.DefaultIfEmpty()
                select new
                {
                    Type = t.ChargeType != null && t.ChargeType != "" ? t.ChargeType : (u != null ? u.UsageType : "UNKNOWN"),
                    Credits = -t.Amount,
                })
            .GroupBy(x => x.Type)
            .Select(g => new { Type = g.Key, Credits = g.Sum(x => (long)x.Credits) })
            .OrderByDescending(x => x.Credits)
            .ThenBy(x => x.Type)
            .ToListAsync(cancellationToken);

        return rows.Select(r => new CreditsByChargeType(r.Type, r.Credits)).ToList();
    }

    public async Task<IReadOnlyList<WorkspaceCredits>> GetTopConsumingWorkspacesAsync(
        DateTime from, DateTime to, int take, long minCredits = 1, CancellationToken cancellationToken = default)
    {
        var rows = await ConsumeIn(from, to)
            .GroupBy(t => t.WorkspaceId)
            .Select(g => new { WorkspaceId = g.Key, Credits = g.Sum(t => -(long)t.Amount) })
            .Where(x => x.Credits >= minCredits)
            .OrderByDescending(x => x.Credits)
            .ThenBy(x => x.WorkspaceId)
            .Take(take)
            .ToListAsync(cancellationToken);

        return rows.Select(r => new WorkspaceCredits(r.WorkspaceId, r.Credits)).ToList();
    }

    public Task<int> CountConsumingWorkspacesAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
        => ConsumeIn(from, to)
            .Select(t => t.WorkspaceId)
            .Distinct()
            .CountAsync(cancellationToken);

    private IQueryable<CreditTransaction> ConsumeIn(DateTime from, DateTime to)
        => _dbSet.IgnoreQueryFilters().AsNoTracking().Where(t =>
            t.Type == TransactionConstants.TransactionTypes.Consume
            && t.CreatedAt >= from
            && t.CreatedAt < to);
}
