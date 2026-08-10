using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Mappers;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.ValueObjects;

namespace WarpTalk.WorkspaceService.Application.Services;

public class WorkspaceInvitationAcceptanceProcessor : IWorkspaceInvitationAcceptanceProcessor
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IBillingSubscriptionClient _billingSubscriptionClient;
    private readonly IAuthIdentityClient _authIdentity;

    public WorkspaceInvitationAcceptanceProcessor(
        IUnitOfWork unitOfWork,
        IBillingSubscriptionClient billingSubscriptionClient,
        IAuthIdentityClient authIdentity)
    {
        _unitOfWork = unitOfWork;
        _billingSubscriptionClient = billingSubscriptionClient;
        _authIdentity = authIdentity;
    }

    public async Task<Result> ValidateAcceptanceAsync(
        WorkspaceInvitation invitation,
        Guid userId,
        string userEmail,
        CancellationToken ct = default)
    {
        if (invitation.Status != InvitationStatus.PENDING.ToString())
        {
            return Result.Failure(string.Format(WorkspaceConstants.Errors.InvitationNoLongerValidFormat, invitation.Status), ErrorCodes.InvalidState);
        }

        if (invitation.ExpiresAt < DateTime.UtcNow)
        {
            invitation.Status = InvitationStatus.EXPIRED.ToString();
            _unitOfWork.WorkspaceInvitationRepository.Update(invitation);
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Failure(WorkspaceConstants.Errors.InvitationExpired, ErrorCodes.InvalidState);
        }

        if (!string.Equals(invitation.Email, userEmail, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure(WorkspaceConstants.Errors.EmailMismatch, ErrorCodes.Forbidden);
        }

        var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(invitation.WorkspaceId, ct);
        if (workspace == null)
        {
            return Result.Failure(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
        }

        if (!EmailAddress.TryParse(userEmail, out var emailAddress) || emailAddress == null)
        {
            return Result.Failure(WorkspaceConstants.Errors.InvalidUserEmail, ErrorCodes.ValidationError);
        }

        var config = WorkspaceHelper.GetWorkspaceConfig(workspace);

        // The stored value IS the decision. Acceptance re-checks the inviter's intent against
        // the settings in force right now and may only admit it unchanged or refuse it — it may
        // not recompute a membership type that passes (BR-140-013). Recomputing was how an
        // invitation issued as Internal/Admin, whose domain later lost verification, still let
        // the invitee in as an External member holding Admin.
        //
        // WT-179 is not re-created by this. That incident came from treating a leftover
        // config.VerifiedDomains entry in the settings JSON as proof the policy was on while
        // RequireVerifiedDomainForInternal was off; the checks below read the flags and the
        // workspace_verified_domains table only, so a workspace with the policy off admits its
        // pending invitations exactly as it did before.
        var membershipType = ResolveStoredMembershipType(invitation);
        var roleName = await _authIdentity.GetRoleNameByIdAsync(invitation.RoleId, ct);

        var policyResult = await WorkspaceInvitationPolicy.ValidateAsync(
            _unitOfWork,
            workspace,
            userEmail,
            membershipType,
            roleName,
            ct);
        if (!policyResult.IsSuccess)
        {
            // Left PENDING deliberately. An Owner still needs to see it in the list to decide
            // between revoking and re-issuing (BR-140-014).
            return Result.Failure(
                string.Format(WorkspaceConstants.Errors.InvitationPolicyConflictFormat, policyResult.Error),
                policyResult.ErrorCode);
        }

        if (membershipType == MembershipType.Internal && config.RequireVerifiedDomainForInternal)
        {
            var isInternalElsewhere = await WorkspaceHelper.IsUserInternalMemberOfAnyEnterpriseWorkspaceAsync(_unitOfWork, userId, userEmail, ct);
            if (isInternalElsewhere)
            {
                return Result.Failure(WorkspaceConstants.Errors.UserAlreadyInternalElsewhere, ErrorCodes.Forbidden);
            }
        }

        var existingMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
            m => m.WorkspaceId == invitation.WorkspaceId && m.UserId == userId, "", ct);

        if (existingMember != null)
        {
            return Result.Failure(WorkspaceConstants.Errors.AlreadyMember, ErrorCodes.InvalidState);
        }

        return Result.Success();
    }

    public async Task<Result> ProcessAcceptanceAsync(
        WorkspaceInvitation invitation,
        Guid userId,
        string userEmail,
        CancellationToken ct = default)
    {
        var validationResult = await ValidateAcceptanceAsync(invitation, userId, userEmail, ct);
        if (!validationResult.IsSuccess)
        {
            return validationResult;
        }

        var capacityCheck = await EnsureTrialAcceptCapacityAsync(invitation.WorkspaceId, ct);
        if (!capacityCheck.IsSuccess)
        {
            return capacityCheck;
        }

        // No overwrite here. ValidateAcceptanceAsync has already confirmed the stored intent is
        // still permitted, and the member is created with exactly the access class and role the
        // inviter chose.
        var newMember = WorkspaceMemberMapper.CreateInvitationMember(
            invitation.WorkspaceId,
            userId,
            invitation.RoleId,
            ResolveStoredMembershipType(invitation).ToString());

        invitation.Status = InvitationStatus.ACCEPTED.ToString();
        invitation.AcceptedAt = DateTime.UtcNow;

        await _unitOfWork.WorkspaceMemberRepository.AddAsync(newMember, ct);
        _unitOfWork.WorkspaceInvitationRepository.Update(invitation);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// The access class the inviter chose, read back off the row.
    /// </summary>
    /// <remarks>
    /// Rows written before MembershipType was mandatory can hold null or an unrecognised string.
    /// Those fall back to External, the lesser grant — an unreadable intent must never be read as
    /// the more privileged one.
    /// </remarks>
    private static MembershipType ResolveStoredMembershipType(WorkspaceInvitation invitation)
    {
        return Enum.TryParse<MembershipType>(invitation.MembershipType, ignoreCase: true, out var stored)
            ? stored
            : MembershipType.External;
    }

    private async Task<Result> EnsureTrialAcceptCapacityAsync(
        Guid workspaceId,
        CancellationToken ct)
    {
        if (!await _billingSubscriptionClient.IsWorkspaceOnActiveTrialAsync(workspaceId, ct))
        {
            return Result.Success();
        }

        var activeMemberCount = await _unitOfWork.WorkspaceMemberRepository.CountActiveMembersByWorkspaceAsync(workspaceId, ct);
        return activeMemberCount >= WorkspaceConstants.TrialWorkspaceMemberLimit
            ? Result.Failure(WorkspaceConstants.Errors.TrialWorkspaceMemberLimitReached, ErrorCodes.Forbidden)
            : Result.Success();
    }
}
