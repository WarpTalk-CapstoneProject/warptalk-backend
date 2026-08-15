using System;
using System.Collections.Generic;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.AuthService.Application.DTOs.Admin;

/// <summary>
/// Query string contract for the platform user directory. Bound with [FromQuery]; every value is
/// validated server-side before it reaches SQL. Paging comes from the shared
/// <see cref="AdminPageRequest"/> so every admin endpoint clamps it identically (WT-205).
/// </summary>
public record AdminUserDirectoryQuery : AdminPageRequest
{
    /// <summary>Matched against email and full name, case-insensitively.</summary>
    public string? Search { get; init; }

    /// <summary>all | active | locked | unverified | deactivated | deleted. Defaults to all.</summary>
    public string? Status { get; init; }

    /// <summary>A platform role name. Null lists every role.</summary>
    public string? Role { get; init; }

    /// <summary>
    /// created_desc | created_asc | name_asc | name_desc | last_login_desc | last_login_asc.
    /// Defaults to created_desc.
    /// </summary>
    public string? Sort { get; init; }
}

/// <summary>
/// One account in the directory.
///
/// <paramref name="Status"/> is derived rather than stored, because "what state is this account
/// in" is spread across five columns and every reader was otherwise free to combine them
/// differently. The precedence is fixed in <c>AdminUserMapper</c>: deleted, then locked, then
/// deactivated, then unverified, then active.
/// </summary>
public record AdminUserSummaryDto(
    Guid Id,
    string Email,
    string FullName,
    string? AvatarUrl,
    string Status,
    IReadOnlyList<string> Roles,
    /// <summary>Sessions live right now — not revoked, not expired.</summary>
    int ActiveSessionCount,
    DateTime? LastLoginAt,
    DateTime CreatedAt,
    DateTime? DeletedAt);

/// <summary>
/// One account, plus the sessions an administrator can actually act on.
///
/// Workspace membership is deliberately absent: it lives in another service, and resolving it
/// here would put auth behind a gRPC call for a screen that already has to work when workspace is
/// down. The directory says who someone is; the workspace directory says where they are.
/// </summary>
public record AdminUserDetailDto(
    AdminUserSummaryDto User,
    /// <summary>True while a failed-login lockout window is still running.</summary>
    bool IsLockedOut,
    DateTime? LockedUntil,
    bool EmailVerified,
    bool IsActive,
    IReadOnlyList<AdminUserSessionDto> ActiveSessions);

/// <summary>
/// One signed-in session. Never carries the token or its hash — an administrator needs to know a
/// session exists and be able to end it, not to be able to use it.
/// </summary>
public record AdminUserSessionDto(
    Guid Id,
    string? DeviceInfo,
    string? IpAddress,
    DateTime CreatedAt,
    DateTime ExpiresAt);

