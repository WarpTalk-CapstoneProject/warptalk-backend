using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;

namespace WarpTalk.AuthService.Infrastructure.Repositories;

public class WorkspaceRepository : GenericRepository<Workspace>, IWorkspaceRepository
{
    public WorkspaceRepository(AuthDbContext context) : base(context)
    {
    }

    public async Task<(List<Workspace> Items, int TotalCount)> GetWorkspacesForUserAsync(Guid userId, int page, int pageSize, string? search = null, CancellationToken ct = default)
    {
        var query = _context.Workspaces
            .AsNoTracking()
            .Where(w => w.WorkspaceMembers.Any(m => m.UserId == userId && m.RemovedAt == null));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.ToLower();
            query = query.Where(w => w.Name.ToLower().Contains(searchLower) || w.Slug.ToLower().Contains(searchLower));
        }

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(w => w.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }
}
