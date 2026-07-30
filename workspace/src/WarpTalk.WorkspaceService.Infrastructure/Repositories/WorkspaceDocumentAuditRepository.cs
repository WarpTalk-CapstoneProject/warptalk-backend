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

public class WorkspaceDocumentAuditRepository : GenericRepository<WorkspaceDocumentAudit>, IWorkspaceDocumentAuditRepository
{
    public WorkspaceDocumentAuditRepository(WorkspaceDbContext context) : base(context)
    {
    }

    public async Task<(List<WorkspaceDocumentAudit> Items, int TotalCount)> GetPagedAuditsAsync(
        Guid documentId,
        int page,
        int pageSize,
        bool isDescending = true,
        CancellationToken ct = default)
    {
        var query = _dbSet.AsNoTracking().Where(a => a.DocumentId == documentId);

        var totalCount = await query.CountAsync(ct);

        query = isDescending 
            ? query.OrderByDescending(a => a.ActionAt) 
            : query.OrderBy(a => a.ActionAt);

        var skip = Math.Max(0, (page - 1) * pageSize);
        var items = await query.Skip(skip).Take(pageSize).ToListAsync(ct);

        return (items, totalCount);
    }
}
