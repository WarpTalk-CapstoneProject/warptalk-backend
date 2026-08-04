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
            .Include(i => i.Workspace)
            .FirstOrDefaultAsync(i =>
                i.WorkspaceId == workspaceId &&
                i.Email.ToLower() == email.ToLower() &&
                i.Status == InvitationStatus.PENDING.ToString(),
                ct);
    }

    public async Task<List<WorkspaceInvitation>> GetPendingInvitationsByEmailAsync(string email, CancellationToken ct = default)
    {
        return await _dbSet
            .Include(i => i.Workspace)
            .Where(i =>
                i.Email.ToLower() == email.ToLower() &&
                i.Status == InvitationStatus.PENDING.ToString())
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<List<WorkspaceInvitation>> GetJoinRequestsByUserAsync(Guid userId, CancellationToken ct = default)
    {
        return await _dbSet
            .Include(i => i.Workspace)
            .Where(i => (i.RequestedBy == userId
                         || (i.RequestedBy == null
                             && i.InvitedBy == userId
                             && i.Status == InvitationStatus.REQUESTED.ToString()))
                        && (i.Status == InvitationStatus.REQUESTED.ToString()
                            || i.Status == InvitationStatus.ACCEPTED.ToString()
                            || i.Status == InvitationStatus.REJECTED.ToString()))
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<(List<WorkspaceInvitation> Items, int TotalCount)> GetInvitationsByWorkspaceAsync(Guid workspaceId, int page, int pageSize, CancellationToken ct = default, string? kind = null)
    {
        var query = _dbSet
            .Include(i => i.Workspace)
            .Where(i => i.WorkspaceId == workspaceId)
            .AsQueryable();

        if (string.Equals(kind, "join-request", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(i => i.RequestedBy != null || i.Status == InvitationStatus.REQUESTED.ToString());
        }
        else if (string.Equals(kind, "outbound", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(i => i.RequestedBy == null && i.Status != InvitationStatus.REQUESTED.ToString());
        }

        var totalCount = await query.CountAsync(ct);
        var skip = Math.Max(0, (page - 1) * pageSize);
        var items = await query.OrderByDescending(i => i.CreatedAt).Skip(skip).Take(pageSize).ToListAsync(ct);

        return (items, totalCount);
    }
}
