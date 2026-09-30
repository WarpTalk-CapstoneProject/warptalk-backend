using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;

using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.Shared;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.Shared.PlatformSettings;

namespace WarpTalk.BillingService.Application.Services;

public class CreditService : ICreditService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<CreditService> _logger;
    private readonly IUsageSettlementService _settlementService;
    private readonly IWorkspaceClient _workspaceClient;
    private readonly IPlatformSettings? _platformSettings;

    public CreditService(
        IUnitOfWork unitOfWork,
        ILogger<CreditService> logger,
        IUsageSettlementService settlementService,
        IWorkspaceClient workspaceClient,
        IPlatformSettings? platformSettings = null)
    {
        _platformSettings = platformSettings;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _settlementService = settlementService;
        _workspaceClient = workspaceClient;
    }



    public async Task<Result<CreditBalanceDto>> GetWorkspaceCreditsAsync(
        Guid workspaceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var subResult = await GetActiveSubscriptionAsync(_unitOfWork, workspaceId, cancellationToken);
            if (!subResult.IsSuccess)
                return Result.Failure<CreditBalanceDto>(subResult.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, subResult.ErrorCode);
            var sub = subResult.Value!;

            return Result.Success(sub.ToCreditBalanceDto(workspaceId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorGettingWorkspaceCredits, workspaceId);
            return Result.Failure<CreditBalanceDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<WorkspaceUsageByMemberDto>> GetUsageByMemberAsync(
        Guid workspaceId,
        DateTime? from,
        DateTime? to,
        CancellationToken cancellationToken = default)
    {
        // A backwards window is a caller mistake, not an empty result: silently returning
        // nothing would read as "nobody spent anything", which is the one answer a spend
        // dashboard must never give wrongly.
        if (from.HasValue && to.HasValue && from.Value > to.Value)
        {
            return Result.Failure<WorkspaceUsageByMemberDto>(
                "The start of the range must not be after its end.",
                ErrorCodes.ValidationError);
        }

        try
        {
            var rows = await _unitOfWork.UsageRecordRepository.GetUsageByMemberAsync(
                workspaceId, from, to, cancellationToken);

            var members = rows
                .Select(r => new MemberCreditUsageDto(r.UserId, r.CreditsConsumed, r.RecordCount, r.LastUsedAt))
                .ToList();

            // Summed from the SAME rows as the breakdown. Reading the total off the
            // subscription instead would answer a different question — what is left, not what
            // these people spent in this window — and an owner comparing the number above the
            // table against the sum of the table would find them disagreeing.
            var total = members.Sum(m => m.CreditsConsumed);

            return Result.Success(new WorkspaceUsageByMemberDto(workspaceId, from, to, total, members));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting per-member usage for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<WorkspaceUsageByMemberDto>(
                ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    public Task<Result<CreditTransactionDto>> ConsumeCreditsDirectlyAsync(
        Guid workspaceId, ConsumeCreditsRequest request, CancellationToken cancellationToken = default)
    {
        return ConcurrencyRetryHelper.ExecuteWithConcurrencyRetryAsync(_unitOfWork, _logger, workspaceId, async () =>
        {
            if (request.Amount <= 0)
                return Result.Failure<CreditTransactionDto>(ApiMessageConstants.ErrorMessages.BillingInvalidAmount, ErrorCodes.BillingInvalidAmount);

            var subResult = await GetActiveSubscriptionAsync(_unitOfWork, workspaceId, cancellationToken);
            if (!subResult.IsSuccess)
                return Result.Failure<CreditTransactionDto>(subResult.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, subResult.ErrorCode);
            var sub = subResult.Value!;

            var settlement = await _settlementService.SettleUsageChargeAsync(
                request.ToSettlementRequest(sub, workspaceId),
                cancellationToken);

            if (!settlement.IsSuccess)
                return Result.Failure<CreditTransactionDto>(settlement.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, settlement.ErrorCode);

            if (settlement.Value?.Applied != true)
                return Result.Failure<CreditTransactionDto>(ApiMessageConstants.ErrorMessages.BillingInsufficientCredits, ErrorCodes.BillingInsufficientCredits);

            return Result.Success(settlement.Value.ToCreditTransactionDto(request, sub, workspaceId));
        }, cancellationToken);
    }



    public async Task<Result<CreditTransactionDto>> AdjustWorkspaceCreditsAsync(
        Guid workspaceId,
        AdjustCreditsRequest request,
        Guid adminUserId,
        CancellationToken cancellationToken = default)
    {
        // Deliberately the BROAD resolution (same as consumption): a cancelled subscription still
        // inside its paid period keeps its balance, and compensating that balance is exactly what
        // this endpoint is for.
        var subResult = await GetActiveSubscriptionAsync(_unitOfWork, workspaceId, cancellationToken);
        if (!subResult.IsSuccess)
            return Result.Failure<CreditTransactionDto>(
                subResult.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError,
                subResult.ErrorCode);

        return await AdjustCreditsAsync(
            subResult.Value!.Id, request.Amount, request.Reason, adminUserId, cancellationToken);
    }

    public async Task<Result<CreditTransactionDto>> AdjustCreditsAsync(
        Guid subscriptionId,
        int amount,
        string reason,
        Guid adminUserId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var staged = await StageAdjustmentAsync(subscriptionId, amount, reason, adminUserId, cancellationToken);
            if (!staged.IsSuccess)
                return Result.Failure<CreditTransactionDto>(staged.Error!, staged.ErrorCode);

            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success(staged.Value!.Transaction.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error manually adjusting credits for SubscriptionId {SubscriptionId}", subscriptionId);
            return Result.Failure<CreditTransactionDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<StagedCreditAdjustment>> StageWorkspaceAdjustmentAsync(
        Guid workspaceId,
        int amount,
        string reason,
        Guid adminUserId,
        CancellationToken cancellationToken = default)
    {
        // Same BROAD resolution as AdjustWorkspaceCreditsAsync: a cancelled subscription still in its
        // paid period keeps its balance, and compensating that balance is what this is for.
        var subResult = await GetActiveSubscriptionAsync(_unitOfWork, workspaceId, cancellationToken);
        if (!subResult.IsSuccess)
        {
            // No live subscription: what the workspace still holds is FROZEN on the row that ended
            // (CreditFreezeService). Adjusting it is how support corrects kept credit — the one
            // manual door to a balance the policy promises never to destroy silently.
            var frozen = await LatestFrozenSubscriptionAsync(workspaceId, cancellationToken);
            if (frozen is not null)
                return await StageFrozenAdjustmentAsync(frozen, amount, reason, adminUserId, cancellationToken);

            return Result.Failure<StagedCreditAdjustment>(
                subResult.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError,
                subResult.ErrorCode);
        }

        return await StageAdjustmentAsync(subResult.Value!.Id, amount, reason, adminUserId, cancellationToken);
    }

    private async Task<Subscription?> LatestFrozenSubscriptionAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var rows = await _unitOfWork.SubscriptionRepository.FindAsync(
            s => s.WorkspaceId == workspaceId && s.DeletedAt == null && s.FrozenCredits > 0,
            cancellationToken);
        return rows?.OrderByDescending(s => s.CreditsFrozenAt).FirstOrDefault();
    }

    private async Task<Result<StagedCreditAdjustment>> StageFrozenAdjustmentAsync(
        Subscription sub, int amount, string reason, Guid adminUserId, CancellationToken cancellationToken)
    {
        if (adminUserId == Guid.Empty)
            return Result.Failure<StagedCreditAdjustment>("AdminUserId is required for audit trail.", "INVALID_REQUEST");
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure<StagedCreditAdjustment>("Adjustment reason is required for audit trail.", "INVALID_REQUEST");
        if (sub.FrozenCredits + amount < 0)
            return Result.Failure<StagedCreditAdjustment>(
                "Adjustment would make the frozen credit balance negative.", ErrorCodes.BillingInsufficientCredits);

        var frozenBefore = sub.FrozenCredits;
        sub.FrozenCredits += amount;
        sub.UpdatedAt = DateTime.UtcNow;
        _unitOfWork.SubscriptionRepository.Update(sub);

        var adjustmentTx = new CreditTransaction
        {
            Id = Guid.NewGuid(),
            SubscriptionId = sub.Id,
            WorkspaceId = sub.WorkspaceId,
            UserId = adminUserId,
            Amount = amount,
            Type = TransactionConstants.TransactionTypes.Adjustment,
            Description = reason.Trim(),
            ReferenceType = TransactionConstants.ReferenceTypes.FrozenCreditAdjustment,
            ReferenceId = sub.Id,
            // The spendable balance, which a frozen adjustment does not move.
            BalanceAfter = sub.CreditsRemaining,
            CreatedAt = DateTime.UtcNow
        };

        await _unitOfWork.CreditTransactionRepository.AddAsync(adjustmentTx, cancellationToken);
        return Result.Success(new StagedCreditAdjustment(sub, adjustmentTx, sub.CreditsRemaining, frozenBefore));
    }

    public async Task<Result<FrozenCreditsDto>> GetFrozenCreditsAsync(
        Guid workspaceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var rows = await _unitOfWork.SubscriptionRepository.FindAsync(
                s => s.WorkspaceId == workspaceId && s.DeletedAt == null,
                cancellationToken) ?? Array.Empty<Subscription>();

            var hasActive = rows.Any(s => s.IsActive);

            // The renew screen's headline: which plan ended, and when. Read from the latest row
            // whatever its balance, because an ended plan with nothing left is still an ended plan.
            string? lastPlanName = null;
            DateTime? lastEndedAt = null;
            if (!hasActive && rows.OrderByDescending(s => s.CurrentPeriodEnd).FirstOrDefault() is { } last)
            {
                lastEndedAt = CreditFreezeService.EndedAt(last);
                lastPlanName = (await _unitOfWork.Plans.GetByIdAsync(last.PlanId, cancellationToken))?.Name;
            }

            var frozen = rows.Where(s => s.FrozenCredits > 0).OrderByDescending(s => s.CreditsFrozenAt).ToList();
            if (frozen.Count == 0)
                return Result.Success(new FrozenCreditsDto(
                    workspaceId, 0, null, null, null, null, hasActive, lastPlanName, lastEndedAt));

            var latest = frozen[0];
            var graceDays = _platformSettings is null
                ? FrozenCreditDefaults.GraceDays
                : await _platformSettings.GetInt32Async(FrozenCreditDefaults.GraceDaysKey, FrozenCreditDefaults.GraceDays, ct: cancellationToken);
            var endedAt = CreditFreezeService.EndedAt(latest);
            var dormantSince = frozen.Select(s => s.FrozenCreditsDormantAt).Where(d => d.HasValue).Min();

            return Result.Success(new FrozenCreditsDto(
                workspaceId,
                frozen.Sum(s => s.FrozenCredits),
                latest.CreditsFrozenAt,
                endedAt,
                dormantSince,
                dormantSince is null ? endedAt.AddDays(graceDays) : null,
                hasActive,
                lastPlanName,
                lastEndedAt));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting frozen credits for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<FrozenCreditsDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// Validates and applies the adjustment to the tracked subscription and ledger, WITHOUT saving.
    ///
    /// Split out so the admin workspace page can record the action in the platform audit log
    /// between staging and committing — the order under which "every adjustment is audited" is a
    /// guarantee rather than a hope. <see cref="AdjustCreditsAsync"/> is this plus the save.
    /// </summary>
    private async Task<Result<StagedCreditAdjustment>> StageAdjustmentAsync(
        Guid subscriptionId,
        int amount,
        string reason,
        Guid adminUserId,
        CancellationToken cancellationToken)
    {
        if (amount == 0)
            return Result.Failure<StagedCreditAdjustment>("Adjustment amount cannot be zero.", "INVALID_REQUEST");
        if (adminUserId == Guid.Empty)
            return Result.Failure<StagedCreditAdjustment>("AdminUserId is required for audit trail.", "INVALID_REQUEST");
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure<StagedCreditAdjustment>("Adjustment reason is required for audit trail.", "INVALID_REQUEST");

        var sub = await _unitOfWork.SubscriptionRepository.GetByIdAsync(subscriptionId, cancellationToken);
        if (sub == null)
        {
            return Result.Failure<StagedCreditAdjustment>("Subscription not found.", ErrorCodes.BillingSubscriptionNotFound);
        }

        if (sub.CreditsRemaining + amount < 0)
            return Result.Failure<StagedCreditAdjustment>("Adjustment would make the credit balance negative.", ErrorCodes.BillingInsufficientCredits);

        var balanceBefore = sub.CreditsRemaining;
        sub.CreditsRemaining += amount;
        sub.UpdatedAt = DateTime.UtcNow;
        _unitOfWork.SubscriptionRepository.Update(sub);

        var adjustmentTx = new CreditTransaction
        {
            Id = Guid.NewGuid(),
            SubscriptionId = sub.Id,
            // The ledger the admin workspace page reads is filtered on workspace_id. Left unset, the
            // row was booked against Guid.Empty and the adjustment never appeared in the ledger of
            // the workspace it was made for.
            WorkspaceId = sub.WorkspaceId,
            UserId = adminUserId,
            Amount = amount,
            Type = "adjustment",
            Description = reason.Trim(),
            ReferenceType = "manual_adjustment",
            ReferenceId = null,
            BalanceAfter = sub.CreditsRemaining,
            CreatedAt = DateTime.UtcNow
        };

        await _unitOfWork.CreditTransactionRepository.AddAsync(adjustmentTx, cancellationToken);

        return Result.Success(new StagedCreditAdjustment(sub, adjustmentTx, balanceBefore));
    }

    public async Task<Result<PaginatedResponse<CreditTransactionDto>>> GetCreditHistoryAsync(
        Guid workspaceId,
        CreditHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        var subs = await _unitOfWork.SubscriptionRepository.FindAsync(
            s => s.WorkspaceId == workspaceId && s.DeletedAt == null,
            cancellationToken);

        var subIds = subs.Select(s => s.Id).ToList();
        if (subIds.Count == 0)
            return Result.Failure<PaginatedResponse<CreditTransactionDto>>(
                ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound,
                ErrorCodes.BillingSubscriptionNotFound);

        var page = await _unitOfWork.CreditTransactionRepository.GetHistoryPageAsync(
            BillingQueryHelper.ToCreditTransactionHistoryFilter(query, subIds),
            cancellationToken);

        return Result.Success(PaginatedResponse<CreditTransactionDto>.Create(
            page.Items.ToDtoList(workspaceId), page.TotalCount, page.PageNumber, page.PageSize));
    }

    public async Task<Result<PaginatedResponse<CreditTransactionDto>>> GetGlobalCreditHistoryAsync(
        GlobalCreditHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!AdminSort.TryResolve(query.Sort, CreditHistorySorts.All, CreditHistorySorts.CreatedDesc, out var sort))
        {
            return Result.Failure<PaginatedResponse<CreditTransactionDto>>(
                $"Unknown sort. Expected one of: {string.Join(", ", CreditHistorySorts.All)}.",
                ErrorCodes.ValidationError);
        }

        var page = await _unitOfWork.CreditTransactionRepository.GetHistoryPageAsync(
            BillingQueryHelper.ToCreditTransactionHistoryFilter(query, null, query.Search, sort),
            cancellationToken);

        var dtos = page.Items.ToDtoList();

        try
        {
            var workspaceIds = BillingQueryHelper.GetWorkspaceIds(dtos, d => d.WorkspaceId);

            if (workspaceIds.Length > 0)
            {
                var namesResult = await _workspaceClient.GetWorkspaceNamesAsync(workspaceIds, cancellationToken);
                if (namesResult.IsSuccess)
                    dtos = BillingQueryHelper.ApplyWorkspaceNames(dtos, namesResult.Value!, d => d.WorkspaceId, (d, name) => d with { WorkspaceName = name });
                else
                    _logger.LogWarning(BillingMessageConstants.LogMessages.FailedToResolveWorkspaceNames);
            }
        }
        catch (Exception wsEx)
        {
            _logger.LogWarning(wsEx, BillingMessageConstants.LogMessages.FailedToResolveWorkspaceNames);
        }

        return Result.Success(PaginatedResponse<CreditTransactionDto>.Create(dtos, page.TotalCount, page.PageNumber, page.PageSize));
    }

    private static async Task<Result<Subscription>> GetActiveSubscriptionAsync(
        IUnitOfWork unitOfWork,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var sub = await unitOfWork.SubscriptionRepository.GetActiveByWorkspaceIdAsync(workspaceId, cancellationToken: cancellationToken);
        if (sub is null)
            return Result.Failure<Subscription>(
                ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound,
                ErrorCodes.BillingSubscriptionNotFound);

        return Result.Success(sub);
    }
}
