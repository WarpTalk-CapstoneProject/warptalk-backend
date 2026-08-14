using System;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceInvitation;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;

namespace WarpTalk.WorkspaceService.Application.Mappers;

public static class WorkspaceInvitationMapper
{
    public static WorkspaceInvitation CreateInvitation(Guid workspaceId, InviteMemberRequest request, Guid roleId, string roleName, Guid inviterUserId, string? tokenHash, string membershipType, DateTime? utcNow = null, int? expiryDays = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var validExpiryDays = expiryDays switch
        {
            null => WorkspaceConstants.DefaultInvitationExpiryDays,
            < WorkspaceConstants.MinWorkspaceInvitationExpiryDays => WorkspaceConstants.DefaultInvitationExpiryDays,
            > WorkspaceConstants.MaxWorkspaceInvitationExpiryDays => WorkspaceConstants.MaxWorkspaceInvitationExpiryDays,
            _ => expiryDays.Value
        };

        return new WorkspaceInvitation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Email = request.Email,
            RoleId = roleId,
            InvitedBy = inviterUserId,
            TokenHash = tokenHash,
            Status = InvitationStatus.PENDING.ToString(),
            DeliveryStatus = InvitationDeliveryStatus.NotSent.ToString(),
            SentCount = 0,
            MembershipType = membershipType,
            ExpiresAt = now.AddDays(validExpiryDays),
            CreatedAt = now
        };
    }

    /// <summary>
    /// BR-34 — the replacement issued when a pending invitation is resent.
    ///
    /// Resending used to overwrite `TokenHash` on the existing row. That does invalidate the old
    /// token, but it leaves nothing to mark REPLACED and no record that a second email was ever
    /// sent with different token material — the status the SRS requires had no row to live on.
    ///
    /// Carries the inviter's original intent forward unchanged: the same invited email, role and
    /// membership type, credited to the same inviter. A resend is the same invitation said again,
    /// not a new decision.
    ///
    /// `SentCount` starts at 0 because it counts sends of THIS token; the superseded row keeps its
    /// own history. The expiry is fresh — the point of resending is that the last one was not
    /// usable, and inheriting a nearly-expired window would reproduce that.
    /// </summary>
    public static WorkspaceInvitation ToReplacementInvitation(
        this WorkspaceInvitation superseded,
        string tokenHash,
        int validExpiryDays,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(superseded);

        return new WorkspaceInvitation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = superseded.WorkspaceId,
            Email = superseded.Email,
            RoleId = superseded.RoleId,
            InvitedBy = superseded.InvitedBy,
            TokenHash = tokenHash,
            Status = InvitationStatus.PENDING.ToString(),
            DeliveryStatus = InvitationDeliveryStatus.NotSent.ToString(),
            SentCount = 0,
            MembershipType = superseded.MembershipType,
            ExpiresAt = now.AddDays(validExpiryDays),
            CreatedAt = now
        };
    }

    public static WorkspaceInvitationDto ToDto(this WorkspaceInvitation invitation, string roleName, JoinRequestEligibility? eligibility = null)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        if (string.IsNullOrWhiteSpace(roleName))
        {
            throw new ArgumentException("Role Name is required when mapping a WorkspaceInvitation.", nameof(roleName));
        }

        return new WorkspaceInvitationDto(
            invitation.Id,
            invitation.WorkspaceId,
            invitation.Email,
            roleName,
            invitation.Status,
            invitation.MembershipType,
            invitation.DeliveryStatus ?? InvitationDeliveryStatus.NotSent.ToString(),
            invitation.ProviderMessageId,
            invitation.LastSentAt,
            invitation.SentCount,
            invitation.ExpiresAt,
            invitation.CreatedAt,
            invitation.AcceptedAt,
            invitation.RequestedBy,
            invitation.ReviewedBy,
            invitation.ReviewedAt,
            invitation.Workspace?.Name,
            invitation.Workspace?.Slug,
            eligibility?.AllowedFinalMembershipTypes,
            eligibility?.RequiresPolicyAction,
            eligibility?.PolicyReason,
            eligibility?.SuggestedActions
        );
    }

    public static VerifyInvitationInternalResponse ToVerifyInternalResponse(this WorkspaceInvitation invitation, string roleName)
    {
        return new VerifyInvitationInternalResponse(
            invitation.Email,
            invitation.WorkspaceId,
            invitation.Workspace?.Name ?? "Unknown Workspace",
            invitation.RoleId,
            roleName,
            invitation.MembershipType
        );
    }

    public static PreviewInvitationResponse ToPreviewResponse(this WorkspaceInvitation invitation, string roleName, string maskedEmail, string currentStatus, bool accountExists)
    {
        return new PreviewInvitationResponse(
            invitation.Workspace?.Name ?? "Unknown Workspace",
            roleName,
            maskedEmail,
            currentStatus,
            invitation.ExpiresAt,
            accountExists
        );
    }
}
