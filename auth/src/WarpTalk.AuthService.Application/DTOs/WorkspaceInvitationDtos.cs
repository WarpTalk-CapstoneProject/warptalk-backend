using System;

namespace WarpTalk.AuthService.Application.DTOs;

/// <param name="Unreachable">
/// WT-596: true when the workspace service could not be ASKED, as opposed to having answered no.
///
/// Both arrive here as <c>IsValid: false</c> with a message, and collapsing them is the same
/// mistake this ticket is about: an invitation that was refused is the caller's problem and a
/// 4xx, while a workspace service that is down is ours and a 503. The gRPC client already knows
/// which happened — the distinction was simply thrown away one layer above it.
/// </param>
public record VerifyInvitationResult(
    bool IsValid,
    string? Email,
    Guid? WorkspaceId,
    string? WorkspaceName,
    Guid? RoleId,
    string? RoleName,
    string? MembershipType,
    string? ErrorMessage,
    bool Unreachable = false
);

/// <param name="Unreachable">See <see cref="VerifyInvitationResult.Unreachable"/>.</param>
public record AcceptInvitationResult(
    bool Success,
    string? ErrorMessage,
    bool Unreachable = false
);
