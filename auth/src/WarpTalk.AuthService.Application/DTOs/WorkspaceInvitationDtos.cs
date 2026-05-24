using System;
using System.ComponentModel.DataAnnotations;
using WarpTalk.AuthService.Domain.Constants;

namespace WarpTalk.AuthService.Application.DTOs;

public record InviteMemberRequest(
    [Required][EmailAddress] string Email,
    [Required] string RoleName
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
    DateTime ExpiresAt
);
