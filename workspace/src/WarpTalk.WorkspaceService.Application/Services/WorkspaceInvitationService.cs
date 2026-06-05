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
using WarpTalk.WorkspaceService.Application.Mappers.WorkspaceInvitation;
using WarpTalk.WorkspaceService.Application.Mappers.WorkspaceMember;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.ValueObjects;
using WarpTalk.Shared;

namespace WarpTalk.WorkspaceService.Application.Services;

public class WorkspaceInvitationService : IWorkspaceInvitationService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<WorkspaceInvitationService> _logger;
    private readonly IAuthIdentityClient _authIdentity;

    public WorkspaceInvitationService(
        IUnitOfWork unitOfWork,
        ILogger<WorkspaceInvitationService> logger,
        IAuthIdentityClient authIdentity)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _authIdentity = authIdentity;
    }

    private async Task<Guid?> GetRoleIdByNameAsync(string roleName, CancellationToken ct)
    {
        var role = await _authIdentity.GetRoleByNameAsync(roleName, ct);
        return role?.Id;
    }

    private async Task<string> GetRoleNameByIdAsync(Guid roleId, CancellationToken ct)
    {
        var role = await _authIdentity.GetRoleByIdAsync(roleId, ct);
        return role?.Name ?? "Member";
    }

    public async Task<Result<InviteMemberResponse>> InviteMemberAsync(Guid workspaceId, InviteMemberRequest request, Guid inviterUserId, CancellationToken ct = default)
    {
        try
        {
            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure<InviteMemberResponse>("Workspace not found.", ErrorCodes.NotFound);
            }

            var inviterMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == inviterUserId, "", ct);

            if (inviterMember == null)
            {
                return Result.Failure<InviteMemberResponse>("User is not a member of this workspace.", ErrorCodes.Forbidden);
            }

            var inviterRoleName = await GetRoleNameByIdAsync(inviterMember.RoleId, ct);
            if (inviterRoleName != WorkspaceMemberRole.Owner.ToRoleName() && inviterRoleName != WorkspaceMemberRole.Admin.ToRoleName())
            {
                return Result.Failure<InviteMemberResponse>("Only Owner or Admin can invite members.", ErrorCodes.Forbidden);
            }

            if (inviterRoleName == WorkspaceMemberRole.Admin.ToRoleName() && request.RoleName == WorkspaceMemberRole.Owner.ToRoleName())
            {
                return Result.Failure<InviteMemberResponse>("Admin cannot assign Owner role.", ErrorCodes.Forbidden);
            }

            if (!EmailAddress.TryParse(request.Email, out var emailAddress) || emailAddress == null)
            {
                return Result.Failure<InviteMemberResponse>("Invalid email format.", ErrorCodes.ValidationError);
            }
            var domain = emailAddress.Domain;

            var finalRoleName = request.RoleName;
            var config = WorkspaceHelper.GetWorkspaceConfig(workspace);
            var isDomainVerified = config.VerifiedDomains != null && config.VerifiedDomains.Any(vd => string.Equals(vd.Trim(), domain, StringComparison.OrdinalIgnoreCase));
            
            if (!isDomainVerified)
            {
                // External partner — anyone can be invited regardless of their primary workspace
                if (!config.AllowExternalCollaboration)
                {
                    return Result.Failure<InviteMemberResponse>("Workspace does not allow external collaboration.", ErrorCodes.Forbidden);
                }
                finalRoleName = WorkspaceMemberRole.Member.ToRoleName();
            }

            var finalRoleId = await GetRoleIdByNameAsync(finalRoleName, ct);
            if (!finalRoleId.HasValue)
            {
                return Result.Failure<InviteMemberResponse>("Invalid role specified.", ErrorCodes.ValidationError);
            }

            var pendingInvite = await _unitOfWork.WorkspaceInvitationRepository.GetPendingByEmailAsync(workspaceId, request.Email, ct);
            if (pendingInvite != null)
            {
                pendingInvite.Status = InvitationStatus.REPLACED.ToString();
                _unitOfWork.WorkspaceInvitationRepository.Update(pendingInvite);
            }

            var rawToken = Guid.NewGuid().ToString("N");
            var tokenHash = TokenHasher.Hash(rawToken);

            var membershipType = isDomainVerified ? MembershipType.Internal.ToString() : MembershipType.External.ToString();
            var newInvitation = WorkspaceInvitationMapper.CreateInvitation(workspaceId, request, finalRoleId.Value, finalRoleName, inviterUserId, tokenHash, membershipType);

            await _unitOfWork.WorkspaceInvitationRepository.AddAsync(newInvitation, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            string emailLanguage = "en";
            var existingUser = await _authIdentity.GetUserByEmailAsync(request.Email, ct);
            
            if (existingUser != null && !string.IsNullOrWhiteSpace(existingUser.PreferredLanguage))
            {
                emailLanguage = existingUser.PreferredLanguage;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(config.DefaultLanguage))
                {
                    emailLanguage = config.DefaultLanguage;
                }
            }

            var response = new InviteMemberResponse(newInvitation.ToDto(finalRoleName), rawToken, emailLanguage);
            return Result.Success(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while inviting member. WorkspaceId: {WorkspaceId}", workspaceId);
            return Result.Failure<InviteMemberResponse>("An unexpected error occurred.", ErrorCodes.InternalServerError);
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
                var roleName = await GetRoleNameByIdAsync(member.RoleId, ct);
                isOwnerOrAdmin = roleName is "Owner" or "Admin";
            }

            if (!isOwnerOrAdmin)
            {
                return Result.Failure<PagedResult<WorkspaceInvitationDto>>("Only Owner or Admin can view invitations.", ErrorCodes.Forbidden);
            }

            var (items, totalCount) = await _unitOfWork.WorkspaceInvitationRepository.GetInvitationsByWorkspaceAsync(workspaceId, query.Page, query.PageSize, ct);
            
            var dtos = new List<WorkspaceInvitationDto>();
            foreach (var invite in items)
            {
                var roleName = await GetRoleNameByIdAsync(invite.RoleId, ct);
                dtos.Add(invite.ToDto(roleName));
            }

            var pagedResult = new PagedResult<WorkspaceInvitationDto>(dtos, query.Page, query.PageSize, totalCount);

            return Result.Success(pagedResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while listing invitations. WorkspaceId: {WorkspaceId}", workspaceId);
            return Result.Failure<PagedResult<WorkspaceInvitationDto>>("An unexpected error occurred.", ErrorCodes.InternalServerError);
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
                var roleName = await GetRoleNameByIdAsync(member.RoleId, ct);
                isOwnerOrAdmin = roleName is "Owner" or "Admin";
            }

            if (!isOwnerOrAdmin)
            {
                return Result.Failure("Only Owner or Admin can revoke invitations.", ErrorCodes.Forbidden);
            }

            var invitation = await _unitOfWork.WorkspaceInvitationRepository.GetByIdAsync(invitationId, ct);
            if (invitation == null || invitation.WorkspaceId != workspaceId)
            {
                return Result.Failure("Invitation not found.", ErrorCodes.NotFound);
            }

            if (invitation.Status != InvitationStatus.PENDING.ToString())
            {
                return Result.Failure("Only pending invitations can be revoked.", ErrorCodes.InvalidState);
            }

            invitation.Status = InvitationStatus.REVOKED.ToString();
            _unitOfWork.WorkspaceInvitationRepository.Update(invitation);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while revoking invitation. InvitationId: {InvitationId}", invitationId);
            return Result.Failure("An unexpected error occurred.", ErrorCodes.InternalServerError);
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
                return Result.Failure<PreviewInvitationResponse>("Invalid or expired invitation token.", ErrorCodes.NotFound);
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

            var roleName = await GetRoleNameByIdAsync(invitation.RoleId, ct);
            var existingUser = await _authIdentity.GetUserByEmailAsync(invitation.Email, ct);
            var accountExists = existingUser != null;

            var response = new PreviewInvitationResponse(
                invitation.Workspace?.Name ?? "Unknown Workspace",
                roleName,
                maskedEmail,
                currentStatus,
                invitation.ExpiresAt,
                accountExists
            );

            return Result.Success(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while previewing invitation.");
            return Result.Failure<PreviewInvitationResponse>("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<VerifyInvitationInternalResponse>> VerifyInvitationTokenInternalAsync(string token, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return Result.Failure<VerifyInvitationInternalResponse>("Token is required.", ErrorCodes.ValidationError);
            }

            var tokenHash = TokenHasher.Hash(token);
            var invitation = await _unitOfWork.WorkspaceInvitationRepository.GetByTokenHashAsync(tokenHash, ct);

            if (invitation == null)
            {
                return Result.Failure<VerifyInvitationInternalResponse>("Invalid or expired invitation token.", ErrorCodes.NotFound);
            }

            if (invitation.Status != InvitationStatus.PENDING.ToString())
            {
                return Result.Failure<VerifyInvitationInternalResponse>($"Invitation is no longer valid. Status: {invitation.Status}", ErrorCodes.InvalidState);
            }

            if (invitation.ExpiresAt < DateTime.UtcNow)
            {
                invitation.Status = InvitationStatus.EXPIRED.ToString();
                _unitOfWork.WorkspaceInvitationRepository.Update(invitation);
                await _unitOfWork.SaveChangesAsync(ct);
                return Result.Failure<VerifyInvitationInternalResponse>("Invitation has expired.", ErrorCodes.InvalidState);
            }

            var roleName = await GetRoleNameByIdAsync(invitation.RoleId, ct);

            var response = new VerifyInvitationInternalResponse(
                invitation.Email,
                invitation.WorkspaceId,
                invitation.Workspace?.Name ?? "Unknown Workspace",
                invitation.RoleId,
                roleName,
                invitation.MembershipType
            );

            return Result.Success(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while verifying invitation token internally.");
            return Result.Failure<VerifyInvitationInternalResponse>("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> AcceptInvitationAsync(AcceptInvitationRequest request, Guid userId, string userEmail, CancellationToken ct = default)
    {
        try
        {
            var tokenHash = TokenHasher.Hash(request.Token);
            var invitation = await _unitOfWork.WorkspaceInvitationRepository.GetByTokenHashAsync(tokenHash, ct);

            if (invitation == null)
            {
                return Result.Failure("Invalid or expired invitation token.", ErrorCodes.NotFound);
            }

            if (invitation.Status != InvitationStatus.PENDING.ToString())
            {
                return Result.Failure($"Invitation is no longer valid. Status: {invitation.Status}", ErrorCodes.InvalidState);
            }

            if (invitation.ExpiresAt < DateTime.UtcNow)
            {
                invitation.Status = InvitationStatus.EXPIRED.ToString();
                _unitOfWork.WorkspaceInvitationRepository.Update(invitation);
                await _unitOfWork.SaveChangesAsync(ct);
                return Result.Failure("Invitation has expired.", ErrorCodes.InvalidState);
            }

            if (!string.Equals(invitation.Email, userEmail, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure("The email used for registration does not match the invitation email.", ErrorCodes.Forbidden);
            }

            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(invitation.WorkspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure("Workspace not found.", ErrorCodes.NotFound);
            }

            if (!EmailAddress.TryParse(userEmail, out var emailAddress) || emailAddress == null)
            {
                return Result.Failure("Invalid user email.", ErrorCodes.ValidationError);
            }
            var userDomain = emailAddress.Domain;
            var config = WorkspaceHelper.GetWorkspaceConfig(workspace);
            var isDomainVerified = config.VerifiedDomains != null && config.VerifiedDomains.Any(vd => string.Equals(vd.Trim(), userDomain, StringComparison.OrdinalIgnoreCase));
            
            if (isDomainVerified)
            {
                // Joining as Internal Member — enforce single-workspace constraint
                var isInternalElsewhere = await WorkspaceHelper.IsUserInternalMemberOfAnyEnterpriseWorkspaceAsync(_unitOfWork, userId, userEmail, ct);
                if (isInternalElsewhere)
                {
                    return Result.Failure("Your internal account already belongs to another Enterprise Workspace.", ErrorCodes.Forbidden);
                }
            }
            // Joining as External Partner — allowed even if internal member of another workspace

            var existingMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == invitation.WorkspaceId && m.UserId == userId, "", ct);
            
            if (existingMember != null)
            {
                return Result.Failure("You are already a member of this workspace.", ErrorCodes.InvalidState);
            }

            var newMember = WorkspaceMemberMapper.CreateInvitationMember(invitation.WorkspaceId, userId, invitation.RoleId, invitation.MembershipType);

            invitation.Status = InvitationStatus.ACCEPTED.ToString();
            invitation.AcceptedAt = DateTime.UtcNow;

            await _unitOfWork.WorkspaceMemberRepository.AddAsync(newMember, ct);
            _unitOfWork.WorkspaceInvitationRepository.Update(invitation);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while accepting invitation.");
            return Result.Failure("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }
}
