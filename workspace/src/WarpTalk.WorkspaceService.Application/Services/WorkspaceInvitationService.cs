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

namespace WarpTalk.WorkspaceService.Application.Services;

public class WorkspaceInvitationService : IWorkspaceInvitationService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<WorkspaceInvitationService> _logger;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly ITranslationRoomClient _translationRoomClient;

    public WorkspaceInvitationService(
        IUnitOfWork unitOfWork,
        ILogger<WorkspaceInvitationService> logger,
        IAuthIdentityClient authIdentity,
        ITranslationRoomClient translationRoomClient)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _authIdentity = authIdentity;
        _translationRoomClient = translationRoomClient;
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
                m => m.WorkspaceId == workspaceId && m.UserId == inviterUserId, "", ct);

            if (inviterMember == null)
            {
                return Result.Failure<InviteMemberResponse>(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var inviterRoleName = await _authIdentity.GetRoleNameByIdAsync(inviterMember.RoleId, ct);
            if (!inviterRoleName.IsOwnerOrAdmin())
            {
                return Result.Failure<InviteMemberResponse>(WorkspaceConstants.Errors.OnlyOwnerAdminCanInvite, ErrorCodes.Forbidden);
            }

            if (inviterRoleName.IsAdmin() && request.RoleName.IsOwner())
            {
                return Result.Failure<InviteMemberResponse>(WorkspaceConstants.Errors.AdminCannotAssignOwner, ErrorCodes.Forbidden);
            }

            if (!EmailAddress.TryParse(request.Email, out var emailAddress) || emailAddress == null)
            {
                return Result.Failure<InviteMemberResponse>(WorkspaceConstants.Errors.InvalidEmailFormat, ErrorCodes.ValidationError);
            }
            var domain = emailAddress.Domain;

            if (!Enum.TryParse<MembershipType>(request.MembershipType, true, out var membershipTypeEnum))
            {
                return Result.Failure<InviteMemberResponse>(WorkspaceConstants.Errors.InvalidMembershipType, ErrorCodes.ValidationError);
            }

            var config = WorkspaceHelper.GetWorkspaceConfig(workspace);
            var isDomainVerified = await _unitOfWork.Repository<WorkspaceVerifiedDomain>().AnyAsync(
                vd => vd.WorkspaceId == workspaceId 
                      && vd.Domain.ToLower() == domain.ToLower() 
                      && vd.Status == "verified" 
                      && vd.VerifiedAt != null 
                      && vd.RevokedAt == null, 
                ct);

            if (membershipTypeEnum == MembershipType.Internal)
            {
                if (config.RequireVerifiedDomainForInternal && !isDomainVerified)
                {
                    return Result.Failure<InviteMemberResponse>(WorkspaceConstants.Errors.CannotInviteInternalWithoutVerifiedDomain, ErrorCodes.ValidationError);
                }
            }
            else if (membershipTypeEnum == MembershipType.External)
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

            var finalRoleName = request.RoleName;
            var finalRoleId = await _authIdentity.GetRoleIdByNameAsync(finalRoleName, ct);
            if (!finalRoleId.HasValue)
            {
                return Result.Failure<InviteMemberResponse>(WorkspaceConstants.Errors.InvalidRoleSpecified, ErrorCodes.ValidationError);
            }

            var pendingInvite = await _unitOfWork.WorkspaceInvitationRepository.GetPendingByEmailAsync(workspaceId, request.Email, ct);
            if (pendingInvite != null)
            {
                pendingInvite.Status = InvitationStatus.REPLACED.ToString();
                _unitOfWork.WorkspaceInvitationRepository.Update(pendingInvite);
            }

            var rawToken = Guid.NewGuid().ToString("N");
            var tokenHash = TokenHasher.Hash(rawToken);

            var membershipType = membershipTypeEnum.ToString();
            var newInvitation = WorkspaceInvitationMapper.CreateInvitation(workspaceId, request, finalRoleId.Value, finalRoleName, inviterUserId, tokenHash, membershipType);

            await _unitOfWork.WorkspaceInvitationRepository.AddAsync(newInvitation, ct);
            await _unitOfWork.SaveChangesAsync(ct);
            string emailLanguage = WorkspaceConstants.DefaultWorkspaceLanguage;
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
            return Result.Failure<InviteMemberResponse>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
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

            var (items, totalCount) = await _unitOfWork.WorkspaceInvitationRepository.GetInvitationsByWorkspaceAsync(workspaceId, query.Page, query.PageSize, ct);
            
            var dtos = new List<WorkspaceInvitationDto>();
            foreach (var invite in items)
            {
                var roleName = await _authIdentity.GetRoleNameByIdAsync(invite.RoleId, ct);
                dtos.Add(invite.ToDto(roleName));
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

            if (await invitation.CheckAndHandleExpirationAsync(_unitOfWork, ct))
            {
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

    public async Task<Result> AcceptInvitationAsync(AcceptInvitationRequest request, Guid userId, string userEmail, CancellationToken ct = default)
    {
        try
        {
            var tokenHash = TokenHasher.Hash(request.Token);
            var invitation = await _unitOfWork.WorkspaceInvitationRepository.GetByTokenHashAsync(tokenHash, ct);

            if (invitation == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.InvalidOrExpiredToken, ErrorCodes.NotFound);
            }

            if (invitation.Status != InvitationStatus.PENDING.ToString())
            {
                return Result.Failure(string.Format(WorkspaceConstants.Errors.InvitationNoLongerValidFormat, invitation.Status), ErrorCodes.InvalidState);
            }

            if (await invitation.CheckAndHandleExpirationAsync(_unitOfWork, ct))
            {
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
            var userDomain = emailAddress.Domain;
            var config = WorkspaceHelper.GetWorkspaceConfig(workspace);
            var isDomainVerified = await _unitOfWork.Repository<WorkspaceVerifiedDomain>().AnyAsync(
                vd => vd.WorkspaceId == invitation.WorkspaceId 
                      && vd.Domain.ToLower() == userDomain.ToLower() 
                      && vd.Status == "verified" 
                      && vd.VerifiedAt != null 
                      && vd.RevokedAt == null, 
                ct);
            
            if (string.Equals(invitation.MembershipType, MembershipType.Internal.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                if (config.RequireVerifiedDomainForInternal && !isDomainVerified)
                {
                    return Result.Failure(WorkspaceConstants.Errors.CannotInviteInternalWithoutVerifiedDomain, ErrorCodes.ValidationError);
                }

                // Joining as Internal Member — enforce single-workspace constraint if target workspace requires domain verification
                if (config.RequireVerifiedDomainForInternal)
                {
                    var isInternalElsewhere = await WorkspaceHelper.IsUserInternalMemberOfAnyEnterpriseWorkspaceAsync(_unitOfWork, userId, userEmail, ct);
                    if (isInternalElsewhere)
                    {
                        return Result.Failure(WorkspaceConstants.Errors.UserAlreadyInternalElsewhere, ErrorCodes.Forbidden);
                    }
                }
            }
            // Joining as External Partner — allowed even if internal member of another workspace

            var existingMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == invitation.WorkspaceId && m.UserId == userId, "", ct);
            
            if (existingMember != null)
            {
                return Result.Failure(WorkspaceConstants.Errors.AlreadyMember, ErrorCodes.InvalidState);
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
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<WorkspaceInvitationDto>> CreateJoinRequestAsync(CreateJoinRequestCommand command, Guid userId, string userEmail, CancellationToken ct = default)
    {
        try
        {
            Guid workspaceId = Guid.Empty;

            // 1. Resolve WorkspaceId
            if (!string.IsNullOrWhiteSpace(command.RoomCode))
            {
                var room = await _translationRoomClient.GetTranslationRoomByCodeAsync(command.RoomCode, ct);
                if (room == null)
                {
                    return Result.Failure<WorkspaceInvitationDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
                }
                workspaceId = room.WorkspaceId;
            }
            else if (!string.IsNullOrWhiteSpace(command.WorkspaceSlug))
            {
                var workspaceBySlug = await _unitOfWork.WorkspaceRepository.FirstOrDefaultAsync(
                    w => w.Slug.ToLower() == command.WorkspaceSlug.ToLower(), "", ct);
                if (workspaceBySlug == null)
                {
                    return Result.Failure<WorkspaceInvitationDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
                }
                workspaceId = workspaceBySlug.Id;
            }
            else
            {
                return Result.Failure<WorkspaceInvitationDto>("Either RoomCode or WorkspaceSlug must be specified.", ErrorCodes.ValidationError);
            }

            // 2. Fetch and check Workspace state
            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null || !workspace.IsActive || workspace.DeletedAt != null)
            {
                return Result.Failure<WorkspaceInvitationDto>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            // 3. Check if already a member
            var isMember = await _unitOfWork.WorkspaceMemberRepository.AnyAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, ct);
            if (isMember)
            {
                return Result.Failure<WorkspaceInvitationDto>(WorkspaceConstants.Errors.AlreadyMember, ErrorCodes.InvalidState);
            }

            // 4. Verify domain and determine membership type
            if (!EmailAddress.TryParse(userEmail, out var emailAddress) || emailAddress == null)
            {
                return Result.Failure<WorkspaceInvitationDto>(WorkspaceConstants.Errors.InvalidEmailFormat, ErrorCodes.ValidationError);
            }
            var domain = emailAddress.Domain;

            var config = WorkspaceHelper.GetWorkspaceConfig(workspace);
            var isDomainVerified = await _unitOfWork.Repository<WorkspaceVerifiedDomain>().AnyAsync(
                vd => vd.WorkspaceId == workspaceId 
                      && vd.Domain.ToLower() == domain.ToLower() 
                      && vd.Status == "verified" 
                      && vd.VerifiedAt != null 
                      && vd.RevokedAt == null, 
                ct);

            string membershipType = MembershipType.Internal.ToString();

            if (isDomainVerified)
            {
                membershipType = MembershipType.Internal.ToString();
            }
            else
            {
                if (config.AllowExternalCollaboration)
                {
                    membershipType = MembershipType.External.ToString();
                }
                else
                {
                    return Result.Failure<WorkspaceInvitationDto>(WorkspaceConstants.Errors.CannotInviteInternalWithoutVerifiedDomain, ErrorCodes.Forbidden);
                }
            }

            // 5. Check duplicate requested / pending invitation
            var existingPendingOrRequest = await _unitOfWork.WorkspaceInvitationRepository.FirstOrDefaultAsync(
                i => i.WorkspaceId == workspaceId 
                     && i.Email.ToLower() == userEmail.ToLower() 
                     && (i.Status == InvitationStatus.PENDING.ToString() || i.Status == InvitationStatus.REQUESTED.ToString())
                     && i.ExpiresAt > DateTime.UtcNow,
                "", ct);

            if (existingPendingOrRequest != null)
            {
                return Result.Failure<WorkspaceInvitationDto>("There is already an active join request or invitation pending for this workspace.", ErrorCodes.InvalidState);
            }

            // 6. Get standard Member role
            var memberRoleId = await _authIdentity.GetRoleIdByNameAsync("Member", ct);
            if (!memberRoleId.HasValue)
            {
                return Result.Failure<WorkspaceInvitationDto>("Default Member role could not be resolved.", ErrorCodes.InternalServerError);
            }

            // 7. Create WorkspaceInvitation with REQUESTED status
            var now = DateTime.UtcNow;
            var newRequest = new WorkspaceInvitation
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                Email = userEmail,
                RoleId = memberRoleId.Value,
                MembershipType = membershipType,
                InvitedBy = userId,
                TokenHash = TokenHasher.Hash($"REQUEST-{userId}-{Guid.NewGuid()}"),
                Status = InvitationStatus.REQUESTED.ToString(),
                ExpiresAt = now.AddDays(7),
                CreatedAt = now
            };

            await _unitOfWork.WorkspaceInvitationRepository.AddAsync(newRequest, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success(newRequest.ToDto("Member"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while creating join request for user {UserId}", userId);
            return Result.Failure<WorkspaceInvitationDto>(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> ApproveJoinRequestAsync(Guid workspaceId, Guid invitationId, Guid adminUserId, CancellationToken ct = default)
    {
        try
        {
            // 1. Verify Admin is owner or admin of the workspace
            var adminMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == adminUserId && m.RemovedAt == null, "", ct);

            if (adminMember == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var adminRoleName = await _authIdentity.GetRoleNameByIdAsync(adminMember.RoleId, ct);
            if (!adminRoleName.IsOwnerOrAdmin())
            {
                return Result.Failure(WorkspaceConstants.Errors.OnlyOwnerAdminCanInvite, ErrorCodes.Forbidden);
            }

            // 2. Retrieve invitation
            var invitation = await _unitOfWork.WorkspaceInvitationRepository.GetByIdAsync(invitationId, ct);
            if (invitation == null || invitation.WorkspaceId != workspaceId)
            {
                return Result.Failure(WorkspaceConstants.Errors.InvitationNotFound, ErrorCodes.NotFound);
            }

            if (invitation.Status != InvitationStatus.REQUESTED.ToString())
            {
                return Result.Failure("Only join requests can be approved.", ErrorCodes.InvalidState);
            }

            if (invitation.ExpiresAt < DateTime.UtcNow)
            {
                invitation.Status = InvitationStatus.EXPIRED.ToString();
                _unitOfWork.WorkspaceInvitationRepository.Update(invitation);
                await _unitOfWork.SaveChangesAsync(ct);
                return Result.Failure("This request has expired.", ErrorCodes.InvalidState);
            }

            // 3. Check if user is already a member
            var existingMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == invitation.InvitedBy && m.RemovedAt == null, "", ct);

            if (existingMember != null)
            {
                invitation.Status = InvitationStatus.ACCEPTED.ToString();
                invitation.AcceptedAt = DateTime.UtcNow;
                _unitOfWork.WorkspaceInvitationRepository.Update(invitation);
                await _unitOfWork.SaveChangesAsync(ct);
                return Result.Failure(WorkspaceConstants.Errors.AlreadyMember, ErrorCodes.InvalidState);
            }

            // 4. Create WorkspaceMember
            var newMember = WorkspaceMemberMapper.CreateInvitationMember(
                workspaceId, 
                invitation.InvitedBy, 
                invitation.RoleId, 
                invitation.MembershipType);

            invitation.Status = InvitationStatus.ACCEPTED.ToString();
            invitation.AcceptedAt = DateTime.UtcNow;

            await _unitOfWork.WorkspaceMemberRepository.AddAsync(newMember, ct);
            _unitOfWork.WorkspaceInvitationRepository.Update(invitation);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while approving join request {InvitationId}", invitationId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> RejectJoinRequestAsync(Guid workspaceId, Guid invitationId, Guid adminUserId, CancellationToken ct = default)
    {
        try
        {
            // 1. Verify Admin is owner or admin of the workspace
            var adminMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == adminUserId && m.RemovedAt == null, "", ct);

            if (adminMember == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var adminRoleName = await _authIdentity.GetRoleNameByIdAsync(adminMember.RoleId, ct);
            if (!adminRoleName.IsOwnerOrAdmin())
            {
                return Result.Failure(WorkspaceConstants.Errors.OnlyOwnerAdminCanRevoke, ErrorCodes.Forbidden);
            }

            // 2. Retrieve invitation
            var invitation = await _unitOfWork.WorkspaceInvitationRepository.GetByIdAsync(invitationId, ct);
            if (invitation == null || invitation.WorkspaceId != workspaceId)
            {
                return Result.Failure(WorkspaceConstants.Errors.InvitationNotFound, ErrorCodes.NotFound);
            }

            if (invitation.Status != InvitationStatus.REQUESTED.ToString())
            {
                return Result.Failure("Only join requests can be rejected.", ErrorCodes.InvalidState);
            }

            invitation.Status = InvitationStatus.REVOKED.ToString();
            _unitOfWork.WorkspaceInvitationRepository.Update(invitation);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while rejecting join request {InvitationId}", invitationId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }
}

