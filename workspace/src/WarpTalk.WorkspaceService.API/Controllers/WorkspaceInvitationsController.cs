using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceInvitation;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.WorkspaceService.API.Controllers;

[ApiController]
[Route("api/v1/workspaces")]
public class WorkspaceInvitationsController : ControllerBase
{
    private readonly IWorkspaceInvitationService _workspaceInvitationService;

    public WorkspaceInvitationsController(IWorkspaceInvitationService workspaceInvitationService)
    {
        _workspaceInvitationService = workspaceInvitationService;
    }

    [Authorize]
    [HttpPost("{workspaceId:guid}/invitations")]
    public async Task<IActionResult> InviteMember(Guid workspaceId, [FromBody] InviteMemberRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _workspaceInvitationService.InviteMemberAsync(workspaceId, request, userId.Value, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [Authorize]
    [HttpGet("{workspaceId:guid}/invitations")]
    public async Task<IActionResult> ListInvitations(Guid workspaceId, [FromQuery] GetWorkspacesQuery query, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _workspaceInvitationService.ListInvitationsAsync(workspaceId, query, userId.Value, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [Authorize]
    [HttpDelete("{workspaceId:guid}/invitations/{invitationId:guid}")]
    public async Task<IActionResult> RevokeInvitation(Guid workspaceId, Guid invitationId, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _workspaceInvitationService.RevokeInvitationAsync(workspaceId, invitationId, userId.Value, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return NoContent();
    }

    [AllowAnonymous]
    [HttpGet("invitations/preview")]
    public async Task<IActionResult> PreviewInvitation([FromQuery] string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) 
            return BadRequest(new ApiErrorResponse("Token is required.", ErrorCodes.ValidationError));

        var result = await _workspaceInvitationService.PreviewInvitationAsync(token, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [Authorize]
    [HttpPost("invitations/accept")]
    public async Task<IActionResult> AcceptInvitation([FromBody] AcceptInvitationRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var userEmail = User.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(userEmail)) return Unauthorized();

        var result = await _workspaceInvitationService.AcceptInvitationAsync(request, userId.Value, userEmail, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return NoContent();
    }

    [Authorize]
    [HttpPost("join-requests")]
    public async Task<IActionResult> CreateJoinRequest([FromBody] CreateJoinRequestCommand command, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var userEmail = User.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(userEmail)) return Unauthorized();

        var result = await _workspaceInvitationService.CreateJoinRequestAsync(command, userId.Value, userEmail, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.Forbidden)
                return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [Authorize]
    [HttpPost("{workspaceId:guid}/join-requests/{invitationId:guid}/approve")]
    public async Task<IActionResult> ApproveJoinRequest(Guid workspaceId, Guid invitationId, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _workspaceInvitationService.ApproveJoinRequestAsync(workspaceId, invitationId, userId.Value, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.Forbidden)
                return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return NoContent();
    }

    [Authorize]
    [HttpPost("{workspaceId:guid}/join-requests/{invitationId:guid}/reject")]
    public async Task<IActionResult> RejectJoinRequest(Guid workspaceId, Guid invitationId, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _workspaceInvitationService.RejectJoinRequestAsync(workspaceId, invitationId, userId.Value, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.Forbidden)
                return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return NoContent();
    }
}

