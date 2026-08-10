using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Extensions;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceKnowledge;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.API.Controllers;

/// <summary>
/// What the system has indexed about this workspace — the chunk text that was embedded and
/// the fact extracted from each — for the workspace Owner/Admin.
///
/// Read-only. The workspace comes from the route and the caller from the token; the service
/// checks the two against workspace_members on every call.
/// </summary>
[ApiController]
[Route("api/v1/workspaces/{workspaceId:guid}/knowledge")]
public class WorkspaceKnowledgeController : ControllerBase
{
    private readonly IWorkspaceKnowledgeService _knowledgeService;

    public WorkspaceKnowledgeController(IWorkspaceKnowledgeService knowledgeService)
    {
        _knowledgeService = knowledgeService;
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> GetKnowledge(
        Guid workspaceId,
        [FromQuery] GetWorkspaceKnowledgeQuery query,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _knowledgeService.GetKnowledgeAsync(workspaceId, query, userId.Value, ct);
        return ToActionResult(result);
    }

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);

        return result.ErrorCode switch
        {
            ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Forbidden => StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.ValidationError => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode)),
        };
    }
}

/// <summary>
/// The same listing for the System Admin portal.
///
/// Routed under ~/api/v1/admin/workspaces, which the gateway already forwards to this
/// service, and gated by the shared system-admin policy — the platform role "admin" seeded in
/// init-db.sql, which is distinct from the workspace-scoped "Admin". Workspace roles live in
/// workspace_members and never reach the token, so no workspace Owner can open this route.
/// </summary>
[ApiController]
[Route("api/v1/admin/workspaces/{workspaceId:guid}/knowledge")]
[Authorize(Policy = SystemAdminAuthorization.PolicyName)]
public class AdminWorkspaceKnowledgeController : ControllerBase
{
    private readonly IWorkspaceKnowledgeService _knowledgeService;

    public AdminWorkspaceKnowledgeController(IWorkspaceKnowledgeService knowledgeService)
    {
        _knowledgeService = knowledgeService;
    }

    [HttpGet]
    public async Task<IActionResult> GetKnowledge(
        Guid workspaceId,
        [FromQuery] GetWorkspaceKnowledgeQuery query,
        CancellationToken ct)
    {
        var result = await _knowledgeService.GetKnowledgeForAdminAsync(workspaceId, query, ct);

        if (result.IsSuccess) return Ok(result.Value);

        return result.ErrorCode switch
        {
            ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.ValidationError => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode)),
        };
    }
}
