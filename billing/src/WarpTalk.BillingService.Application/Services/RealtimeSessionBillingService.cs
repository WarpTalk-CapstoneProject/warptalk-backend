using WarpTalk.BillingService.Domain.Constants;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Interfaces;

using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class RealtimeSessionBillingService : IRealtimeSessionBillingService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<RealtimeSessionBillingService> _logger;
    private readonly IRedisBillingStore _redisStore;

    public RealtimeSessionBillingService(
        IUnitOfWork unitOfWork,
        ILogger<RealtimeSessionBillingService> logger,
        IRedisBillingStore redisStore)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _redisStore = redisStore;
    }

    public async Task<Result<Guid>> StartSessionAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var sub = await _unitOfWork.SubscriptionRepository.GetActiveByWorkspaceIdAsync(workspaceId, cancellationToken: cancellationToken);

        if (sub is null)
            return Result.Failure<Guid>(ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound, ErrorCodes.BillingSubscriptionNotFound);

        if (sub.ServiceState == SubscriptionConstants.ServiceStates.Suspended)
            return Result.Failure<Guid>(BillingMessageConstants.ApiErrorMessages.BillingAiServiceSuspended, ErrorCodes.InvalidState);

        var sessionId = Guid.NewGuid();
        var setResult = await _redisStore.SetSessionActiveAsync(sessionId, TimeSpan.FromSeconds(75), cancellationToken);
        if (!setResult.IsSuccess)
            _logger.LogWarning(BillingMessageConstants.LogMessages.FailedToPublishRealtimeCreditUpdateForWorkspace, setResult.Error);

        return Result.Success(sessionId);
    }

    public async Task<Result<bool>> ProcessHeartbeatAsync(Guid sessionId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var isActiveResult = await _redisStore.IsSessionActiveAsync(sessionId, cancellationToken);
        if (!isActiveResult.IsSuccess || !isActiveResult.Value)
            return Result.Failure<bool>(BillingMessageConstants.ApiErrorMessages.BillingSessionInactive, ErrorCodes.InvalidState);

        await _redisStore.SetSessionActiveAsync(sessionId, TimeSpan.FromSeconds(75), cancellationToken);
        return Result.Success(true);
    }

    public async Task<Result<CreditReservationDto>> ReserveCreditsAsync(
        ReserveCreditsRequest request,
        CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(Result.Failure<CreditReservationDto>(
            BillingMessageConstants.DisabledFeatureMessages.CreditReservation,
            ErrorCodes.InvalidState));
    }

    public async Task<Result<CreditTransactionDto>> ConfirmConsumeAsync(
        Guid workspaceId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(Result.Failure<CreditTransactionDto>(
            BillingMessageConstants.DisabledFeatureMessages.CreditReservationConfirmation,
            ErrorCodes.InvalidState));
    }

    public async Task<Result<bool>> RefundReservationAsync(
        Guid workspaceId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(Result.Failure<bool>(
            BillingMessageConstants.DisabledFeatureMessages.CreditReservationRefund,
            ErrorCodes.InvalidState));
    }


}
