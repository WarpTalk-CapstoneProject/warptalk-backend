using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.API.Controllers;

/// <summary>
/// Read-only resolved entitlements (WT-263 layer ③) for a workspace. Served by the gateway's
/// existing <c>/api/v1/workspaces/{**catch-all}</c> route.
/// </summary>
[ApiController]
[Route("api/v1/workspaces/{workspaceId:guid}/entitlements")]
public class WorkspaceEntitlementsController : ControllerBase
{
    private readonly IWorkspaceEntitlementService _entitlementService;

    public WorkspaceEntitlementsController(IWorkspaceEntitlementService entitlementService)
    {
        _entitlementService = entitlementService;
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> GetEntitlements(Guid workspaceId, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _entitlementService.GetEntitlementsAsync(workspaceId, userId.Value, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Forbidden)
                return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.InternalServerError)
                return StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value);
    }
}
