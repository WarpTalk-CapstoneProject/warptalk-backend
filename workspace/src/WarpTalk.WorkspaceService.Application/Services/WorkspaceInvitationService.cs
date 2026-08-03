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

    public WorkspaceInvitationService(
        IUnitOfWork unitOfWork,
        ILogger<WorkspaceInvitationService> logger,
        IAuthIdentityClient authIdentity,
        ITranslationRoomClient translationRoomClient,
        IWorkspaceInvitationEmailComposer emailComposer)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _authIdentity = authIdentity;
        _translationRoomClient = translationRoomClient;
        _emailComposer = emailComposer;
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
            var membershipTypeEnum = await WorkspaceHelper.DetermineMembershipTypeAsync(
                _unitOfWork,
                emailAddress.Value,
                workspace,
                ct);

            if (membershipTypeEnum == MembershipType.External)
            {
                if (!config.AllowExternalCollaboration)
                {
                    return Result.Failure<InviteMemberResponse>(WorkspaceConstants.Errors.ExternalCollaborationNotAllowed, ErrorCodes.Forbidden);
                }
                if (!request.RoleName.IsMember())
                {
                    return Result.Failure<InviteMemberResponse>(WorkspaceConstants.Errors.ExternalMemberMustHaveMemberRole, ErrorCodes.ValidationError);
                }
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
                else
                {
                    return Result.Failure<InviteMemberResponse>("An active pending invitation already exists for this email address.", ErrorCodes.Conflict);
                }
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
            var response = new InviteMemberResponse(newInvitation.ToDto(finalRoleName), null, emailLanguage, warning);

            return Result.Success(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while inviting member. WorkspaceId: {WorkspaceId}", workspaceId);
            return Result.Failure<InviteMemberResponse>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
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
            invitation.TokenHash = TokenHasher.Hash(invitationToken);
            _unitOfWork.WorkspaceInvitationRepository.Update(invitation);
            await _unitOfWork.SaveChangesAsync(ct);

            var inviterUser = await _authIdentity.GetUserByIdAsync(invitation.InvitedBy, ct);
            var inviterName = inviterUser != null ? inviterUser.FullName : "A Workspace Admin";
            var emailResult = await _emailComposer.SendInvitationEmailAsync(invitation, workspace, inviterName, roleName, invitationToken, ct);

            invitation.LastSentAt = DateTime.UtcNow;
            invitation.SentCount++;

            if (emailResult.IsSuccess)
            {
                invitation.DeliveryStatus = "Sent";
                invitation.ProviderMessageId = emailResult.MessageId;
            }
            else
            {
                invitation.DeliveryStatus = "Failed";
                _logger.LogWarning("Retry delivery failed for invitation {InvitationId}: {Error}", invitation.Id, emailResult.ErrorMessage);
            }

            _unitOfWork.WorkspaceInvitationRepository.Update(invitation);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success(invitation.ToDto(roleName));
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

        return await WorkspaceInvitationHelper.ProcessAcceptanceAsync(_unitOfWork, invitation, userId, userEmail, ct);
    }

    public async Task<Result> AcceptInvitationByIdAsync(Guid invitationId, Guid userId, string userEmail, CancellationToken ct = default)
    {
        var invitation = await _unitOfWork.WorkspaceInvitationRepository.GetByIdAsync(invitationId, ct);
        if (invitation == null)
        {
            return Result.Failure(WorkspaceConstants.Errors.InvitationNotFound, ErrorCodes.NotFound);
        }

        return await WorkspaceInvitationHelper.ProcessAcceptanceAsync(_unitOfWork, invitation, userId, userEmail, ct);
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

            var existingMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == requesterId && m.RemovedAt == null, "", ct);
            if (existingMember != null)
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

            var newMember = WorkspaceMemberMapper.CreateInvitationMember(
                workspaceId,
                requesterId,
                memberRoleId.Value,
                membershipType.ToString(),
                reviewedAt);

            await _unitOfWork.WorkspaceMemberRepository.AddAsync(newMember, ct);
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

}
