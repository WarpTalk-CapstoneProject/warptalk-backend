using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Mappers;
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
        // WT-179: gate on the membership type acceptance will ACTUALLY use, not the one stored
        // on the invitation. ProcessAcceptInvitationAsync calls this same helper right after
        // this method returns and overwrites invitation.MembershipType with the result, so the
        // stored value has no bearing on the outcome — gating on it could only ever reject
        // someone the very next line would have admitted.
        //
        // The two also disagreed on the rule itself. DetermineMembershipTypeAsync decides "does
        // this workspace separate internal from external?" from the policy flags alone, while this
        // gate also treated a non-empty config.VerifiedDomains as if the policy were on. A
        // workspace with RequireVerifiedDomainForInternal = false but a leftover
        // VerifiedDomains entry in its settings JSON therefore stored every invitee as
        // Internal (flags off ⇒ Internal) and then refused every one of them at acceptance
        // whose domain was not verified — with no workaround. That is exactly what happened to
        // `testworkspace` on production: three pending invitations, all unacceptable.
        // GetWorkspaceConfig already states the rule this restores — the dedicated columns are
        // the authorization source of truth, and stale settings JSON must not change policy.
        var membershipType = await WorkspaceHelper.DetermineMembershipTypeAsync(unitOfWork, userEmail, workspace, ct);

        if (membershipType == MembershipType.Internal)
        {
            // Does Internal membership here require a verified domain? Policy flags only — the
            // same rule DetermineMembershipTypeAsync applies, so this gate cannot contradict the
            // type that method just derived. Defensive in practice: with the policy on, an
            // unverified domain already resolves to External, so Internal-with-unverified-domain
            // is unreachable — kept so the invariant still holds if the derivation changes.
            var requiresVerifiedDomain = workspace.RequireVerifiedDomainForInternal || config.RequireVerifiedDomainForInternal;
            if (requiresVerifiedDomain && !isDomainVerified)
            {
                return Result.Failure(WorkspaceConstants.Errors.CannotInviteInternalWithoutVerifiedDomain, ErrorCodes.ValidationError);
            }

            // A separate question with a deliberately wider definition: is this an "enterprise"
            // workspace, i.e. does the one-internal-workspace-per-user rule apply? Listing
            // verified domains in the settings counts here even with the policy flag off, which
            // matches how IsUserInternalMemberOfAnyEnterpriseWorkspaceAsync classifies the *other*
            // workspaces it scans. Conflating this with the question above is what broke WT-179.
            var isEnterpriseWorkspace = requiresVerifiedDomain || config.VerifiedDomains.Any();
            if (isEnterpriseWorkspace)
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

    public static async Task<Result> ProcessAcceptanceAsync(
        IUnitOfWork unitOfWork,
        IBillingSubscriptionClient billingSubscriptionClient,
        WorkspaceInvitation invitation,
        Guid userId,
        string userEmail,
        CancellationToken ct)
    {
        var validationResult = await ValidateAcceptanceAsync(unitOfWork, invitation, userId, userEmail, ct);
        if (!validationResult.IsSuccess)
        {
            return validationResult;
        }

        var workspace = await unitOfWork.WorkspaceRepository.GetByIdAsync(invitation.WorkspaceId, ct);
        if (workspace == null)
        {
            return Result.Failure(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
        }

        var membershipType = await WorkspaceHelper.DetermineMembershipTypeAsync(
            unitOfWork,
            userEmail,
            workspace,
            ct);
        var config = WorkspaceHelper.GetWorkspaceConfig(workspace);
        if (membershipType == MembershipType.External && !config.AllowExternalCollaboration)
        {
            return Result.Failure(WorkspaceConstants.Errors.ExternalCollaborationNotAllowed, ErrorCodes.Forbidden);
        }

        var capacityCheck = await EnsureTrialAcceptCapacityAsync(unitOfWork, billingSubscriptionClient, invitation.WorkspaceId, ct);
        if (!capacityCheck.IsSuccess)
        {
            return capacityCheck;
        }

        invitation.MembershipType = membershipType.ToString();
        var newMember = WorkspaceMemberMapper.CreateInvitationMember(
            invitation.WorkspaceId,
            userId,
            invitation.RoleId,
            invitation.MembershipType);

        invitation.Status = InvitationStatus.ACCEPTED.ToString();
        invitation.AcceptedAt = DateTime.UtcNow;

        await unitOfWork.WorkspaceMemberRepository.AddAsync(newMember, ct);
        unitOfWork.WorkspaceInvitationRepository.Update(invitation);
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    private static async Task<Result> EnsureTrialAcceptCapacityAsync(
        IUnitOfWork unitOfWork,
        IBillingSubscriptionClient billingSubscriptionClient,
        Guid workspaceId,
        CancellationToken ct)
    {
        if (!await billingSubscriptionClient.IsWorkspaceOnActiveTrialAsync(workspaceId, ct))
        {
            return Result.Success();
        }

        var activeMemberCount = await unitOfWork.WorkspaceMemberRepository.CountActiveMembersByWorkspaceAsync(workspaceId, ct);
        return activeMemberCount >= WorkspaceConstants.TrialWorkspaceMemberLimit
            ? Result.Failure(WorkspaceConstants.Errors.TrialWorkspaceMemberLimitReached, ErrorCodes.Forbidden)
            : Result.Success();
    }
}
