using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Mappers.Admin;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Domain.ReadModels;

namespace WarpTalk.WorkspaceService.Application.Services;

public class AdminWorkspaceService : IAdminWorkspaceService
{
    private const int MaxReasonLength = 500;
    private const int LifecycleHistoryLimit = 50;

    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        WorkspaceLifecycleStatus.All,
        WorkspaceLifecycleStatus.Active,
        WorkspaceLifecycleStatus.Suspended,
        WorkspaceLifecycleStatus.Deleted,
    };

    private static readonly string[] AllowedSorts =
    [
        WorkspaceDirectorySort.CreatedDesc,
        WorkspaceDirectorySort.CreatedAsc,
        WorkspaceDirectorySort.NameAsc,
        WorkspaceDirectorySort.NameDesc,
        WorkspaceDirectorySort.MembersDesc,
        WorkspaceDirectorySort.MembersAsc,
        WorkspaceDirectorySort.UpdatedDesc,
    ];

    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuthIdentityClient _authIdentityClient;
    private readonly IAdminAuditLogRepository _adminAuditLogRepository;
    private readonly IWorkspaceEventPublisher _eventPublisher;
    private readonly ILogger<AdminWorkspaceService> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Nullable for the same reason WorkspaceMemberService's is: an unreachable notification mesh
    /// must not fail a suspension that is already committed, and tests construct this service
    /// without one.
    /// </summary>
    private readonly WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient? _notificationClient;

    // The wire values, spelled here rather than imported: these constants live in
    // NotificationService.Domain, which this service does not reference — the same reason
    // WorkspaceMemberService writes "WORKSPACE_ROLE_CHANGED" as a literal. Both are registered in
    // NotificationValidator.Schemas; an unregistered type carrying metadata is rejected outright.
    private const string WorkspaceSuspendedNotificationType = "WORKSPACE_SUSPENDED";
    private const string WorkspaceReactivatedNotificationType = "WORKSPACE_REACTIVATED";

    public AdminWorkspaceService(
        IUnitOfWork unitOfWork,
        IAuthIdentityClient authIdentityClient,
        IAdminAuditLogRepository adminAuditLogRepository,
        IWorkspaceEventPublisher eventPublisher,
        ILogger<AdminWorkspaceService> logger,
        TimeProvider? timeProvider = null,
        WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient? notificationClient = null)
    {
        _unitOfWork = unitOfWork;
        _authIdentityClient = authIdentityClient;
        _adminAuditLogRepository = adminAuditLogRepository;
        _eventPublisher = eventPublisher;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _notificationClient = notificationClient;
    }

    public async Task<Result<AdminPagedResult<AdminWorkspaceSummaryDto>>> GetDirectoryAsync(
        AdminWorkspaceDirectoryQuery query,
        CancellationToken ct = default)
    {
        var status = string.IsNullOrWhiteSpace(query.Status)
            ? WorkspaceLifecycleStatus.All
            : query.Status.Trim().ToLowerInvariant();
        if (!AllowedStatuses.Contains(status))
        {
            return Result.Failure<AdminPagedResult<AdminWorkspaceSummaryDto>>(
                WorkspaceAdminErrors.UnknownStatusFilter, ErrorCodes.ValidationError);
        }

        if (!AdminSort.TryResolve(query.Sort, AllowedSorts, WorkspaceDirectorySort.CreatedDesc, out var sort))
        {
            return Result.Failure<AdminPagedResult<AdminWorkspaceSummaryDto>>(
                WorkspaceAdminErrors.UnknownSort, ErrorCodes.ValidationError);
        }

        if (query.MinMembers is { } min && query.MaxMembers is { } max && min > max)
        {
            return Result.Failure<AdminPagedResult<AdminWorkspaceSummaryDto>>(
                WorkspaceAdminErrors.InvalidMemberCountRange, ErrorCodes.ValidationError);
        }

        var (page, pageSize) = query.Normalize();

        var filter = new WorkspaceDirectoryFilter(
            page,
            pageSize,
            query.Search,
            status,
            query.MinMembers is { } minMembers ? Math.Max(minMembers, 0) : null,
            query.MaxMembers is { } maxMembers ? Math.Max(maxMembers, 0) : null,
            sort);

        try
        {
            var (rows, total) = await _unitOfWork.WorkspaceRepository.GetAdminDirectoryAsync(filter, ct);
            var owners = await ResolveOwnersAsync(rows.Select(row => row.OwnerId), ct);

            var items = rows
                .Select(row => AdminWorkspaceMapper.ToSummary(row, owners.GetValueOrDefault(row.OwnerId)))
                .ToList();

            return Result.Success(new AdminPagedResult<AdminWorkspaceSummaryDto>(items, page, pageSize, total));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin workspace directory query failed.");
            return Result.Failure<AdminPagedResult<AdminWorkspaceSummaryDto>>(
                WorkspaceConstants.Errors.UnexpectedErrorFetchingWorkspaces, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<AdminWorkspaceDetailDto>> GetDetailAsync(Guid workspaceId, CancellationToken ct = default)
    {
        try
        {
            var detail = await BuildDetailAsync(workspaceId, ct);
            return detail is null
                ? Result.Failure<AdminWorkspaceDetailDto>(
                    WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound)
                : Result.Success(detail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin workspace detail query failed. WorkspaceId: {WorkspaceId}", workspaceId);
            return Result.Failure<AdminWorkspaceDetailDto>(
                WorkspaceConstants.Errors.UnexpectedErrorFetchingWorkspace, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<AdminWorkspaceDetailDto>> GetDetailBySlugAsync(string slug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return Result.Failure<AdminWorkspaceDetailDto>(
                WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
        }

        try
        {
            var row = await _unitOfWork.WorkspaceRepository.GetAdminDetailBySlugAsync(slug, ct);
            var detail = await BuildDetailFromRowAsync(row, ct);
            return detail is null
                ? Result.Failure<AdminWorkspaceDetailDto>(
                    WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound)
                : Result.Success(detail);
        }
        catch (Exception ex)
        {
            // The slug is the caller's, so it is logged as a parameter rather than interpolated.
            _logger.LogError(ex, "Admin workspace detail query by slug failed. Slug: {Slug}", slug);
            return Result.Failure<AdminWorkspaceDetailDto>(
                WorkspaceConstants.Errors.UnexpectedErrorFetchingWorkspace, ErrorCodes.InternalServerError);
        }
    }

    public Task<Result<AdminWorkspaceDetailDto>> SuspendAsync(
        Guid workspaceId,
        string reason,
        Guid actorId,
        string? correlationId,
        CancellationToken ct = default)
        => ChangeLifecycleAsync(workspaceId, reason, actorId, correlationId, suspend: true, ct);

    public Task<Result<AdminWorkspaceDetailDto>> ReactivateAsync(
        Guid workspaceId,
        string reason,
        Guid actorId,
        string? correlationId,
        CancellationToken ct = default)
        => ChangeLifecycleAsync(workspaceId, reason, actorId, correlationId, suspend: false, ct);

    public async Task<Result<AdminWorkspaceDetailDto>> DeleteAsync(
        Guid workspaceId,
        string reason,
        Guid actorId,
        string? correlationId,
        CancellationToken ct = default)
    {
        var trimmedReason = reason?.Trim() ?? string.Empty;
        if (trimmedReason.Length == 0)
        {
            return Result.Failure<AdminWorkspaceDetailDto>(
                WorkspaceAdminErrors.ReasonRequired, ErrorCodes.ValidationError);
        }

        if (trimmedReason.Length > MaxReasonLength)
        {
            return Result.Failure<AdminWorkspaceDetailDto>(
                WorkspaceAdminErrors.ReasonTooLong, ErrorCodes.ValidationError);
        }

        try
        {
            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace is null)
            {
                return Result.Failure<AdminWorkspaceDetailDto>(
                    WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            if (workspace.DeletedAt != null)
            {
                return Result.Failure<AdminWorkspaceDetailDto>(
                    WorkspaceAdminErrors.DeletedWorkspaceIsImmutable, ErrorCodes.Conflict);
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;

            workspace.DeletedAt = now;
            workspace.UpdatedAt = now;
            workspace.UpdatedBy = actorId;

            // WT-417 semantics, same as the Owner's own delete: the membership rows go with the
            // workspace. Unlike that path there is no tracked `executingMember` here — the actor
            // is a platform admin, not a member — so every row from the AsNoTracking read can be
            // Update()d without colliding with the identity map.
            var members = await _unitOfWork.WorkspaceMemberRepository.GetActiveMembersByWorkspaceAsync(workspaceId, ct);
            foreach (var member in members)
            {
                member.RemovedAt = now;
                member.RemovedBy = actorId;
                _unitOfWork.WorkspaceMemberRepository.Update(member);
            }

            _unitOfWork.WorkspaceRepository.Update(workspace);

            await _adminAuditLogRepository.AppendAsync(
                new WorkspaceAdminAction
                {
                    Id = Guid.NewGuid(),
                    SourceService = AdminAuditSources.WorkspaceService,
                    WorkspaceId = workspaceId,
                    EntityType = AdminAuditEntityTypes.Workspace,
                    EntityId = workspaceId,
                    Action = WorkspaceAdminActionTypes.Delete,
                    Reason = trimmedReason,
                    Result = AdminAuditResults.Succeeded,
                    PerformedBy = actorId,
                    PerformedAt = now,
                    CorrelationId = correlationId,
                    BeforeSummary = JsonSerializer.Serialize(
                        new Dictionary<string, string?> { ["status"] = workspace.IsActive ? "active" : "suspended" }),
                    AfterSummary = JsonSerializer.Serialize(
                        new Dictionary<string, string?> { ["status"] = "deleted" }),
                },
                ct);

            await _eventPublisher.PublishWorkspaceDeletedAsync(workspaceId, actorId, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation(
                "System admin {ActorId} deleted workspace {WorkspaceId}. CorrelationId: {CorrelationId}",
                actorId, workspaceId, correlationId);

            var detail = await BuildDetailAsync(workspaceId, ct);
            return detail is null
                ? Result.Failure<AdminWorkspaceDetailDto>(
                    WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound)
                : Result.Success(detail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin workspace delete failed. WorkspaceId: {WorkspaceId}", workspaceId);
            return Result.Failure<AdminWorkspaceDetailDto>(
                WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<IReadOnlyList<AdminWorkspaceMemberDto>>> GetMembersAsync(
        Guid workspaceId,
        CancellationToken ct = default)
    {
        try
        {
            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace is null)
            {
                return Result.Failure<IReadOnlyList<AdminWorkspaceMemberDto>>(
                    WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var members = await _unitOfWork.WorkspaceMemberRepository.GetActiveMembersByWorkspaceAsync(workspaceId, ct);

            // Role ids repeat across the roster (three roles, N members) — resolve each id once.
            var roleNames = new Dictionary<Guid, string>();
            foreach (var roleId in members.Select(m => m.RoleId).Distinct())
            {
                var role = await _authIdentityClient.GetRoleByIdAsync(roleId, ct);
                roleNames[roleId] = role?.Name ?? "unknown";
            }

            var users = await ResolveOwnersAsync(members.Select(m => m.UserId), ct);

            var rows = members
                .Select(member =>
                {
                    users.TryGetValue(member.UserId, out var user);
                    return new AdminWorkspaceMemberDto(
                        member.UserId,
                        user?.FullName,
                        user?.Email,
                        user?.AvatarUrl,
                        Resolved: user is not null,
                        Role: roleNames[member.RoleId],
                        MembershipType: member.MembershipType,
                        Status: member.Status,
                        CanCreateMeetings: member.CanCreateMeetings,
                        JoinedAt: member.JoinedAt);
                })
                .OrderBy(row => RoleRank(row.Role))
                .ThenBy(row => row.JoinedAt)
                .ToList();

            return Result.Success<IReadOnlyList<AdminWorkspaceMemberDto>>(rows);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin workspace members query failed. WorkspaceId: {WorkspaceId}", workspaceId);
            return Result.Failure<IReadOnlyList<AdminWorkspaceMemberDto>>(
                WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    /// <summary>Owner first, then Admins, then everyone else — the order a roster is read in.</summary>
    private static int RoleRank(string role) => role.ToLowerInvariant() switch
    {
        "owner" => 0,
        "admin" => 1,
        _ => 2,
    };

    private async Task<Result<AdminWorkspaceDetailDto>> ChangeLifecycleAsync(
        Guid workspaceId,
        string reason,
        Guid actorId,
        string? correlationId,
        bool suspend,
        CancellationToken ct)
    {
        var trimmedReason = reason?.Trim() ?? string.Empty;
        if (trimmedReason.Length == 0)
        {
            return Result.Failure<AdminWorkspaceDetailDto>(
                WorkspaceAdminErrors.ReasonRequired, ErrorCodes.ValidationError);
        }

        if (trimmedReason.Length > MaxReasonLength)
        {
            return Result.Failure<AdminWorkspaceDetailDto>(
                WorkspaceAdminErrors.ReasonTooLong, ErrorCodes.ValidationError);
        }

        try
        {
            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace is null)
            {
                return Result.Failure<AdminWorkspaceDetailDto>(
                    WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            // A soft-deleted workspace has left the lifecycle: restoring it is a separate,
            // unapproved operation, so neither transition may quietly resurrect it.
            if (workspace.DeletedAt != null)
            {
                return Result.Failure<AdminWorkspaceDetailDto>(
                    WorkspaceAdminErrors.DeletedWorkspaceIsImmutable, ErrorCodes.Conflict);
            }

            if (suspend && !workspace.IsActive)
            {
                return Result.Failure<AdminWorkspaceDetailDto>(
                    WorkspaceAdminErrors.AlreadySuspended, ErrorCodes.Conflict);
            }

            if (!suspend && workspace.IsActive)
            {
                return Result.Failure<AdminWorkspaceDetailDto>(
                    WorkspaceAdminErrors.AlreadyActive, ErrorCodes.Conflict);
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;

            // Suspension flips is_active only. No workspace data is removed and no earlier
            // audit row is rewritten, so history survives every transition.
            workspace.IsActive = !suspend;
            workspace.UpdatedAt = now;
            workspace.UpdatedBy = actorId;
            _unitOfWork.WorkspaceRepository.Update(workspace);

            // Written directly rather than published: this service owns the audit store, so
            // the row lands in the same transaction as the lifecycle change (WT-210) — the
            // append-only repository shares this scope's DbContext, so that still holds.
            await _adminAuditLogRepository.AppendAsync(
                new WorkspaceAdminAction
                {
                    Id = Guid.NewGuid(),
                    SourceService = AdminAuditSources.WorkspaceService,
                    WorkspaceId = workspaceId,
                    EntityType = AdminAuditEntityTypes.Workspace,
                    EntityId = workspaceId,
                    Action = suspend
                        ? WorkspaceAdminActionTypes.Suspend
                        : WorkspaceAdminActionTypes.Reactivate,
                    Reason = trimmedReason,
                    Result = AdminAuditResults.Succeeded,
                    PerformedBy = actorId,
                    PerformedAt = now,
                    CorrelationId = correlationId,
                    BeforeSummary = JsonSerializer.Serialize(
                        new Dictionary<string, string?> { ["status"] = suspend ? "active" : "suspended" }),
                    AfterSummary = JsonSerializer.Serialize(
                        new Dictionary<string, string?> { ["status"] = suspend ? "suspended" : "active" }),
                },
                ct);

            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation(
                "System admin {ActorId} {Action} workspace {WorkspaceId}. CorrelationId: {CorrelationId}",
                actorId,
                suspend ? WorkspaceAdminActionTypes.Suspend : WorkspaceAdminActionTypes.Reactivate,
                workspaceId,
                correlationId);

            // WT-454 — after the commit, never inside it. The owner is being told about a change
            // that has already happened; a notification mesh that is down must not roll back an
            // admin action or make the endpoint fail.
            await NotifyOwnerOfLifecycleChangeAsync(workspace, trimmedReason, suspend, ct);

            var detail = await BuildDetailAsync(workspaceId, ct);
            return detail is null
                ? Result.Failure<AdminWorkspaceDetailDto>(
                    WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound)
                : Result.Success(detail);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Admin workspace lifecycle change failed. WorkspaceId: {WorkspaceId}, Suspend: {Suspend}",
                workspaceId,
                suspend);
            return Result.Failure<AdminWorkspaceDetailDto>(
                WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// Tell the workspace owner their workspace was suspended, and why — or that it is back. WT-454.
    /// </summary>
    /// <remarks>
    /// The whole of this used to be missing: ChangeLifecycleAsync wrote the row, appended the audit
    /// entry, logged, and stopped. The owner's workspace simply stopped working, with no message
    /// naming a reason and nothing to act on, so every case became a manual support mail.
    ///
    /// The reason the admin typed is carried through verbatim. It is already required and
    /// length-checked by the caller, so there is nothing to invent here — which matters, because a
    /// suspension notice explained by the system rather than by the person who suspended it is
    /// worse than none.
    ///
    /// Best-effort, and deliberately swallowing: the lifecycle change is committed by the time this
    /// runs. Same shape and same argument as NotifyMemberRoleChangedAsync.
    /// </remarks>
    private async Task NotifyOwnerOfLifecycleChangeAsync(
        Workspace workspace,
        string reason,
        bool suspend,
        CancellationToken ct)
    {
        if (_notificationClient == null) return;

        try
        {
            var workspaceName = string.IsNullOrWhiteSpace(workspace.Name) ? "your workspace" : workspace.Name;

            var request = new WarpTalk.Shared.Protos.SendNotificationRequest
            {
                UserId = workspace.OwnerId.ToString(),
                Type = suspend ? WorkspaceSuspendedNotificationType : WorkspaceReactivatedNotificationType,
                Title = suspend
                    ? $"{workspaceName} has been suspended"
                    : $"{workspaceName} is active again",
                Body = suspend
                    ? $"An administrator suspended {workspaceName}. Reason: {reason}"
                    : $"An administrator lifted the suspension on {workspaceName}. Reason: {reason}",
                // A suspended workspace cannot be opened, so the link goes to the account's own
                // workspace list rather than into the one the reader is locked out of.
                ActionUrl = suspend
                    ? "/workspace"
                    : workspace.Slug is { Length: > 0 } slug ? $"/{slug}" : "/workspace",
            };

            request.Metadata.Add("workspace_id", workspace.Id.ToString());
            request.Metadata.Add("workspace_name", workspaceName);
            // Required by the WORKSPACE_SUSPENDED schema, and omitted from the reactivation one
            // where the body already carries it and there is nothing to act on.
            if (suspend) request.Metadata.Add("reason", reason);

            await _notificationClient.SendNotificationAsync(request, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not notify owner {OwnerId} that workspace {WorkspaceId} was {Action}; the lifecycle change itself is committed.",
                workspace.OwnerId,
                workspace.Id,
                suspend ? "suspended" : "reactivated");
        }
    }

    private async Task<AdminWorkspaceDetailDto?> BuildDetailAsync(Guid workspaceId, CancellationToken ct)
    {
        var row = await _unitOfWork.WorkspaceRepository.GetAdminDetailAsync(workspaceId, ct);
        return await BuildDetailFromRowAsync(row, ct);
    }

    /// <summary>
    /// Everything after the row is found, so a lookup by slug produces exactly the detail a
    /// lookup by id does. Keyed on the row's own id rather than on whatever the caller was
    /// holding — the two must not be able to drift.
    /// </summary>
    private async Task<AdminWorkspaceDetailDto?> BuildDetailFromRowAsync(
        WorkspaceDirectoryRow? row,
        CancellationToken ct)
    {
        if (row is null) return null;

        var owner = await _authIdentityClient.GetUserByIdAsync(row.OwnerId, ct);
        // Ordered and limited in SQL by the audit repository rather than in memory, and scoped
        // by entity so a platform-wide action never leaks into a workspace's history.
        var history = await _adminAuditLogRepository.GetForEntityAsync(
            AdminAuditEntityTypes.Workspace, row.Id, LifecycleHistoryLimit, ct);

        return AdminWorkspaceMapper.ToDetail(row, owner, history);
    }

    /// <summary>
    /// Resolves owners for one page in parallel over the distinct ids. The Auth gRPC client
    /// already degrades an unreachable Auth service to null, which the mapper reports as an
    /// unresolved owner instead of inventing a name.
    /// </summary>
    private async Task<Dictionary<Guid, User>> ResolveOwnersAsync(IEnumerable<Guid> ownerIds, CancellationToken ct)
    {
        var distinctIds = ownerIds.Distinct().ToList();
        if (distinctIds.Count == 0) return new Dictionary<Guid, User>();

        var users = await Task.WhenAll(
            distinctIds.Select(async ownerId => (ownerId, user: await _authIdentityClient.GetUserByIdAsync(ownerId, ct))));

        return users
            .Where(entry => entry.user is not null)
            .ToDictionary(entry => entry.ownerId, entry => entry.user!);
    }
}
