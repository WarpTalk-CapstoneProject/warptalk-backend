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
    [HttpGet("{workspaceId:guid}/members/{userId:guid}/role-change-preview")]
    public async Task<IActionResult> PreviewRoleChange(Guid workspaceId, Guid userId, [FromQuery] string toRole, CancellationToken ct)
    {
        var currentUserId = User.GetUserId();
        if (currentUserId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _workspaceMemberService.PreviewMemberRoleChangeAsync(workspaceId, userId, toRole, currentUserId.Value, ct);
        return ToActionResult(result);
    }

    [Authorize]
    [HttpPost("{workspaceId:guid}/members/{userId:guid}/role-change")]
    public async Task<IActionResult> ApplyRoleChange(Guid workspaceId, Guid userId, [FromBody] ApplyWorkspaceRoleChangeRequest request, CancellationToken ct)
    {
        var currentUserId = User.GetUserId();
        if (currentUserId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _workspaceMemberService.ApplyMemberRoleChangeAsync(workspaceId, userId, request, currentUserId.Value, ct);
        return ToActionResult(result);
    }

    [Authorize]
    [HttpGet("{workspaceId:guid}/members")]
    public async Task<IActionResult> ListMembers(Guid workspaceId, [FromQuery] GetWorkspacesQuery query, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _workspaceMemberService.ListMembersAsync(workspaceId, query, userId.Value, ct);
        return ToActionResult(result);
    }

    [Authorize]
    [HttpDelete("{workspaceId:guid}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid workspaceId, Guid userId, CancellationToken ct)
    {
        var currentUserId = User.GetUserId();
        if (currentUserId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _workspaceMemberService.RemoveMemberAsync(workspaceId, userId, currentUserId.Value, ct);
        return ToNoContentResult(result);
    }

    [Authorize]
    [HttpPut("{workspaceId:guid}/members/{userId:guid}/role")]
    public async Task<IActionResult> ChangeMemberRole(Guid workspaceId, Guid userId, [FromBody] ChangeMemberRoleRequest request, CancellationToken ct)
    {
        var currentUserId = User.GetUserId();
        if (currentUserId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _workspaceMemberService.ChangeMemberRoleAsync(workspaceId, userId, request.RoleName, currentUserId.Value, ct);
        return ToNoContentResult(result);
    }

    [Authorize]
    [HttpPost("{workspaceId:guid}/members/transfer-ownership")]
    public async Task<IActionResult> TransferOwnership(Guid workspaceId, [FromBody] TransferOwnershipRequest request, CancellationToken ct)
    {
        var currentUserId = User.GetUserId();
        if (currentUserId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _workspaceMemberService.TransferOwnershipAsync(workspaceId, request.NewOwnerId, currentUserId.Value, ct);
        return ToNoContentResult(result);
    }

    [Authorize]
    [HttpPatch("{workspaceId:guid}/members/{userId:guid}")]
    public async Task<IActionResult> UpdateWorkspaceMember(Guid workspaceId, Guid userId, [FromBody] UpdateWorkspaceMemberRequest request, CancellationToken ct)
    {
        var currentUserId = User.GetUserId();
        if (currentUserId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _workspaceMemberService.UpdateMemberAsync(workspaceId, userId, request, currentUserId.Value, ct);
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
