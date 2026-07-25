using WarpTalk.BillingService.Domain.Constants;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;

using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class RealtimeSessionBillingService : IRealtimeSessionBillingService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<RealtimeSessionBillingService> _logger;
    private readonly IBillingMessagePublisher _messagePublisher;
    private readonly IRedisBillingStore _redisStore;
    private readonly IConfiguration _configuration;

    public RealtimeSessionBillingService(
        IUnitOfWork unitOfWork,
        ILogger<RealtimeSessionBillingService> logger,
        IBillingMessagePublisher messagePublisher,
        IRedisBillingStore redisStore,
        IConfiguration configuration)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _messagePublisher = messagePublisher;
        _redisStore = redisStore;
        _configuration = configuration;
    }

    public async Task<Result<Guid>> StartSessionAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var sub = await _unitOfWork.SubscriptionRepository.GetActiveByWorkspaceIdAsync(workspaceId, cancellationToken: cancellationToken);

        if (sub is null)
            return Result.Failure<Guid>(ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound, ErrorCodes.BillingSubscriptionNotFound);

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
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == request.HostWorkspaceId && s.IsActive && s.DeletedAt == null && s.CurrentPeriodEnd >= DateTime.UtcNow,
                cancellationToken);

            if (sub is null)
                return Result.Failure<CreditReservationDto>(ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound, ErrorCodes.BillingSubscriptionNotFound);

            var plan = await _unitOfWork.PlanRepository.GetByIdAsync(sub.PlanId, cancellationToken);
            if (plan == null)
                return Result.Failure<CreditReservationDto>(ApiMessageConstants.ErrorMessages.BillingPlanNotFound, ErrorCodes.BillingPlanNotFound);

            var sttRateSec = double.Parse(_configuration[BillingRateConstants.Keys.FullSttPerSecond] ?? "1.0", System.Globalization.CultureInfo.InvariantCulture);

            int cost = CreditRatesHelper.CalculateMeetingReservationCost(request.ParticipantCount, request.MediaStreamType, sttRateSec);

            if (sub.CreditsRemaining < cost)
                return Result.Failure<CreditReservationDto>(ApiMessageConstants.ErrorMessages.BillingInsufficientCredits, ErrorCodes.BillingInsufficientCredits);

            var reservation = new RedisCreditReservationDto
            {
                SubscriptionId = sub.Id,
                WorkspaceId = request.HostWorkspaceId,
                IdempotencyKey = request.IdempotencyKey,
                Amount = cost
            };

            sub.CreditsRemaining -= cost; // Reserve the amount immediately
            _unitOfWork.SubscriptionRepository.Update(sub);

            // Create Temp Log for Redis instead of directly writing to Postgres
            Guid.TryParse(request.IdempotencyKey, out var refIdVal);
            var tempLog = UsageMapper.CreateTempUsageLogDto(new CreateTempUsageLogRequest(
                SubscriptionId: sub.Id,
                UserId: sub.UserId.ToString(),
                WorkspaceId: request.HostWorkspaceId,
                UsageType: BillingMessageConstants.Notifications.Realtime.UsageTypeReservation,
                ChargeType: TransactionConstants.TransactionTypes.Consume,
                ReferenceId: refIdVal == Guid.Empty ? Guid.NewGuid() : refIdVal,
                ReferenceType: TransactionConstants.ReferenceTypes.CreditReservation,
                Quantity: 1,
                Unit: BillingMessageConstants.Notifications.Realtime.UnitSession,
                CreditsConsumed: cost,
                IdempotencyKey: request.IdempotencyKey,
                Details: BillingMessageConstants.Notifications.Realtime.ReservationDescription));

            await _redisStore.PushTempUsageLogDtoAsync(tempLog, cancellationToken);

            // Intermediate reservation is fully maintained on Redis via RedisCreditReservationDto
            await _redisStore.SetReservationAsync(reservation, TimeSpan.FromMinutes(15), cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // Publish credit update immediately when reserved
            await BillingNotificationHelper.PublishCreditUpdateAsync(
                _messagePublisher,
                _logger,
                NotificationMapper.ToCreditsUpdatedMessage(
                    request.HostWorkspaceId,
                    sub.CreditsRemaining,
                    BillingMessageConstants.Notifications.Realtime.CreditsReservedTitle,
                    string.Format(BillingMessageConstants.Notifications.Realtime.CreditsReservedBodyTemplate, cost)),
                cancellationToken);

            return Result.Success(new CreditReservationDto(
                Guid.NewGuid(),
                sub.Id,
                request.IdempotencyKey,
                cost,
                BillingMessageConstants.Notifications.Realtime.ReservationStatus,
                DateTime.UtcNow.AddMinutes(15)
            ));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorReservingCredits, request.HostWorkspaceId);
            return Result.Failure<CreditReservationDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<CreditTransactionDto>> ConfirmConsumeAsync(
        Guid workspaceId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (_, reservation) = await ReservationHelper.ValidateAndGetReservationAsync(
                new ReservationLookupRequest(
                    _redisStore,
                    _logger,
                    idempotencyKey,
                    TransactionConstants.TransactionTypes.Consume),
                cancellationToken);

            if (reservation == null)
            {
                return Result.Failure<CreditTransactionDto>(BillingMessageConstants.ApiErrorMessages.BillingReservationNotFound, ErrorCodes.BillingTransactionNotFound);
            }

            var sub = await _unitOfWork.SubscriptionRepository.GetByIdAsync(reservation.SubscriptionId, cancellationToken);
            if (sub == null || sub.WorkspaceId != workspaceId)
            {
                return Result.Failure<CreditTransactionDto>(BillingMessageConstants.ApiErrorMessages.BillingSubscriptionInvalid, ErrorCodes.BillingSubscriptionNotFound);
            }

            sub.CreditsUsedThisCycle += reservation.Amount;
            sub.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.SubscriptionRepository.Update(sub);

            Guid.TryParse(idempotencyKey, out var refId);
            
            var tempLog = UsageMapper.CreateTempUsageLogDto(new CreateTempUsageLogRequest(
                SubscriptionId: sub.Id,
                UserId: sub.UserId.ToString(),
                WorkspaceId: workspaceId,
                UsageType: BillingMessageConstants.Notifications.Realtime.UsageTypeConfirm,
                ChargeType: TransactionConstants.TransactionTypes.Consume,
                ReferenceId: refId == Guid.Empty ? null : refId,
                ReferenceType: TransactionConstants.ReferenceTypes.CreditReservation,
                Quantity: 1,
                Unit: BillingMessageConstants.Notifications.Realtime.UnitSession,
                CreditsConsumed: 0,
                IdempotencyKey: idempotencyKey + BillingMessageConstants.Notifications.Realtime.IdempotencyConfirmSuffix,
                Details: BillingMessageConstants.Notifications.Realtime.ConsumedDescription));
            
            await _redisStore.PushTempUsageLogDtoAsync(tempLog, cancellationToken);

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // Publish credit update when consumption confirmed
            await BillingNotificationHelper.PublishCreditUpdateAsync(
                _messagePublisher,
                _logger,
                NotificationMapper.ToCreditsUpdatedMessage(
                    workspaceId,
                    sub.CreditsRemaining,
                    BillingMessageConstants.Notifications.Realtime.CreditsConsumedTitle,
                    BillingMessageConstants.Notifications.Realtime.CreditsConsumedBody),
                cancellationToken);

            var dto = new CreditTransactionDto(
                Guid.NewGuid(), // Temp ID
                -reservation.Amount,
                TransactionConstants.TransactionTypes.Consume,
                BillingMessageConstants.Notifications.Realtime.ConsumedDescription,
                TransactionConstants.ReferenceTypes.CreditReservation,
                null,
                sub.CreditsRemaining,
                tempLog.CreatedAt,
                sub.WorkspaceId,
                null,
                sub.UserId,
                null
            );

            return Result.Success(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorConsumingCredits, idempotencyKey);
            return Result.Failure<CreditTransactionDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<bool>> RefundReservationAsync(
        Guid workspaceId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (_, reservation) = await ReservationHelper.ValidateAndGetReservationAsync(
                new ReservationLookupRequest(
                    _redisStore,
                    _logger,
                    idempotencyKey,
                    TransactionConstants.TransactionTypes.Refund),
                cancellationToken);

            if (reservation == null)
            {
                return Result.Failure<bool>(BillingMessageConstants.ApiErrorMessages.BillingReservationNotFound, ErrorCodes.BillingTransactionNotFound);
            }

            var sub = await _unitOfWork.SubscriptionRepository.GetByIdAsync(reservation.SubscriptionId, cancellationToken);
            if (sub == null || sub.WorkspaceId != workspaceId)
            {
                return Result.Failure<bool>(BillingMessageConstants.ApiErrorMessages.BillingSubscriptionInvalid, ErrorCodes.BillingSubscriptionNotFound);
            }

            sub.ApplyRefund(reservation.Amount);
            _unitOfWork.SubscriptionRepository.Update(sub);

            Guid.TryParse(idempotencyKey, out var refId);
            var tempLog = UsageMapper.CreateTempUsageLogDto(new CreateTempUsageLogRequest(
                SubscriptionId: sub.Id,
                UserId: sub.UserId.ToString(),
                WorkspaceId: workspaceId,
                UsageType: BillingMessageConstants.Notifications.Realtime.UsageTypeRefund,
                ChargeType: TransactionConstants.TransactionTypes.Refund,
                ReferenceId: refId == Guid.Empty ? null : refId,
                ReferenceType: TransactionConstants.ReferenceTypes.CreditReservation,
                Quantity: 1,
                Unit: BillingMessageConstants.Notifications.Realtime.UnitSession,
                CreditsConsumed: reservation.Amount,
                IdempotencyKey: idempotencyKey + BillingMessageConstants.Notifications.Realtime.IdempotencyRefundSuffix,
                Details: BillingMessageConstants.Notifications.Realtime.RefundedDescription));

            await _redisStore.PushTempUsageLogDtoAsync(tempLog, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await BillingNotificationHelper.PublishCreditUpdateAsync(
                _messagePublisher,
                _logger,
                NotificationMapper.ToCreditsUpdatedMessage(
                    workspaceId,
                    sub.CreditsRemaining,
                    BillingMessageConstants.Notifications.Realtime.CreditsRefundedTitle,
                    string.Format(BillingMessageConstants.Notifications.Realtime.CreditsRefundedBodyTemplate, reservation.Amount)),
                cancellationToken);

            return Result.Success(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorRefundingCredits, idempotencyKey);
            return Result.Failure<bool>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }


}
