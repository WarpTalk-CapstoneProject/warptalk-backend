using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.AuthService.Application.DTOs.Admin;
using WarpTalk.AuthService.Application.Services;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;

namespace WarpTalk.AuthService.API.Controllers;

/// <summary>
/// Platform staff, their roles and the permission catalog (G10): /admin/staff and /admin/roles.
///
/// Reads need staff.read, every write staff.manage. The attribute is only the door: the service
/// then applies the guard rails with the actor's exact permissions — no self-changes, nothing
/// granted beyond what the actor holds, never the last active Super Admin — and audits each write
/// before committing it.
/// </summary>
[ApiController]
[Route("api/v1/admin/staff")]
public class AdminStaffController : ControllerBase
{
    private readonly IStaffAdminService _staff;

    public AdminStaffController(IStaffAdminService staff) => _staff = staff;

    // ── Staff members ────────────────────────────────────────────────────────────────────────

    [HttpGet]
    [RequirePermission(AdminPermissions.StaffRead)]
    public async Task<IActionResult> List([FromQuery] StaffDirectoryQuery query, CancellationToken ct) =>
        ToActionResult(await _staff.ListStaffAsync(query, ct));

    [HttpGet("{userId:guid}")]
    [RequirePermission(AdminPermissions.StaffRead)]
    public async Task<IActionResult> Get(Guid userId, CancellationToken ct) =>
        ToActionResult(await _staff.GetStaffAsync(userId, ct));

    [HttpPost("{userId:guid}/role")]
    [RequirePermission(AdminPermissions.StaffManage)]
    public async Task<IActionResult> ChangeRole(Guid userId, [FromBody] ChangeStaffRoleRequest request, CancellationToken ct)
    {
        if (!TryResolveActor(out var actor)) return UnauthorizedActor();
        return ToActionResult(await _staff.ChangeRoleAsync(actor, userId, request, ct));
    }

    [HttpPost("{userId:guid}/suspend")]
    [RequirePermission(AdminPermissions.StaffManage)]
    public async Task<IActionResult> Suspend(Guid userId, [FromBody] StaffActionRequest request, CancellationToken ct)
    {
        if (!TryResolveActor(out var actor)) return UnauthorizedActor();
        return ToActionResult(await _staff.SuspendAsync(actor, userId, request, ct));
    }

    [HttpPost("{userId:guid}/reactivate")]
    [RequirePermission(AdminPermissions.StaffManage)]
    public async Task<IActionResult> Reactivate(Guid userId, [FromBody] StaffActionRequest request, CancellationToken ct)
    {
        if (!TryResolveActor(out var actor)) return UnauthorizedActor();
        return ToActionResult(await _staff.ReactivateAsync(actor, userId, request, ct));
    }

    /// <summary>POST with a reason rather than DELETE: the reason is required and DELETE bodies are not portable.</summary>
    [HttpPost("{userId:guid}/remove")]
    [RequirePermission(AdminPermissions.StaffManage)]
    public async Task<IActionResult> Remove(Guid userId, [FromBody] StaffActionRequest request, CancellationToken ct)
    {
        if (!TryResolveActor(out var actor)) return UnauthorizedActor();
        var result = await _staff.RemoveAsync(actor, userId, request, ct);
        return result.IsSuccess ? NoContent() : Error(result);
    }

    // ── Invitations ──────────────────────────────────────────────────────────────────────────

    [HttpGet("invitations")]
    [RequirePermission(AdminPermissions.StaffRead)]
    public async Task<IActionResult> ListInvitations(CancellationToken ct) =>
        ToActionResult(await _staff.ListInvitationsAsync(ct));

    /// <summary>
    /// Invite by email. An address that already has an account is granted access at once
    /// (outcome "granted"); one that does not gets an invitation that activates on its first
    /// verified sign-in (outcome "invited"). Either way an email goes out; emailSent says whether
    /// it did.
    /// </summary>
    [HttpPost("invitations")]
    [RequirePermission(AdminPermissions.StaffManage)]
    public async Task<IActionResult> Invite([FromBody] InviteStaffRequest request, CancellationToken ct)
    {
        if (!TryResolveActor(out var actor)) return UnauthorizedActor();
        return ToActionResult(await _staff.InviteAsync(actor, request, ct));
    }

