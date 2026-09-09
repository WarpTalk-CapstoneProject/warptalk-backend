using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.API.Controllers;

/// <summary>
/// Platform-wide workspace directory, detail, and lifecycle actions for the System Admin
/// portal (WT-204).
///
/// Gated by the shared system-admin policy (WT-205), which requires the exact platform role
/// value "admin" seeded in init-db.sql — distinct from the workspace-scoped "Admin" seeded
/// alongside it. Workspace Owner/Admin/Member live in workspace_members and never reach the
/// token, so they cannot open these endpoints by any route.
///
/// The actor is always taken from the authenticated claims; no request body may name one.
/// </summary>
[ApiController]
[Route("api/v1/admin/workspaces")]
[Authorize(Policy = SystemAdminAuthorization.PolicyName)]
public class AdminWorkspacesController : ControllerBase
{
    private readonly IAdminWorkspaceService _adminWorkspaceService;

    public AdminWorkspacesController(IAdminWorkspaceService adminWorkspaceService)
    {
        _adminWorkspaceService = adminWorkspaceService;
    }

    [HttpGet]
    public async Task<IActionResult> GetDirectory(
        [FromQuery] AdminWorkspaceDirectoryQuery query,
        CancellationToken ct)
    {
        var result = await _adminWorkspaceService.GetDirectoryAsync(query, ct);
        return ToActionResult(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetDetail(Guid id, CancellationToken ct)
    {
        var result = await _adminWorkspaceService.GetDetailAsync(id, ct);
        return ToActionResult(result);
    }

    /// <summary>
    /// The same detail by slug (WT-560), so the portal's address bar can name the workspace
    /// instead of carrying its primary key.
    ///
    /// The literal segment keeps this off the `{id:guid}` route: a slug is not a Guid, so
    /// without it every request here would simply 404 on the route constraint.
    /// </summary>
    [HttpGet("by-slug/{slug}")]
    public async Task<IActionResult> GetDetailBySlug(string slug, CancellationToken ct)
    {
        var result = await _adminWorkspaceService.GetDetailBySlugAsync(slug, ct);
        return ToActionResult(result);
    }

    [HttpPost("{id:guid}/suspend")]
    public async Task<IActionResult> Suspend(
        Guid id,
        [FromBody] AdminWorkspaceLifecycleRequest request,
        CancellationToken ct)
    {
        if (!AdminActorContext.TryResolve(User, HttpContext, out var actor))
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));

        var result = await _adminWorkspaceService.SuspendAsync(
            id, request?.Reason ?? string.Empty, actor.ActorId, actor.CorrelationId, ct);
        return ToActionResult(result);
    }

    [HttpPost("{id:guid}/reactivate")]
    public async Task<IActionResult> Reactivate(
        Guid id,
        [FromBody] AdminWorkspaceLifecycleRequest request,
        CancellationToken ct)
    {
        if (!AdminActorContext.TryResolve(User, HttpContext, out var actor))
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));

        var result = await _adminWorkspaceService.ReactivateAsync(
            id, request?.Reason ?? string.Empty, actor.ActorId, actor.CorrelationId, ct);
        return ToActionResult(result);
    }

    /// <summary>
    /// Soft delete. POST rather than HTTP DELETE because it carries a mandatory reason body and
    /// is not idempotent in the audit trail — every acceptance appends a row.
    /// </summary>
    [HttpPost("{id:guid}/delete")]
    public async Task<IActionResult> Delete(
        Guid id,
        [FromBody] AdminWorkspaceLifecycleRequest request,
        CancellationToken ct)
    {
        if (!AdminActorContext.TryResolve(User, HttpContext, out var actor))
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));

        var result = await _adminWorkspaceService.DeleteAsync(
            id, request?.Reason ?? string.Empty, actor.ActorId, actor.CorrelationId, ct);
        return ToActionResult(result);
    }

    /// <summary>
    /// The roster: membership facts only. The tenant's content — documents, knowledge, meeting
    /// artifacts — is deliberately NOT exposed to the admin portal; membership is platform
    /// bookkeeping the admin needs to operate suspensions, deletions and support requests.
    /// </summary>
    [HttpGet("{id:guid}/members")]
    public async Task<IActionResult> GetMembers(Guid id, CancellationToken ct)
    {
        var result = await _adminWorkspaceService.GetMembersAsync(id, ct);
        return ToActionResult(result);
    }

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);

        return result.ErrorCode switch
        {
            ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Forbidden => StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Conflict => Conflict(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.ValidationError => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode)),
        };
    }
}
