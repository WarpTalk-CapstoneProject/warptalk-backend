using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Interfaces;

namespace WarpTalk.WorkspaceService.Application.Helpers;

public static class WorkspaceInvitationHelper
{
    public static async Task<bool> CheckAndHandleExpirationAsync(this WorkspaceInvitation invitation, IUnitOfWork unitOfWork, CancellationToken ct)
    {
        if (string.Equals(invitation.Status, InvitationStatus.PENDING.ToString(), StringComparison.OrdinalIgnoreCase)
            && invitation.ExpiresAt < DateTime.UtcNow)
        {
            invitation.Status = InvitationStatus.EXPIRED.ToString();
            unitOfWork.WorkspaceInvitationRepository.Update(invitation);
            await unitOfWork.SaveChangesAsync(ct);
            return true; // Expired
        }
        return false; // Not expired
    }
}
