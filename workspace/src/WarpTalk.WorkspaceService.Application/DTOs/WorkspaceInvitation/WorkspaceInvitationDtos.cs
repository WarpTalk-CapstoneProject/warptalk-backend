using System;
using System.ComponentModel.DataAnnotations;

namespace WarpTalk.WorkspaceService.Application.DTOs.WorkspaceInvitation;

public record InviteMemberRequest(
    [Required][EmailAddress] string Email,
    [Required] string RoleName,
    [Required] string MembershipType
);

public record AcceptInvitationRequest(
    [Required] string Token
);

public record WorkspaceInvitationDto(
    Guid Id,
    Guid WorkspaceId,
    string Email,
    string RoleName,
    string Status,
    string MembershipType,
    DateTime ExpiresAt,
    DateTime CreatedAt,
    DateTime? AcceptedAt
);

public record InviteMemberResponse(
    WorkspaceInvitationDto Invitation,
    string RawToken, // temporary vì chưa có handle notifications/email cho invitation
    string EmailLanguage
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
