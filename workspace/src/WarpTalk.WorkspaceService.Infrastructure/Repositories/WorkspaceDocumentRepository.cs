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

public class WorkspaceDocumentRepository : GenericRepository<WorkspaceDocument>, IWorkspaceDocumentRepository
{
    public WorkspaceDocumentRepository(WorkspaceDbContext context) : base(context)
    {
    }

    public async Task<(List<WorkspaceDocument> Items, int TotalCount)> GetPagedDocumentsAsync(
        Guid workspaceId,
        int page,
        int pageSize,
        bool isDescending = true,
        CancellationToken ct = default)
    {
        var query = _dbSet.AsNoTracking().Where(d => d.WorkspaceId == workspaceId && d.DeletedAt == null);

        var totalCount = await query.CountAsync(ct);

        query = isDescending 
            ? query.OrderByDescending(d => d.CreatedAt) 
            : query.OrderBy(d => d.CreatedAt);

        var skip = Math.Max(0, (page - 1) * pageSize);
        var items = await query.Skip(skip).Take(pageSize).ToListAsync(ct);

        return (items, totalCount);
    }
}
