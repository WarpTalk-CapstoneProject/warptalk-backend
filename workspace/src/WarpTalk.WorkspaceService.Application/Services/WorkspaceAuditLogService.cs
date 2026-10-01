using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceAuditLog;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Extensions;
using WarpTalk.WorkspaceService.Domain.Interfaces;

namespace WarpTalk.WorkspaceService.Application.Services;

/// <summary>
/// Tenant-facing read over the platform audit log (workspace.workspace_admin_actions).
///
/// That store only records actions taken by WarpTalk platform administrators — nothing a
/// workspace member does is written there. So the tenant view is a projection with three rules:
/// <list type="number">
///   <item>Scope is forced from the route: WorkspaceId is always the caller's verified workspace.</item>
///   <item>Only reviewed categories (<see cref="TenantVisibleEntityTypes"/>) and only succeeded
///   actions are returned. A new category published by another service stays hidden until
///   someone decides a tenant should see it.</item>
///   <item>The actor is redacted to "WarpTalk staff", and the free-text reason and correlation id
///   are withheld: both were written for the internal trail, not for the customer.</item>
/// </list>
/// </summary>
public class WorkspaceAuditLogService : IWorkspaceAuditLogService
{
    public const string StaffActorType = "staff";
    public const string StaffActorDisplayName = "WarpTalk staff";

    /// <summary>
    /// Categories a workspace Owner/Admin may see about their own workspace. Workspace lifecycle
    /// (suspend / reactivate / delete) and credit adjustments are actions on the tenant itself.
    /// Platform accounts ("user") are about an individual, not the tenant, and pricing / rates /
    /// glossary / notifications are platform-wide.
    /// </summary>
    public static readonly IReadOnlyCollection<string> TenantVisibleEntityTypes =
    [
        AdminAuditEntityTypes.Workspace,
        AdminAuditEntityTypes.CreditAdjustment,
    ];

    private readonly IUnitOfWork _unitOfWork;
    private readonly IAdminAuditLogRepository _repository;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly ILogger<WorkspaceAuditLogService> _logger;

    public WorkspaceAuditLogService(
        IUnitOfWork unitOfWork,
        IAdminAuditLogRepository repository,
        IAuthIdentityClient authIdentity,
        ILogger<WorkspaceAuditLogService> logger)
    {
        _unitOfWork = unitOfWork;
        _repository = repository;
        _authIdentity = authIdentity;
        _logger = logger;
    }

    public async Task<Result<AdminPagedResult<WorkspaceAuditLogEntryDto>>> QueryAsync(
        Guid workspaceId,
        Guid userId,
        WorkspaceAuditLogQuery query,
        CancellationToken ct = default)
    {
        try
        {
            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
            if (member is null)
            {
                return Result.Failure<AdminPagedResult<WorkspaceAuditLogEntryDto>>(
                    WorkspaceConstants.Errors.UserNotActiveMember, ErrorCodes.Forbidden);
            }

            var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);
            if (!roleName.IsOwnerOrAdmin())
            {
                return Result.Failure<AdminPagedResult<WorkspaceAuditLogEntryDto>>(
                    WorkspaceConstants.Errors.OnlyOwnerAdminCanViewAuditLog, ErrorCodes.Forbidden);
            }

            if (AdminAuditLogService.ValidateRange(query.From, query.To) is { } rangeError)
            {
                return Result.Failure<AdminPagedResult<WorkspaceAuditLogEntryDto>>(
                    rangeError, ErrorCodes.ValidationError);
            }

            var (page, pageSize) = query.Normalize();
            var entityType = string.IsNullOrWhiteSpace(query.EntityType) ? null : query.EntityType.Trim();

            var (rows, total) = await _repository.QueryAsync(
                new AdminAuditLogFilter(
                    page,
                    pageSize,
                    ActorId: null,
                    Action: string.IsNullOrWhiteSpace(query.Action) ? null : query.Action.Trim(),
                    EntityType: entityType,
                    EntityId: null,
                    WorkspaceId: workspaceId,
                    SourceService: null,
                    Result: AdminAuditResults.Succeeded,
                    From: AdminAuditLogService.ToUtc(query.From),
                    To: AdminAuditLogService.ToUtc(query.To),
                    EntityTypes: TenantVisibleEntityTypes),
                ct);

            var items = rows
                // Belt and braces: the repository already filtered on these, but a row that slipped
                // through would disclose another tenant's history.
                .Where(row => row.WorkspaceId == workspaceId && TenantVisibleEntityTypes.Contains(row.EntityType))
                .Select(ToDto)
                .ToList();

            return Result.Success(new AdminPagedResult<WorkspaceAuditLogEntryDto>(items, page, pageSize, total));
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Workspace audit log query failed. WorkspaceId: {WorkspaceId}, UserId: {UserId}", workspaceId, userId);
            return Result.Failure<AdminPagedResult<WorkspaceAuditLogEntryDto>>(
                WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    public static WorkspaceAuditLogEntryDto ToDto(WorkspaceAdminAction row)
    {
        // Reuse the admin mapping so summaries go through the same read-time redaction.
        var admin = AdminAuditLogService.ToDto(row);
        return new WorkspaceAuditLogEntryDto(
            admin.Id,
            admin.Action,
            admin.Entity.Type,
            admin.Entity.Id,
            StaffActorType,
            StaffActorDisplayName,
            admin.PerformedAt,
            admin.BeforeSummary,
            admin.AfterSummary);
    }
}
