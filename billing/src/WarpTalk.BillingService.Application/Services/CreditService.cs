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

namespace WarpTalk.BillingService.Application.Services;

public class CreditService : ICreditService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<CreditService> _logger;
    private readonly IUsageSettlementService _settlementService;

    public CreditService(
        IUnitOfWork unitOfWork,
        ILogger<CreditService> logger,
        IUsageSettlementService settlementService)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _settlementService = settlementService;
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
        CreditHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        var page = await _unitOfWork.CreditTransactionRepository.GetHistoryPageAsync(
            BillingQueryHelper.ToCreditTransactionHistoryFilter(query, null),
            cancellationToken);

        var dtos = page.Items.ToDtoList();

        try
        {
            var workspaceIds = BillingQueryHelper.GetWorkspaceIds(dtos, d => d.WorkspaceId);

            if (workspaceIds.Length > 0)
            {
                var workspaceNames = await _unitOfWork.CreditTransactionRepository.GetWorkspaceNamesAsync(workspaceIds, cancellationToken);
                dtos = BillingQueryHelper.ApplyWorkspaceNames(dtos, workspaceNames, d => d.WorkspaceId, (d, name) => d with { WorkspaceName = name });
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
