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

        if (membershipType == MembershipType.Internal)
        {
            var isInternalElsewhere = await WorkspaceHelper.IsUserInternalMemberOfAnyEnterpriseWorkspaceAsync(_unitOfWork, userId, userEmail, ct);
            if (isInternalElsewhere)
            {
                return Result.Failure(WorkspaceConstants.Errors.UserAlreadyInternalElsewhere, ErrorCodes.Forbidden);
            }
        }

        // WT-417: `RemovedAt == null`, which this lookup did not have.
        //
        // Leaving a workspace, being removed from one, or having one deleted under you is a SOFT
        // delete — the row stays and RemovedAt is stamped. Without the predicate, that row read
        // as a live membership forever, so accepting ANY later invitation to that workspace was
        // answered 409 AlreadyMember. Production: one account could not accept an invitation to
        // a workspace it had left, internal or external, by link or by web, every time.
        //
        // The same predicate was already on the sibling guards — RequestToJoinAsync has it, and
        // ApproveJoinRequestAsync got it in WT-416. This was the third door and the last one
        // still open. Revival itself happens in ProcessAcceptanceAsync, because the row must be
        // reused rather than re-inserted; see there.
        var liveMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
            m => m.WorkspaceId == invitation.WorkspaceId && m.UserId == userId && m.RemovedAt == null,
            "",
            ct);

        if (liveMember != null)
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
        var membershipType = ResolveStoredMembershipType(invitation).ToString();

        // WT-417. Validation above has confirmed there is no LIVE membership, but a departed one
        // may still hold the row: workspace_members carries UNIQUE (workspace_id, user_id) with
        // no `WHERE removed_at IS NULL`, so the schema allows one row per person per workspace
        // forever while the code tells joins apart by RemovedAt. Inserting here would hit the
        // constraint and surface as a 500 — which is the 400/500 half of the report, waiting
        // behind the 409 that fired first.
        var departedMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
            m => m.WorkspaceId == invitation.WorkspaceId && m.UserId == userId, "", ct);

        if (departedMember != null)
        {
            departedMember.ReviveAsMember(invitation.RoleId, membershipType);
            _unitOfWork.WorkspaceMemberRepository.Update(departedMember);
        }
        else
        {
            await _unitOfWork.WorkspaceMemberRepository.AddAsync(
                WorkspaceMemberMapper.CreateInvitationMember(
                    invitation.WorkspaceId, userId, invitation.RoleId, membershipType),
                ct);
        }

        invitation.Status = InvitationStatus.ACCEPTED.ToString();
        invitation.AcceptedAt = DateTime.UtcNow;

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
