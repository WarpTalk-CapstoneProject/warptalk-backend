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

    private record WorkspaceNameResult(Guid Id, string Name);

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
}
