using System;
using System.Collections.Generic;

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

public record WorkspaceRoleChangePreviewDto(
    Guid TargetUserId,
    string CurrentRole,
    string TargetRole,
    string MembershipType,
    bool CanCreateMeetings,
    IReadOnlyList<string> Impact,
    DateTime ExpiresAt,
    string? PreviewToken = null,
    DateTime? CoolingOffUntil = null
);

public record ApplyWorkspaceRoleChangeRequest(
    string TargetRole,
    string IdempotencyKey,
    string PreviewToken,
    string? CorrelationId = null
);

public record WorkspaceRoleChangeResultDto(
    Guid TargetUserId,
    string OldRole,
    string NewRole,
    DateTime EffectiveAt,
    string EffectiveBehavior,
    Guid AuditId,
    WorkspaceMemberDto? Member = null,
    string? IdempotencyKey = null
);

public record TransferOwnershipRequest(
    Guid NewOwnerId
);

public record UpdateWorkspaceMemberRequest(
    bool CanCreateMeetings
);
