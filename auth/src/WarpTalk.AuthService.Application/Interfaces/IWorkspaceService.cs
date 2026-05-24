using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.Shared;

using WarpTalk.AuthService.Domain.Settings;

namespace WarpTalk.AuthService.Application.Interfaces;

public interface IWorkspaceService
{
    Task<Result<WorkspaceDto>> CreateWorkspaceAsync(CreateWorkspaceRequest request, Guid userId, CancellationToken ct = default);
    Task<Result<PagedResult<WorkspaceDto>>> GetWorkspacesAsync(GetWorkspacesQuery query, Guid userId, CancellationToken ct = default);
    Task<Result<WorkspaceDto>> GetWorkspaceByIdAsync(Guid workspaceId, Guid userId, CancellationToken ct = default);
    Task<Result<SelectWorkspaceResponse>> SelectWorkspaceAsync(Guid workspaceId, Guid userId, CancellationToken ct = default);
    Task<Result<InviteMemberResponse>> InviteMemberAsync(Guid workspaceId, InviteMemberRequest request, Guid inviterUserId, CancellationToken ct = default);
    Task<Result<PagedResult<WorkspaceInvitationDto>>> ListInvitationsAsync(Guid workspaceId, GetWorkspacesQuery query, Guid userId, CancellationToken ct = default);
    Task<Result> RevokeInvitationAsync(Guid workspaceId, Guid invitationId, Guid userId, CancellationToken ct = default);
    Task<Result<PreviewInvitationResponse>> PreviewInvitationAsync(string token, CancellationToken ct = default);
    Task<Result> AcceptInvitationAsync(AcceptInvitationRequest request, Guid userId, string userEmail, CancellationToken ct = default);
    Task<Result> TransferOwnershipAsync(Guid workspaceId, Guid newOwnerId, CancellationToken ct = default);
    Task<Result<PagedResult<WorkspaceMemberDto>>> ListMembersAsync(Guid workspaceId, GetWorkspacesQuery query, Guid userId, CancellationToken ct = default);
    Task<Result> RemoveMemberAsync(Guid workspaceId, Guid memberUserId, Guid executingUserId, CancellationToken ct = default);
    Task<Result> ChangeMemberRoleAsync(Guid workspaceId, Guid memberUserId, string roleName, Guid executingUserId, CancellationToken ct = default);
    Task<Result<WorkspaceConfiguration>> GetWorkspaceSettingsAsync(Guid workspaceId, Guid userId, CancellationToken ct = default);
    Task<Result> UpdateWorkspaceSettingsAsync(Guid workspaceId, WorkspaceConfiguration settings, Guid userId, CancellationToken ct = default);
}

