using System;
using System.Collections.Generic;

namespace WarpTalk.AuthService.Application.DTOs.Admin;

/// <summary>One permission of the catalog, with how widely it is held.</summary>
public sealed record StaffPermissionDto(
    string Code,
    string Area,
    string Description,
    bool IsRead,
    int RoleCount,
    int MemberCount);

public sealed record StaffPermissionCatalogDto(
    IReadOnlyList<string> Areas,
    IReadOnlyList<StaffPermissionDto> Permissions);

public sealed record StaffRoleDto(
    Guid Id,
    string Slug,
    string Name,
    string? Description,
    bool IsBuiltIn,
    bool IsSuperAdmin,
    // Effective codes: every catalog code for Super Admin.
    IReadOnlyList<string> Permissions,
    int MemberCount,
    int PendingInvitationCount,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record StaffRoleRefDto(Guid Id, string Slug, string Name, bool IsBuiltIn);

public sealed record StaffMemberDto(
    Guid UserId,
    string Email,
    string FullName,
    string? AvatarUrl,
    Guid RoleId,
    string RoleSlug,
    string RoleName,
    bool IsSuperAdmin,
    // active | suspended
    string Status,
    string? StatusReason,
    DateTime? StatusChangedAt,
    // migrated | invited | invitation_accepted | legacy_bridge
    string Source,
    Guid? InvitedBy,
    DateTime CreatedAt,
    // Last admin request that reached the auth service's access check (throttled to 5 min).
    DateTime? LastActiveAt,
    DateTime? LastSignInAt,
    // False when the underlying account is deactivated: such a member has no access.
    bool AccountActive);

public sealed record StaffInvitationDto(
    Guid Id,
    string Email,
    Guid RoleId,
    string RoleName,
    // pending | accepted | revoked | expired
    string Status,
    Guid InvitedBy,
    string? Note,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? AcceptedAt,
    DateTime? RevokedAt,
    string? RevokeReason);

/// <summary>
/// Filters for /admin/staff, the same vocabulary as the admin list toolkit: q, role (slug),
/// status (active|suspended), lastActive (7d|30d|90d|never|inactive30d), sort, dir, page, pageSize.
/// </summary>
public sealed class StaffDirectoryQuery
{
    public string? Q { get; set; }
    public string? Role { get; set; }
    public string? Status { get; set; }
    public string? LastActive { get; set; }
    public string? Sort { get; set; }
    public string? Dir { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public sealed record StaffDirectoryPageDto(
    IReadOnlyList<StaffMemberDto> Items,
    int Total,
    int Page,
    int PageSize,
    // Counts per status over the whole directory, for the status tabs.
    IReadOnlyDictionary<string, int> StatusCounts);

public sealed record InviteStaffRequest(string Email, Guid RoleId, string? Reason);

public sealed record InviteStaffResultDto(
    // granted (the address already had an account) | invited (it did not)
    string Outcome,
    StaffMemberDto? Member,
    StaffInvitationDto? Invitation,
    bool EmailSent);

public sealed record ChangeStaffRoleRequest(Guid RoleId, string? Reason);

/// <summary>Suspend, reactivate, remove, revoke an invitation, delete a role: the reason is required.</summary>
public sealed record StaffActionRequest(string? Reason);

public sealed record SaveStaffRoleRequest(string Name, string? Description, IReadOnlyList<string>? Permissions, string? Reason);

public sealed record DuplicateStaffRoleRequest(string? Name);

public sealed record PermissionHoldersDto(
    StaffPermissionDto Permission,
    IReadOnlyList<StaffRoleRefDto> Roles,
    IReadOnlyList<StaffMemberDto> Members);

/// <summary>GET /api/v1/auth/staff-access: the caller's own access, for the web to render from.</summary>
public sealed record StaffSelfAccessDto(
    bool IsStaff,
    string? RoleSlug,
    string? RoleName,
    bool IsSuperAdmin,
    IReadOnlyList<string> Permissions);
