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
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _workspaceInvitationService.InviteMemberAsync(workspaceId, request, userId.Value, ct);
        if (!result.IsSuccess)
            return ToActionResult(result);

        return Created(string.Empty, result.Value);
    }

    [Authorize]
    [HttpPost("{workspaceId:guid}/invitations/{invitationId:guid}/retry-delivery")]
    public async Task<IActionResult> RetryDelivery(Guid workspaceId, Guid invitationId, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _workspaceInvitationService.RetryDeliveryAsync(workspaceId, invitationId, userId.Value, ct);
        return ToActionResult(result);
    }

    [Authorize]
    [HttpGet("{workspaceId:guid}/invitations")]
    public async Task<IActionResult> ListInvitations(Guid workspaceId, [FromQuery] GetWorkspacesQuery query, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _workspaceInvitationService.ListInvitationsAsync(workspaceId, query, userId.Value, ct);
        return ToActionResult(result);
    }

    [Authorize]
    [HttpDelete("{workspaceId:guid}/invitations/{invitationId:guid}")]
    public async Task<IActionResult> RevokeInvitation(Guid workspaceId, Guid invitationId, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _workspaceInvitationService.RevokeInvitationAsync(workspaceId, invitationId, userId.Value, ct);
        return ToNoContentResult(result);
    }

    [Authorize]
    [HttpGet("invitations/pending")]
    public async Task<IActionResult> GetPendingInvitations(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var userEmail = User.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(userEmail)) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _workspaceInvitationService.GetPendingInvitationsForUserAsync(userId.Value, userEmail, ct);
        return ToActionResult(result);
    }

    [Authorize]
    [HttpGet("join-requests/mine")]
    public async Task<IActionResult> GetMyJoinRequests(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _workspaceInvitationService.GetJoinRequestsForUserAsync(userId.Value, ct);
        return ToActionResult(result);
    }

    [AllowAnonymous]
    [HttpGet("invitations/preview")]
    public async Task<IActionResult> PreviewInvitation([FromQuery] string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            return BadRequest(new ApiErrorResponse("Token is required.", ErrorCodes.ValidationError));

        var result = await _workspaceInvitationService.PreviewInvitationAsync(token, ct);
        return ToActionResult(result);
    }

    [Authorize]
    [HttpPost("invitations/accept")]
    public async Task<IActionResult> AcceptInvitation([FromBody] AcceptInvitationRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var userEmail = User.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(userEmail)) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _workspaceInvitationService.AcceptInvitationAsync(request, userId.Value, userEmail, ct);
        return ToNoContentResult(result);
    }

    [Authorize]
    [HttpPost("invitations/{invitationId:guid}/accept")]
    public async Task<IActionResult> AcceptInvitationById(Guid invitationId, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var userEmail = User.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(userEmail)) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _workspaceInvitationService.AcceptInvitationByIdAsync(invitationId, userId.Value, userEmail, ct);
        return ToNoContentResult(result);
    }

    [Authorize]
    [HttpPost("join-requests")]
    public async Task<IActionResult> CreateJoinRequest([FromBody] CreateJoinRequestCommand command, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var userEmail = User.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(userEmail)) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _workspaceInvitationService.CreateJoinRequestAsync(command, userId.Value, userEmail, ct);
        return ToActionResult(result);
    }

    [Authorize]
    [HttpPost("{workspaceId:guid}/join-requests/{invitationId:guid}/approve")]
    public async Task<IActionResult> ApproveJoinRequest(
        Guid workspaceId,
        Guid invitationId,
        [FromBody] ApproveJoinRequestRequest? request,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _workspaceInvitationService.ApproveJoinRequestAsync(workspaceId, invitationId, userId.Value, request, ct);
        return ToActionResult(result);
    }

    [Authorize]
    [HttpPost("{workspaceId:guid}/join-requests/{invitationId:guid}/reject")]
    public async Task<IActionResult> RejectJoinRequest(Guid workspaceId, Guid invitationId, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _workspaceInvitationService.RejectJoinRequestAsync(workspaceId, invitationId, userId.Value, ct);
        return ToNoContentResult(result);
    }

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess)
            return Ok(result.Value);

        return result.ErrorCode switch
        {
            ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Forbidden => StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Conflict => Conflict(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.ValidationError => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode))
        };
    }

    private IActionResult ToNoContentResult(Result result)
    {
        if (result.IsSuccess)
            return NoContent();

        return result.ErrorCode switch
        {
            ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Forbidden => StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Conflict => Conflict(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.ValidationError => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode))
        };
    }
}
