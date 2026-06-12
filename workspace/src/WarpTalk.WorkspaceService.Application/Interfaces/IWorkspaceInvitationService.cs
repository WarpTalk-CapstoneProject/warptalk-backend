using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceInvitation;
using WarpTalk.Shared;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

public interface IWorkspaceInvitationService
{
    Task<Result<InviteMemberResponse>> InviteMemberAsync(Guid workspaceId, InviteMemberRequest request, Guid inviterUserId, CancellationToken ct = default);
    Task<Result<PagedResult<WorkspaceInvitationDto>>> ListInvitationsAsync(Guid workspaceId, GetWorkspacesQuery query, Guid userId, CancellationToken ct = default);
    Task<Result> RevokeInvitationAsync(Guid workspaceId, Guid invitationId, Guid userId, CancellationToken ct = default);
    Task<Result<PreviewInvitationResponse>> PreviewInvitationAsync(string token, CancellationToken ct = default);
    Task<Result> AcceptInvitationAsync(AcceptInvitationRequest request, Guid userId, string userEmail, CancellationToken ct = default);
    Task<Result<VerifyInvitationInternalResponse>> VerifyInvitationTokenInternalAsync(string token, CancellationToken ct = default);
}
