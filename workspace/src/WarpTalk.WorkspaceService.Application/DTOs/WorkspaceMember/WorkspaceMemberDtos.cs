using System;

namespace WarpTalk.WorkspaceService.Application.DTOs.WorkspaceMember;

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
    string MembershipType,
    bool CanCreateMeetings
);

public record ChangeMemberRoleRequest(
    string RoleName
);

public record TransferOwnershipRequest(
    Guid NewOwnerId
);

public record UpdateWorkspaceMemberRequest(
    bool CanCreateMeetings
);
