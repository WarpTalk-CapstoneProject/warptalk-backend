using System;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceInvitation;
using WarpTalk.WorkspaceService.Application.Validators;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;

namespace WarpTalk.WorkspaceService.Application.Mappers.WorkspaceInvitation;

public static class WorkspaceInvitationMapper
{
    public static Domain.Entities.WorkspaceInvitation CreateInvitation(Guid workspaceId, InviteMemberRequest request, Guid roleId, string roleName, Guid inviterUserId, string tokenHash, string membershipType)
    {
        return new Domain.Entities.WorkspaceInvitation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Email = request.Email,
            RoleId = roleId,
            InvitedBy = inviterUserId,
            TokenHash = tokenHash,
            Status = InvitationStatus.PENDING.ToString(),
            MembershipType = membershipType,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow
        };
    }

    public static WorkspaceInvitationDto ToDto(this Domain.Entities.WorkspaceInvitation invitation, string roleName)
    {
        WorkspaceInvitationValidator.ValidateForMapping(invitation, roleName);

        return new WorkspaceInvitationDto(
            invitation.Id,
            invitation.WorkspaceId,
            invitation.Email,
            roleName,
            invitation.Status.ToString(),
            invitation.ExpiresAt,
            invitation.CreatedAt,
            invitation.AcceptedAt
        );
    }
}
