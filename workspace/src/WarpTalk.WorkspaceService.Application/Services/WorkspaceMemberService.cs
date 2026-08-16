using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
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

namespace WarpTalk.WorkspaceService.Application.Services;

public class WorkspaceMemberService : IWorkspaceMemberService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<WorkspaceMemberService> _logger;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly IWorkspaceEventPublisher _eventPublisher;
    private readonly IConfiguration _configuration;
    private readonly WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient? _notificationClient;

    public WorkspaceMemberService(
        IUnitOfWork unitOfWork,
        ILogger<WorkspaceMemberService> logger,
        IAuthIdentityClient authIdentity,
        IWorkspaceEventPublisher eventPublisher,
        IConfiguration configuration,
        // Optional so every existing construction site — and the whole test suite — keeps
        // working (same precedent as TranslationRoomService). A workspace service that cannot
        // reach the notification mesh still changes roles; it just cannot ring the bell.
        WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient? notificationClient = null)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _authIdentity = authIdentity;
        _eventPublisher = eventPublisher;
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _notificationClient = notificationClient;
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
            if (currentOwnerMember == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.UserNotActiveMember, ErrorCodes.Forbidden);
            }

            var targetOldRoleName = await _authIdentity.GetRoleNameByIdAsync(newOwnerMember.RoleId, ct);
            if (targetOldRoleName.IsOwner())
            {
                return Result.Failure(WorkspaceConstants.Errors.CannotChangeOwnerRole, ErrorCodes.ValidationError);
            }

            workspace.OwnerId = newOwnerId;
            _unitOfWork.WorkspaceRepository.Update(workspace);

            currentOwnerMember.RoleId = adminRoleId.Value;
            _unitOfWork.WorkspaceMemberRepository.Update(currentOwnerMember);

            newOwnerMember.RoleId = ownerRoleId.Value;
            _unitOfWork.WorkspaceMemberRepository.Update(newOwnerMember);

            var demotionId = Guid.NewGuid();
            var promotionId = Guid.NewGuid();
            var transferEffectiveAt = DateTime.UtcNow;
            await _eventPublisher.PublishMemberRoleChangedAsync(
                workspaceId, executingUserId, ownerRoleName, adminRoleName, executingUserId,
                demotionId, null, currentOwnerMember.MembershipType, "next-request-or-session", transferEffectiveAt, $"transfer:{demotionId:N}", ct);
            await _eventPublisher.PublishMemberRoleChangedAsync(
                workspaceId, newOwnerId, targetOldRoleName, ownerRoleName, executingUserId,
                promotionId, null, newOwnerMember.MembershipType, "next-request-or-session", transferEffectiveAt, $"transfer:{promotionId:N}", ct);

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

            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                List<WorkspaceMember> membersForSearch;
                if (isOwnerOrAdmin)
                {
                    // Owner/Admin searches may include suspended members, but never tombstones.
                    var allMembers = await _unitOfWork.WorkspaceMemberRepository.FindAsync(
                        m => m.WorkspaceId == workspaceId && m.RemovedAt == null, "", ct);
                    membersForSearch = allMembers.OrderByDescending(m => m.JoinedAt).ToList();
                }
                else
                {
                    membersForSearch = (await _unitOfWork.WorkspaceMemberRepository.GetActiveMembersByWorkspaceAsync(workspaceId, ct))
                        .OrderByDescending(m => m.JoinedAt)
                        .ToList();
                }

                var search = query.Search.Trim();
                // Match on name OR email ignoring case AND diacritics, so "manh" finds
                // "Trần Mạnh Tuấn" (WT-231) — an OrdinalIgnoreCase Contains only folds case,
                // and nobody types the accents when searching.
                var filteredDtos = (await WorkspaceMemberDtoHelper.BuildAsync(membersForSearch, _authIdentity, ct))
                    .Where(m => SearchTextHelper.Matches(m.FullName, search)
                                || SearchTextHelper.Matches(m.Email, search))
                    .ToList();

                var searchedItems = filteredDtos
                    .Skip(Math.Max(0, (query.Page - 1) * query.PageSize))
                    .Take(query.PageSize)
                    .ToList();

                return Result.Success(new PagedResult<WorkspaceMemberDto>(
                    searchedItems, query.Page, query.PageSize, filteredDtos.Count));
            }

            var (members, totalCount) = await _unitOfWork.WorkspaceMemberRepository.GetPagedMembersAsync(
                workspaceId, query.Page, query.PageSize, isOwnerOrAdmin, isDescending: true, ct);

            // Fetch user profiles for paged members in parallel
            var userResults = await Task.WhenAll(members.Select(m => _authIdentity.GetUserByIdAsync(m.UserId, ct)));
            var userMap = members.Zip(userResults, (m, u) => (m.UserId, User: u))
                .ToDictionary(x => x.UserId, x => x.User);

            // Fetch all distinct roles in parallel
            var distinctRoleIds = members.Select(m => m.RoleId).Distinct().ToList();
            var roleResults = await Task.WhenAll(distinctRoleIds.Select(rId => _authIdentity.GetRoleByIdAsync(rId, ct)));
            var roleMap = distinctRoleIds.Zip(roleResults, (id, r) => (id, Name: r?.Name ?? "Member"))
                .ToDictionary(x => x.id, x => x.Name);

            var memberDtos = new List<WorkspaceMemberDto>();
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

                memberDtos.Add(m.ToDto(fullName, email, avatarUrl, roleName));
            }

            var pagedResult = new PagedResult<WorkspaceMemberDto>(memberDtos, query.Page, query.PageSize, totalCount);
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
                return Result.Failure(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var executingMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == executingUserId && m.RemovedAt == null, "", ct);
            if (executingMember == null)
            {
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
                        return Result.Failure(WorkspaceConstants.Errors.RequiredOwnerRoleNotFound, ErrorCodes.ValidationError);
                    }

                    var activeOwnersCount = await _unitOfWork.WorkspaceMemberRepository.CountActiveOwnersAsync(workspaceId, ownerRoleId.Value, ct);
                    if (activeOwnersCount <= 1)
                    {
                        return Result.Failure(WorkspaceConstants.Errors.CannotLeaveAsLastOwner, ErrorCodes.ValidationError);
                    }
                }

                executingMember.RemovedAt = DateTime.UtcNow;
                executingMember.RemovedBy = executingUserId;
                executingMember.Status = WorkspaceMemberStatus.Removed.ToStorageValue();

                _unitOfWork.WorkspaceMemberRepository.Update(executingMember);
                await _eventPublisher.PublishMemberRemovedAsync(workspaceId, executingUserId, executingUserId, ct);
                await _unitOfWork.SaveChangesAsync(ct);

                return Result.Success();
            }

            if (!execRoleName.IsOwnerOrAdmin())
            {
                return Result.Failure(WorkspaceConstants.Errors.OnlyOwnerAdminCanRemoveMembers, ErrorCodes.Forbidden);
            }

            var targetMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == memberUserId && m.RemovedAt == null, "", ct);
            if (targetMember == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.TargetMemberNotFoundOrRemoved, ErrorCodes.NotFound);
            }

            var targetRoleName = await _authIdentity.GetRoleNameByIdAsync(targetMember.RoleId, ct);

            if (targetRoleName.IsOwner())
            {
                return Result.Failure(WorkspaceConstants.Errors.CannotRemoveOwner, ErrorCodes.Forbidden);
            }

            // Admins are peers: one may not remove another. UpdateMemberAsync has always
            // had this guard; removal — the more destructive operation — did not, so an
            // Admin could evict a peer Admin through the API. The web client disables the
            // button for exactly this case (`isAdmin && memberRole === "admin"` in
            // members/page.tsx); WT-142: "FE disabled states do not replace backend
            // authorization."
            //
            // Self-removal is unaffected: `memberUserId == executingUserId` returns
            // above, so an Admin can still leave the workspace voluntarily.
            if (execRoleName.IsAdmin() && targetRoleName.IsAdmin())
            {
                return Result.Failure(WorkspaceConstants.Errors.AdminCannotRemovePeerAdmin, ErrorCodes.Forbidden);
            }

            targetMember.RemovedAt = DateTime.UtcNow;
            targetMember.RemovedBy = executingUserId;
            targetMember.Status = WorkspaceMemberStatus.Removed.ToStorageValue();

            _unitOfWork.WorkspaceMemberRepository.Update(targetMember);
            await _eventPublisher.PublishMemberRemovedAsync(workspaceId, memberUserId, executingUserId, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while removing member. WorkspaceId: {WorkspaceId}, TargetUserId: {TargetUserId}", workspaceId, memberUserId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public Task<Result> ChangeMemberRoleAsync(Guid workspaceId, Guid memberUserId, string roleName, Guid executingUserId, CancellationToken ct = default)
        => ChangeMemberRoleCoreAsync(workspaceId, memberUserId, roleName, executingUserId, null, null, ct);

    public async Task<Result> ChangeMemberRoleCoreAsync(Guid workspaceId, Guid memberUserId, string roleName, Guid executingUserId, ApplyWorkspaceRoleChangeRequest? request, Guid? eventId, CancellationToken ct = default)
    {
        try
        {
            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            if (!roleName.IsAdmin() && roleName != WorkspaceMemberRole.Member.ToRoleName())
            {
                return Result.Failure(WorkspaceConstants.Errors.RoleMustBeAdminOrMember, ErrorCodes.ValidationError);
            }

            var executingMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == executingUserId && m.RemovedAt == null, "", ct);
            if (executingMember == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.UserNotActiveMember, ErrorCodes.Forbidden);
            }

            var execRoleName = await _authIdentity.GetRoleNameByIdAsync(executingMember.RoleId, ct);
            if (!execRoleName.IsOwner())
            {
                return Result.Failure(WorkspaceConstants.Errors.OnlyOwnerAdminCanChangeRoles, ErrorCodes.Forbidden);
            }

            var targetMember = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == memberUserId && m.RemovedAt == null, "", ct);
            if (targetMember == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.TargetMemberNotFoundOrRemoved, ErrorCodes.NotFound);
            }

            var targetRoleName = await _authIdentity.GetRoleNameByIdAsync(targetMember.RoleId, ct);

            if (request != null)
            {
                if (!RolePreviewSigningKeyHelper.TryResolve(_configuration, out var previewSigningKey))
                {
                    _logger.LogError("Role preview signing key is not configured.");
                    return Result.Failure(WorkspaceConstants.Errors.RolePreviewSigningKeyNotConfigured, ErrorCodes.ValidationError);
                }

                if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
                {
                    return Result.Failure(WorkspaceConstants.Errors.InvalidIdempotencyKey, ErrorCodes.ValidationError);
                }

                if (!RolePreviewTokenHelper.TryReadPreviewToken(request.PreviewToken, previewSigningKey, out var preview)
                    || preview.WorkspaceId != workspaceId
                    || preview.TargetUserId != memberUserId)
                {
                    return Result.Failure(WorkspaceConstants.Errors.InvalidRoleChangePreview, ErrorCodes.ValidationError);
                }
                var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (preview.ExpiresAtUnix < nowUnix)
                {
                    return Result.Failure(WorkspaceConstants.Errors.RoleChangePreviewExpired, ErrorCodes.ValidationError);
                }
                if (!string.Equals(preview.NewRole, roleName, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(preview.OldRole, targetRoleName, StringComparison.OrdinalIgnoreCase))
                {
                    return Result.Failure(WorkspaceConstants.Errors.RoleChangeStale, ErrorCodes.Conflict);
                }
                if (roleName.IsAdmin() && preview.CoolingOffUntilUnix > nowUnix)
                {
                    return Result.Failure(WorkspaceConstants.Errors.CoolingOffNotComplete, ErrorCodes.Conflict);
                }
            }

            if (string.Equals(targetMember.MembershipType, MembershipType.External.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure(WorkspaceConstants.Errors.ExternalRoleImmutable, ErrorCodes.ValidationError);
            }

            if (memberUserId == executingUserId)
            {
                return Result.Failure(WorkspaceConstants.Errors.CannotChangeOwnRole, ErrorCodes.ValidationError);
            }

            if (targetRoleName.IsOwner())
            {
                return Result.Failure(WorkspaceConstants.Errors.CannotChangeOwnerRole, ErrorCodes.Forbidden);
            }

            if (string.Equals(targetRoleName, roleName, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Success();
            }

            var newRoleId = await _authIdentity.GetRoleIdByNameAsync(roleName, ct);
            if (newRoleId == null)
            {
                return Result.Failure(WorkspaceConstants.Errors.RoleNotFound, ErrorCodes.ValidationError);
            }

            targetMember.RoleId = newRoleId.Value;

            _unitOfWork.WorkspaceMemberRepository.Update(targetMember);

            var committedEventId = eventId ?? Guid.NewGuid();
            await _eventPublisher.PublishMemberRoleChangedAsync(
                workspaceId, memberUserId, targetRoleName, roleName, executingUserId,
                committedEventId, request?.CorrelationId, targetMember.MembershipType,
                "next-request-or-session", DateTime.UtcNow, request?.IdempotencyKey, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            // WT-431 (Linear): after the commit, never before — a failed save must not announce a
            // role change that did not happen. The outbox event above is published and consumed by
            // nothing; the person whose permissions just changed found out by reloading the page.
            await NotifyMemberRoleChangedAsync(workspaceId, memberUserId, targetRoleName, roleName, ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while changing member role. WorkspaceId: {WorkspaceId}, TargetUserId: {TargetUserId}", workspaceId, memberUserId);
            return Result.Failure(WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// Rings the bell for the person whose permissions just moved — a Notification Center row and
    /// the realtime toast in one call (NotificationGrpcServiceImpl persists and Redis-publishes).
    ///
    /// Best-effort by design: an unreachable notification mesh must not fail a role change that is
    /// already committed, and a null client (tests, degraded config) means the change simply goes
    /// unannounced — which is exactly the pre-WT-431 behaviour, not a new failure mode.
    /// </summary>
    private async Task NotifyMemberRoleChangedAsync(
        Guid workspaceId,
        Guid memberUserId,
        string previousRole,
        string newRole,
        CancellationToken ct)
    {
        if (_notificationClient == null) return;

        try
        {
            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            var workspaceName = workspace?.Name ?? "your workspace";

            var request = new WarpTalk.Shared.Protos.SendNotificationRequest
            {
                UserId = memberUserId.ToString(),
                Type = "WORKSPACE_ROLE_CHANGED",
                Title = $"Your role in {workspaceName} changed",
                Body = $"You are now {newRole} (previously {previousRole}). Your permissions apply from your next request.",
                ActionUrl = workspace?.Slug is { Length: > 0 } slug ? $"/{slug}" : "/",
            };
            request.Metadata.Add("workspace_id", workspaceId.ToString());
            request.Metadata.Add("old_role", previousRole);
            request.Metadata.Add("new_role", newRole);

            await _notificationClient.SendNotificationAsync(request, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not notify user {UserId} about their role change in workspace {WorkspaceId}; the change itself is committed.",
                memberUserId, workspaceId);
        }
    }

    public async Task<Result<WorkspaceRoleChangeResultDto>> ApplyMemberRoleChangeAsync(Guid workspaceId, Guid memberUserId, ApplyWorkspaceRoleChangeRequest request, Guid executingUserId, CancellationToken ct = default)
    {
        var eventId = Guid.NewGuid();
        var memberBefore = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
            m => m.WorkspaceId == workspaceId && m.UserId == memberUserId && m.RemovedAt == null, "", ct);
        var oldRole = memberBefore == null
            ? string.Empty
            : await _authIdentity.GetRoleNameByIdAsync(memberBefore.RoleId, ct);
        var result = await ChangeMemberRoleCoreAsync(workspaceId, memberUserId, request.TargetRole, executingUserId, request, eventId, ct);
        if (!result.IsSuccess)
            return Result.Failure<WorkspaceRoleChangeResultDto>(result.Error ?? WorkspaceConstants.Errors.UnexpectedError, result.ErrorCode);

        WorkspaceMemberDto? memberProjection = null;
        var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == memberUserId, "", ct);
        if (member != null)
        {
            var user = await _authIdentity.GetUserByIdAsync(memberUserId, ct);
            var currentRole = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
            memberProjection = member.ToDto(user?.FullName ?? string.Empty, user?.Email ?? string.Empty, user?.AvatarUrl, currentRole);
        }

        return Result.Success(new WorkspaceRoleChangeResultDto(
            memberUserId,
            oldRole,
            request.TargetRole,
            DateTime.UtcNow,
            "next-request-or-session",
            eventId,
            memberProjection,
            request.IdempotencyKey));
    }

    public async Task<Result<WorkspaceRoleChangePreviewDto>> PreviewMemberRoleChangeAsync(Guid workspaceId, Guid memberUserId, string roleName, Guid executingUserId, CancellationToken ct = default)
    {
        var actor = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == executingUserId && m.RemovedAt == null, "", ct);
        if (actor == null) return Result.Failure<WorkspaceRoleChangePreviewDto>(WorkspaceConstants.Errors.UserNotActiveMember, ErrorCodes.Forbidden);
        var actorRole = await _authIdentity.GetRoleNameByIdAsync(actor.RoleId, ct);
        if (!actorRole.IsOwner()) return Result.Failure<WorkspaceRoleChangePreviewDto>(WorkspaceConstants.Errors.OnlyOwnerAdminCanChangeRoles, ErrorCodes.Forbidden);
        var target = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == memberUserId && m.RemovedAt == null, "", ct);
        if (target == null) return Result.Failure<WorkspaceRoleChangePreviewDto>(WorkspaceConstants.Errors.TargetMemberNotFoundOrRemoved, ErrorCodes.NotFound);
        var currentRole = await _authIdentity.GetRoleNameByIdAsync(target.RoleId, ct);
        if (memberUserId == executingUserId) return Result.Failure<WorkspaceRoleChangePreviewDto>(WorkspaceConstants.Errors.CannotChangeOwnRole, ErrorCodes.ValidationError);
        if (currentRole.IsOwner()) return Result.Failure<WorkspaceRoleChangePreviewDto>(WorkspaceConstants.Errors.CannotChangeOwnerRole, ErrorCodes.Forbidden);
        if (!string.Equals(target.MembershipType, MembershipType.Internal.ToString(), StringComparison.OrdinalIgnoreCase)) return Result.Failure<WorkspaceRoleChangePreviewDto>(WorkspaceConstants.Errors.ExternalRoleImmutable, ErrorCodes.ValidationError);
        if (!roleName.IsAdmin() && !roleName.IsMember()) return Result.Failure<WorkspaceRoleChangePreviewDto>(WorkspaceConstants.Errors.RoleMustBeAdminOrMember, ErrorCodes.ValidationError);
        if (!RolePreviewSigningKeyHelper.TryResolve(_configuration, out var previewSigningKey))
        {
            _logger.LogError("Role preview signing key is not configured.");
            return Result.Failure<WorkspaceRoleChangePreviewDto>(WorkspaceConstants.Errors.RolePreviewSigningKeyNotConfigured, ErrorCodes.ValidationError);
        }

        var now = DateTime.UtcNow;
        var expiresAt = now.AddMinutes(15);
        var coolingOffUntil = roleName.IsAdmin() ? now.AddSeconds(60) : (DateTime?)null;
        var previewToken = RolePreviewTokenHelper.CreatePreviewToken(
            workspaceId,
            memberUserId,
            currentRole,
            roleName,
            new DateTimeOffset(expiresAt).ToUnixTimeSeconds(),
            new DateTimeOffset(coolingOffUntil ?? now).ToUnixTimeSeconds(),
            previewSigningKey);

        return Result.Success(new WorkspaceRoleChangePreviewDto(memberUserId, currentRole, roleName, target.MembershipType, target.CanCreateMeetings, [], expiresAt, previewToken, coolingOffUntil));
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

            // An Admin may not edit any Admin's member settings — including their own.
            // The `memberUserId != executingUserId` carve-out that used to sit here let
            // an Admin PATCH their own row and restore a CanCreateMeetings permission an
            // Owner had just revoked, which is the exact revocation WT-249 made a real
            // enforcement gate (ValidateMeetingCreationAsync grants no Owner/Admin
            // bypass). The web client already disables the self-toggle
            // (members/page.tsx); WT-142: "FE disabled states do not replace backend
            // authorization."
            //
            // Owner self-edit stays allowed on purpose. ValidateMeetingCreationAsync
            // reads CanCreateMeetings with no role bypass, and an Admin cannot edit an
            // Owner (guard above), so blocking Owner self-edit would strand a sole Owner
            // who had switched their own hosting off.
            if (execRoleName.IsAdmin() && targetRoleName.IsAdmin())
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
