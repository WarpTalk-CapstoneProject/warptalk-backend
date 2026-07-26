using System;
using System.ComponentModel.DataAnnotations;

namespace WarpTalk.WorkspaceService.Application.DTOs.WorkspaceInvitation;

public record InviteMemberRequest(
    [Required][EmailAddress] string Email,
    [Required] string RoleName,
    string? MembershipType = null
);

public record AcceptInvitationRequest(
    string? Token = null
);

public record CreateJoinRequestCommand(
    string? RoomCode,
    string? WorkspaceSlug
);

public record WorkspaceInvitationDto(
    Guid Id,
    Guid WorkspaceId,
    string Email,
    string RoleName,
    string Status,
    string MembershipType,
    string DeliveryStatus,
    string? ProviderMessageId,
    DateTime? LastSentAt,
    int SentCount,
    DateTime ExpiresAt,
    DateTime CreatedAt,
    DateTime? AcceptedAt
);

public record InviteMemberResponse(
    WorkspaceInvitationDto Invitation,
    string? RawToken,
    string EmailLanguage,
    string? Warning = null
);

public record PreviewInvitationResponse(
    string WorkspaceName,
    string RoleName,
    string MaskedEmail,
    string Status,
    DateTime ExpiresAt,
    bool AccountExists
);

public record VerifyInvitationInternalResponse(
    string Email,
    Guid WorkspaceId,
    string WorkspaceName,
    Guid RoleId,
    string RoleName,
    string MembershipType
);
