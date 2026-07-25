using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.ValueObjects;

namespace WarpTalk.WorkspaceService.Application.Helpers;

public static class WorkspaceInvitationHelper
{
    public static async Task<Result> ValidateAcceptanceAsync(
        IUnitOfWork unitOfWork,
        WorkspaceInvitation invitation,
        Guid userId,
        string userEmail,
        CancellationToken ct)
    {
        if (invitation.Status != InvitationStatus.PENDING.ToString())
        {
            return Result.Failure(string.Format(WorkspaceConstants.Errors.InvitationNoLongerValidFormat, invitation.Status), ErrorCodes.InvalidState);
        }

        if (invitation.ExpiresAt < DateTime.UtcNow)
        {
            invitation.Status = InvitationStatus.EXPIRED.ToString();
            unitOfWork.WorkspaceInvitationRepository.Update(invitation);
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Failure(WorkspaceConstants.Errors.InvitationExpired, ErrorCodes.InvalidState);
        }

        if (!string.Equals(invitation.Email, userEmail, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure(WorkspaceConstants.Errors.EmailMismatch, ErrorCodes.Forbidden);
        }

        var workspace = await unitOfWork.WorkspaceRepository.GetByIdAsync(invitation.WorkspaceId, ct);
        if (workspace == null)
        {
            return Result.Failure(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
        }

        if (!EmailAddress.TryParse(userEmail, out var emailAddress) || emailAddress == null)
        {
            return Result.Failure(WorkspaceConstants.Errors.InvalidUserEmail, ErrorCodes.ValidationError);
        }

        var userDomain = emailAddress.Domain;
        var config = WorkspaceHelper.GetWorkspaceConfig(workspace);
        var verifiedStatus = VerifiedDomainStatus.Verified.ToString().ToLower();
        var isDomainVerified = await unitOfWork.WorkspaceVerifiedDomainRepository.AnyAsync(
            vd => vd.WorkspaceId == invitation.WorkspaceId 
                  && vd.Domain.ToLower() == userDomain.ToLower() 
                  && vd.Status == verifiedStatus 
                  && vd.VerifiedAt != null 
                  && vd.RevokedAt == null, 
            ct);
        
        if (string.Equals(invitation.MembershipType, MembershipType.Internal.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            var requiresVerification = workspace.RequireVerifiedDomainForInternal || config.RequireVerifiedDomainForInternal || config.VerifiedDomains.Any();
            if (requiresVerification && !isDomainVerified)
            {
                return Result.Failure(WorkspaceConstants.Errors.CannotInviteInternalWithoutVerifiedDomain, ErrorCodes.ValidationError);
            }

            if (requiresVerification)
            {
                var isInternalElsewhere = await WorkspaceHelper.IsUserInternalMemberOfAnyEnterpriseWorkspaceAsync(unitOfWork, userId, userEmail, ct);
                if (isInternalElsewhere)
                {
                    return Result.Failure(WorkspaceConstants.Errors.UserAlreadyInternalElsewhere, ErrorCodes.Forbidden);
                }
            }
        }

        var existingMember = await unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
            m => m.WorkspaceId == invitation.WorkspaceId && m.UserId == userId, "", ct);
        
        if (existingMember != null)
        {
            return Result.Failure(WorkspaceConstants.Errors.AlreadyMember, ErrorCodes.InvalidState);
        }

        return Result.Success();
    }
}
