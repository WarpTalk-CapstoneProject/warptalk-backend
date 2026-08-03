using System;
using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Application.DTOs.Admin;

/// <summary>
/// Query string contract for the system-admin workspace directory. Bound with [FromQuery];
/// every value is validated server-side before it reaches SQL.
/// </summary>
public record AdminWorkspaceDirectoryQuery
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public string? Search { get; init; }

    /// <summary>all | active | suspended | deleted. Defaults to all.</summary>
    public string? Status { get; init; }

    public int? MinMembers { get; init; }
    public int? MaxMembers { get; init; }

    /// <summary>
    /// created_desc | created_asc | name_asc | name_desc | members_desc | members_asc |
    /// updated_desc. Defaults to created_desc.
    /// </summary>
    public string? Sort { get; init; }
}

/// <summary>Owner identity resolved from the Auth service.</summary>
/// <param name="Resolved">
/// False when Auth could not be reached or the user no longer exists. The row is still
/// returned — the caller renders a degraded owner cell rather than a fabricated name.
/// </param>
public record AdminWorkspaceOwnerDto(
    Guid Id,
    string? FullName,
    string? Email,
    string? AvatarUrl,
    bool Resolved);

public record AdminWorkspaceSummaryDto(
    Guid Id,
    string Name,
    string Slug,
    string? LogoUrl,
    string Status,
    AdminWorkspaceOwnerDto Owner,
    int MemberCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime LastActivityAt);

public record AdminWorkspaceDetailDto(
    Guid Id,
    string Name,
    string Slug,
    string? LogoUrl,
    string Status,
    AdminWorkspaceOwnerDto Owner,
    int MemberCount,
    int InternalMemberCount,
    int ExternalMemberCount,
    int PendingInvitationCount,
    int DocumentCount,
    int VerifiedDomainCount,
    bool AllowExternalCollaboration,
    bool RequireVerifiedDomainForInternal,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime LastActivityAt,
    DateTime? DeletedAt,
    AdminWorkspaceLifecycleEventDto? CurrentSuspension,
    IReadOnlyList<AdminWorkspaceLifecycleEventDto> LifecycleHistory);

public record AdminWorkspaceLifecycleEventDto(
    Guid Id,
    string Action,
    string Reason,
    Guid PerformedBy,
    DateTime PerformedAt);

public record AdminWorkspaceLifecycleRequest(string Reason);

public record AdminPagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int Total);
