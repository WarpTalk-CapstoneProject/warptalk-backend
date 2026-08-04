using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Domain.Interfaces;

public interface IWorkspaceMemberRepository : IGenericRepository<WorkspaceMember>
{
    Task<List<WorkspaceMember>> GetActiveMembersByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default);
    Task<int> CountActiveMembersByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default);
    Task<int> CountActiveOwnersAsync(Guid workspaceId, Guid ownerRoleId, CancellationToken ct = default);

    Task<(List<WorkspaceMember> Items, int TotalCount)> GetPagedMembersAsync(
        Guid workspaceId,
        int page,
        int pageSize,
        bool includeInactiveAndBanned = false,
        bool isDescending = true,
        CancellationToken ct = default);
}
