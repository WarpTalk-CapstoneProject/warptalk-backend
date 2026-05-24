using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;

namespace WarpTalk.AuthService.Infrastructure.Repositories;

public class WorkspaceInvitationRepository : GenericRepository<WorkspaceInvitation>, IWorkspaceInvitationRepository
{
    public WorkspaceInvitationRepository(AuthDbContext context) : base(context)
    {
    }

    public async Task<WorkspaceInvitation?> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default)
    {
        return await _dbSet
            .Include(i => i.Workspace)
            .Include(i => i.Role)
            .FirstOrDefaultAsync(i => i.TokenHash == tokenHash, ct);
    }

    public async Task<WorkspaceInvitation?> GetPendingByEmailAsync(Guid workspaceId, string email, CancellationToken ct = default)
    {
        return await _dbSet
            .FirstOrDefaultAsync(i => 
                i.WorkspaceId == workspaceId && 
                i.Email == email && 
                i.Status == InvitationStatus.Pending && 
                i.ExpiresAt > DateTime.UtcNow, 
                ct);
    }

    public async Task<(List<WorkspaceInvitation> Items, int TotalCount)> GetInvitationsByWorkspaceAsync(Guid workspaceId, int page, int pageSize, CancellationToken ct = default)
    {
        var query = _dbSet
            .Include(i => i.Role)
            .Where(i => i.WorkspaceId == workspaceId)
            .OrderByDescending(i => i.CreatedAt);

        var totalCount = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }
}
