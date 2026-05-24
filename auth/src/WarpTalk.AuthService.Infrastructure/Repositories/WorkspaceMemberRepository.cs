using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
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
}
