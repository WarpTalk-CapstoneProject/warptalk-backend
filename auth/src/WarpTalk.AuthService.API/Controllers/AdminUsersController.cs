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

    /// <summary>
    /// Ends every session this account has open.
    ///
    /// POST rather than DELETE: nothing is removed. The refresh tokens stay as rows with a
    /// revocation time on them, which is what lets the account's history still show that it was
    /// signed in and when that stopped.
    /// </summary>
    [HttpPost("{id:guid}/revoke-sessions")]
    public async Task<IActionResult> RevokeSessions(
        Guid id,
        [FromBody] AdminUserActionRequest request,
        CancellationToken ct)
    {
        if (!TryResolveActor(out var actor)) return UnauthorizedActor();
        return ToActionResult(await _adminUserService.RevokeSessionsAsync(id, actor, request, ct));
    }

    [HttpPost("{id:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(
        Guid id,
        [FromBody] AdminUserActionRequest request,
        CancellationToken ct)
    {
        if (!TryResolveActor(out var actor)) return UnauthorizedActor();
        return ToActionResult(
            await _adminUserService.SetAccountActiveAsync(id, isActive: false, actor, request, ct));
    }

    [HttpPost("{id:guid}/reactivate")]
    public async Task<IActionResult> Reactivate(
        Guid id,
        [FromBody] AdminUserActionRequest request,
        CancellationToken ct)
    {
        if (!TryResolveActor(out var actor)) return UnauthorizedActor();
        return ToActionResult(
            await _adminUserService.SetAccountActiveAsync(id, isActive: true, actor, request, ct));
    }

    /// <summary>Clears a failed-login lockout. Separate from reactivate: they are different states.</summary>
    [HttpPost("{id:guid}/unlock")]
    public async Task<IActionResult> Unlock(
        Guid id,
        [FromBody] AdminUserActionRequest request,
        CancellationToken ct)
    {
        if (!TryResolveActor(out var actor)) return UnauthorizedActor();
        return ToActionResult(await _adminUserService.UnlockAsync(id, actor, request, ct));
    }

    /// <summary>
    /// The actor, from the token and never from the request.
    ///
    /// A token that passed the admin policy but carries no usable subject is a 401, not a
    /// placeholder actor: an audit entry naming the wrong person is worse than no action at all.
    /// </summary>
    private bool TryResolveActor(out AdminActorContext actor) =>
        AdminActorContext.TryResolve(User, HttpContext, out actor);

    private IActionResult UnauthorizedActor() =>
        Unauthorized(new ApiErrorResponse(
            "The token carries no usable subject.", ErrorCodes.Unauthorized));

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

