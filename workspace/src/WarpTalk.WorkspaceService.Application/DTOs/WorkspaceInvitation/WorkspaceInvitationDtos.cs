using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using WarpTalk.WorkspaceService.Domain.Constants;

namespace WarpTalk.WorkspaceService.Application.DTOs.WorkspaceInvitation;

public record InviteMemberRequest(
    // StringLength as well as EmailAddress: workspace_invitations.email is varchar(320), and
    // [EmailAddress] happily accepts an address far longer than that, so the only thing standing
    // between a long one and the column was Postgres.
    //
    // [property:] rather than bare attributes. On a positional record the default target is the
    // CONSTRUCTOR PARAMETER. ASP.NET reads those during model binding, so the rules did apply to
    // requests — but they were invisible to every other validation path, including
    // Validator.TryValidateObject, which is what lets these be tested without standing up MVC.
    // A rule that only one caller can see is a rule the next refactor moves and nobody notices.
    [property: Required]
    [property: EmailAddress]
    [property: StringLength(WorkspaceConstants.InvitationEmailMaxLength)]
        string Email,
    [property: Required] string RoleName,
    string? MembershipType = null
);

/// <summary>
/// What the invite form needs to render the Access type choice without re-implementing the
/// domain rules client-side: which option to pre-select, which to disable, and the reason to
/// show next to a disabled one.
/// </summary>
public record InvitationPolicyResponse(
    string SuggestedMembershipType,
    IReadOnlyList<string> AllowedMembershipTypes,
    bool RequireVerifiedDomainForInternal,
    bool AllowExternalCollaboration,
    bool AllowSubdomains,
    bool IsEmailDomainVerified,
    bool IsPublicEmailDomain,
    string? InternalDisabledReason,
    string? ExternalDisabledReason
);

public record AcceptInvitationRequest(
    string? Token = null
);

public record CreateJoinRequestCommand(
    string? RoomCode,
    string? WorkspaceSlug
);

public record ApproveJoinRequestRequest(
    string? MembershipType = null
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
    DateTime? AcceptedAt,
    Guid? RequestedBy = null,
    Guid? ReviewedBy = null,
    DateTime? ReviewedAt = null,
    string? WorkspaceName = null,
    string? WorkspaceSlug = null,
    IReadOnlyList<string>? AllowedFinalMembershipTypes = null,
    bool? RequiresPolicyAction = null,
    string? PolicyReason = null,
    IReadOnlyList<string>? SuggestedActions = null
);

public record ApproveJoinRequestResponse(
    WorkspaceInvitationDto Invitation,
    string ApprovalEmailStatus,
    string? ApprovalEmailError = null
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
