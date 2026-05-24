using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Domain.Interfaces;

public interface IWorkspaceMemberRepository : IGenericRepository<WorkspaceMember>
{
    Task<bool> IsOwnerOrAdminAsync(Guid workspaceId, Guid userId, CancellationToken ct = default);
    Task<(List<WorkspaceMember> Items, int TotalCount)> GetMembersByWorkspaceAsync(Guid workspaceId, int page, int pageSize, string? search, CancellationToken ct = default);
    Task<int> CountActiveOwnersAsync(Guid workspaceId, CancellationToken ct = default);
}


