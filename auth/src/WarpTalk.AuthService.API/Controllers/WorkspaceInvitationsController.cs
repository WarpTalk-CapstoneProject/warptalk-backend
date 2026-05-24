using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.API.Controllers;

[ApiController]
[Route("api/v1/workspaces")]
public class WorkspaceInvitationsController : BaseApiController
{
    private readonly IWorkspaceService _workspaceService;

    public WorkspaceInvitationsController(IWorkspaceService workspaceService)
    {
        _workspaceService = workspaceService;
    }

    [Authorize]
    [HttpPost("{workspaceId:guid}/invitations")]
    public async Task<Result<InviteMemberResponse>> InviteMember(Guid workspaceId, [FromBody] InviteMemberRequest request, CancellationToken ct)
    {
        return await _workspaceService.InviteMemberAsync(workspaceId, request, CurrentUserId, ct);
    }

    [Authorize]
    [HttpGet("{workspaceId:guid}/invitations")]
    public async Task<Result<PagedResult<WorkspaceInvitationDto>>> ListInvitations(Guid workspaceId, [FromQuery] GetWorkspacesQuery query, CancellationToken ct)
    {
        return await _workspaceService.ListInvitationsAsync(workspaceId, query, CurrentUserId, ct);
    }

    [Authorize]
    [HttpDelete("{workspaceId:guid}/invitations/{invitationId:guid}")]
    public async Task<Result> RevokeInvitation(Guid workspaceId, Guid invitationId, CancellationToken ct)
    {
        return await _workspaceService.RevokeInvitationAsync(workspaceId, invitationId, CurrentUserId, ct);
    }

    [AllowAnonymous]
    [HttpGet("invitations/preview")]
    public async Task<Result<PreviewInvitationResponse>> PreviewInvitation([FromQuery] string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) 
            return Result.Failure<PreviewInvitationResponse>("Token is required.", ErrorCodes.ValidationError);

        return await _workspaceService.PreviewInvitationAsync(token, ct);
    }

    [Authorize]
    [HttpPost("invitations/accept")]
    public async Task<ActionResult<Result>> AcceptInvitation([FromBody] AcceptInvitationRequest request, CancellationToken ct)
    {
        var userEmail = User.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(userEmail)) return Unauthorized();

        return await _workspaceService.AcceptInvitationAsync(request, CurrentUserId, userEmail, ct);
    }
}