    [HttpPost("invitations/{invitationId:guid}/revoke")]
    [RequirePermission(AdminPermissions.StaffManage)]
    public async Task<IActionResult> RevokeInvitation(Guid invitationId, [FromBody] StaffActionRequest request, CancellationToken ct)
    {
        if (!TryResolveActor(out var actor)) return UnauthorizedActor();
        return ToActionResult(await _staff.RevokeInvitationAsync(actor, invitationId, request, ct));
    }

    // ── Roles ────────────────────────────────────────────────────────────────────────────────

    [HttpGet("roles")]
    [RequirePermission(AdminPermissions.StaffRead)]
    public async Task<IActionResult> ListRoles(CancellationToken ct) =>
        ToActionResult(await _staff.ListRolesAsync(ct));

    [HttpGet("roles/{roleId:guid}")]
    [RequirePermission(AdminPermissions.StaffRead)]
    public async Task<IActionResult> GetRole(Guid roleId, CancellationToken ct) =>
        ToActionResult(await _staff.GetRoleAsync(roleId, ct));

    [HttpPost("roles")]
    [RequirePermission(AdminPermissions.StaffManage)]
    public async Task<IActionResult> CreateRole([FromBody] SaveStaffRoleRequest request, CancellationToken ct)
    {
        if (!TryResolveActor(out var actor)) return UnauthorizedActor();
        return ToActionResult(await _staff.CreateRoleAsync(actor, request, ct));
    }

    [HttpPut("roles/{roleId:guid}")]
    [RequirePermission(AdminPermissions.StaffManage)]
    public async Task<IActionResult> UpdateRole(Guid roleId, [FromBody] SaveStaffRoleRequest request, CancellationToken ct)
    {
        if (!TryResolveActor(out var actor)) return UnauthorizedActor();
        return ToActionResult(await _staff.UpdateRoleAsync(actor, roleId, request, ct));
    }

    [HttpPost("roles/{roleId:guid}/duplicate")]
    [RequirePermission(AdminPermissions.StaffManage)]
    public async Task<IActionResult> DuplicateRole(Guid roleId, [FromBody] DuplicateStaffRoleRequest request, CancellationToken ct)
    {
        if (!TryResolveActor(out var actor)) return UnauthorizedActor();
        return ToActionResult(await _staff.DuplicateRoleAsync(actor, roleId, request, ct));
    }

    [HttpPost("roles/{roleId:guid}/delete")]
    [RequirePermission(AdminPermissions.StaffManage)]
    public async Task<IActionResult> DeleteRole(Guid roleId, [FromBody] StaffActionRequest request, CancellationToken ct)
    {
        if (!TryResolveActor(out var actor)) return UnauthorizedActor();
        var result = await _staff.DeleteRoleAsync(actor, roleId, request, ct);
        return result.IsSuccess ? NoContent() : Error(result);
    }

    // ── Permissions ──────────────────────────────────────────────────────────────────────────

    [HttpGet("permissions")]
    [RequirePermission(AdminPermissions.StaffRead)]
    public async Task<IActionResult> Permissions(CancellationToken ct) =>
        ToActionResult(await _staff.GetPermissionCatalogAsync(ct));

    /// <summary>"Who has this permission": the roles that grant it and the people holding them.</summary>
    [HttpGet("permissions/{code}/holders")]
    [RequirePermission(AdminPermissions.StaffRead)]
    public async Task<IActionResult> PermissionHolders(string code, CancellationToken ct) =>
        ToActionResult(await _staff.GetPermissionHoldersAsync(code, ct));

    private bool TryResolveActor(out AdminActorContext actor) =>
        AdminActorContext.TryResolve(User, HttpContext, out actor);

    private IActionResult UnauthorizedActor() =>
        Unauthorized(new ApiErrorResponse("The token carries no usable subject.", ErrorCodes.Unauthorized));

    private IActionResult ToActionResult<T>(Result<T> result) => result.IsSuccess ? Ok(result.Value) : Error(result);

    private IActionResult Error(Result result) =>
        result.ErrorCode switch
        {
            ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Forbidden => StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Conflict => Conflict(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.ValidationError => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode)),
        };
}
