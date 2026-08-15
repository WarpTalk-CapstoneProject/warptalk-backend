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

        // The aggregate is projected into an ANONYMOUS type and the record is constructed after
        // materialisation. Ordering a constructor-bound projection is what broke this query.
        //
        // `WorkspaceMemberUsage` is a positional record, so its properties come from constructor
        // PARAMETERS rather than member bindings. EF Core cannot map `u.CreditsConsumed` on such a
        // projection back to the `g.Sum(...)` it came from, so the whole tree failed to translate
        // and every request to /credits/workspace/{id}/usage-by-member returned 500 — the
        // dashboard's "Member usage could not be loaded."
        //
        // An anonymous type is member-bound, so the ORDER BY still happens in PostgreSQL and this
        // does not become an in-memory sort over every member of the workspace.
        var aggregates = await query
            .GroupBy(r => r.UserId!.Value)
            .Select(g => new
            {
                UserId = g.Key,
                CreditsConsumed = g.Sum(r => r.CreditsConsumed),
                RecordCount = g.Count(),
                LastUsedAt = g.Max(r => (DateTime?)r.RecordedAt),
            })
            .OrderByDescending(a => a.CreditsConsumed)
            .ToListAsync(ct);

        return aggregates
            .Select(a => new WorkspaceMemberUsage(
                a.UserId,
                a.CreditsConsumed,
                a.RecordCount,
                a.LastUsedAt))
            .ToList();
    }
}
