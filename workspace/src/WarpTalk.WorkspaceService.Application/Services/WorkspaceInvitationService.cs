using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceInvitation;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Mappers;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.ValueObjects;
using WarpTalk.Shared;
using WarpTalk.Shared.Interfaces;

namespace WarpTalk.WorkspaceService.Application.Services;

public class WorkspaceInvitationService : IWorkspaceInvitationService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<WorkspaceInvitationService> _logger;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly ITranslationRoomClient _translationRoomClient;
    private readonly IWorkspaceInvitationEmailComposer _emailComposer;
    private readonly IBillingSubscriptionClient _billingSubscriptionClient;
    private readonly IWorkspaceInvitationAcceptanceProcessor _acceptanceProcessor;

    public WorkspaceInvitationService(
        IUnitOfWork unitOfWork,
        ILogger<WorkspaceInvitationService> logger,
        IAuthIdentityClient authIdentity,
        ITranslationRoomClient translationRoomClient,
        IWorkspaceInvitationEmailComposer emailComposer,
        IBillingSubscriptionClient billingSubscriptionClient,
        IWorkspaceInvitationAcceptanceProcessor acceptanceProcessor)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _authIdentity = authIdentity;
        _translationRoomClient = translationRoomClient;
        _emailComposer = emailComposer;
        _billingSubscriptionClient = billingSubscriptionClient;
        _acceptanceProcessor = acceptanceProcessor;
    }

    public async Task<Result<InviteMemberResponse>> InviteMemberAsync(Guid workspaceId, InviteMemberRequest request, Guid inviterUserId, CancellationToken ct = default)
    {
        try
        {
            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure<InviteMemberResponse>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var inviterMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == inviterUserId && m.RemovedAt == null, "", ct);

            if (inviterMember == null)
            {
                return Result.Failure<InviteMemberResponse>(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var inviterRoleName = await _authIdentity.GetRoleNameByIdAsync(inviterMember.RoleId, ct);
            if (!inviterRoleName.IsOwnerOrAdmin())
            {
                return Result.Failure<InviteMemberResponse>(WorkspaceConstants.Errors.OnlyOwnerAdminCanInvite, ErrorCodes.Forbidden);
            }

            if (request.RoleName.IsOwner())
            {
                return Result.Failure<InviteMemberResponse>(
                    WorkspaceConstants.Errors.RoleMustBeAdminOrMember,
                    ErrorCodes.ValidationError);
            }

            if (inviterRoleName.IsAdmin() && request.RoleName.IsAdmin())
            {
                return Result.Failure<InviteMemberResponse>(
                    WorkspaceConstants.Errors.AdminCannotPromoteToAdmin,
                    ErrorCodes.Forbidden);
            }

            if (!EmailAddress.TryParse(request.Email, out var emailAddress) || emailAddress == null)
            {
                return Result.Failure<InviteMemberResponse>(WorkspaceConstants.Errors.InvalidEmailFormat, ErrorCodes.ValidationError);
            }
            var config = WorkspaceHelper.GetWorkspaceConfig(workspace);

            // The inviter decides the access class; the domain only decides which choices are
            // legal. MembershipType is therefore required, not inferred.
            //
            // The fallback that used to stand here inferred it from the invitee's email, and the
            // inference could not express what an inviter might want: External was unreachable
            // whenever the invitee's domain happened to be verified, and unreachable outright in a
            // workspace with no domain policy, where the inference answers Internal for every
            // address (BR-140-011). Keeping it "for older clients" meant those clients silently
            // got a decision nobody made; refusing is the honest answer.
            if (string.IsNullOrWhiteSpace(request.MembershipType)
                || !Enum.TryParse<MembershipType>(request.MembershipType, ignoreCase: true, out var membershipTypeEnum))
            {
                return Result.Failure<InviteMemberResponse>(WorkspaceConstants.Errors.InvalidMembershipType, ErrorCodes.ValidationError);
            }

            var policyResult = await WorkspaceInvitationPolicy.ValidateAsync(
                _unitOfWork,
                workspace,
                emailAddress.Value,
                membershipTypeEnum,
                request.RoleName,
                ct);
            if (!policyResult.IsSuccess)
            {
                return Result.Failure<InviteMemberResponse>(policyResult.Error!, policyResult.ErrorCode);
            }

            var finalRoleName = request.RoleName.IsAdmin()
                ? WorkspaceMemberRole.Admin.ToRoleName()
                : request.RoleName.IsMember()
                    ? WorkspaceMemberRole.Member.ToRoleName()
                    : string.Empty;
            if (string.IsNullOrEmpty(finalRoleName))
            {
                return Result.Failure<InviteMemberResponse>(
                    WorkspaceConstants.Errors.RoleMustBeAdminOrMember,
                    ErrorCodes.ValidationError);
            }
            var finalRoleId = await _authIdentity.GetRoleIdByNameAsync(finalRoleName, ct);
            if (!finalRoleId.HasValue)
            {
                return Result.Failure<InviteMemberResponse>(WorkspaceConstants.Errors.InvalidRoleSpecified, ErrorCodes.ValidationError);
            }

            var existingPendingInvite = await _unitOfWork.WorkspaceInvitationRepository.GetPendingByEmailAsync(workspaceId, request.Email, ct);
            if (existingPendingInvite != null)
            {
                if (existingPendingInvite.ExpiresAt < DateTime.UtcNow)
                {
                    existingPendingInvite.Status = InvitationStatus.EXPIRED.ToString();
                    _unitOfWork.WorkspaceInvitationRepository.Update(existingPendingInvite);
                    await _unitOfWork.SaveChangesAsync(ct);
                }
                else if (await IsNoLongerAcceptableAsync(existingPendingInvite, workspace, ct))
                {
                    // WT-375. The pending invitation cannot be accepted any more — the workspace's
                    // access policy moved after it was sent, and acceptance refuses it (see
                    // WorkspaceInvitationAcceptanceProcessor: the stored membership type is the
                    // decision and may only be admitted unchanged or refused, never recomputed
                    // into one that passes, BR-140-013).
                    //
                    // That rule is right and stays. What was wrong is that it left no way out:
                    // the acceptance error tells the Owner to "revoke it and send a new one", and
                    // sending a new one was refused because the dead invitation was still PENDING.
                    // An Owner who flipped External collaboration ON specifically so somebody
                    // could join had no UI anywhere to unstick them.
                    //
                    // So a re-invite supersedes it. Only in this branch: a pending invitation that
                    // WOULD still be accepted is a real duplicate and stays a conflict, because
                    // re-issuing it silently invalidates a link the invitee may already be holding.
                    existingPendingInvite.Status = InvitationStatus.REVOKED.ToString();
                    _unitOfWork.WorkspaceInvitationRepository.Update(existingPendingInvite);
                    await _unitOfWork.SaveChangesAsync(ct);
                }
                else
                {
                    return Result.Failure<InviteMemberResponse>("An active pending invitation already exists for this email address.", ErrorCodes.Conflict);
                }
            }

            var capacityCheck = await EnsureTrialInviteCapacityAsync(workspaceId, ct);
            if (!capacityCheck.IsSuccess)
            {
                return Result.Failure<InviteMemberResponse>(capacityCheck.Error!, capacityCheck.ErrorCode);
            }

            var membershipType = membershipTypeEnum.ToString();
            var invitationToken = WorkspaceInvitationTokenGenerator.Generate();
            var newInvitation = WorkspaceInvitationMapper.CreateInvitation(
                workspaceId,
                request,
                finalRoleId.Value,
                finalRoleName,
                inviterUserId,
                TokenHasher.Hash(invitationToken),
                membershipType,
                expiryDays: config.InvitationExpiryDays);

            await _unitOfWork.WorkspaceInvitationRepository.AddAsync(newInvitation, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            var inviterUser = await _authIdentity.GetUserByIdAsync(inviterUserId, ct);
            var inviterName = inviterUser != null ? inviterUser.FullName : "A Workspace Admin";

            // Attempt transactional email send via Resend
            var emailResult = await _emailComposer.SendInvitationEmailAsync(newInvitation, workspace, inviterName, finalRoleName, invitationToken, ct);

            string? warning = null;
            if (emailResult.IsSuccess)
            {
                newInvitation.DeliveryStatus = "Sent";
                newInvitation.ProviderMessageId = emailResult.MessageId;
                newInvitation.LastSentAt = DateTime.UtcNow;
                newInvitation.SentCount = 1;
            }
            else
            {
                newInvitation.DeliveryStatus = "Failed";
                newInvitation.LastSentAt = DateTime.UtcNow;
                warning = $"Invitation created successfully, but Resend email delivery failed: {emailResult.ErrorMessage}";
                _logger.LogWarning("Email delivery failed for invitation {InvitationId}: {Error}", newInvitation.Id, emailResult.ErrorMessage);
            }

            _unitOfWork.WorkspaceInvitationRepository.Update(newInvitation);
            await _unitOfWork.SaveChangesAsync(ct);

            string emailLanguage = config.DefaultLanguage ?? WorkspaceConstants.DefaultWorkspaceLanguage;

            // The raw token is returned to the inviter so the UI can offer a shareable link.
            // It was generated above, hashed into the row, handed to the email composer, and
            // then dropped here — the DTO has carried a RawToken field the whole time and it
            // was always null, so the only way to reach an invitation was the email. When
            // delivery fails (see the warning branch above) that left a perfectly valid
            // invitation nobody could ever open.
            //
            // Disclosure is bounded: this endpoint is [Authorize] and Owner/Admin-only, the
            // caller is the person who just created the invitation, and the link still only
            // admits the email it was issued for. The stored value stays hashed; this is the
            // one moment the plaintext exists, and it is not logged.
            var response = new InviteMemberResponse(
                newInvitation.ToDto(finalRoleName), invitationToken, emailLanguage, warning);

            return Result.Success(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while inviting member. WorkspaceId: {WorkspaceId}", workspaceId);
            return Result.Failure<InviteMemberResponse>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<InvitationPolicyResponse>> GetInvitationPolicyAsync(Guid workspaceId, string? email, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure<InvitationPolicyResponse>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            // Same gate as inviting. The response describes who this workspace is willing to
            // admit and on what terms, which is not something a plain member needs to read.
            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
            if (member == null)
            {
                return Result.Failure<InvitationPolicyResponse>(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
            if (!roleName.IsOwnerOrAdmin())
            {
                return Result.Failure<InvitationPolicyResponse>(WorkspaceConstants.Errors.OnlyOwnerAdminCanInvite, ErrorCodes.Forbidden);
            }

            var evaluation = await WorkspaceInvitationPolicy.EvaluateAsync(_unitOfWork, workspace, email, ct);

            return Result.Success(new InvitationPolicyResponse(
                evaluation.SuggestedMembershipType.ToString(),
                evaluation.AllowedMembershipTypes,
                evaluation.RequireVerifiedDomainForInternal,
                evaluation.AllowExternalCollaboration,
                evaluation.AllowSubdomains,
                evaluation.IsEmailDomainVerified,
                evaluation.IsPublicEmailDomain,
                evaluation.InternalDisabledReason,
                evaluation.ExternalDisabledReason));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating invitation policy for workspace {WorkspaceId}", workspaceId);
            return Result.Failure<InvitationPolicyResponse>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<WorkspaceInvitationDto>> RetryDeliveryAsync(Guid workspaceId, Guid invitationId, Guid inviterUserId, CancellationToken ct = default)
    {
        try
        {
            var inviterMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == inviterUserId && m.RemovedAt == null, "", ct);

            if (inviterMember == null)
            {
                return Result.Failure<WorkspaceInvitationDto>(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var inviterRoleName = await _authIdentity.GetRoleNameByIdAsync(inviterMember.RoleId, ct);
            if (!inviterRoleName.IsOwnerOrAdmin())
            {
                return Result.Failure<WorkspaceInvitationDto>(WorkspaceConstants.Errors.OnlyOwnerAdminCanInvite, ErrorCodes.Forbidden);
            }

            var invitation = await _unitOfWork.WorkspaceInvitationRepository.GetByIdAsync(invitationId, ct);
            if (invitation == null || invitation.WorkspaceId != workspaceId)
            {
                return Result.Failure<WorkspaceInvitationDto>(WorkspaceConstants.Errors.InvitationNotFound, ErrorCodes.NotFound);
            }

            if (invitation.Status != InvitationStatus.PENDING.ToString())
            {
                return Result.Failure<WorkspaceInvitationDto>("Only PENDING invitations can have email delivery retried.", ErrorCodes.InvalidState);
            }

            if (invitation.DeliveryStatus != "Failed")
            {
                return Result.Failure<WorkspaceInvitationDto>("Retry email is allowed only when delivery status is Failed.", ErrorCodes.InvalidState);
            }

            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure<WorkspaceInvitationDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var roleName = await _authIdentity.GetRoleNameByIdAsync(invitation.RoleId, ct);
            var invitationToken = WorkspaceInvitationTokenGenerator.Generate();

            // BR-34 — a resend SUPERSEDES rather than overwrites.
            //
            // This used to write the new token hash straight onto the existing row. The old token
            // did stop working, so the security property held, but the SRS status REPLACED had no
            // row to live on and nothing recorded that a second email had gone out under different
            // token material. One row cannot be both the superseded invitation and its replacement.
            //
            // Order matters: the old row is marked before the new one is added, so a reader that
            // catches the transaction mid-flight never sees two PENDING invitations for the same
            // address. Both writes commit together.
            var now = DateTime.UtcNow;
            invitation.Status = InvitationStatus.REPLACED.ToString();
            _unitOfWork.WorkspaceInvitationRepository.Update(invitation);

            // The same source the create path reads, so a workspace that shortened its invitation
            // window gets that window on a resend too rather than the default.
            var config = WorkspaceHelper.GetWorkspaceConfig(workspace);
            var replacement = invitation.ToReplacementInvitation(
                TokenHasher.Hash(invitationToken),
                config.InvitationExpiryDays,
                now);
            await _unitOfWork.WorkspaceInvitationRepository.AddAsync(replacement, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            var inviterUser = await _authIdentity.GetUserByIdAsync(replacement.InvitedBy, ct);
            var inviterName = inviterUser != null ? inviterUser.FullName : "A Workspace Admin";
            var emailResult = await _emailComposer.SendInvitationEmailAsync(replacement, workspace, inviterName, roleName, invitationToken, ct);

            replacement.LastSentAt = DateTime.UtcNow;
            replacement.SentCount++;

            if (emailResult.IsSuccess)
            {
                replacement.DeliveryStatus = "Sent";
                replacement.ProviderMessageId = emailResult.MessageId;
            }
            else
            {
                replacement.DeliveryStatus = "Failed";
                _logger.LogWarning("Retry delivery failed for invitation {InvitationId}: {Error}", replacement.Id, emailResult.ErrorMessage);
            }

            _unitOfWork.WorkspaceInvitationRepository.Update(replacement);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success(replacement.ToDto(roleName));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while retrying invitation email delivery. InvitationId: {InvitationId}", invitationId);
            return Result.Failure<WorkspaceInvitationDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<PagedResult<WorkspaceInvitationDto>>> ListInvitationsAsync(Guid workspaceId, GetWorkspacesQuery query, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);

            var isOwnerOrAdmin = false;
            if (member != null)
            {
                var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
                isOwnerOrAdmin = roleName.IsOwnerOrAdmin();
            }

            if (!isOwnerOrAdmin)
            {
                return Result.Failure<PagedResult<WorkspaceInvitationDto>>(WorkspaceConstants.Errors.OnlyOwnerAdminCanViewInvitations, ErrorCodes.Forbidden);
            }

            if (!string.IsNullOrWhiteSpace(query.Kind)
                && !string.Equals(query.Kind, "outbound", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(query.Kind, "join-request", StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure<PagedResult<WorkspaceInvitationDto>>("Invitation kind must be outbound or join-request.", ErrorCodes.ValidationError);
            }

            var (items, totalCount) = await _unitOfWork.WorkspaceInvitationRepository.GetInvitationsByWorkspaceAsync(workspaceId, query.Page, query.PageSize, ct, query.Kind);

            // Lazy expiration materialization
            var hasChanges = false;
            foreach (var invite in items)
            {
                if (invite.Status == InvitationStatus.PENDING.ToString() && invite.ExpiresAt < DateTime.UtcNow)
                {
                    invite.Status = InvitationStatus.EXPIRED.ToString();
                    _unitOfWork.WorkspaceInvitationRepository.Update(invite);
                    hasChanges = true;
                }
            }

            if (hasChanges)
            {
                await _unitOfWork.SaveChangesAsync(ct);
            }

            var dtos = new List<WorkspaceInvitationDto>();
            foreach (var invite in items)
            {
                var roleName = await _authIdentity.GetRoleNameByIdAsync(invite.RoleId, ct);
                dtos.Add(await WorkspaceInvitationDtoAdapter.ToJoinRequestAwareDtoAsync(_unitOfWork, invite, roleName, ct));
            }

            var pagedResult = new PagedResult<WorkspaceInvitationDto>(dtos, query.Page, query.PageSize, totalCount);

            return Result.Success(pagedResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while listing invitations. WorkspaceId: {WorkspaceId}", workspaceId);
            return Result.Failure<PagedResult<WorkspaceInvitationDto>>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> RevokeInvitationAsync(Guid workspaceId, Guid invitationId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);

            var isOwnerOrAdmin = false;
            if (member != null)
            {
                var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
                isOwnerOrAdmin = roleName.IsOwnerOrAdmin();
            }

            if (!isOwnerOrAdmin)
            {
                return Result.Failure(WorkspaceConstants.Errors.OnlyOwnerAdminCanRevoke, ErrorCodes.Forbidden);
            }

            var invitation = await _unitOfWork.WorkspaceInvitationRepository.GetByIdAsync(invitationId, ct);
            if (invitation == null || invitation.WorkspaceId != workspaceId)
            {
                return Result.Failure(WorkspaceConstants.Errors.InvitationNotFound, ErrorCodes.NotFound);
            }

            if (invitation.Status != InvitationStatus.PENDING.ToString())
            {
                return Result.Failure(WorkspaceConstants.Errors.OnlyPendingCanBeRevoked, ErrorCodes.InvalidState);
            }

            invitation.Status = InvitationStatus.REVOKED.ToString();
            _unitOfWork.WorkspaceInvitationRepository.Update(invitation);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while revoking invitation. InvitationId: {InvitationId}", invitationId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<PreviewInvitationResponse>> PreviewInvitationAsync(string token, CancellationToken ct = default)
    {
        try
        {
            var tokenHash = TokenHasher.Hash(token);
            var invitation = await _unitOfWork.WorkspaceInvitationRepository.GetByTokenHashAsync(tokenHash, ct);

            if (invitation == null)
            {
                return Result.Failure<PreviewInvitationResponse>(WorkspaceConstants.Errors.InvalidOrExpiredToken, ErrorCodes.NotFound);
            }

            string currentStatus = invitation.Status;
            if (invitation.Status == InvitationStatus.PENDING.ToString() && invitation.ExpiresAt < DateTime.UtcNow)
            {
                currentStatus = InvitationStatus.EXPIRED.ToString();
            }

            string maskedEmail = invitation.Email;
            if (EmailAddress.TryParse(invitation.Email, out var emailAddress) && emailAddress != null)
            {
                maskedEmail = emailAddress.MaskedValue;
            }

            var roleName = await _authIdentity.GetRoleNameByIdAsync(invitation.RoleId, ct);
            var existingUser = await _authIdentity.GetUserByEmailAsync(invitation.Email, ct);
            var accountExists = existingUser != null;

            var response = invitation.ToPreviewResponse(roleName, maskedEmail, currentStatus, accountExists);

            return Result.Success(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while previewing invitation.");
            return Result.Failure<PreviewInvitationResponse>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<VerifyInvitationInternalResponse>> VerifyInvitationTokenInternalAsync(string token, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return Result.Failure<VerifyInvitationInternalResponse>(WorkspaceConstants.Errors.TokenRequired, ErrorCodes.ValidationError);
            }

            var tokenHash = TokenHasher.Hash(token);
            var invitation = await _unitOfWork.WorkspaceInvitationRepository.GetByTokenHashAsync(tokenHash, ct);

            if (invitation == null)
            {
                return Result.Failure<VerifyInvitationInternalResponse>(WorkspaceConstants.Errors.InvalidOrExpiredToken, ErrorCodes.NotFound);
            }

            if (invitation.Status != InvitationStatus.PENDING.ToString())
            {
                return Result.Failure<VerifyInvitationInternalResponse>(string.Format(WorkspaceConstants.Errors.InvitationNoLongerValidFormat, invitation.Status), ErrorCodes.InvalidState);
            }

            if (invitation.ExpiresAt < DateTime.UtcNow)
            {
                invitation.Status = InvitationStatus.EXPIRED.ToString();
                _unitOfWork.WorkspaceInvitationRepository.Update(invitation);
                await _unitOfWork.SaveChangesAsync(ct);
                return Result.Failure<VerifyInvitationInternalResponse>(WorkspaceConstants.Errors.InvitationExpired, ErrorCodes.InvalidState);
            }

            var roleName = await _authIdentity.GetRoleNameByIdAsync(invitation.RoleId, ct);
            var response = invitation.ToVerifyInternalResponse(roleName);
            return Result.Success(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while verifying invitation token internally.");
            return Result.Failure<VerifyInvitationInternalResponse>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<List<WorkspaceInvitationDto>>> GetPendingInvitationsForUserAsync(Guid userId, string userEmail, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(userEmail))
            {
                return Result.Failure<List<WorkspaceInvitationDto>>("User email is required.", ErrorCodes.ValidationError);
            }

            var invitations = await _unitOfWork.WorkspaceInvitationRepository.GetPendingInvitationsByEmailAsync(userEmail, ct);

            var activeInvitations = new List<WorkspaceInvitationDto>();
            var hasExpiredChanges = false;

            foreach (var invite in invitations)
            {
                if (invite.ExpiresAt < DateTime.UtcNow)
                {
                    invite.Status = InvitationStatus.EXPIRED.ToString();
                    _unitOfWork.WorkspaceInvitationRepository.Update(invite);
                    hasExpiredChanges = true;
                }
                else
                {
                    var roleName = await _authIdentity.GetRoleNameByIdAsync(invite.RoleId, ct);
                    activeInvitations.Add(invite.ToDto(roleName));
                }
            }

            if (hasExpiredChanges)
            {
                await _unitOfWork.SaveChangesAsync(ct);
            }

            return Result.Success(activeInvitations);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while getting pending invitations for user {UserEmail}", userEmail);
            return Result.Failure<List<WorkspaceInvitationDto>>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<List<WorkspaceInvitationDto>>> GetJoinRequestsForUserAsync(Guid userId, CancellationToken ct = default)
    {
        try
        {
            var requests = await _unitOfWork.WorkspaceInvitationRepository.GetJoinRequestsByUserAsync(userId, ct);
            var result = new List<WorkspaceInvitationDto>();

            foreach (var request in requests)
            {
                var roleName = await _authIdentity.GetRoleNameByIdAsync(request.RoleId, ct);
                result.Add(await WorkspaceInvitationDtoAdapter.ToJoinRequestAwareDtoAsync(_unitOfWork, request, roleName, ct));
            }

            return Result.Success(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while getting join requests for user {UserId}", userId);
            return Result.Failure<List<WorkspaceInvitationDto>>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> AcceptInvitationAsync(AcceptInvitationRequest request, Guid userId, string userEmail, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            var pendingInvites = await GetPendingInvitationsForUserAsync(userId, userEmail, ct);
            if (!pendingInvites.IsSuccess || pendingInvites.Value is null || !pendingInvites.Value.Any())
            {
                return Result.Failure("No active pending invitations found for your verified email address.", ErrorCodes.NotFound);
            }
            return await AcceptInvitationByIdAsync(pendingInvites.Value!.First().Id, userId, userEmail, ct);
        }

        var tokenHash = TokenHasher.Hash(request.Token);
        var invitation = await _unitOfWork.WorkspaceInvitationRepository.GetByTokenHashAsync(tokenHash, ct);
        if (invitation == null)
        {
            return Result.Failure(WorkspaceConstants.Errors.InvalidOrExpiredToken, ErrorCodes.NotFound);
        }

        try
        {
            return await _acceptanceProcessor.ProcessAcceptanceAsync(invitation, userId, userEmail, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while accepting invitation {InvitationId}.", invitation.Id);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> AcceptInvitationByIdAsync(Guid invitationId, Guid userId, string userEmail, CancellationToken ct = default)
    {
        var invitation = await _unitOfWork.WorkspaceInvitationRepository.GetByIdAsync(invitationId, ct);
        if (invitation == null)
        {
            return Result.Failure(WorkspaceConstants.Errors.InvitationNotFound, ErrorCodes.NotFound);
        }

        try
        {
            return await _acceptanceProcessor.ProcessAcceptanceAsync(invitation, userId, userEmail, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while accepting invitation {InvitationId}.", invitation.Id);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<WorkspaceInvitationDto>> CreateJoinRequestAsync(CreateJoinRequestCommand command, Guid userId, string userEmail, CancellationToken ct = default)
    {
        try
        {
            Guid workspaceId = Guid.Empty;

            if (!string.IsNullOrWhiteSpace(command.RoomCode))
            {
                var room = await _translationRoomClient.GetTranslationRoomByCodeAsync(command.RoomCode, ct);
                if (room == null)
                {
                    return Result.Failure<WorkspaceInvitationDto>(WorkspaceConstants.Errors.TranslationRoomNotFound, ErrorCodes.NotFound);
                }
                workspaceId = room.WorkspaceId;
            }
            else if (!string.IsNullOrWhiteSpace(command.WorkspaceSlug))
            {
                var workspace = await _unitOfWork.WorkspaceRepository.FirstOrDefaultAsync(w => w.Slug == command.WorkspaceSlug, "", ct);
                if (workspace == null)
                {
                    return Result.Failure<WorkspaceInvitationDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
                }
                workspaceId = workspace.Id;
            }
            else
            {
                return Result.Failure<WorkspaceInvitationDto>(WorkspaceConstants.Errors.RoomCodeOrWorkspaceSlugRequired, ErrorCodes.ValidationError);
            }

            var existingMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);

            if (existingMember != null)
            {
                return Result.Failure<WorkspaceInvitationDto>(WorkspaceConstants.Errors.AlreadyMember, ErrorCodes.InvalidState);
            }

            var existingPendingRequest = await _unitOfWork.WorkspaceInvitationRepository.FirstOrDefaultAsync(
                i => i.WorkspaceId == workspaceId && i.Email.ToLower() == userEmail.ToLower() && i.Status == InvitationStatus.REQUESTED.ToString(), "", ct);

            if (existingPendingRequest != null)
            {
                var memberRoleName = await _authIdentity.GetRoleNameByIdAsync(existingPendingRequest.RoleId, ct);
                return Result.Success(await WorkspaceInvitationDtoAdapter.ToJoinRequestAwareDtoAsync(_unitOfWork, existingPendingRequest, memberRoleName, ct));
            }

            var defaultMemberRoleId = await _authIdentity.GetRoleIdByNameAsync("Member", ct);
            if (!defaultMemberRoleId.HasValue)
            {
                return Result.Failure<WorkspaceInvitationDto>(WorkspaceConstants.Errors.MemberRoleNotFound, ErrorCodes.InternalServerError);
            }

            var workspaceForClassification = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspaceForClassification == null)
            {
                return Result.Failure<WorkspaceInvitationDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }
            if (!workspaceForClassification.IsActive || workspaceForClassification.DeletedAt != null)
            {
                return Result.Failure<WorkspaceInvitationDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var eligibility = await WorkspaceHelper.EvaluateJoinRequestEligibilityAsync(
                _unitOfWork,
                userEmail,
                userId,
                workspaceForClassification,
                ct);

            var request = new InviteMemberRequest(userEmail, "Member", eligibility.InferredMembershipType.ToString());
            var joinRequest = WorkspaceInvitationMapper.CreateInvitation(
                workspaceId,
                request,
                defaultMemberRoleId.Value,
                "Member",
                userId,
                TokenHasher.Hash($"join-request:{userId:N}:{Guid.NewGuid():N}"),
                eligibility.InferredMembershipType.ToString());
            joinRequest.Status = InvitationStatus.REQUESTED.ToString();
            joinRequest.RequestedBy = userId;
            joinRequest.Workspace = workspaceForClassification;

            await _unitOfWork.WorkspaceInvitationRepository.AddAsync(joinRequest, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success(joinRequest.ToDto("Member", eligibility));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while creating join request.");
            return Result.Failure<WorkspaceInvitationDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<ApproveJoinRequestResponse>> ApproveJoinRequestAsync(
        Guid workspaceId,
        Guid invitationId,
        Guid adminUserId,
        ApproveJoinRequestRequest? request = null,
        CancellationToken ct = default)
    {
        try
        {
            var adminMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == adminUserId && m.RemovedAt == null, "", ct);

            if (adminMember == null)
            {
                return Result.Failure<ApproveJoinRequestResponse>(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var roleName = await _authIdentity.GetRoleNameByIdAsync(adminMember.RoleId, ct);
            if (!roleName.IsOwnerOrAdmin())
            {
                return Result.Failure<ApproveJoinRequestResponse>(WorkspaceConstants.Errors.OnlyOwnerAdminCanApprove, ErrorCodes.Forbidden);
            }

            var invitation = await _unitOfWork.WorkspaceInvitationRepository.GetByIdAsync(invitationId, ct);
            if (invitation == null || invitation.WorkspaceId != workspaceId)
            {
                return Result.Failure<ApproveJoinRequestResponse>(WorkspaceConstants.Errors.InvitationNotFound, ErrorCodes.NotFound);
            }

            if (invitation.Status != InvitationStatus.REQUESTED.ToString())
            {
                return Result.Failure<ApproveJoinRequestResponse>(WorkspaceConstants.Errors.OnlyRequestedCanBeApproved, ErrorCodes.InvalidState);
            }

            var capacityCheck = await EnsureTrialInviteCapacityAsync(workspaceId, ct);
            if (!capacityCheck.IsSuccess)
            {
                return Result.Failure<ApproveJoinRequestResponse>(capacityCheck.Error!, capacityCheck.ErrorCode);
            }

            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure<ApproveJoinRequestResponse>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }
            invitation.Workspace = workspace;

            var requesterId = invitation.RequestedBy ?? invitation.InvitedBy;
            var eligibility = await WorkspaceHelper.EvaluateJoinRequestEligibilityAsync(
                _unitOfWork,
                invitation.Email,
                requesterId,
                workspace,
                ct);

            var selectedMembershipType = request?.MembershipType;
            if (string.IsNullOrWhiteSpace(selectedMembershipType))
            {
                selectedMembershipType = eligibility.AllowedFinalMembershipTypes.Count == 1
                    ? eligibility.AllowedFinalMembershipTypes[0]
                    : invitation.MembershipType;
            }

            if (!Enum.TryParse<MembershipType>(selectedMembershipType, true, out var membershipType))
            {
                return Result.Failure<ApproveJoinRequestResponse>(WorkspaceConstants.Errors.InvalidMembershipType, ErrorCodes.ValidationError);
            }

            var isAllowedMembershipType = eligibility.AllowedFinalMembershipTypes.Any(
                allowed => string.Equals(allowed, membershipType.ToString(), StringComparison.OrdinalIgnoreCase));
            if (!isAllowedMembershipType)
            {
                return Result.Failure<ApproveJoinRequestResponse>(
                    eligibility.PolicyReason ?? WorkspaceConstants.Errors.InvalidMembershipType,
                    ErrorCodes.ValidationError);
            }

            // ANY row for this pair, removed or not. WT-416.
            //
            // This used to filter on RemovedAt == null, which made a departed member invisible
            // here — and then AddAsync below inserted a second row for the same
            // (workspace_id, user_id). That pair carries a UNIQUE constraint with NO
            // `WHERE removed_at IS NULL` predicate, so the insert threw, the catch-all turned it
            // into "An unexpected error occurred", and the owner got a 500. Three members of one
            // production workspace could not be let back in.
            //
            // Leaving is a soft delete, so the slot is still occupied by the row that recorded
            // the departure. Approving a rejoin has to REUSE it.
            var existingMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == requesterId, "", ct);
            if (existingMember != null && existingMember.RemovedAt == null)
            {
                return Result.Failure<ApproveJoinRequestResponse>(WorkspaceConstants.Errors.AlreadyMember, ErrorCodes.Conflict);
            }

            var memberRoleId = await _authIdentity.GetRoleIdByNameAsync("Member", ct);
            if (!memberRoleId.HasValue)
            {
                return Result.Failure<ApproveJoinRequestResponse>(WorkspaceConstants.Errors.MemberRoleNotFound, ErrorCodes.InternalServerError);
            }

            var reviewedAt = DateTime.UtcNow;
            invitation.RoleId = memberRoleId.Value;
            invitation.MembershipType = membershipType.ToString();
            invitation.Status = InvitationStatus.ACCEPTED.ToString();
            invitation.AcceptedAt = reviewedAt;
            invitation.ReviewedBy = adminUserId;
            invitation.ReviewedAt = reviewedAt;

            if (existingMember != null)
            {
                // A returning member. Their row already holds the slot the unique constraint
                // guards, so it is revived rather than duplicated. ReviveAsMember sets exactly
                // what CreateInvitationMember sets, from the same helpers, so somebody rejoining
                // does not quietly get different defaults from somebody joining for the first
                // time.
                existingMember.ReviveAsMember(memberRoleId.Value, membershipType.ToString(), reviewedAt);
                _unitOfWork.WorkspaceMemberRepository.Update(existingMember);
            }
            else
            {
                var newMember = WorkspaceMemberMapper.CreateInvitationMember(
                    workspaceId,
                    requesterId,
                    memberRoleId.Value,
                    membershipType.ToString(),
                    reviewedAt);

                await _unitOfWork.WorkspaceMemberRepository.AddAsync(newMember, ct);
            }
            _unitOfWork.WorkspaceInvitationRepository.Update(invitation);
            await _unitOfWork.SaveChangesAsync(ct);

            SendEmailResponse emailResult;
            try
            {
                emailResult = await _emailComposer.SendJoinRequestApprovedEmailAsync(invitation, workspace, ct);
            }
            catch (Exception emailException)
            {
                _logger.LogWarning(emailException, "Approval email failed for join request {InvitationId}", invitation.Id);
                emailResult = new SendEmailResponse(false, null, emailException.Message);
            }

            invitation.DeliveryStatus = emailResult.IsSuccess ? "Sent" : "Failed";
            invitation.ProviderMessageId = emailResult.MessageId;
            invitation.LastSentAt = DateTime.UtcNow;
            invitation.SentCount++;
            _unitOfWork.WorkspaceInvitationRepository.Update(invitation);
            try
            {
                await _unitOfWork.SaveChangesAsync(ct);
            }
            catch (Exception deliveryPersistenceException)
            {
                _logger.LogWarning(deliveryPersistenceException, "Could not persist approval email delivery status for {InvitationId}", invitation.Id);
            }

            var approvalResponse = new ApproveJoinRequestResponse(
                invitation.ToDto("Member"),
                emailResult.IsSuccess ? "Sent" : "Failed",
                emailResult.IsSuccess ? null : emailResult.ErrorMessage);
            return Result.Success(approvalResponse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while approving join request.");
            return Result.Failure<ApproveJoinRequestResponse>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> RejectJoinRequestAsync(Guid workspaceId, Guid invitationId, Guid adminUserId, CancellationToken ct = default)
    {
        try
        {
            var adminMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == adminUserId && m.RemovedAt == null, "", ct);

            if (adminMember == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var roleName = await _authIdentity.GetRoleNameByIdAsync(adminMember.RoleId, ct);
            if (!roleName.IsOwnerOrAdmin())
            {
                return Result.Failure(WorkspaceConstants.Errors.OnlyOwnerAdminCanReject, ErrorCodes.Forbidden);
            }

            var invitation = await _unitOfWork.WorkspaceInvitationRepository.GetByIdAsync(invitationId, ct);
            if (invitation == null || invitation.WorkspaceId != workspaceId)
            {
                return Result.Failure(WorkspaceConstants.Errors.InvitationNotFound, ErrorCodes.NotFound);
            }

            if (invitation.Status != InvitationStatus.REQUESTED.ToString())
            {
                return Result.Failure(WorkspaceConstants.Errors.OnlyRequestedCanBeRejected, ErrorCodes.InvalidState);
            }

            invitation.Status = InvitationStatus.REJECTED.ToString();
            invitation.ReviewedBy = adminUserId;
            invitation.ReviewedAt = DateTime.UtcNow;
            _unitOfWork.WorkspaceInvitationRepository.Update(invitation);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while rejecting join request.");
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<WorkspaceInvitationDto>> CreateLeaveRequestAsync(
        Guid workspaceId,
        Guid userId,
        string userEmail,
        CancellationToken ct = default)
    {
        try
        {
            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null || !workspace.IsActive || workspace.DeletedAt != null)
            {
                return Result.Failure<WorkspaceInvitationDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
            if (member == null)
            {
                return Result.Failure<WorkspaceInvitationDto>(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
            if (roleName.IsOwner())
            {
                var ownerRoleId = await _authIdentity.GetRoleIdByNameAsync(WorkspaceMemberRole.Owner.ToRoleName(), ct);
                if (ownerRoleId != null)
                {
                    var activeOwnersCount = await _unitOfWork.WorkspaceMemberRepository.CountActiveOwnersAsync(workspaceId, ownerRoleId.Value, ct);
                    if (activeOwnersCount <= 1)
                    {
                        return Result.Failure<WorkspaceInvitationDto>(WorkspaceConstants.Errors.CannotLeaveAsLastOwner, ErrorCodes.ValidationError);
                    }
                }
            }

            var existingPendingLeave = await _unitOfWork.WorkspaceInvitationRepository.FirstOrDefaultAsync(
                i => i.WorkspaceId == workspaceId && i.RequestedBy == userId && i.Status == InvitationStatus.LEAVE_REQUESTED.ToString(), "", ct);

            if (existingPendingLeave != null)
            {
                return Result.Success(await WorkspaceInvitationDtoAdapter.ToJoinRequestAwareDtoAsync(_unitOfWork, existingPendingLeave, roleName, ct));
            }

            var defaultMemberRoleId = await _authIdentity.GetRoleIdByNameAsync("Member", ct) ?? member.RoleId;

            var request = new InviteMemberRequest(userEmail, roleName, member.MembershipType);
            var leaveRequest = WorkspaceInvitationMapper.CreateInvitation(
                workspaceId,
                request,
                defaultMemberRoleId,
                roleName,
                userId,
                TokenHasher.Hash($"leave-request:{userId:N}:{Guid.NewGuid():N}"),
                member.MembershipType);
            leaveRequest.Status = InvitationStatus.LEAVE_REQUESTED.ToString();
            leaveRequest.RequestedBy = userId;
            leaveRequest.Workspace = workspace;

            await _unitOfWork.WorkspaceInvitationRepository.AddAsync(leaveRequest, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success(await WorkspaceInvitationDtoAdapter.ToJoinRequestAwareDtoAsync(_unitOfWork, leaveRequest, roleName, ct));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while creating leave request for workspace {WorkspaceId}, user {UserId}", workspaceId, userId);
            return Result.Failure<WorkspaceInvitationDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> ApproveLeaveRequestAsync(
        Guid workspaceId,
        Guid leaveRequestId,
        Guid adminUserId,
        CancellationToken ct = default)
    {
        try
        {
            var adminMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == adminUserId && m.RemovedAt == null, "", ct);

            if (adminMember == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var adminRoleName = await _authIdentity.GetRoleNameByIdAsync(adminMember.RoleId, ct);
            if (!adminRoleName.IsOwnerOrAdmin())
            {
                return Result.Failure(WorkspaceConstants.Errors.OnlyOwnerAdminCanReviewLeaveRequest, ErrorCodes.Forbidden);
            }

            var leaveRequest = await _unitOfWork.WorkspaceInvitationRepository.GetByIdAsync(leaveRequestId, ct);
            if (leaveRequest == null || leaveRequest.WorkspaceId != workspaceId)
            {
                return Result.Failure(WorkspaceConstants.Errors.LeaveRequestNotFound, ErrorCodes.NotFound);
            }

            if (leaveRequest.Status != InvitationStatus.LEAVE_REQUESTED.ToString())
            {
                return Result.Failure(WorkspaceConstants.Errors.LeaveRequestNotFound, ErrorCodes.InvalidState);
            }

            var targetUserId = leaveRequest.RequestedBy ?? leaveRequest.InvitedBy;
            var targetMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == targetUserId && m.RemovedAt == null, "", ct);

            var reviewedAt = DateTime.UtcNow;
            leaveRequest.Status = InvitationStatus.ACCEPTED.ToString();
            leaveRequest.ReviewedBy = adminUserId;
            leaveRequest.ReviewedAt = reviewedAt;
            _unitOfWork.WorkspaceInvitationRepository.Update(leaveRequest);

            if (targetMember != null)
            {
                targetMember.RemovedAt = reviewedAt;
                targetMember.RemovedBy = adminUserId;
                targetMember.Status = WorkspaceMemberStatus.Removed.ToStorageValue();
                _unitOfWork.WorkspaceMemberRepository.Update(targetMember);
            }

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while approving leave request {LeaveRequestId}.", leaveRequestId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> RejectLeaveRequestAsync(
        Guid workspaceId,
        Guid leaveRequestId,
        Guid adminUserId,
        CancellationToken ct = default)
    {
        try
        {
            var adminMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == adminUserId && m.RemovedAt == null, "", ct);

            if (adminMember == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var adminRoleName = await _authIdentity.GetRoleNameByIdAsync(adminMember.RoleId, ct);
            if (!adminRoleName.IsOwnerOrAdmin())
            {
                return Result.Failure(WorkspaceConstants.Errors.OnlyOwnerAdminCanReviewLeaveRequest, ErrorCodes.Forbidden);
            }

            var leaveRequest = await _unitOfWork.WorkspaceInvitationRepository.GetByIdAsync(leaveRequestId, ct);
            if (leaveRequest == null || leaveRequest.WorkspaceId != workspaceId)
            {
                return Result.Failure(WorkspaceConstants.Errors.LeaveRequestNotFound, ErrorCodes.NotFound);
            }

            if (leaveRequest.Status != InvitationStatus.LEAVE_REQUESTED.ToString())
            {
                return Result.Failure(WorkspaceConstants.Errors.LeaveRequestNotFound, ErrorCodes.InvalidState);
            }

            leaveRequest.Status = InvitationStatus.REJECTED.ToString();
            leaveRequest.ReviewedBy = adminUserId;
            leaveRequest.ReviewedAt = DateTime.UtcNow;
            _unitOfWork.WorkspaceInvitationRepository.Update(leaveRequest);

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while rejecting leave request {LeaveRequestId}.", leaveRequestId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// Whether a PENDING invitation would now be refused at acceptance.
    ///
    /// Asked against the same policy acceptance asks, so the two can never disagree about which
    /// invitations are dead — an invitation this says is fine but acceptance refuses would be
    /// stuck all over again, which is the WT-375 defect itself.
    /// </summary>
    private async Task<bool> IsNoLongerAcceptableAsync(
        WorkspaceInvitation invitation,
        Workspace workspace,
        CancellationToken ct)
    {
        if (!Enum.TryParse<MembershipType>(invitation.MembershipType, ignoreCase: true, out var storedType))
        {
            // A membership type nothing can parse is not a live invitation to protect.
            return true;
        }

        var roleName = await _authIdentity.GetRoleNameByIdAsync(invitation.RoleId, ct);
        var policyResult = await WorkspaceInvitationPolicy.ValidateAsync(
            _unitOfWork,
            workspace,
            invitation.Email,
            storedType,
            roleName,
            ct);

        return !policyResult.IsSuccess;
    }

    private async Task<Result> EnsureTrialInviteCapacityAsync(Guid workspaceId, CancellationToken ct)
    {
        if (!await _billingSubscriptionClient.IsWorkspaceOnActiveTrialAsync(workspaceId, ct))
        {
            return Result.Success();
        }

        var activeMemberCount = await _unitOfWork.WorkspaceMemberRepository.CountActiveMembersByWorkspaceAsync(workspaceId, ct);
        var pendingInvitations = await _unitOfWork.WorkspaceInvitationRepository.FindAsync(
            i => i.WorkspaceId == workspaceId &&
                 i.Status == InvitationStatus.PENDING.ToString() &&
                 i.ExpiresAt >= DateTime.UtcNow,
            "",
            ct);

        return activeMemberCount + pendingInvitations.Count >= WorkspaceConstants.TrialWorkspaceMemberLimit
            ? Result.Failure(WorkspaceConstants.Errors.TrialWorkspaceMemberLimitReached, ErrorCodes.Forbidden)
            : Result.Success();
    }

}
