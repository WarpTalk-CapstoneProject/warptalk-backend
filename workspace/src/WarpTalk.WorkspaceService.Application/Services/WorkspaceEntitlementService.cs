using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.DTOs.Entitlements;
using WarpTalk.WorkspaceService.Application.Entitlements;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Interfaces;

namespace WarpTalk.WorkspaceService.Application.Services;

/// <summary>
/// Read side of the entitlement snapshot for the workspace "Features" page.
///
/// It REPORTS the snapshot and never resolves anything: BillingService's EntitlementResolver is the
/// only code allowed to compute an entitlement, and this service already holds its published
/// answer locally. Reading here rather than calling billing also means the page keeps working
/// through a billing outage, exactly like enforcement does.
///
/// AUDIENCE: every active member, not only Owner/Admin. The same numbers already reach members
/// through GET settings (maxActiveRoomsCeiling, maxLanguagesCeiling) and through the denial
/// messages meeting creation returns; hiding the list would only hide the explanation, not the
/// limit.
/// </summary>
public sealed class WorkspaceEntitlementService : IWorkspaceEntitlementService
{
    public const string KindFlag = "flag";
    public const string KindLimit = "limit";
    public const string KindText = "text";

    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<WorkspaceEntitlementService> _logger;

    public WorkspaceEntitlementService(IUnitOfWork unitOfWork, ILogger<WorkspaceEntitlementService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result<WorkspaceEntitlementsDto>> GetEntitlementsAsync(
        Guid workspaceId,
        Guid userId,
        CancellationToken ct = default)
    {
        try
        {
            var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
            if (workspace == null || workspace.DeletedAt != null)
            {
                return Result.Failure<WorkspaceEntitlementsDto>(
                    WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound);
            }

            var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);
            if (member == null)
            {
                return Result.Failure<WorkspaceEntitlementsDto>(
                    WorkspaceConstants.Errors.UserNotMember, ErrorCodes.Forbidden);
            }

            var snapshot = await _unitOfWork.WorkspaceEntitlementSnapshotRepository
                .GetForWorkspaceAsync(workspaceId, ct);
            if (snapshot == null)
            {
                return Result.Success(Unknown);
            }

            var entitlements = WorkspaceEntitlements.FromSnapshot(
                snapshot.EntitlementsJson, snapshot.HasActiveSubscription);
            if (!entitlements.IsKnown)
            {
                return Result.Success(Unknown);
            }

            var items = entitlements.All
                .Select(entry => new WorkspaceEntitlementDto(
                    entry.Key,
                    KindOf(entry.Value.Value),
                    entry.Value.Value,
                    entry.Value.Source,
                    entry.Value.Ceiling,
                    entry.Value.Ceiling == null ? null : entry.Value.CeilingSource))
                .ToList();

            return Result.Success(new WorkspaceEntitlementsDto(
                IsKnown: true,
                snapshot.PlanSlug,
                snapshot.HasActiveSubscription,
                DateTime.SpecifyKind(snapshot.ResolvedAt, DateTimeKind.Utc),
                items));
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error occurred while reading workspace entitlements. WorkspaceId: {WorkspaceId}, UserId: {UserId}",
                workspaceId,
                userId);
            return Result.Failure<WorkspaceEntitlementsDto>(
                WorkspaceConstants.Errors.UnexpectedError, ErrorCodes.InternalServerError);
        }
    }

    private static WorkspaceEntitlementsDto Unknown { get; } =
        new(false, null, false, null, Array.Empty<WorkspaceEntitlementDto>());

    /// <summary>Shape of a published value. Billing writes booleans as "true"/"false" and numbers
    /// with InvariantCulture, so the shape is decidable without a copy of billing's key list.</summary>
    public static string KindOf(string value)
    {
        if (bool.TryParse(value, out _))
        {
            return KindFlag;
        }

        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            ? KindLimit
            : KindText;
    }
}
