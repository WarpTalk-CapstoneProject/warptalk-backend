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
            var errorResponse = new ApiErrorResponse(result.Error, result.ErrorCode);
            if (result.ErrorCode == ErrorCodes.Forbidden) return StatusCode(403, errorResponse);
            return BadRequest(errorResponse);
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
            var errorResponse = new ApiErrorResponse(result.Error, result.ErrorCode);
            if (result.ErrorCode == ErrorCodes.Forbidden) return StatusCode(403, errorResponse);
            return BadRequest(errorResponse);
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
            var errorResponse = new ApiErrorResponse(result.Error, result.ErrorCode);
            if (result.ErrorCode == ErrorCodes.Forbidden) return StatusCode(403, errorResponse);
            return BadRequest(errorResponse);
        }
        return NoContent();
    }
}
