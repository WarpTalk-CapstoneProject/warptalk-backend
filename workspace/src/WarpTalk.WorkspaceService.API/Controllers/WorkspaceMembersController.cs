using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceMember;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.WorkspaceService.API.Controllers;

[ApiController]
[Route("api/v1/workspaces")]
public class WorkspaceMembersController : ControllerBase
{
    private readonly IWorkspaceMemberService _workspaceMemberService;

    public WorkspaceMembersController(IWorkspaceMemberService workspaceMemberService)
    {
        _workspaceMemberService = workspaceMemberService;
    }

    [Authorize]
    [HttpGet("{workspaceId:guid}/members")]
    public async Task<IActionResult> ListMembers(Guid workspaceId, [FromQuery] GetWorkspacesQuery query, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _workspaceMemberService.ListMembersAsync(workspaceId, query, userId.Value, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Forbidden)
                return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Conflict)
                return Conflict(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [Authorize]
    [HttpDelete("{workspaceId:guid}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid workspaceId, Guid userId, CancellationToken ct)
    {
        var currentUserId = User.GetUserId();
        if (currentUserId == null) return Unauthorized();

        var result = await _workspaceMemberService.RemoveMemberAsync(workspaceId, userId, currentUserId.Value, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Forbidden)
                return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Conflict)
                return Conflict(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return NoContent();
    }

    [Authorize]
    [HttpPut("{workspaceId:guid}/members/{userId:guid}/role")]
    public async Task<IActionResult> ChangeMemberRole(Guid workspaceId, Guid userId, [FromBody] ChangeMemberRoleRequest request, CancellationToken ct)
    {
        var currentUserId = User.GetUserId();
        if (currentUserId == null) return Unauthorized();

        var result = await _workspaceMemberService.ChangeMemberRoleAsync(workspaceId, userId, request.RoleName, currentUserId.Value, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Forbidden)
                return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Conflict)
                return Conflict(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return NoContent();
    }

    [Authorize]
    [HttpPost("{workspaceId:guid}/members/transfer-ownership")]
    public async Task<IActionResult> TransferOwnership(Guid workspaceId, [FromBody] TransferOwnershipRequest request, CancellationToken ct)
    {
        var currentUserId = User.GetUserId();
        if (currentUserId == null) return Unauthorized();

        var result = await _workspaceMemberService.TransferOwnershipAsync(workspaceId, request.NewOwnerId, currentUserId.Value, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Forbidden)
                return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Conflict)
                return Conflict(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return NoContent();
    }

    [Authorize]
    [HttpPatch("{workspaceId:guid}/members/{userId:guid}")]
    public async Task<IActionResult> UpdateWorkspaceMember(Guid workspaceId, Guid userId, [FromBody] UpdateWorkspaceMemberRequest request, CancellationToken ct)
    {
        var currentUserId = User.GetUserId();
        if (currentUserId == null) return Unauthorized();

        var result = await _workspaceMemberService.UpdateMemberAsync(workspaceId, userId, request, currentUserId.Value, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Forbidden)
                return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Conflict)
                return Conflict(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return NoContent();
    }
}
