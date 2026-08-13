using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;

namespace WarpTalk.WorkspaceService.Infrastructure.Repositories;

public class WorkspaceMemberRepository : GenericRepository<WorkspaceMember>, IWorkspaceMemberRepository
{
    private static readonly string ActiveStatus = WorkspaceMemberStatus.Active.ToStorageValue();

    public WorkspaceMemberRepository(WorkspaceDbContext context) : base(context)
    {
    }

    public async Task<List<WorkspaceMember>> GetActiveMembersByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default)
    {
        return await _dbSet
            .AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId 
                        && m.Status.ToLower() == ActiveStatus
                        && m.RemovedAt == null)
            .OrderBy(m => m.JoinedAt)
            .ToListAsync(ct);
    }

    public async Task<int> CountActiveMembersByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default)
    {
        return await _dbSet.CountAsync(
            m => m.WorkspaceId == workspaceId && m.RemovedAt == null,
            ct);
    }

    public async Task<int> CountActiveOwnersAsync(Guid workspaceId, Guid ownerRoleId, CancellationToken ct = default)
    {
        return await _dbSet
            .AsNoTracking()
            .CountAsync(m => m.WorkspaceId == workspaceId 
                             && m.RoleId == ownerRoleId 
                             && m.Status.ToLower() == ActiveStatus
                             && m.RemovedAt == null, ct);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetCoMemberUserIdsAsync(
        Guid userId,
        IReadOnlyCollection<Guid> candidateUserIds,
        CancellationToken ct = default)
    {
        if (candidateUserIds.Count == 0)
        {
            return new List<Guid>();
        }

        // The caller's own active workspaces, left as an IQueryable so it becomes a subquery in the
        // SAME statement rather than a second round trip.
        var callerWorkspaceIds = _dbSet
            .AsNoTracking()
            .Where(m => m.UserId == userId
                        && m.Status.ToLower() == ActiveStatus
                        && m.RemovedAt == null)
            .Select(m => m.WorkspaceId);

        // One query for the whole batch: every active membership row belonging to a candidate, in a
        // workspace the caller is also actively in. Distinct because a pair sharing three workspaces
        // must still yield one id.
        return await _dbSet
            .AsNoTracking()
            .Where(m => candidateUserIds.Contains(m.UserId)
                        && m.Status.ToLower() == ActiveStatus
                        && m.RemovedAt == null
                        && callerWorkspaceIds.Contains(m.WorkspaceId))
            .Select(m => m.UserId)
            .Distinct()
            .ToListAsync(ct);
    }

    /// <summary>Defines membership visibility for workspace directory queries.</summary>
    public static Expression<Func<WorkspaceMember, bool>> DirectoryVisibilityFilter(bool includeInactiveAndBanned)
        => includeInactiveAndBanned
            ? m => m.RemovedAt == null
            : m => m.RemovedAt == null && m.Status.ToLower() == ActiveStatus;

    public async Task<(List<WorkspaceMember> Items, int TotalCount)> GetPagedMembersAsync(
        Guid workspaceId,
        int page,
        int pageSize,
        bool includeInactiveAndBanned = false,
        bool isDescending = true,
        CancellationToken ct = default)
    {
        var query = _dbSet.AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId)
            .Where(DirectoryVisibilityFilter(includeInactiveAndBanned));

        var totalCount = await query.CountAsync(ct);

        query = isDescending 
            ? query.OrderByDescending(m => m.JoinedAt) 
            : query.OrderBy(m => m.JoinedAt);

        var skip = Math.Max(0, (page - 1) * pageSize);
        var items = await query.Skip(skip).Take(pageSize).ToListAsync(ct);

        return (items, totalCount);
    }
}
