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
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class UsageService : IUsageService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<UsageService> _logger;

    public UsageService(
        IUnitOfWork unitOfWork,
        ILogger<UsageService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public int CalculateCreditCost(int audioSeconds, int tokenCount, int gpuInferenceMs, bool isVoiceClone, Plan plan)
    {
        // 1. Speech-to-Text: 1.0 Credit / Second
        double cost = audioSeconds * 1.0;

        // 2. Translation: 1.0 Credit / 100 characters
        cost += (tokenCount / 100.0) * 1.0;

        // 3. Text-to-Speech: 1.0 Credit / Second (Neural) or 1.5 Credits / Second (Cloned)
        double ttsSeconds = gpuInferenceMs / 1000.0;
        double ttsRate = isVoiceClone ? 1.5 : 1.0;
        cost += ttsSeconds * ttsRate;

        if (cost <= 0 && (audioSeconds > 0 || tokenCount > 0 || gpuInferenceMs > 0))
        {
            return 1;
        }

        return (int)Math.Max(1, Math.Ceiling(cost));
    }

    public Task<Result<CreditBalanceDto>> RecordUsageAsync(
        RecordUsageRequest request, CancellationToken cancellationToken = default)
    {
        return ConcurrencyRetryHelper.ExecuteWithConcurrencyRetryAsync(_unitOfWork, _logger, request.HostWorkspaceId, async () =>
        {
            if (request.CreditsConsumed <= 0)
                return Result.Failure<CreditBalanceDto>("Credits consumed must be greater than zero.", ErrorCodes.ValidationError);

            var sub = await _unitOfWork.SubscriptionRepository.GetActiveByWorkspaceIdAsync(request.HostWorkspaceId, true, cancellationToken);

            if (sub is null)
            {
                return Result.Failure<CreditBalanceDto>(
                    "No active subscription found for the host workspace.",
                    ErrorCodes.BillingSubscriptionNotFound);
            }

            var plan = await _unitOfWork.PlanRepository.GetByIdAsync(sub.PlanId, cancellationToken);
            if (plan is null)
                return Result.Failure<CreditBalanceDto>("Plan not found.", ErrorCodes.BillingPlanNotFound);



            if (sub.CreditsRemaining < request.CreditsConsumed)
            {
                return Result.Failure<CreditBalanceDto>(
                    "Insufficient credits in the host workspace.",
                    ErrorCodes.BillingInsufficientCredits);
            }

            sub.CreditsRemaining -= request.CreditsConsumed;
            sub.CreditsUsedThisCycle += request.CreditsConsumed;
            sub.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.SubscriptionRepository.Update(sub);

            // 1. Create Transaction (Accounting)
            var tx = request.ToCreditTransaction(sub);
            await _unitOfWork.CreditTransactionRepository.AddAsync(tx, cancellationToken);

            // 2. Create Usage Record (Analytics)
            var usage = request.ToUsageRecord(sub);
            await _unitOfWork.UsageRecordRepository.AddAsync(usage, cancellationToken);

            await _unitOfWork.SaveChangesAsync(cancellationToken);

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
                return Result.Failure<bool>("No active subscription found.", ErrorCodes.BillingSubscriptionNotFound);

            var usage = request.ToUsageRecord(sub);
            await _unitOfWork.UsageRecordRepository.AddAsync(usage, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error logging usage record for workspace {WorkspaceId}", request.HostWorkspaceId);
            return Result.Failure<bool>("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }
}
