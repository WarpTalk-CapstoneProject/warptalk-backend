using System;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceInvitation;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;

namespace WarpTalk.WorkspaceService.Application.Mappers;

public static class WorkspaceInvitationMapper
{
    public static WorkspaceInvitation CreateInvitation(Guid workspaceId, InviteMemberRequest request, Guid roleId, string roleName, Guid inviterUserId, string? tokenHash, string membershipType, DateTime? utcNow = null, int? expiryDays = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var validExpiryDays = expiryDays.HasValue && expiryDays.Value > 0
            ? expiryDays.Value
            : WorkspaceConstants.DefaultInvitationExpiryDays;

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

    public static WorkspaceInvitationDto ToDto(this WorkspaceInvitation invitation, string roleName)
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
            invitation.Workspace?.Slug
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
