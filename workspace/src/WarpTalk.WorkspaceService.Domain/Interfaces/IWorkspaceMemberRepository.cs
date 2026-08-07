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

    /// <summary>
    /// WT-335: the subset of <paramref name="candidateUserIds"/> that share at least one ACTIVE
    /// workspace with <paramref name="userId"/>.
    ///
    /// Deliberately a set-in/set-out method rather than a per-user predicate: its caller is the
    /// Gateway's presence query, which arrives with up to 500 ids at once. A
    /// <c>bool SharesWorkspaceAsync(a, b)</c> would be the natural-looking shape and would turn one
    /// request into 500 queries. This is one query — a self-join on workspace_id, both sides
    /// filtered to active membership.
    /// </summary>
    Task<List<Guid>> GetCoMemberUserIdsAsync(
        Guid userId,
        IReadOnlyCollection<Guid> candidateUserIds,
        CancellationToken ct = default);

    Task<(List<WorkspaceMember> Items, int TotalCount)> GetPagedMembersAsync(
        Guid workspaceId,
        int page,
        int pageSize,
        bool includeInactiveAndBanned = false,
        bool isDescending = true,
        CancellationToken ct = default);
}
