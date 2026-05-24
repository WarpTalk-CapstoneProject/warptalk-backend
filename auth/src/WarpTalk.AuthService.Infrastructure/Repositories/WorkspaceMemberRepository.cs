using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared.Extensions;
using WarpTalk.AuthService.Infrastructure.Persistence;


namespace WarpTalk.AuthService.Infrastructure.Repositories;

public class WorkspaceMemberRepository : GenericRepository<WorkspaceMember>, IWorkspaceMemberRepository
{
    public WorkspaceMemberRepository(AuthDbContext context) : base(context)
    {
    }

    public async Task<bool> IsOwnerOrAdminAsync(Guid workspaceId, Guid userId, CancellationToken ct = default)
    {
        var member = await _dbSet
            .Include(m => m.Role)
            .FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId, ct);

        if (member == null || member.Role == null) return false;

        var roleName = member.Role.Name;
        return roleName == "Owner" || roleName == "Admin";
    }

    public async Task<(List<WorkspaceMember> Items, int TotalCount)> GetMembersByWorkspaceAsync(Guid workspaceId, int page, int pageSize, string? search, CancellationToken ct = default)
    {
        var query = _dbSet
            .Include(m => m.User)
            .Include(m => m.Role)
            .Where(m => m.WorkspaceId == workspaceId && m.RemovedAt == null);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.ToLower();
            query = query.Where(m => m.User.FullName.ToLower().Contains(searchLower) || m.User.Email.ToLower().Contains(searchLower));
        }

        return await query
            .OrderBy(m => m.JoinedAt)
            .ToPagedListAsync(page, pageSize, ct);
    }


    public async Task<int> CountActiveOwnersAsync(Guid workspaceId, CancellationToken ct = default)
    {
        return await _dbSet
            .CountAsync(m => m.WorkspaceId == workspaceId && m.Role.Name == "Owner" && m.RemovedAt == null, ct);
    }
}


