using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceInvitation;
using WarpTalk.Shared;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

public interface IWorkspaceInvitationService
{
    Task<Result<InviteMemberResponse>> InviteMemberAsync(Guid workspaceId, InviteMemberRequest request, Guid inviterUserId, CancellationToken ct = default);
    Task<Result<InvitationPolicyResponse>> GetInvitationPolicyAsync(Guid workspaceId, string? email, Guid userId, CancellationToken ct = default);
    Task<Result<WorkspaceInvitationDto>> RetryDeliveryAsync(Guid workspaceId, Guid invitationId, Guid inviterUserId, CancellationToken ct = default);
    Task<Result<PagedResult<WorkspaceInvitationDto>>> ListInvitationsAsync(Guid workspaceId, GetWorkspacesQuery query, Guid userId, CancellationToken ct = default);
    Task<Result> RevokeInvitationAsync(Guid workspaceId, Guid invitationId, Guid userId, CancellationToken ct = default);
    Task<Result<PreviewInvitationResponse>> PreviewInvitationAsync(string token, CancellationToken ct = default);
    Task<Result> AcceptInvitationAsync(AcceptInvitationRequest request, Guid userId, string userEmail, CancellationToken ct = default);
    Task<Result> AcceptInvitationByIdAsync(Guid invitationId, Guid userId, string userEmail, CancellationToken ct = default);
    Task<Result<List<WorkspaceInvitationDto>>> GetPendingInvitationsForUserAsync(Guid userId, string userEmail, CancellationToken ct = default);
    Task<Result<List<WorkspaceInvitationDto>>> GetJoinRequestsForUserAsync(Guid userId, CancellationToken ct = default);
    Task<Result<VerifyInvitationInternalResponse>> VerifyInvitationTokenInternalAsync(string token, CancellationToken ct = default);
    Task<Result<WorkspaceInvitationDto>> CreateJoinRequestAsync(CreateJoinRequestCommand command, Guid userId, string userEmail, CancellationToken ct = default);
    Task<Result<ApproveJoinRequestResponse>> ApproveJoinRequestAsync(Guid workspaceId, Guid invitationId, Guid adminUserId, ApproveJoinRequestRequest? request = null, CancellationToken ct = default);
    Task<Result> RejectJoinRequestAsync(Guid workspaceId, Guid invitationId, Guid adminUserId, CancellationToken ct = default);
}
