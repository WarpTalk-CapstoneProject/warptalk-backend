using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.AuthService.Application.DTOs.Admin;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;

namespace WarpTalk.AuthService.API.Controllers;

/// <summary>
/// The platform user directory for the System Admin portal.
///
/// Gated by the shared system-admin policy, which requires the exact platform role value "admin"
/// seeded in init-db.sql — distinct from the workspace-scoped "Admin" seeded alongside it. This
/// is the first admin surface in the auth service, so <c>AddWarpTalkSystemAdminAuthorization()</c>
/// had to be registered in Program.cs: without it the policy name resolves to nothing and every
/// request here fails at runtime rather than being refused.
///
/// The actor is always taken from the authenticated claims; no request body may name one.
/// </summary>
[ApiController]
[Route("api/v1/admin/users")]
[Authorize(Policy = SystemAdminAuthorization.PolicyName)]
public class AdminUsersController : ControllerBase
{
    private readonly IAdminUserService _adminUserService;

    public AdminUsersController(IAdminUserService adminUserService)
    {
        _adminUserService = adminUserService;
    }

    [HttpGet]
    public async Task<IActionResult> GetDirectory(
        [FromQuery] AdminUserDirectoryQuery query,
        CancellationToken ct)
    {
        var result = await _adminUserService.GetDirectoryAsync(query, ct);
        return ToActionResult(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetDetail(Guid id, CancellationToken ct)
    {
        var result = await _adminUserService.GetDetailAsync(id, ct);
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

