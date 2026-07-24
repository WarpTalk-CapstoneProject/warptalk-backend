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
            return Result.Failure<Guid>("Subscription not found.", ErrorCodes.BillingSubscriptionNotFound);

        var sessionId = Guid.NewGuid();
        await _redisStore.SetSessionActiveAsync(sessionId, TimeSpan.FromSeconds(75), cancellationToken);

        return Result.Success(sessionId);
    }

    public async Task<Result<bool>> ProcessHeartbeatAsync(Guid sessionId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var isActive = await _redisStore.IsSessionActiveAsync(sessionId, cancellationToken);
        if (!isActive)
            return Result.Failure<bool>("Session is inactive or expired.", ErrorCodes.InvalidState);

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
                return Result.Failure<CreditReservationDto>("Subscription not found.", ErrorCodes.BillingSubscriptionNotFound);

            var plan = await _unitOfWork.PlanRepository.GetByIdAsync(sub.PlanId, cancellationToken);
            if (plan == null)
                return Result.Failure<CreditReservationDto>("Plan not found.", ErrorCodes.BillingPlanNotFound);

            var sttRateSec = double.Parse(_configuration["BillingRates:SttPerSecond"] ?? "1.0", System.Globalization.CultureInfo.InvariantCulture);

            int cost = CreditRatesHelper.CalculateMeetingReservationCost(request.ParticipantCount, request.MediaStreamType, sttRateSec);

            if (sub.CreditsRemaining < cost)
                return Result.Failure<CreditReservationDto>("Insufficient credits.", ErrorCodes.BillingInsufficientCredits);

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
            var tempLog = UsageMapper.CreateTempUsageLogDto(
                sub.Id,
                sub.UserId.ToString(),
                request.HostWorkspaceId,
                "RealtimeReservation",
                TransactionConstants.TransactionTypes.Consume,
                refIdVal == Guid.Empty ? Guid.NewGuid() : refIdVal,
                TransactionConstants.ReferenceTypes.CreditReservation,
                1,
                "session",
                cost,
                request.IdempotencyKey,
                "AI Real-time session reservation"
            );

            await _redisStore.PushTempUsageLogDtoAsync(tempLog, cancellationToken);

            // Intermediate reservation is fully maintained on Redis via RedisCreditReservationDto
            await _redisStore.SetReservationAsync(reservation, TimeSpan.FromMinutes(15), cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // Publish credit update immediately when reserved
            await PublishCreditUpdateAsync(
                NotificationMapper.ToCreditsUpdatedMessage(
                    request.HostWorkspaceId,
                    sub.CreditsRemaining,
                    "Credits Reserved",
                    $"Real-time translation session started. Reserved {cost} credits."),
                cancellationToken);

            return Result.Success(new CreditReservationDto(
                Guid.NewGuid(),
                sub.Id,
                request.IdempotencyKey,
                cost,
                "Reserved",
                DateTime.UtcNow.AddMinutes(15)
            ));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reserving credits for {WorkspaceId}", request.HostWorkspaceId);
            return Result.Failure<CreditReservationDto>("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<CreditTransactionDto>> ConfirmConsumeAsync(
        Guid workspaceId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (_, reservation) = await ValidateAndGetReservationAsync(idempotencyKey, TransactionConstants.TransactionTypes.Consume, cancellationToken);

            if (reservation == null)
            {
                return Result.Failure<CreditTransactionDto>("Reservation not found or already processed.", ErrorCodes.BillingTransactionNotFound);
            }

            var sub = await _unitOfWork.SubscriptionRepository.GetByIdAsync(reservation.SubscriptionId, cancellationToken);
            if (sub == null || sub.WorkspaceId != workspaceId)
            {
                return Result.Failure<CreditTransactionDto>("Subscription invalid.", ErrorCodes.BillingSubscriptionNotFound);
            }

            sub.CreditsUsedThisCycle += reservation.Amount;
            sub.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.SubscriptionRepository.Update(sub);

            Guid.TryParse(idempotencyKey, out var refId);
            
            var tempLog = UsageMapper.CreateTempUsageLogDto(
                sub.Id,
                sub.UserId.ToString(),
                workspaceId,
                "RealtimeConfirm",
                TransactionConstants.TransactionTypes.Consume,
                refId == Guid.Empty ? null : refId,
                TransactionConstants.ReferenceTypes.CreditReservation,
                1,
                "session",
                0, // Already consumed during reservation
                idempotencyKey + "_confirm",
                "AI Real-time consumption confirmed"
            );
            
            await _redisStore.PushTempUsageLogDtoAsync(tempLog, cancellationToken);

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // Publish credit update when consumption confirmed
            await PublishCreditUpdateAsync(
                NotificationMapper.ToCreditsUpdatedMessage(
                    workspaceId,
                    sub.CreditsRemaining,
                    "Credits Consumed",
                    "Real-time translation session finished."),
                cancellationToken);

            var dto = new CreditTransactionDto(
                Guid.NewGuid(), // Temp ID
                -reservation.Amount,
                TransactionConstants.TransactionTypes.Consume,
                "AI Real-time consumption confirmed",
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
            _logger.LogError(ex, "Error consuming credits for {IdempotencyKey}", idempotencyKey);
            return Result.Failure<CreditTransactionDto>("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<bool>> RefundReservationAsync(
        Guid workspaceId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (_, reservation) = await ValidateAndGetReservationAsync(idempotencyKey, TransactionConstants.TransactionTypes.Refund, cancellationToken);

            if (reservation == null)
            {
                return Result.Failure<bool>("Reservation not found or already processed.", ErrorCodes.BillingTransactionNotFound);
            }

            var sub = await _unitOfWork.SubscriptionRepository.GetByIdAsync(reservation.SubscriptionId, cancellationToken);
            if (sub == null || sub.WorkspaceId != workspaceId)
            {
                return Result.Failure<bool>("Subscription invalid.", ErrorCodes.BillingSubscriptionNotFound);
            }

            sub.CreditsRemaining += reservation.Amount;
            sub.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.SubscriptionRepository.Update(sub);

            Guid.TryParse(idempotencyKey, out var refId);
            var tempLog = UsageMapper.CreateTempUsageLogDto(
                sub.Id,
                sub.UserId.ToString(),
                workspaceId,
                "RealtimeRefund",
                TransactionConstants.TransactionTypes.Refund,
                refId == Guid.Empty ? null : refId,
                TransactionConstants.ReferenceTypes.CreditReservation,
                1,
                "session",
                reservation.Amount,
                idempotencyKey + "_refund",
                "AI Real-time session refunded"
            );

            await _redisStore.PushTempUsageLogDtoAsync(tempLog, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // Publish credit update on refund
            await PublishCreditUpdateAsync(
                NotificationMapper.ToCreditsUpdatedMessage(
                    workspaceId,
                    sub.CreditsRemaining,
                    "Credits Refunded",
                    $"Refunded {reservation.Amount} credits."),
                cancellationToken);

            return Result.Success(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refunding credits for {IdempotencyKey}", idempotencyKey);
            return Result.Failure<bool>("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }

    private async Task PublishCreditUpdateAsync(WarpTalk.Shared.Models.RealtimeNotificationMessage msg, CancellationToken cancellationToken)
    {
        try
        {
            await _messagePublisher.PublishAsync(Domain.Constants.BillingMessageConstants.Notifications.Channel, msg, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, Domain.Constants.BillingMessageConstants.LogMessages.FailedToPublishRealtimeCreditUpdateForWorkspace, msg.UserId);
        }
    }

    private async Task<(CreditTransaction? existingTx, RedisCreditReservationDto? reservation)> ValidateAndGetReservationAsync(
        string idempotencyKey, string transactionType, CancellationToken cancellationToken)
    {
        // CreditTransaction is now pushed to Temp Logs, so we cannot query the DB for it immediately.
        // GetAndRemoveReservationAsync is atomic, providing idempotency guarantees naturally.
        var reservation = await _redisStore.GetAndRemoveReservationAsync(idempotencyKey, cancellationToken);
        return (null, reservation);
    }
}
