using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceMember;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Mappers.WorkspaceMember;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.WorkspaceService.Application.Services;

public class WorkspaceMemberService : IWorkspaceMemberService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<WorkspaceMemberService> _logger;
    private readonly IAuthIdentityClient _authIdentity;

    public WorkspaceMemberService(
        IUnitOfWork unitOfWork,
        ILogger<WorkspaceMemberService> logger,
        IAuthIdentityClient authIdentity)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _authIdentity = authIdentity;
    }

    private async Task<string> GetRoleNameByIdAsync(Guid roleId, CancellationToken ct)
    {
        var role = await _authIdentity.GetRoleByIdAsync(roleId, ct);
        return role?.Name ?? "Member";
    }

    private async Task<Guid?> GetRoleIdByNameAsync(string roleName, CancellationToken ct)
    {
        var role = await _authIdentity.GetRoleByNameAsync(roleName, ct);
        return role?.Id;
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
                m => m.WorkspaceId == workspaceId && m.UserId == newOwnerId && m.RemovedAt == null, "", ct);
            if (newOwnerMember == null)
            {
                return Result.Failure("New owner must be an active member of the workspace.", ErrorCodes.ValidationError);
            }

            var isExternal = await WorkspaceHelper.IsUserExternalMemberAsync(_unitOfWork, workspaceId, newOwnerId, ct);
            if (isExternal)
            {
                return Result.Failure("Cannot transfer ownership to an external member.", ErrorCodes.Forbidden);
            }

            var ownerRoleName = WorkspaceMemberRole.Owner.ToRoleName();
            var adminRoleName = WorkspaceMemberRole.Admin.ToRoleName();

            var ownerRoleId = await GetRoleIdByNameAsync(ownerRoleName, ct);
            var adminRoleId = await GetRoleIdByNameAsync(adminRoleName, ct);

            if (ownerRoleId == null || adminRoleId == null)
            {
                return Result.Failure("Required roles not found.", ErrorCodes.ValidationError);
            }

            var currentOwnerMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == executingUserId && m.RemovedAt == null, "", ct);

            workspace.OwnerId = newOwnerId;
            _unitOfWork.WorkspaceRepository.Update(workspace);

            if (currentOwnerMember != null)
            {
                currentOwnerMember.RoleId = adminRoleId.Value;
                _unitOfWork.WorkspaceMemberRepository.Update(currentOwnerMember);
            }

            newOwnerMember.RoleId = ownerRoleId.Value;
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

            var members = await _unitOfWork.WorkspaceMemberRepository.GetActiveMembersByWorkspaceAsync(workspaceId, ct);

            var filteredDtos = new List<WorkspaceMemberDto>();
            var roleCache = new Dictionary<Guid, string>();

            foreach (var m in members)
            {
                var user = await _authIdentity.GetUserByIdAsync(m.UserId, ct);
                var fullName = user?.FullName ?? "Unknown";
                var email = user?.Email ?? "Unknown";
                var avatarUrl = user?.AvatarUrl;

                if (!roleCache.TryGetValue(m.RoleId, out var roleName))
                {
                    var role = await _authIdentity.GetRoleByIdAsync(m.RoleId, ct);
                    roleName = role?.Name ?? "Member";
                    roleCache[m.RoleId] = roleName;
                }

                if (isExternalCaller && roleName != "Owner" && roleName != "Admin")
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(query.Search))
                {
                    var searchLower = query.Search.ToLower();
                    if (!fullName.ToLower().Contains(searchLower) && !email.ToLower().Contains(searchLower))
                    {
                        continue;
                    }
                }

                var dto = m.ToDto(fullName, email, avatarUrl, roleName);
                filteredDtos.Add(dto);
            }

            var totalCount = filteredDtos.Count;
            var pagedItems = filteredDtos.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList();

            var pagedResult = new PagedResult<WorkspaceMemberDto>(pagedItems, query.Page, query.PageSize, totalCount);

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
                m => m.WorkspaceId == workspaceId && m.UserId == executingUserId && m.RemovedAt == null, "", ct);
            if (executingMember == null)
            {
                return Result.Failure("User is not an active member of this workspace.", ErrorCodes.Forbidden);
            }

            var execRoleName = await GetRoleNameByIdAsync(executingMember.RoleId, ct);

            if (memberUserId == executingUserId)
            {
                if (execRoleName == WorkspaceMemberRole.Owner.ToRoleName())
                {
                    var ownerRoleId = await GetRoleIdByNameAsync(WorkspaceMemberRole.Owner.ToRoleName(), ct);
                    if (ownerRoleId == null)
                    {
                        return Result.Failure("Required owner role not found.", ErrorCodes.ValidationError);
                    }

                    var activeOwnersCount = await _unitOfWork.WorkspaceMemberRepository.CountActiveOwnersAsync(workspaceId, ownerRoleId.Value, ct);
                    if (activeOwnersCount <= 1)
                    {
                        return Result.Failure("Cannot leave the workspace as the last owner. Please transfer ownership first.", ErrorCodes.ValidationError);
                    }
                }

                executingMember.RemovedAt = DateTime.UtcNow;
                executingMember.RemovedBy = executingUserId;
                executingMember.Status = WorkspaceMemberStatus.Removed.ToString();

                _unitOfWork.WorkspaceMemberRepository.Update(executingMember);
                await _unitOfWork.SaveChangesAsync(ct);
                return Result.Success();
            }

            if (execRoleName != WorkspaceMemberRole.Owner.ToRoleName() && execRoleName != WorkspaceMemberRole.Admin.ToRoleName())
            {
                return Result.Failure("Only Owner or Admin can remove members.", ErrorCodes.Forbidden);
            }

            var targetMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == memberUserId && m.RemovedAt == null, "", ct);
            if (targetMember == null)
            {
                return Result.Failure("Target member not found or already removed.", ErrorCodes.NotFound);
            }

            var targetRoleName = await GetRoleNameByIdAsync(targetMember.RoleId, ct);

            if (targetRoleName == WorkspaceMemberRole.Owner.ToRoleName())
            {
                return Result.Failure("Cannot remove the Owner of the workspace.", ErrorCodes.Forbidden);
            }

            targetMember.RemovedAt = DateTime.UtcNow;
            targetMember.RemovedBy = executingUserId;
            targetMember.Status = WorkspaceMemberStatus.Removed.ToString();

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

            if (roleName != WorkspaceMemberRole.Admin.ToRoleName() && roleName != WorkspaceMemberRole.Member.ToRoleName())
            {
                return Result.Failure("Role name must be Admin or Member.", ErrorCodes.ValidationError);
            }

            var executingMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == executingUserId && m.RemovedAt == null, "", ct);
            if (executingMember == null)
            {
                return Result.Failure("User is not an active member of this workspace.", ErrorCodes.Forbidden);
            }

            var execRoleName = await GetRoleNameByIdAsync(executingMember.RoleId, ct);
            if (execRoleName != WorkspaceMemberRole.Owner.ToRoleName() && execRoleName != WorkspaceMemberRole.Admin.ToRoleName())
            {
                return Result.Failure("Only Owner or Admin can change member roles.", ErrorCodes.Forbidden);
            }

            var targetMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == memberUserId && m.RemovedAt == null, "", ct);
            if (targetMember == null)
            {
                return Result.Failure("Target member not found or already removed.", ErrorCodes.NotFound);
            }

            var targetRoleName = await GetRoleNameByIdAsync(targetMember.RoleId, ct);

            if (memberUserId == executingUserId)
            {
                if (targetRoleName == WorkspaceMemberRole.Owner.ToRoleName())
                {
                    var ownerRoleId = await GetRoleIdByNameAsync(WorkspaceMemberRole.Owner.ToRoleName(), ct);
                    if (ownerRoleId == null)
                    {
                        return Result.Failure("Required owner role not found.", ErrorCodes.ValidationError);
                    }

                    var activeOwnersCount = await _unitOfWork.WorkspaceMemberRepository.CountActiveOwnersAsync(workspaceId, ownerRoleId.Value, ct);
                    if (activeOwnersCount <= 1)
                    {
                        return Result.Failure("Cannot demote the last owner. Please transfer ownership first.", ErrorCodes.ValidationError);
                    }
                }
            }
            else
            {
                if (targetRoleName == WorkspaceMemberRole.Owner.ToRoleName())
                {
                    return Result.Failure("Cannot change the Owner's role.", ErrorCodes.Forbidden);
                }
            }

            if (execRoleName == WorkspaceMemberRole.Admin.ToRoleName())
            {
                if (targetRoleName == WorkspaceMemberRole.Admin.ToRoleName() && memberUserId != executingUserId)
                {
                    return Result.Failure("Admin cannot change another Admin's role.", ErrorCodes.Forbidden);
                }

                if (roleName == WorkspaceMemberRole.Admin.ToRoleName() && memberUserId != executingUserId)
                {
                    return Result.Failure("Admin cannot promote members to Admin role.", ErrorCodes.Forbidden);
                }
            }

            var newRoleId = await GetRoleIdByNameAsync(roleName, ct);
            if (newRoleId == null)
            {
                return Result.Failure("Role not found.", ErrorCodes.ValidationError);
            }

            targetMember.RoleId = newRoleId.Value;

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
}
