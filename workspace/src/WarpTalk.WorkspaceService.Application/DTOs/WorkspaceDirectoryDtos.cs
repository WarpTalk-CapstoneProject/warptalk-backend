using System;

namespace WarpTalk.WorkspaceService.Application.DTOs;

/// <summary>
/// Projections behind the workspace gRPC surface, which other services call
/// server-to-server. Each one mirrors a single RPC response so the boundary is left
/// with request parsing and field mapping only.
/// </summary>
public record WorkspaceMemberDetailsDto(
    string RoleName,
    string MembershipType,
    bool IsActive,
    bool CanCreateMeetings
);

public record WorkspaceNameDto(
    Guid WorkspaceId,
    string WorkspaceName
);

public record MeetingCreationDecisionDto(
    bool IsAllowed,
    string ErrorMessage
)
{
    public static MeetingCreationDecisionDto Allowed() => new(true, string.Empty);

    public static MeetingCreationDecisionDto Denied(string reason) => new(false, reason);
}

public record WorkspaceSettingsSnapshotDto(
    int ArtifactRetentionDays,
    bool AllowExternalCollaboration,
    bool IsProfanityFilterEnabled,
    bool AllowExternalLlm,
    bool UseGlobalGlossary,
    /// <summary>
    /// WT-342: the workspace's default stance on host approval for new meetings. Read by
    /// TranslationRoomService; an explicit per-meeting choice still overrides it.
    /// </summary>
    bool EnforceHostApprovalDefault
);

public record WorkspacePreflightDto(
    bool IsActive,
    string WorkspaceName,
    string WorkspaceSlug,
    bool IsDomainMatched,
    bool AllowExternalCollaboration
);
