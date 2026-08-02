using System;
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

public class UsageService : IUsageService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<UsageService> _logger;
    private readonly IUsageSettlementService _settlementService;

    public UsageService(
        IUnitOfWork unitOfWork,
        ILogger<UsageService> logger,
        IUsageSettlementService settlementService)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _settlementService = settlementService;
    }


    public Task<Result<CreditBalanceDto>> RecordUsageAsync(
        RecordUsageRequest request, CancellationToken cancellationToken = default)
    {
        return ConcurrencyRetryHelper.ExecuteWithConcurrencyRetryAsync(_unitOfWork, _logger, request.HostWorkspaceId, async () =>
        {
            if (request.CreditsConsumed <= 0)
                return Result.Failure<CreditBalanceDto>(BillingMessageConstants.ApiErrorMessages.BillingCreditsConsumedInvalid, ErrorCodes.ValidationError);

            var sub = await _unitOfWork.SubscriptionRepository.GetActiveByWorkspaceIdAsync(request.HostWorkspaceId, true, cancellationToken);

            if (sub is null)
            {
                return Result.Failure<CreditBalanceDto>(
                    BillingMessageConstants.ApiErrorMessages.BillingHostSubscriptionNotFound,
                    ErrorCodes.BillingSubscriptionNotFound);
            }

            var plan = await _unitOfWork.PlanRepository.GetByIdAsync(sub.PlanId, cancellationToken);
            if (plan is null)
                return Result.Failure<CreditBalanceDto>(ApiMessageConstants.ErrorMessages.BillingPlanNotFound, ErrorCodes.BillingPlanNotFound);



            var settlement = await _settlementService.SettleUsageChargeAsync(
                request.ToSettlementRequest(sub),
                cancellationToken);

            if (!settlement.IsSuccess)
                return Result.Failure<CreditBalanceDto>(settlement.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, settlement.ErrorCode);

            if (settlement.Value?.Applied != true)
                return Result.Failure<CreditBalanceDto>(BillingMessageConstants.ApiErrorMessages.BillingHostInsufficientCredits, ErrorCodes.BillingInsufficientCredits);

            var settlementValue = settlement.Value!;
            sub.CreditsRemaining = settlementValue.BalanceAfter ?? sub.CreditsRemaining;
            sub.ServiceState = settlementValue.ServiceState ?? sub.ServiceState;
            sub.SuspendedReason = settlementValue.SuspendedReason;

            return Result.Success(sub.ToCreditBalanceDto(request.HostWorkspaceId));
        }, cancellationToken);
    }
}
