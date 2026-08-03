using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;

namespace WarpTalk.WorkspaceService.Infrastructure.Repositories;

public class WorkspaceDocumentAccessPolicyRepository : GenericRepository<WorkspaceDocumentAccessPolicy>, IWorkspaceDocumentAccessPolicyRepository
{
    public WorkspaceDocumentAccessPolicyRepository(WorkspaceDbContext context) : base(context)
    {
    }

    public async Task<(List<WorkspaceDocumentAccessPolicy> Items, int TotalCount)> GetPagedAccessPoliciesAsync(
        Guid documentId,
        int page,
        int pageSize,
        bool isDescending = true,
        CancellationToken ct = default)
    {
        var query = _dbSet.AsNoTracking().Where(p => p.DocumentId == documentId);

        var totalCount = await query.CountAsync(ct);

        query = isDescending
            ? query.OrderByDescending(p => p.CreatedAt)
            : query.OrderBy(p => p.CreatedAt);

        var skip = Math.Max(0, (page - 1) * pageSize);
        var items = await query.Skip(skip).Take(pageSize).ToListAsync(ct);

        return (items, totalCount);
    }
}
