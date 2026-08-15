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

public class UsageRecordRepository : GenericRepository<UsageRecord>, IUsageRecordRepository
{
    public UsageRecordRepository(BillingDbContext context) : base(context)
    {
    }

    public async Task<IReadOnlyList<WorkspaceMemberUsage>> GetUsageByMemberAsync(
        Guid workspaceId,
        DateTime? from,
        DateTime? to,
        CancellationToken ct = default)
    {
        var query = _dbSet
            .AsNoTracking()
            .Where(r => r.WorkspaceId == workspaceId && r.UserId != null);

        if (from.HasValue)
        {
            query = query.Where(r => r.RecordedAt >= from.Value);
        }
        if (to.HasValue)
        {
            query = query.Where(r => r.RecordedAt <= to.Value);
        }

        return await query
            .GroupBy(r => r.UserId!.Value)
            .Select(g => new WorkspaceMemberUsage(
                g.Key,
                g.Sum(r => r.CreditsConsumed),
                g.Count(),
                g.Max(r => (DateTime?)r.RecordedAt)))
            .OrderByDescending(u => u.CreditsConsumed)
            .ToListAsync(ct);
    }
}
