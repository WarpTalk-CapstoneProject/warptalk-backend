using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceMember;
using WarpTalk.Shared;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

public interface IWorkspaceMemberService
{
    Task<Result<PagedResult<WorkspaceMemberDto>>> ListMembersAsync(Guid workspaceId, GetWorkspacesQuery query, Guid userId, CancellationToken ct = default);
    Task<Result> RemoveMemberAsync(Guid workspaceId, Guid memberUserId, Guid executingUserId, CancellationToken ct = default);
    Task<Result> ChangeMemberRoleAsync(Guid workspaceId, Guid memberUserId, string roleName, Guid executingUserId, CancellationToken ct = default);
    Task<Result> TransferOwnershipAsync(Guid workspaceId, Guid newOwnerId, Guid executingUserId, CancellationToken ct = default);
    Task<Result> UpdateMemberAsync(Guid workspaceId, Guid memberUserId, UpdateWorkspaceMemberRequest request, Guid executingUserId, CancellationToken ct = default);
}
