using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.AuthService.API.Controllers;

[ApiController]
[Route("api/v1/workspaces")]
public class WorkspaceInvitationsController : ControllerBase
{
    private readonly IWorkspaceService _workspaceService;

    public WorkspaceInvitationsController(IWorkspaceService workspaceService)
    {
        _workspaceService = workspaceService;
    }

    [Authorize]
    [HttpPost("{workspaceId:guid}/invitations")]
    public async Task<IActionResult> InviteMember(Guid workspaceId, [FromBody] InviteMemberRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _workspaceService.InviteMemberAsync(workspaceId, request, userId.Value, ct);
        if (!result.IsSuccess)
        {
            var errorResponse = new ApiErrorResponse(result.Error, result.ErrorCode);
            if (result.ErrorCode == ErrorCodes.Forbidden) return StatusCode(403, errorResponse);
            return BadRequest(errorResponse);
        }
        return Ok(result.Value);
    }

    [Authorize]
    [HttpGet("{workspaceId:guid}/invitations")]
    public async Task<IActionResult> ListInvitations(Guid workspaceId, [FromQuery] GetWorkspacesQuery query, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _workspaceService.ListInvitationsAsync(workspaceId, query, userId.Value, ct);
        if (!result.IsSuccess)
        {
            var errorResponse = new ApiErrorResponse(result.Error, result.ErrorCode);
            if (result.ErrorCode == ErrorCodes.Forbidden) return StatusCode(403, errorResponse);
            return BadRequest(errorResponse);
        }
        return Ok(result.Value);
    }

    [Authorize]
    [HttpDelete("{workspaceId:guid}/invitations/{invitationId:guid}")]
    public async Task<IActionResult> RevokeInvitation(Guid workspaceId, Guid invitationId, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _workspaceService.RevokeInvitationAsync(workspaceId, invitationId, userId.Value, ct);
        if (!result.IsSuccess)
        {
            var errorResponse = new ApiErrorResponse(result.Error, result.ErrorCode);
            if (result.ErrorCode == ErrorCodes.Forbidden) return StatusCode(403, errorResponse);
            return BadRequest(errorResponse);
        }
        return NoContent();
    }

    [AllowAnonymous]
    [HttpGet("invitations/preview")]
    public async Task<IActionResult> PreviewInvitation([FromQuery] string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) 
            return BadRequest(new ApiErrorResponse("Token is required.", ErrorCodes.ValidationError));

        var result = await _workspaceService.PreviewInvitationAsync(token, ct);
        if (!result.IsSuccess)
        {
            var errorResponse = new ApiErrorResponse(result.Error, result.ErrorCode);
            if (result.ErrorCode == ErrorCodes.NotFound || result.ErrorCode == ErrorCodes.UserNotFound)
            {
                return NotFound(errorResponse);
            }
            return BadRequest(errorResponse);
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

        var result = await _workspaceService.AcceptInvitationAsync(request, userId.Value, userEmail, ct);
        if (!result.IsSuccess)
        {
            var errorResponse = new ApiErrorResponse(result.Error, result.ErrorCode);
            if (result.ErrorCode == ErrorCodes.Forbidden) return StatusCode(403, errorResponse);
            return BadRequest(errorResponse);
        }
        return NoContent();
    }
}
