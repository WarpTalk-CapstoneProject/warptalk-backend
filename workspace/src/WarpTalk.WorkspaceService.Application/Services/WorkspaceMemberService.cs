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
using WarpTalk.WorkspaceService.Application.Mappers;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.Shared;
using MassTransit;
using WarpTalk.Shared.Events;

namespace WarpTalk.WorkspaceService.Application.Services;

public class WorkspaceMemberService : IWorkspaceMemberService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<WorkspaceMemberService> _logger;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly IWorkspaceEventPublisher _eventPublisher;

    public WorkspaceMemberService(
        IUnitOfWork unitOfWork,
        ILogger<WorkspaceMemberService> logger,
        IAuthIdentityClient authIdentity,
        IWorkspaceEventPublisher eventPublisher)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _authIdentity = authIdentity;
        _eventPublisher = eventPublisher;
    }


    public async Task<Result> TransferOwnershipAsync(Guid workspaceId, Guid newOwnerId, Guid executingUserId, CancellationToken ct = default)
    {
        try
        {
            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            if (workspace.OwnerId != executingUserId)
            {
                return Result.Failure(WorkspaceConstants.Errors.OnlyOwnerCanTransferOwnership, ErrorCodes.Forbidden);
            }

            var newOwnerMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == newOwnerId && m.RemovedAt == null, "", ct);
            if (newOwnerMember == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.NewOwnerMustBeActiveMember, ErrorCodes.ValidationError);
            }

            var isExternal = await WorkspaceHelper.IsUserExternalMemberAsync(_unitOfWork, workspaceId, newOwnerId, ct);
            if (isExternal)
            {
                return Result.Failure(WorkspaceConstants.Errors.CannotTransferToExternal, ErrorCodes.Forbidden);
            }

            var ownerRoleName = WorkspaceMemberRole.Owner.ToRoleName();
            var adminRoleName = WorkspaceMemberRole.Admin.ToRoleName();

            var ownerRoleId = await _authIdentity.GetRoleIdByNameAsync(ownerRoleName, ct);
            var adminRoleId = await _authIdentity.GetRoleIdByNameAsync(adminRoleName, ct);

            if (ownerRoleId == null || adminRoleId == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.RequiredRolesNotFound, ErrorCodes.ValidationError);
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
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<PagedResult<WorkspaceMemberDto>>> ListMembersAsync(Guid workspaceId, GetWorkspacesQuery query, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var caller = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
            if (caller == null)
            {
                return Result.Failure<PagedResult<WorkspaceMemberDto>>(WorkspaceConstants.Errors.UserNotActiveMember, ErrorCodes.Forbidden);
            }

            if (!string.Equals(caller.MembershipType, MembershipType.Internal.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure<PagedResult<WorkspaceMemberDto>>(WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure<PagedResult<WorkspaceMemberDto>>(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var callerRole = await _authIdentity.GetRoleNameByIdAsync(caller.RoleId, ct);
            var isOwnerOrAdmin = callerRole.IsOwnerOrAdmin();

            List<WorkspaceMember> members;
            if (isOwnerOrAdmin)
            {
                var allMembers = await _unitOfWork.WorkspaceMemberRepository.FindAsync(m => m.WorkspaceId == workspaceId, "", ct);
                members = allMembers.OrderBy(m => m.JoinedAt).ToList();
            }
            else
            {
                members = await _unitOfWork.WorkspaceMemberRepository.GetActiveMembersByWorkspaceAsync(workspaceId, ct);
            }

            // Fetch all user profiles in parallel (eliminates N sequential gRPC calls)
            var userResults = await Task.WhenAll(members.Select(m => _authIdentity.GetUserByIdAsync(m.UserId, ct)));
            var userMap = members.Zip(userResults, (m, u) => (m.UserId, User: u))
                .ToDictionary(x => x.UserId, x => x.User);

            // Fetch all distinct roles in parallel
            var distinctRoleIds = members.Select(m => m.RoleId).Distinct().ToList();
            var roleResults = await Task.WhenAll(distinctRoleIds.Select(rId => _authIdentity.GetRoleByIdAsync(rId, ct)));
            var roleMap = distinctRoleIds.Zip(roleResults, (id, r) => (id, Name: r?.Name ?? "Member"))
                .ToDictionary(x => x.id, x => x.Name);

            var filteredDtos = new List<WorkspaceMemberDto>();

            foreach (var m in members)
            {
                var user = userMap.GetValueOrDefault(m.UserId);
                var fullName = user?.FullName ?? "Unknown";
                // Internal workspace members can see each other's email — this list also
                // backs the meeting-room invite picker (WT-181), which needs it to let a
                // non-owner member pick a teammate instead of typing their email by hand.
                var email = user?.Email ?? string.Empty;
                var avatarUrl = user?.AvatarUrl;
                var roleName = roleMap.GetValueOrDefault(m.RoleId, "Member");

                // Match on name OR email, ignoring case and diacritics so "manh" finds
                // "Trần Mạnh Tuấn" (WT-231). Falls back to the real email only — the old
                // "Unknown" placeholder made every profile-less member match the term "unknown".
                if (!string.IsNullOrWhiteSpace(query.Search)
                    && !SearchTextHelper.Matches(fullName, query.Search)
                    && !SearchTextHelper.Matches(email, query.Search))
                {
                    continue;
                }

                filteredDtos.Add(m.ToDto(fullName, email, avatarUrl, roleName));
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
            await _unitOfWork.BeginTransactionAsync(ct);

            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                await _unitOfWork.RollbackTransactionAsync(ct);
                return Result.Failure(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var executingMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == executingUserId && m.RemovedAt == null, "", ct);
            if (executingMember == null)
            {
                await _unitOfWork.RollbackTransactionAsync(ct);
                return Result.Failure(WorkspaceConstants.Errors.UserNotActiveMember, ErrorCodes.Forbidden);
            }

            var execRoleName = await _authIdentity.GetRoleNameByIdAsync(executingMember.RoleId, ct);

            if (memberUserId == executingUserId)
            {
                if (execRoleName.IsOwner())
                {
                    var ownerRoleId = await _authIdentity.GetRoleIdByNameAsync(WorkspaceMemberRole.Owner.ToRoleName(), ct);
                    if (ownerRoleId == null)
                    {
                        await _unitOfWork.RollbackTransactionAsync(ct);
                        return Result.Failure(WorkspaceConstants.Errors.RequiredOwnerRoleNotFound, ErrorCodes.ValidationError);
                    }

                    var activeOwnersCount = await _unitOfWork.WorkspaceMemberRepository.CountActiveOwnersAsync(workspaceId, ownerRoleId.Value, ct);
                    if (activeOwnersCount <= 1)
                    {
                        await _unitOfWork.RollbackTransactionAsync(ct);
                        return Result.Failure(WorkspaceConstants.Errors.CannotLeaveAsLastOwner, ErrorCodes.ValidationError);
                    }
                }

                executingMember.RemovedAt = DateTime.UtcNow;
                executingMember.RemovedBy = executingUserId;
                executingMember.Status = WorkspaceMemberStatus.Removed.ToString();

                _unitOfWork.WorkspaceMemberRepository.Update(executingMember);
                await _eventPublisher.PublishMemberRemovedAsync(workspaceId, executingUserId, executingUserId, ct);
                await _unitOfWork.SaveChangesAsync(ct);
                await _unitOfWork.CommitTransactionAsync(ct);

                return Result.Success();
            }

            if (!execRoleName.IsOwnerOrAdmin())
            {
                await _unitOfWork.RollbackTransactionAsync(ct);
                return Result.Failure(WorkspaceConstants.Errors.OnlyOwnerAdminCanRemoveMembers, ErrorCodes.Forbidden);
            }

            var targetMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == memberUserId && m.RemovedAt == null, "", ct);
            if (targetMember == null)
            {
                await _unitOfWork.RollbackTransactionAsync(ct);
                return Result.Failure(WorkspaceConstants.Errors.TargetMemberNotFoundOrRemoved, ErrorCodes.NotFound);
            }

            var targetRoleName = await _authIdentity.GetRoleNameByIdAsync(targetMember.RoleId, ct);

            if (targetRoleName.IsOwner())
            {
                await _unitOfWork.RollbackTransactionAsync(ct);
                return Result.Failure(WorkspaceConstants.Errors.CannotRemoveOwner, ErrorCodes.Forbidden);
            }

            targetMember.RemovedAt = DateTime.UtcNow;
            targetMember.RemovedBy = executingUserId;
            targetMember.Status = WorkspaceMemberStatus.Removed.ToString();

            _unitOfWork.WorkspaceMemberRepository.Update(targetMember);
            await _eventPublisher.PublishMemberRemovedAsync(workspaceId, memberUserId, executingUserId, ct);
            await _unitOfWork.SaveChangesAsync(ct);
            await _unitOfWork.CommitTransactionAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            await _unitOfWork.RollbackTransactionAsync(ct);
            _logger.LogError(ex, "Error occurred while removing member. WorkspaceId: {WorkspaceId}, TargetUserId: {TargetUserId}", workspaceId, memberUserId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> ChangeMemberRoleAsync(Guid workspaceId, Guid memberUserId, string roleName, Guid executingUserId, CancellationToken ct = default)
    {
        try
        {
            await _unitOfWork.BeginTransactionAsync(ct);

            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                await _unitOfWork.RollbackTransactionAsync(ct);
                return Result.Failure(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            if (!roleName.IsAdmin() && roleName != WorkspaceMemberRole.Member.ToRoleName())
            {
                await _unitOfWork.RollbackTransactionAsync(ct);
                return Result.Failure(WorkspaceConstants.Errors.RoleMustBeAdminOrMember, ErrorCodes.ValidationError);
            }

            var executingMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == executingUserId && m.RemovedAt == null, "", ct);
            if (executingMember == null)
            {
                await _unitOfWork.RollbackTransactionAsync(ct);
                return Result.Failure(WorkspaceConstants.Errors.UserNotActiveMember, ErrorCodes.Forbidden);
            }

            var execRoleName = await _authIdentity.GetRoleNameByIdAsync(executingMember.RoleId, ct);
            if (!execRoleName.IsOwnerOrAdmin())
            {
                await _unitOfWork.RollbackTransactionAsync(ct);
                return Result.Failure(WorkspaceConstants.Errors.OnlyOwnerAdminCanChangeRoles, ErrorCodes.Forbidden);
            }

            var targetMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == memberUserId && m.RemovedAt == null, "", ct);
            if (targetMember == null)
            {
                await _unitOfWork.RollbackTransactionAsync(ct);
                return Result.Failure(WorkspaceConstants.Errors.TargetMemberNotFoundOrRemoved, ErrorCodes.NotFound);
            }

            var targetRoleName = await _authIdentity.GetRoleNameByIdAsync(targetMember.RoleId, ct);

            if (memberUserId == executingUserId)
            {
                if (targetRoleName.IsOwner())
                {
                    var ownerRoleId = await _authIdentity.GetRoleIdByNameAsync(WorkspaceMemberRole.Owner.ToRoleName(), ct);
                    if (ownerRoleId == null)
                    {
                        await _unitOfWork.RollbackTransactionAsync(ct);
                        return Result.Failure(WorkspaceConstants.Errors.RequiredOwnerRoleNotFound, ErrorCodes.ValidationError);
                    }

                    var activeOwnersCount = await _unitOfWork.WorkspaceMemberRepository.CountActiveOwnersAsync(workspaceId, ownerRoleId.Value, ct);
                    if (activeOwnersCount <= 1)
                    {
                        await _unitOfWork.RollbackTransactionAsync(ct);
                        return Result.Failure(WorkspaceConstants.Errors.CannotDemoteLastOwner, ErrorCodes.ValidationError);
                    }
                }
            }
            else
            {
                if (targetRoleName.IsOwner())
                {
                    await _unitOfWork.RollbackTransactionAsync(ct);
                    return Result.Failure(WorkspaceConstants.Errors.CannotChangeOwnerRole, ErrorCodes.Forbidden);
                }
            }

            if (execRoleName.IsAdmin())
            {
                if (targetRoleName.IsAdmin() && memberUserId != executingUserId)
                {
                    await _unitOfWork.RollbackTransactionAsync(ct);
                    return Result.Failure(WorkspaceConstants.Errors.AdminCannotChangeAdminRole, ErrorCodes.Forbidden);
                }

                if (roleName.IsAdmin() && memberUserId != executingUserId)
                {
                    await _unitOfWork.RollbackTransactionAsync(ct);
                    return Result.Failure(WorkspaceConstants.Errors.AdminCannotPromoteToAdmin, ErrorCodes.Forbidden);
                }
            }

            var newRoleId = await _authIdentity.GetRoleIdByNameAsync(roleName, ct);
            if (newRoleId == null)
            {
                await _unitOfWork.RollbackTransactionAsync(ct);
                return Result.Failure(WorkspaceConstants.Errors.RoleNotFound, ErrorCodes.ValidationError);
            }

            targetMember.RoleId = newRoleId.Value;

            _unitOfWork.WorkspaceMemberRepository.Update(targetMember);
            await _unitOfWork.SaveChangesAsync(ct);
            await _unitOfWork.CommitTransactionAsync(ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            await _unitOfWork.RollbackTransactionAsync(ct);
            _logger.LogError(ex, "Error occurred while changing member role. WorkspaceId: {WorkspaceId}, TargetUserId: {TargetUserId}", workspaceId, memberUserId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> UpdateMemberAsync(Guid workspaceId, Guid memberUserId, UpdateWorkspaceMemberRequest request, Guid executingUserId, CancellationToken ct = default)
    {
        try
        {
            var executingMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == executingUserId && m.RemovedAt == null, "", ct);
            if (executingMember == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.UserNotActiveMember, ErrorCodes.Forbidden);
            }

            var execRoleName = await _authIdentity.GetRoleNameByIdAsync(executingMember.RoleId, ct);
            if (!execRoleName.IsOwnerOrAdmin())
            {
                return Result.Failure("Only workspace owners or admins can manage member settings.", ErrorCodes.Forbidden);
            }

            var targetMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == memberUserId && m.RemovedAt == null, "", ct);
            if (targetMember == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.TargetMemberNotFoundOrRemoved, ErrorCodes.NotFound);
            }

            var targetRoleName = await _authIdentity.GetRoleNameByIdAsync(targetMember.RoleId, ct);
            if (targetRoleName.IsOwner() && !execRoleName.IsOwner())
            {
                return Result.Failure("Admins cannot modify settings of workspace owners.", ErrorCodes.Forbidden);
            }

            if (execRoleName.IsAdmin() && targetRoleName.IsAdmin() && memberUserId != executingUserId)
            {
                return Result.Failure(WorkspaceConstants.Errors.AdminCannotModifyPeerAdmin, ErrorCodes.Forbidden);
            }

            targetMember.CanCreateMeetings = request.CanCreateMeetings;

            _unitOfWork.WorkspaceMemberRepository.Update(targetMember);
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while updating member settings. WorkspaceId: {WorkspaceId}, TargetUserId: {TargetUserId}", workspaceId, memberUserId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }
}
