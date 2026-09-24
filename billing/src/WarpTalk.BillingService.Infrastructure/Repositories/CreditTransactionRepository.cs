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
        var items = await ApplyHistorySort(filtered, filter.Sort)
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

    /// <summary>
    /// created_desc is the ledger's historical order and is left exactly as it was — no
    /// tiebreaker added — so the workspace-scoped history is byte-for-byte unchanged. The new
    /// keys end on Id so their pages are stable.
    /// </summary>
    public static IQueryable<CreditTransaction> ApplyHistorySort(IQueryable<CreditTransaction> query, string sort) => sort switch
    {
        CreditHistorySorts.CreatedAsc => query.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id),
        CreditHistorySorts.AmountDesc => query
            .OrderByDescending(t => Math.Abs(t.Amount)).ThenByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id),
        CreditHistorySorts.AmountAsc => query
            .OrderBy(t => Math.Abs(t.Amount)).ThenByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id),
        _ => query.OrderByDescending(t => t.CreatedAt),
    };

    /// <summary>
    /// The ledger's WHERE clause. Public so tests can run it over plain rows and ask Npgsql to
    /// translate it (ToQueryString); the description search uses ILike, which only the latter
    /// can evaluate.
    /// </summary>
    public static IQueryable<CreditTransaction> ApplyHistoryFilters(
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

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            if (Guid.TryParse(term, out var id))
            {
                filtered = filtered.Where(t => t.Id == id || t.ReferenceId == id);
            }
            else
            {
                var pattern = $"%{term}%";
                filtered = filtered.Where(t => t.Description != null && EF.Functions.ILike(t.Description, pattern));
            }
        }

        return filtered;
    }

    // ── Admin Insights (2026-09-17) ──────────────────────────────────────────
    //
    // Consume rows only. Soft-delete query filters are ignored: usage on a since-deleted
    // subscription was still consumed (and still cost the provider).

    public Task<ConsumptionTotals> GetConsumptionTotalsAsync(
        DateTime from, DateTime to, CancellationToken cancellationToken = default)
        => ReadConsumptionTotalsAsync(ConsumeIn(@from, to), cancellationToken);

    public Task<ConsumptionTotals> GetWorkspaceConsumptionTotalsAsync(
        Guid workspaceId, DateTime from, DateTime to, CancellationToken cancellationToken = default)
        => ReadConsumptionTotalsAsync(
            ConsumeIn(@from, to).Where(t => t.WorkspaceId == workspaceId),
            cancellationToken);

    public async Task<IReadOnlyList<LedgerPoint>> GetWorkspaceLedgerPointsAsync(
        Guid workspaceId, DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        var rows = await _dbSet.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId && t.CreatedAt >= from && t.CreatedAt < to)
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .Select(t => new { t.CreatedAt, t.Type, t.Amount, t.BalanceAfter })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new LedgerPoint(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc), r.Type, r.Amount, r.BalanceAfter))
            .ToList();
    }

    private async Task<ConsumptionTotals> ReadConsumptionTotalsAsync(
        IQueryable<CreditTransaction> consumed, CancellationToken cancellationToken)
    {
        var rows =
            from t in consumed
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

    // ── Profit and loss (2026-09-24) ─────────────────────────────────────────
    //
    // Half-hour UTC slots rather than days: the report buckets on the LOCAL days of the request's
    // time zone (Vietnam's day starts at 17:00Z), which a UTC date_trunc('day') cannot do, and a
    // half hour still splits a +05:30 zone exactly. Anonymous projections only; the records are
    // built in memory.

    public async Task<IReadOnlyList<ConsumptionSlotRow>> GetConsumptionSlotsAsync(
        DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        var grouped = await ConsumptionSlotsQuery(from, to).ToListAsync(cancellationToken);

        return grouped
            .Select(x => new ConsumptionSlotRow(
                SlotStart(x.Day, x.Hour, x.Half),
                x.Type,
                AiProviderCatalog.Resolve(x.Provider, x.Type),
                x.PlanId,
                x.Credits,
                x.Transactions,
                x.CoveredCredits,
                x.CoveredTransactions,
                x.CostUsd ?? 0m,
                x.Overage))
            // Two card providers can resolve to one name ("OpenAI" / "openai"): merge them.
            .GroupBy(x => (x.SlotStart, x.ChargeType, x.Provider, x.PlanId))
            .Select(g => g.Count() == 1
                ? g.First()
                : new ConsumptionSlotRow(
                    g.Key.SlotStart, g.Key.ChargeType, g.Key.Provider, g.Key.PlanId,
                    g.Sum(x => x.Credits), g.Sum(x => x.Transactions), g.Sum(x => x.CoveredCredits),
                    g.Sum(x => x.CoveredTransactions), g.Sum(x => x.CostUsd), g.Sum(x => x.OverageCredits)))
            .OrderBy(x => x.SlotStart)
            .ToList();
    }

    public async Task<IReadOnlyList<WorkspaceSlotRow>> GetWorkspaceSlotsAsync(
        DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        var grouped = await WorkspaceSlotsQuery(from, to).ToListAsync(cancellationToken);
        return grouped
            .Select(x => new WorkspaceSlotRow(SlotStart(x.Day, x.Hour, x.Half), x.WorkspaceId, x.PlanId, x.Credits))
            .OrderBy(x => x.SlotStart)
            .ToList();
    }

    /// <summary>The slot query, exposed so a test can check that PostgreSQL can be asked it (ToQueryString) without a database.</summary>
    public IQueryable<ConsumptionSlotGroup> ConsumptionSlotsQuery(DateTime from, DateTime to)
    {
        var rows =
            from t in ConsumeIn(@from, to)
            join u in _context.UsageRecords.IgnoreQueryFilters() on t.UsageRecordId equals (Guid?)u.Id into usage
            from u in usage.DefaultIfEmpty()
            join r in _context.UsageRateCards on t.PricingRateCardId equals (Guid?)r.Id into cards
            from r in cards.DefaultIfEmpty()
            join s in _context.Subscriptions.IgnoreQueryFilters() on t.SubscriptionId equals s.Id into subscriptions
            from s in subscriptions.DefaultIfEmpty()
            select new
            {
                // Same type key and coverage rule as GetConsumptionTotalsAsync.
                Type = t.ChargeType != null && t.ChargeType != "" ? t.ChargeType : (u != null ? u.UsageType : "UNKNOWN"),
                Provider = r != null ? r.Provider : null,
                PlanId = s != null ? (Guid?)s.PlanId : null,
                Day = t.CreatedAt.Date,
                Hour = t.CreatedAt.Hour,
                Half = t.CreatedAt.Minute >= 30 ? 1 : 0,
                Credits = -t.Amount,
                Overage = Math.Max(0, -t.BalanceAfter) - Math.Max(0, t.Amount - t.BalanceAfter),
                Covered = u != null && r != null && r.ProviderUnitCost != null && (r.Unit == null || r.Unit == u.Unit),
                CostUsd = (decimal?)u!.Quantity * r!.ProviderUnitCost,
            };

        return rows
            .GroupBy(x => new { x.Day, x.Hour, x.Half, x.Type, x.Provider, x.PlanId })
            .Select(g => new ConsumptionSlotGroup
            {
                Day = g.Key.Day,
                Hour = g.Key.Hour,
                Half = g.Key.Half,
                Type = g.Key.Type,
                Provider = g.Key.Provider,
                PlanId = g.Key.PlanId,
                Credits = g.Sum(x => (long)x.Credits),
                Transactions = g.Count(),
                CoveredCredits = g.Sum(x => x.Covered ? (long)x.Credits : 0L),
                CoveredTransactions = g.Count(x => x.Covered),
                CostUsd = g.Sum(x => x.Covered ? x.CostUsd : 0m),
                Overage = g.Sum(x => (long)x.Overage),
            });
    }

    /// <summary>The workspace slot query, exposed for the same translation test.</summary>
    public IQueryable<WorkspaceSlotGroup> WorkspaceSlotsQuery(DateTime from, DateTime to)
    {
        var rows =
            from t in ConsumeIn(@from, to)
            join s in _context.Subscriptions.IgnoreQueryFilters() on t.SubscriptionId equals s.Id into subscriptions
            from s in subscriptions.DefaultIfEmpty()
            select new
            {
                t.WorkspaceId,
                PlanId = s != null ? (Guid?)s.PlanId : null,
                Day = t.CreatedAt.Date,
                Hour = t.CreatedAt.Hour,
                Half = t.CreatedAt.Minute >= 30 ? 1 : 0,
                Credits = -t.Amount,
            };

        return rows
            .GroupBy(x => new { x.Day, x.Hour, x.Half, x.WorkspaceId, x.PlanId })
            .Select(g => new WorkspaceSlotGroup
            {
                Day = g.Key.Day,
                Hour = g.Key.Hour,
                Half = g.Key.Half,
                WorkspaceId = g.Key.WorkspaceId,
                PlanId = g.Key.PlanId,
                Credits = g.Sum(x => (long)x.Credits),
            });
    }

    private static DateTime SlotStart(DateTime day, int hour, int half)
        => DateTime.SpecifyKind(day.Date, DateTimeKind.Utc).AddHours(hour).AddMinutes(half * 30);

    /// <summary>Settable-property shape (not a positional record), so EF can project into it.</summary>
    public sealed class ConsumptionSlotGroup
    {
        public DateTime Day { get; init; }
        public int Hour { get; init; }
        public int Half { get; init; }
        public string Type { get; init; } = string.Empty;
        public string? Provider { get; init; }
        public Guid? PlanId { get; init; }
        public long Credits { get; init; }
        public int Transactions { get; init; }
        public long CoveredCredits { get; init; }
        public int CoveredTransactions { get; init; }
        public decimal? CostUsd { get; init; }
        public long Overage { get; init; }
    }

    public sealed class WorkspaceSlotGroup
    {
        public DateTime Day { get; init; }
        public int Hour { get; init; }
        public int Half { get; init; }
        public Guid WorkspaceId { get; init; }
        public Guid? PlanId { get; init; }
        public long Credits { get; init; }
    }

    private IQueryable<CreditTransaction> ConsumeIn(DateTime from, DateTime to)
        => _dbSet.IgnoreQueryFilters().AsNoTracking().Where(t =>
            t.Type == TransactionConstants.TransactionTypes.Consume
            && t.CreatedAt >= from
            && t.CreatedAt < to);
}
