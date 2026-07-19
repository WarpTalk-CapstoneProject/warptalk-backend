using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.Shared.Extensions;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;

namespace WarpTalk.WorkspaceService.Infrastructure.Repositories;

public class WorkspaceInvitationRepository : GenericRepository<WorkspaceInvitation>, IWorkspaceInvitationRepository
{
    public WorkspaceInvitationRepository(WorkspaceDbContext context) : base(context)
    {
    }

    public async Task<WorkspaceInvitation?> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default)
    {
        return await _dbSet
            .Include(i => i.Workspace)
            .FirstOrDefaultAsync(i => i.TokenHash == tokenHash, ct);
    }

    public async Task<WorkspaceInvitation?> GetPendingByEmailAsync(Guid workspaceId, string email, CancellationToken ct = default)
    {
        return await _dbSet
            .FirstOrDefaultAsync(i =>
                i.WorkspaceId == workspaceId &&
                i.Email == email &&
                i.Status == InvitationStatus.PENDING.ToString() &&
                i.ExpiresAt > DateTime.UtcNow,
                ct);
    }

    public async Task<(List<WorkspaceInvitation> Items, int TotalCount)> GetInvitationsByWorkspaceAsync(Guid workspaceId, int page, int pageSize, CancellationToken ct = default)
    {
        var query = _dbSet
            .Where(i => i.WorkspaceId == workspaceId)
            .OrderByDescending(i => i.CreatedAt);

        var pagedList = await query.ToPagedListAsync(page, pageSize, ct);

        return pagedList;
    }
}
