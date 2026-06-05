using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;

namespace WarpTalk.WorkspaceService.Infrastructure.Repositories;

public class WorkspaceMemberRepository : GenericRepository<WorkspaceMember>, IWorkspaceMemberRepository
{
    public WorkspaceMemberRepository(WorkspaceDbContext context) : base(context)
    {
    }

    public async Task<List<WorkspaceMember>> GetActiveMembersByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default)
    {
        return await _dbSet
            .Where(m => m.WorkspaceId == workspaceId && m.RemovedAt == null)
            .OrderBy(m => m.JoinedAt)
            .ToListAsync(ct);
    }

    public async Task<int> CountActiveOwnersAsync(Guid workspaceId, Guid ownerRoleId, CancellationToken ct = default)
    {
        return await _dbSet.CountAsync(
            m => m.WorkspaceId == workspaceId && m.RoleId == ownerRoleId && m.RemovedAt == null,
            ct);
    }
}
