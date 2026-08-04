using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;
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
    private readonly IUsageRateCardResolverService _rateCardResolver;

    public UsageService(
        IUnitOfWork unitOfWork,
        ILogger<UsageService> logger,
        IUsageSettlementService settlementService,
        IUsageRateCardResolverService rateCardResolver)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _settlementService = settlementService;
        _rateCardResolver = rateCardResolver;
    }


    public Task<Result<CreditBalanceDto>> RecordUsageAsync(
        RecordUsageRequest request, CancellationToken cancellationToken = default)
    {
        return ConcurrencyRetryHelper.ExecuteWithConcurrencyRetryAsync(_unitOfWork, _logger, request.HostWorkspaceId, async () =>
        {
            if (request.CreditsConsumed <= 0)
                return Result.Failure<CreditBalanceDto>(BillingMessageConstants.ApiErrorMessages.BillingCreditsConsumedInvalid, ErrorCodes.ValidationError);

            var sub = await _unitOfWork.SubscriptionRepository.GetActiveByWorkspaceIdAsync(request.HostWorkspaceId, includePlan: true, cancellationToken: cancellationToken);

            if (sub is null)
            {
                return Result.Failure<CreditBalanceDto>(
                    BillingMessageConstants.ApiErrorMessages.BillingHostSubscriptionNotFound,
                    ErrorCodes.BillingSubscriptionNotFound);
            }

            if (ShouldSkipCharge(request.Details))
            {
                return Result.Success(sub.ToCreditBalanceDto(request.HostWorkspaceId));
            }

            var plan = await _unitOfWork.Plans.GetByIdAsync(sub.PlanId, cancellationToken);
            if (plan is null)
                return Result.Failure<CreditBalanceDto>(ApiMessageConstants.ErrorMessages.BillingPlanNotFound, ErrorCodes.BillingPlanNotFound);

            var settlementRequest = request.ToSettlementRequest(sub);
            var rateResult = await _rateCardResolver.ResolveRateCardAsync(
                settlementRequest.ChargeType, settlementRequest.Unit, "VND", null, null, cancellationToken);

            if (rateResult.IsSuccess && rateResult.Value != null)
            {
                settlementRequest = settlementRequest with
                {
                    PricingRateCardId = rateResult.Value.Id,
                    UnitPriceSnapshot = rateResult.Value.UnitPrice
                };
            }
            else
            {
                _logger.LogError("Rate card not found. ChargeType={ChargeType}, Unit={Unit}. Event dropped.", settlementRequest.ChargeType, settlementRequest.Unit);
                return Result.Failure<CreditBalanceDto>("Rate card not found", "RATE_CARD_NOT_FOUND");
            }

            var settlement = await _settlementService.SettleUsageChargeAsync(
                settlementRequest,
                cancellationToken);

            if (!settlement.IsSuccess)
                return Result.Failure<CreditBalanceDto>(settlement.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, settlement.ErrorCode);

            var settlementValue = settlement.Value!;

            if (!settlementValue.Applied)
            {
                if (settlementValue.TransactionId.HasValue)
                {
                    // Idempotency triggered, transaction already exists
                    _logger.LogInformation("Idempotency triggered for RecordUsageRequest. IdempotencyKey={IdempotencyKey}", settlementRequest.IdempotencyKey);
                }
                else
                {
                    return Result.Failure<CreditBalanceDto>(BillingMessageConstants.ApiErrorMessages.BillingHostInsufficientCredits, ErrorCodes.BillingInsufficientCredits);
                }
            }

            sub.CreditsRemaining = settlementValue.BalanceAfter ?? sub.CreditsRemaining;
            sub.ServiceState = settlementValue.ServiceState ?? sub.ServiceState;
            sub.SuspendedReason = settlementValue.SuspendedReason;

            return Result.Success(sub.ToCreditBalanceDto(request.HostWorkspaceId));
        }, cancellationToken);
    }

    private static bool ShouldSkipCharge(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
            return false;

        try
        {
            var json = JsonNode.Parse(details);
            var sourceLanguage = json?["source_lang"]?.ToString();
            var targetLanguage = json?["target_lang"]?.ToString();

            if (!string.IsNullOrEmpty(sourceLanguage) &&
                sourceLanguage.Equals(targetLanguage, StringComparison.OrdinalIgnoreCase))
                return true;

            return json?["cache_hit"]?.GetValue<bool?>() == true;
        }
        catch
        {
            return false;
        }
    }
}
