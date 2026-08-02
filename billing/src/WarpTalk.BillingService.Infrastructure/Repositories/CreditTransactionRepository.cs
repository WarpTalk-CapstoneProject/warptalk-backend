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

    public async Task<Dictionary<Guid, string>> GetWorkspaceNamesAsync(IEnumerable<Guid> workspaceIds, CancellationToken cancellationToken = default)
    {
        var idsArray = workspaceIds.ToArray();
        if (idsArray.Length == 0)
            return new Dictionary<Guid, string>();

        var results = await _context.Database
            .SqlQuery<WorkspaceNameResult>($"SELECT id AS \"Id\", name AS \"Name\" FROM workspace.workspaces WHERE id = ANY({idsArray})")
            .ToListAsync(cancellationToken);

        return results.ToDictionary(r => r.Id, r => r.Name);
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
}

internal sealed class WorkspaceNameResult
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
