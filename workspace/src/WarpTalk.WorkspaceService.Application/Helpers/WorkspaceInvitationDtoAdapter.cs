using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceInvitation;
using WarpTalk.WorkspaceService.Application.Mappers;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Interfaces;

namespace WarpTalk.WorkspaceService.Application.Helpers;

public static class WorkspaceInvitationDtoAdapter
{
    public static async Task<WorkspaceInvitationDto> ToJoinRequestAwareDtoAsync(
        IUnitOfWork unitOfWork,
        WorkspaceInvitation invitation,
        string roleName,
        CancellationToken ct)
    {
        if (!string.Equals(invitation.Status, InvitationStatus.REQUESTED.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return invitation.ToDto(roleName);
        }

        var workspace = invitation.Workspace
            ?? await unitOfWork.WorkspaceRepository.GetByIdAsync(invitation.WorkspaceId, ct);
        if (workspace == null)
        {
            return invitation.ToDto(roleName);
        }

        invitation.Workspace = workspace;
        var requesterId = invitation.RequestedBy ?? invitation.InvitedBy;
        var eligibility = await WorkspaceHelper.EvaluateJoinRequestEligibilityAsync(
            unitOfWork,
            invitation.Email,
            requesterId,
            workspace,
            ct);
        return invitation.ToDto(roleName, eligibility);
    }
}
