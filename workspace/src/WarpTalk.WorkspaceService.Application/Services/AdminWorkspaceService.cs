using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
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
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 20;
    private const int MaxReasonLength = 500;
    private const int LifecycleHistoryLimit = 50;

    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        WorkspaceLifecycleStatus.All,
        WorkspaceLifecycleStatus.Active,
        WorkspaceLifecycleStatus.Suspended,
        WorkspaceLifecycleStatus.Deleted,
    };

    private static readonly HashSet<string> AllowedSorts = new(StringComparer.OrdinalIgnoreCase)
    {
        WorkspaceDirectorySort.CreatedDesc,
        WorkspaceDirectorySort.CreatedAsc,
        WorkspaceDirectorySort.NameAsc,
        WorkspaceDirectorySort.NameDesc,
        WorkspaceDirectorySort.MembersDesc,
        WorkspaceDirectorySort.MembersAsc,
        WorkspaceDirectorySort.UpdatedDesc,
    };

    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuthIdentityClient _authIdentityClient;
    private readonly ILogger<AdminWorkspaceService> _logger;
    private readonly TimeProvider _timeProvider;

    public AdminWorkspaceService(
        IUnitOfWork unitOfWork,
        IAuthIdentityClient authIdentityClient,
        ILogger<AdminWorkspaceService> logger,
        TimeProvider? timeProvider = null)
    {
        _unitOfWork = unitOfWork;
        _authIdentityClient = authIdentityClient;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
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

        var sort = string.IsNullOrWhiteSpace(query.Sort)
            ? WorkspaceDirectorySort.CreatedDesc
            : query.Sort.Trim().ToLowerInvariant();
        if (!AllowedSorts.Contains(sort))
        {
            return Result.Failure<AdminPagedResult<AdminWorkspaceSummaryDto>>(
                WorkspaceAdminErrors.UnknownSort, ErrorCodes.ValidationError);
        }

        if (query.MinMembers is { } min && query.MaxMembers is { } max && min > max)
        {
            return Result.Failure<AdminPagedResult<AdminWorkspaceSummaryDto>>(
                WorkspaceAdminErrors.InvalidMemberCountRange, ErrorCodes.ValidationError);
        }

        var page = query.Page <= 0 ? 1 : query.Page;
        var pageSize = query.PageSize <= 0 ? DefaultPageSize : Math.Min(query.PageSize, MaxPageSize);

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

            await _unitOfWork.WorkspaceAdminActionRepository.AddAsync(
                new WorkspaceAdminAction
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    Action = suspend
                        ? WorkspaceAdminActionTypes.Suspend
                        : WorkspaceAdminActionTypes.Reactivate,
                    Reason = trimmedReason,
                    PerformedBy = actorId,
                    PerformedAt = now,
                    CorrelationId = correlationId,
                },
                ct);

            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation(
                "System admin {ActorId} {Action} workspace {WorkspaceId}. CorrelationId: {CorrelationId}",
                actorId,
                suspend ? WorkspaceAdminActionTypes.Suspend : WorkspaceAdminActionTypes.Reactivate,
                workspaceId,
                correlationId);

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

    private async Task<AdminWorkspaceDetailDto?> BuildDetailAsync(Guid workspaceId, CancellationToken ct)
    {
        var row = await _unitOfWork.WorkspaceRepository.GetAdminDetailAsync(workspaceId, ct);
        if (row is null) return null;

        var owner = await _authIdentityClient.GetUserByIdAsync(row.OwnerId, ct);
        var history = (await _unitOfWork.WorkspaceAdminActionRepository.FindAsync(
                action => action.WorkspaceId == workspaceId, "", ct))
            .OrderByDescending(action => action.PerformedAt)
            .Take(LifecycleHistoryLimit)
            .ToList();

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
