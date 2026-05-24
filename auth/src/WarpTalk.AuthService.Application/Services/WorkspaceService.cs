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

            var baseSlug = SlugHelper.GenerateSlug(request.Name);
            var slug = await SlugHelper.ResolveSlugCollisionAsync(baseSlug, _unitOfWork.WorkspaceRepository, ct);

            var workspace = request.ToEntity(slug, userId);
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
            var isMember = await _unitOfWork.WorkspaceMemberRepository.AnyAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId, ct);
            if (!isMember)
            {
                return Result.Failure<SelectWorkspaceResponse>("User is not a member of this workspace.", ErrorCodes.Forbidden);
            }

            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure<SelectWorkspaceResponse>("Workspace not found.", ErrorCodes.NotFound);
            }

            await _workspaceCache.SetActiveWorkspaceAsync(userId, workspaceId, ct);

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

            if (string.Equals(workspace.Type, WorkspaceType.Personal.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure<InviteMemberResponse>("Inviting members is not allowed in a Personal Workspace.", ErrorCodes.Forbidden);
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

            var targetRole = await _unitOfWork.RoleRepository.FirstOrDefaultAsync(r => r.Name == request.RoleName, "", ct);
            if (targetRole == null)
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

            var newInvitation = new WorkspaceInvitation
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                Email = request.Email,
                RoleId = targetRole.Id,
                Role = targetRole,
                InvitedBy = inviterUserId,
                TokenHash = tokenHash,
                Status = InvitationStatus.PENDING.ToString(),
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                CreatedAt = DateTime.UtcNow
            };

            await _unitOfWork.WorkspaceInvitationRepository.AddAsync(newInvitation, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            string emailLanguage = "en";
            var existingUser = await _unitOfWork.UserRepository.FirstOrDefaultAsync(u => u.Email == request.Email, "", ct);
            
            if (existingUser != null && !string.IsNullOrWhiteSpace(existingUser.PreferredLanguage))
            {
                emailLanguage = existingUser.PreferredLanguage;
            }
            else if (!string.IsNullOrWhiteSpace(workspace.Settings))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(workspace.Settings);
                    if (doc.RootElement.TryGetProperty("DefaultLanguage", out var langElement) || doc.RootElement.TryGetProperty("defaultLanguage", out langElement))
                    {
                        var lang = langElement.GetString();
                        if (!string.IsNullOrWhiteSpace(lang)) emailLanguage = lang;
                    }
                }
                catch { /* Ignore parse errors and fallback to en */ }
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

            string maskedEmail = MaskEmail(invitation.Email);
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

            var existingMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == invitation.WorkspaceId && m.UserId == userId, "", ct);
            
            if (existingMember != null)
            {
                return Result.Failure("You are already a member of this workspace.", ErrorCodes.InvalidState);
            }

            var newMember = new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = invitation.WorkspaceId,
                UserId = userId,
                RoleId = invitation.RoleId,
                Status = "Active",
                JoinedAt = DateTime.UtcNow
            };

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

    private string MaskEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) return email;
        var parts = email.Split('@');
        var name = parts[0];
        var domain = parts[1];
        if (name.Length <= 2) return $"{name[0]}***@{domain}";
        return $"{name.Substring(0, 2)}***@{domain}";
    }

    public async Task<Result> TransferOwnershipAsync(Guid workspaceId, Guid newOwnerId, CancellationToken ct = default)
    {
        try
        {
            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure("Workspace not found.", ErrorCodes.NotFound);
            }

            if (string.Equals(workspace.Type, WorkspaceType.Personal.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure("Transferring ownership is not allowed in a Personal Workspace.", ErrorCodes.Forbidden);
            }

            // Actual transfer logic would go here
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while transferring ownership. WorkspaceId: {WorkspaceId}", workspaceId);
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

            var (items, totalCount) = await _unitOfWork.WorkspaceMemberRepository.GetMembersByWorkspaceAsync(
                workspaceId, query.Page, query.PageSize, query.Search, ct);

            var dtos = items.Select(m => m.ToDto()).ToList();
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

            if (string.Equals(workspace.Type, WorkspaceType.Personal.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure("Managing members is not allowed in a Personal Workspace.", ErrorCodes.Forbidden);
            }

            var executingMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == executingUserId && m.RemovedAt == null, "Role", ct);
            if (executingMember == null)
            {
                return Result.Failure("User is not an active member of this workspace.", ErrorCodes.Forbidden);
            }

            var execRoleName = executingMember.Role?.Name ?? WorkspaceUserRole.Member.ToRoleName();

            // Self-removal / Leave workspace
            if (memberUserId == executingUserId)
            {
                if (execRoleName == WorkspaceUserRole.Owner.ToRoleName())
                {
                    // Check if they are the last active Owner
                    var activeOwnersCount = await _unitOfWork.WorkspaceMemberRepository.CountActiveOwnersAsync(workspaceId, ct);
                    if (activeOwnersCount <= 1)
                    {
                        return Result.Failure("Cannot leave the workspace as the last owner. Please transfer ownership first.", ErrorCodes.ValidationError);
                    }
                }

                executingMember.RemovedAt = DateTime.UtcNow;
                executingMember.RemovedBy = executingUserId;
                executingMember.Status = "Removed";

                _unitOfWork.WorkspaceMemberRepository.Update(executingMember);
                await _unitOfWork.SaveChangesAsync(ct);
                return Result.Success();
            }

            // Removing someone else
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
            targetMember.Status = "Removed";

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

            if (string.Equals(workspace.Type, WorkspaceType.Personal.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure("Managing roles is not allowed in a Personal Workspace.", ErrorCodes.Forbidden);
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

            // Self demotion
            if (memberUserId == executingUserId)
            {
                if (targetRoleName == WorkspaceUserRole.Owner.ToRoleName())
                {
                    // Check if last owner
                    var activeOwnersCount = await _unitOfWork.WorkspaceMemberRepository.CountActiveOwnersAsync(workspaceId, ct);
                    if (activeOwnersCount <= 1)
                    {
                        return Result.Failure("Cannot demote the last owner. Please transfer ownership first.", ErrorCodes.ValidationError);
                    }
                }
            }
            else
            {
                // Changing someone else's role
                if (targetRoleName == WorkspaceUserRole.Owner.ToRoleName())
                {
                    return Result.Failure("Cannot change the Owner's role.", ErrorCodes.Forbidden);
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

    public async Task<Result<WorkspaceConfiguration>> GetWorkspaceSettingsAsync(Guid workspaceId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);

            if (member == null)
            {
                return Result.Failure<WorkspaceConfiguration>("User is not a member of this workspace.", ErrorCodes.Forbidden);
            }

            var settings = await _unitOfWork.WorkspaceRepository.GetSettingsAsync(workspaceId, ct);
            return Result.Success(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching workspace settings. WorkspaceId: {WorkspaceId}, UserId: {UserId}", workspaceId, userId);
            return Result.Failure<WorkspaceConfiguration>("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> UpdateWorkspaceSettingsAsync(Guid workspaceId, WorkspaceConfiguration settings, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var isOwnerOrAdmin = await _unitOfWork.WorkspaceMemberRepository.IsOwnerOrAdminAsync(workspaceId, userId, ct);
            if (!isOwnerOrAdmin)
            {
                return Result.Failure("Only Owner or Admin can update workspace settings.", ErrorCodes.Forbidden);
            }

            if (settings == null)
            {
                return Result.Failure("Invalid settings payload.", ErrorCodes.ValidationError);
            }

            var updated = await _unitOfWork.WorkspaceRepository.UpdateSettingsAsync(workspaceId, settings, userId, ct);
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

