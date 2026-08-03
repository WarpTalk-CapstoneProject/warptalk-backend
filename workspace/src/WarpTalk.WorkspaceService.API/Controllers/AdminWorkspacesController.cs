using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.API.Controllers;

/// <summary>
/// Platform-wide workspace directory, detail, and lifecycle actions for the System Admin
/// portal (WT-204).
///
/// [Authorize(Roles = "admin")] checks the platform-wide "admin" system role seeded in
/// init-db.sql and put into the JWT's ClaimTypes.Role claims by JwtTokenGenerator — the same
/// gate ~/api/v1/admin/global-glossary and ~/api/v1/admin/notifications use. Workspace-scoped
/// Owner/Admin/Member roles live in workspace_members and never reach the token, so they
/// cannot open these endpoints.
///
/// The actor is always taken from the authenticated claims; no request body may name one.
/// </summary>
[ApiController]
[Route("api/v1/admin/workspaces")]
[Authorize(Roles = "admin")]
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

    [HttpPost("{id:guid}/suspend")]
    public async Task<IActionResult> Suspend(
        Guid id,
        [FromBody] AdminWorkspaceLifecycleRequest request,
        CancellationToken ct)
    {
        var actorId = User.GetUserId();
        if (actorId == null)
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));

        var result = await _adminWorkspaceService.SuspendAsync(
            id, request?.Reason ?? string.Empty, actorId.Value, HttpContext.TraceIdentifier, ct);
        return ToActionResult(result);
    }

    [HttpPost("{id:guid}/reactivate")]
    public async Task<IActionResult> Reactivate(
        Guid id,
        [FromBody] AdminWorkspaceLifecycleRequest request,
        CancellationToken ct)
    {
        var actorId = User.GetUserId();
        if (actorId == null)
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));

        var result = await _adminWorkspaceService.ReactivateAsync(
            id, request?.Reason ?? string.Empty, actorId.Value, HttpContext.TraceIdentifier, ct);
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
