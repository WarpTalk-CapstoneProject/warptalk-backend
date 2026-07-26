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
    private readonly IBillingRateService _rateService;
    private readonly IUsageSettlementService _settlementService;

    public UsageService(
        IUnitOfWork unitOfWork,
        ILogger<UsageService> logger,
        IBillingRateService rateService,
        IUsageSettlementService settlementService)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _rateService = rateService;
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

    public async Task<Result<bool>> LogUsageOnlyAsync(
        RecordUsageRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == request.HostWorkspaceId && s.DeletedAt == null,
                cancellationToken);

            if (sub is null)
                return Result.Failure<bool>(ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound, ErrorCodes.BillingSubscriptionNotFound);

            var usage = request.ToUsageRecord(sub);
            await _unitOfWork.UsageRecordRepository.AddAsync(usage, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorLoggingUsageRecord, request.HostWorkspaceId);
            return Result.Failure<bool>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    public Task<Result<CreditBalanceDto>> ChargeVoiceCloneAsync(ChargeVoiceCloneRequest request, CancellationToken cancellationToken = default)
    {
        return RecordUsageAsync(request.ToRecordUsageRequest(), cancellationToken);
    }

    public async Task<Result<CreditBalanceDto>> ChargeAiAssistantAsync(ChargeAiAssistantRequest request, CancellationToken cancellationToken = default)
    {
        // Fetch current Admin-configurable rates at call-time so rate changes take effect immediately
        var ratesResult = await _rateService.GetServiceRatesAsync(cancellationToken);
        if (!ratesResult.IsSuccess)
            return Result.Failure<CreditBalanceDto>(ratesResult.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, ratesResult.ErrorCode);

        var rates = ratesResult.Value!;
        var recordRequest = request.ToRecordUsageRequest(
            inputRatePer1KTokens: rates.AiAssistantInputPer1000Tokens,
            outputRatePer1KTokens: rates.AiAssistantOutputPer1000Tokens
        );
        return await RecordUsageAsync(recordRequest, cancellationToken);
    }

    public Task<Result<CreditBalanceDto>> ChargeDocumentTranslationAsync(ChargeDocumentTranslationRequest request, CancellationToken cancellationToken = default)
    {
        return RecordUsageAsync(request.ToRecordUsageRequest(), cancellationToken);
    }
}
