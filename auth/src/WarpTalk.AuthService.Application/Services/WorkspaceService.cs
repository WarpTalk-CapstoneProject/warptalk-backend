using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Helpers;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.Interfaces.Caching;
using WarpTalk.AuthService.Application.Mappers;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Enums;
using WarpTalk.AuthService.Domain.Extensions;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Domain.Settings;
using WarpTalk.AuthService.Domain.ValueObjects;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Application.Services;

public class WorkspaceService : IWorkspaceService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceCacheService _workspaceCache;
    private readonly ILogger<WorkspaceService> _logger;

    public WorkspaceService(IUnitOfWork unitOfWork, IWorkspaceCacheService workspaceCache, ILogger<WorkspaceService> logger)
    {
        _unitOfWork = unitOfWork;
        _workspaceCache = workspaceCache;
        _logger = logger;
    }

    public async Task<Result<WorkspaceDto>> CreateWorkspaceAsync(CreateWorkspaceRequest request, Guid userId, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Result.Failure<WorkspaceDto>("Workspace name is required.", ErrorCodes.ValidationError);
            }

            var user = await _unitOfWork.UserRepository.GetByIdAsync(userId, ct);
            if (user == null)
            {
                return Result.Failure<WorkspaceDto>("User not found.", ErrorCodes.UserNotFound);
            }

            if (!EmailAddress.TryParse(user.Email, out var emailAddress) || emailAddress == null)
            {
                return Result.Failure<WorkspaceDto>("Invalid user email.", ErrorCodes.ValidationError);
            }

            var isInternalElsewhere = await WorkspaceHelper.IsUserInternalMemberOfAnyEnterpriseWorkspaceAsync(_unitOfWork, userId, ct);
            if (isInternalElsewhere)
            {
                return Result.Failure<WorkspaceDto>("User is already an internal member of another Enterprise Workspace.", ErrorCodes.ValidationError);
            }

            var owningWorkspaceId = await WorkspaceHelper.GetWorkspaceIdVerifyingDomainAsync(_unitOfWork, emailAddress.Domain, ct);
            if (owningWorkspaceId.HasValue)
            {
                return Result.Failure<WorkspaceDto>("This email belongs to a corporate domain registered with another workspace.", ErrorCodes.ValidationError);
            }

            var baseSlug = SlugHelper.GenerateSlug(request.Name);
            var slug = await SlugHelper.ResolveSlugCollisionAsync(baseSlug, _unitOfWork.WorkspaceRepository, ct);

            var workspace = request.ToEntity(slug, userId);
            var config = new WorkspaceConfiguration
            {
                VerifiedDomains = new List<string> { emailAddress.Domain }
            };
            workspace.Settings = JsonSerializer.Serialize(config);

            var ownerRoleName = WorkspaceUserRole.Owner.ToRoleName();
            var ownerRole = await _unitOfWork.RoleRepository.FirstOrDefaultAsync(r => r.Name == ownerRoleName, "", ct);
            var workspaceMember = WorkspaceMapper.CreateOwnerMember(workspace.Id, userId, ownerRole);

            await _unitOfWork.WorkspaceRepository.AddAsync(workspace, ct);
            await _unitOfWork.WorkspaceMemberRepository.AddAsync(workspaceMember, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success(workspace.ToDto(WorkspaceUserRole.Owner));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while creating workspace. UserId: {UserId}, WorkspaceName: {WorkspaceName}", userId, request.Name);
            return Result.Failure<WorkspaceDto>("An unexpected error occurred while creating the workspace.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<PagedResult<WorkspaceDto>>> GetWorkspacesAsync(GetWorkspacesQuery query, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var (workspaces, totalCount) = await _unitOfWork.WorkspaceRepository.GetWorkspacesForUserAsync(userId, query.Page, query.PageSize, query.Search, ct);

            var workspaceDtos = new List<WorkspaceDto>();
            foreach (var ws in workspaces)
            {
                var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                    m => m.WorkspaceId == ws.Id && m.UserId == userId, 
                    "Role", 
                    ct
                );
                var defaultRoleName = WorkspaceUserRole.Member.ToRoleName();
                var roleName = member?.Role?.Name ?? defaultRoleName;

                workspaceDtos.Add(ws.ToDto(roleName));
            }

            var pagedResult = new PagedResult<WorkspaceDto>(workspaceDtos, query.Page, query.PageSize, totalCount);
            return Result.Success(pagedResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching workspaces for user. UserId: {UserId}", userId);
            return Result.Failure<PagedResult<WorkspaceDto>>("An unexpected error occurred while fetching workspaces.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<WorkspaceDto>> GetWorkspaceByIdAsync(Guid workspaceId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId,
                "Role",
                ct
            );

            if (member == null)
            {
                return Result.Failure<WorkspaceDto>("User is not a member of this workspace.", ErrorCodes.Forbidden);
            }

            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure<WorkspaceDto>("Workspace not found.", ErrorCodes.NotFound);
            }

            var defaultRoleName = WorkspaceUserRole.Member.ToRoleName();
            var roleName = member.Role?.Name ?? defaultRoleName;
            return Result.Success(workspace.ToDto(roleName));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching workspace by ID. WorkspaceId: {WorkspaceId}, UserId: {UserId}", workspaceId, userId);
            return Result.Failure<WorkspaceDto>("An unexpected error occurred while fetching the workspace.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<SelectWorkspaceResponse>> SelectWorkspaceAsync(Guid workspaceId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "Role", ct);

            if (member == null)
            {
                return Result.Failure<SelectWorkspaceResponse>("User is not a member of this workspace.", ErrorCodes.Forbidden);
            }

            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure<SelectWorkspaceResponse>("Workspace not found.", ErrorCodes.NotFound);
            }

            if (member.User == null)
            {
                member.User = await _unitOfWork.UserRepository.GetByIdAsync(userId, ct);
            }

            var role = member.Role?.Name ?? WorkspaceUserRole.Member.ToRoleName();
            var membershipType = WorkspaceHelper.DetermineMembershipType(member, workspace).ToString();

            await _workspaceCache.SetActiveWorkspaceDetailsAsync(userId, workspaceId, role, membershipType, ct);

            var response = new SelectWorkspaceResponse(workspaceId, workspace.Name, workspace.Slug);
            return Result.Success(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while selecting workspace. WorkspaceId: {WorkspaceId}, UserId: {UserId}", workspaceId, userId);
            return Result.Failure<SelectWorkspaceResponse>("An unexpected error occurred while selecting the workspace.", ErrorCodes.InternalServerError);
        }
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
                m => m.WorkspaceId == workspaceId && m.UserId == inviterUserId, "Role", ct);

            if (inviterMember == null)
            {
                return Result.Failure<InviteMemberResponse>("User is not a member of this workspace.", ErrorCodes.Forbidden);
            }

            var inviterRoleName = inviterMember.Role?.Name ?? WorkspaceUserRole.Member.ToRoleName();
            if (inviterRoleName != WorkspaceUserRole.Owner.ToRoleName() && inviterRoleName != WorkspaceUserRole.Admin.ToRoleName())
            {
                return Result.Failure<InviteMemberResponse>("Only Owner or Admin can invite members.", ErrorCodes.Forbidden);
            }

            if (inviterRoleName == WorkspaceUserRole.Admin.ToRoleName() && request.RoleName == WorkspaceUserRole.Owner.ToRoleName())
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
                finalRoleName = WorkspaceUserRole.Member.ToRoleName();
            }

            var finalRole = await _unitOfWork.RoleRepository.FirstOrDefaultAsync(r => r.Name == finalRoleName, "", ct);
            if (finalRole == null)
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

            var newInvitation = WorkspaceMapper.CreateInvitation(workspaceId, request, finalRole, inviterUserId, tokenHash);

            await _unitOfWork.WorkspaceInvitationRepository.AddAsync(newInvitation, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            string emailLanguage = "en";
            var existingUser = await _unitOfWork.UserRepository.FirstOrDefaultAsync(u => u.Email == request.Email, "", ct);
            
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

            var response = new InviteMemberResponse(newInvitation.ToDto(), rawToken, emailLanguage);
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
            var isOwnerOrAdmin = await _unitOfWork.WorkspaceMemberRepository.IsOwnerOrAdminAsync(workspaceId, userId, ct);
            if (!isOwnerOrAdmin)
            {
                return Result.Failure<PagedResult<WorkspaceInvitationDto>>("Only Owner or Admin can view invitations.", ErrorCodes.Forbidden);
            }

            var (items, totalCount) = await _unitOfWork.WorkspaceInvitationRepository.GetInvitationsByWorkspaceAsync(workspaceId, query.Page, query.PageSize, ct);
            
            var dtos = items.Select(i => i.ToDto()).ToList();
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
            var isOwnerOrAdmin = await _unitOfWork.WorkspaceMemberRepository.IsOwnerOrAdminAsync(workspaceId, userId, ct);
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
            var response = new PreviewInvitationResponse(
                invitation.Workspace?.Name ?? "Unknown Workspace",
                invitation.Role?.Name ?? "Member",
                maskedEmail,
                currentStatus,
                invitation.ExpiresAt
            );

            return Result.Success(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while previewing invitation.");
            return Result.Failure<PreviewInvitationResponse>("An unexpected error occurred.", ErrorCodes.InternalServerError);
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
                var isInternalElsewhere = await WorkspaceHelper.IsUserInternalMemberOfAnyEnterpriseWorkspaceAsync(_unitOfWork, userId, ct);
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

            var newMember = WorkspaceMapper.CreateInvitationMember(invitation.WorkspaceId, userId, invitation.RoleId);

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


    public async Task<Result> TransferOwnershipAsync(Guid workspaceId, Guid newOwnerId, Guid executingUserId, CancellationToken ct = default)
    {
        try
        {
            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure("Workspace not found.", ErrorCodes.NotFound);
            }

            if (workspace.OwnerId != executingUserId)
            {
                return Result.Failure("Only the workspace owner can transfer ownership.", ErrorCodes.Forbidden);
            }

            var newOwnerMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == newOwnerId && m.RemovedAt == null, "Role", ct);
            if (newOwnerMember == null)
            {
                return Result.Failure("New owner must be an active member of the workspace.", ErrorCodes.ValidationError);
            }

            var isExternal = await WorkspaceHelper.IsUserExternalMemberAsync(_unitOfWork, workspaceId, newOwnerId, ct);
            if (isExternal)
            {
                return Result.Failure("Cannot transfer ownership to an external member.", ErrorCodes.Forbidden);
            }

            var ownerRoleName = WorkspaceUserRole.Owner.ToRoleName();
            var adminRoleName = WorkspaceUserRole.Admin.ToRoleName();

            var ownerRole = await _unitOfWork.RoleRepository.FirstOrDefaultAsync(r => r.Name == ownerRoleName, "", ct);
            var adminRole = await _unitOfWork.RoleRepository.FirstOrDefaultAsync(r => r.Name == adminRoleName, "", ct);

            if (ownerRole == null || adminRole == null)
            {
                return Result.Failure("Required roles not found.", ErrorCodes.ValidationError);
            }

            var currentOwnerMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == executingUserId && m.RemovedAt == null, "", ct);

            workspace.OwnerId = newOwnerId;
            _unitOfWork.WorkspaceRepository.Update(workspace);

            if (currentOwnerMember != null)
            {
                currentOwnerMember.RoleId = adminRole.Id;
                currentOwnerMember.Role = adminRole;
                _unitOfWork.WorkspaceMemberRepository.Update(currentOwnerMember);
            }

            newOwnerMember.RoleId = ownerRole.Id;
            newOwnerMember.Role = ownerRole;
            _unitOfWork.WorkspaceMemberRepository.Update(newOwnerMember);

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while transferring ownership. WorkspaceId: {WorkspaceId}, ExecutingUserId: {ExecutingUserId}, NewOwnerId: {NewOwnerId}", workspaceId, executingUserId, newOwnerId);
            return Result.Failure("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<PagedResult<WorkspaceMemberDto>>> ListMembersAsync(Guid workspaceId, GetWorkspacesQuery query, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var isMember = await _unitOfWork.WorkspaceMemberRepository.AnyAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, ct);
            if (!isMember)
            {
                return Result.Failure<PagedResult<WorkspaceMemberDto>>("User is not a member of this workspace.", ErrorCodes.Forbidden);
            }

            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure<PagedResult<WorkspaceMemberDto>>("Workspace not found.", ErrorCodes.NotFound);
            }

            var isExternalCaller = await WorkspaceHelper.IsUserExternalMemberAsync(_unitOfWork, workspaceId, userId, ct);

            var (items, totalCount) = await _unitOfWork.WorkspaceMemberRepository.GetMembersByWorkspaceAsync(
                workspaceId, query.Page, query.PageSize, query.Search, isExternalCaller, ct);

            var dtos = items.Select(m => m.ToDto(workspace)).ToList();
            var pagedResult = new PagedResult<WorkspaceMemberDto>(dtos, query.Page, query.PageSize, totalCount);

            return Result.Success(pagedResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while listing workspace members. WorkspaceId: {WorkspaceId}", workspaceId);
            return Result.Failure<PagedResult<WorkspaceMemberDto>>("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> RemoveMemberAsync(Guid workspaceId, Guid memberUserId, Guid executingUserId, CancellationToken ct = default)
    {
        try
        {
            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure("Workspace not found.", ErrorCodes.NotFound);
            }

            var executingMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == executingUserId && m.RemovedAt == null, "Role", ct);
            if (executingMember == null)
            {
                return Result.Failure("User is not an active member of this workspace.", ErrorCodes.Forbidden);
            }

            var execRoleName = executingMember.Role?.Name ?? WorkspaceUserRole.Member.ToRoleName();

            if (memberUserId == executingUserId)
            {
                if (execRoleName == WorkspaceUserRole.Owner.ToRoleName())
                {
                    var activeOwnersCount = await _unitOfWork.WorkspaceMemberRepository.CountActiveOwnersAsync(workspaceId, ct);
                    if (activeOwnersCount <= 1)
                    {
                        return Result.Failure("Cannot leave the workspace as the last owner. Please transfer ownership first.", ErrorCodes.ValidationError);
                    }
                }

                executingMember.RemovedAt = DateTime.UtcNow;
                executingMember.RemovedBy = executingUserId;
                executingMember.Status = WorkspaceMemberStatus.Removed;

                _unitOfWork.WorkspaceMemberRepository.Update(executingMember);
                await _unitOfWork.SaveChangesAsync(ct);
                return Result.Success();
            }

            if (execRoleName != WorkspaceUserRole.Owner.ToRoleName() && execRoleName != WorkspaceUserRole.Admin.ToRoleName())
            {
                return Result.Failure("Only Owner or Admin can remove members.", ErrorCodes.Forbidden);
            }

            var targetMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == memberUserId && m.RemovedAt == null, "Role", ct);
            if (targetMember == null)
            {
                return Result.Failure("Target member not found or already removed.", ErrorCodes.NotFound);
            }

            var targetRoleName = targetMember.Role?.Name ?? WorkspaceUserRole.Member.ToRoleName();

            if (targetRoleName == WorkspaceUserRole.Owner.ToRoleName())
            {
                return Result.Failure("Cannot remove the Owner of the workspace.", ErrorCodes.Forbidden);
            }

            targetMember.RemovedAt = DateTime.UtcNow;
            targetMember.RemovedBy = executingUserId;
            targetMember.Status = WorkspaceMemberStatus.Removed;

            _unitOfWork.WorkspaceMemberRepository.Update(targetMember);
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while removing member. WorkspaceId: {WorkspaceId}, TargetUserId: {TargetUserId}", workspaceId, memberUserId);
            return Result.Failure("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> ChangeMemberRoleAsync(Guid workspaceId, Guid memberUserId, string roleName, Guid executingUserId, CancellationToken ct = default)
    {
        try
        {
            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure("Workspace not found.", ErrorCodes.NotFound);
            }

            if (roleName != WorkspaceUserRole.Admin.ToRoleName() && roleName != WorkspaceUserRole.Member.ToRoleName())
            {
                return Result.Failure("Role name must be Admin or Member.", ErrorCodes.ValidationError);
            }

            var executingMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == executingUserId && m.RemovedAt == null, "Role", ct);
            if (executingMember == null)
            {
                return Result.Failure("User is not an active member of this workspace.", ErrorCodes.Forbidden);
            }

            var execRoleName = executingMember.Role?.Name ?? WorkspaceUserRole.Member.ToRoleName();
            if (execRoleName != WorkspaceUserRole.Owner.ToRoleName() && execRoleName != WorkspaceUserRole.Admin.ToRoleName())
            {
                return Result.Failure("Only Owner or Admin can change member roles.", ErrorCodes.Forbidden);
            }

            var targetMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == memberUserId && m.RemovedAt == null, "Role", ct);
            if (targetMember == null)
            {
                return Result.Failure("Target member not found or already removed.", ErrorCodes.NotFound);
            }

            var targetRoleName = targetMember.Role?.Name ?? WorkspaceUserRole.Member.ToRoleName();

            if (memberUserId == executingUserId)
            {
                if (targetRoleName == WorkspaceUserRole.Owner.ToRoleName())
                {
                    var activeOwnersCount = await _unitOfWork.WorkspaceMemberRepository.CountActiveOwnersAsync(workspaceId, ct);
                    if (activeOwnersCount <= 1)
                    {
                        return Result.Failure("Cannot demote the last owner. Please transfer ownership first.", ErrorCodes.ValidationError);
                    }
                }
            }
            else
            {
                if (targetRoleName == WorkspaceUserRole.Owner.ToRoleName())
                {
                    return Result.Failure("Cannot change the Owner's role.", ErrorCodes.Forbidden);
                }
            }

            if (execRoleName == WorkspaceUserRole.Admin.ToRoleName())
            {
                if (targetRoleName == WorkspaceUserRole.Admin.ToRoleName() && memberUserId != executingUserId)
                {
                    return Result.Failure("Admin cannot change another Admin's role.", ErrorCodes.Forbidden);
                }

                if (roleName == WorkspaceUserRole.Admin.ToRoleName() && memberUserId != executingUserId)
                {
                    return Result.Failure("Admin cannot promote members to Admin role.", ErrorCodes.Forbidden);
                }
            }

            var newRole = await _unitOfWork.RoleRepository.FirstOrDefaultAsync(r => r.Name == roleName, "", ct);
            if (newRole == null)
            {
                return Result.Failure("Role not found.", ErrorCodes.ValidationError);
            }

            targetMember.RoleId = newRole.Id;
            targetMember.Role = newRole;

            _unitOfWork.WorkspaceMemberRepository.Update(targetMember);
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while changing member role. WorkspaceId: {WorkspaceId}, TargetUserId: {TargetUserId}", workspaceId, memberUserId);
            return Result.Failure("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<WorkspaceSettingsDto>> GetWorkspaceSettingsAsync(Guid workspaceId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);

            if (member == null)
            {
                return Result.Failure<WorkspaceSettingsDto>("User is not a member of this workspace.", ErrorCodes.Forbidden);
            }

            var settings = await _unitOfWork.WorkspaceRepository.GetSettingsAsync(workspaceId, ct);
            return Result.Success(settings.ToSettingsDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching workspace settings. WorkspaceId: {WorkspaceId}, UserId: {UserId}", workspaceId, userId);
            return Result.Failure<WorkspaceSettingsDto>("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> UpdateWorkspaceSettingsAsync(Guid workspaceId, WorkspaceSettingsDto settings, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure("Workspace not found.", ErrorCodes.NotFound);
            }

            var executingMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "Role", ct);
            if (executingMember == null)
            {
                return Result.Failure("User is not an active member of this workspace.", ErrorCodes.Forbidden);
            }

            var execRoleName = executingMember.Role?.Name ?? WorkspaceUserRole.Member.ToRoleName();
            if (execRoleName != WorkspaceUserRole.Owner.ToRoleName() && execRoleName != WorkspaceUserRole.Admin.ToRoleName())
            {
                return Result.Failure("Only Owner or Admin can update workspace settings.", ErrorCodes.Forbidden);
            }

            if (settings == null)
            {
                return Result.Failure("Invalid settings payload.", ErrorCodes.ValidationError);
            }

            var currentConfig = WorkspaceHelper.GetWorkspaceConfig(workspace);
            if (execRoleName == WorkspaceUserRole.Admin.ToRoleName())
            {
                if (currentConfig.AllowExternalCollaboration != settings.AllowExternalCollaboration)
                {
                    return Result.Failure("Only the workspace owner can modify AllowExternalCollaboration setting.", ErrorCodes.Forbidden);
                }
            }

            var newConfig = settings.ToConfiguration();
            var updated = await _unitOfWork.WorkspaceRepository.UpdateSettingsAsync(workspaceId, newConfig, userId, ct);
            if (!updated)
            {
                return Result.Failure("Workspace not found.", ErrorCodes.NotFound);
            }

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while updating workspace settings. WorkspaceId: {WorkspaceId}, UserId: {UserId}", workspaceId, userId);
            return Result.Failure("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }
}

