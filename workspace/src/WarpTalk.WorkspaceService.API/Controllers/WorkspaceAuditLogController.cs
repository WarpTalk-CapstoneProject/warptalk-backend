using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceAuditLog;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.API.Controllers;

/// <summary>
/// Workspace-scoped, read-only audit log for the workspace Owner and Admins.
/// The workspace comes from the route and is verified against the caller's membership in the
/// service; it is never taken from the query string.
/// </summary>
[ApiController]
[Route("api/v1/workspaces/{workspaceId:guid}/audit-log")]
[Authorize]
public class WorkspaceAuditLogController : ControllerBase
{
    private readonly IWorkspaceAuditLogService _workspaceAuditLogService;

    public WorkspaceAuditLogController(IWorkspaceAuditLogService workspaceAuditLogService)
    {
        _workspaceAuditLogService = workspaceAuditLogService;
    }

    [HttpGet]
    public async Task<IActionResult> Query(
        Guid workspaceId,
        [FromQuery] WorkspaceAuditLogQuery query,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _workspaceAuditLogService.QueryAsync(workspaceId, userId.Value, query, ct);
        if (result.IsSuccess) return Ok(result.Value);

        return result.ErrorCode switch
        {
            ErrorCodes.ValidationError => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Forbidden => StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode)),
        };
    }
}
