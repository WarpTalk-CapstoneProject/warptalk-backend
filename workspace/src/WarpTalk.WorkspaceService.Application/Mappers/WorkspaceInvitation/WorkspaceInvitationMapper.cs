using System;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceInvitation;
using WarpTalk.WorkspaceService.Application.Validators;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;

namespace WarpTalk.WorkspaceService.Application.Mappers;

public static class WorkspaceInvitationMapper
{
    public static WorkspaceInvitation CreateInvitation(Guid workspaceId, InviteMemberRequest request, Guid roleId, string roleName, Guid inviterUserId, string tokenHash, string membershipType)
    {
        return new WorkspaceInvitation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Email = request.Email,
            RoleId = roleId,
            InvitedBy = inviterUserId,
            TokenHash = tokenHash,
            Status = InvitationStatus.PENDING.ToString(),
            MembershipType = membershipType,
            ExpiresAt = DateTime.UtcNow.AddDays(WorkspaceConstants.DefaultInvitationExpiryDays),
            CreatedAt = DateTime.UtcNow
        };
    }

    public static WorkspaceInvitationDto ToDto(this WorkspaceInvitation invitation, string roleName)
    {
        WorkspaceInvitationValidator.ValidateForMapping(invitation, roleName);

        return new WorkspaceInvitationDto(
            invitation.Id,
            invitation.WorkspaceId,
            invitation.Email,
            roleName,
            invitation.Status.ToString(),
            invitation.MembershipType,
            invitation.ExpiresAt,
            invitation.CreatedAt,
            invitation.AcceptedAt
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
