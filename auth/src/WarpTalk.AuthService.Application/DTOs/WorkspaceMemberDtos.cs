using System;

namespace WarpTalk.AuthService.Application.DTOs;

public record WorkspaceMemberDto(
    Guid Id,
    Guid WorkspaceId,
    Guid UserId,
    string FullName,
    string Email,
    string? AvatarUrl,
    string RoleName,
    string Status,
    DateTime JoinedAt,
    string MembershipType
);

public record ChangeMemberRoleRequest(
    string RoleName
);
