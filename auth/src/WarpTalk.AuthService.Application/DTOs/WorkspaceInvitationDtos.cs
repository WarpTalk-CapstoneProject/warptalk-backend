using System;

namespace WarpTalk.AuthService.Application.DTOs;

public record VerifyInvitationResult(
    bool IsValid,
    string? Email,
    Guid? WorkspaceId,
    string? WorkspaceName,
    Guid? RoleId,
    string? RoleName,
    string? MembershipType,
    string? ErrorMessage
);

public record AcceptInvitationResult(
    bool Success,
    string? ErrorMessage
);
