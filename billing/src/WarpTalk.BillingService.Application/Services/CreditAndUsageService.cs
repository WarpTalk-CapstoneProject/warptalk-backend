using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using NotificationClient = WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient;
using NotificationRequest = WarpTalk.Shared.Protos.SendNotificationRequest;

namespace WarpTalk.BillingService.Application.Services;

public class CreditAndUsageService : ICreditAndUsageService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<CreditAndUsageService> _logger;
    private readonly IBillingMessagePublisher _messagePublisher;
    private readonly IRedisBillingStore _redisStore;
    private readonly IConfiguration _configuration;
    private readonly NotificationClient? _notificationClient;

    public CreditAndUsageService(
        IUnitOfWork unitOfWork,
        ILogger<CreditAndUsageService> logger,
        IBillingMessagePublisher messagePublisher,
        IRedisBillingStore redisStore,
        IConfiguration configuration,
        NotificationClient? notificationClient = null)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _messagePublisher = messagePublisher;
        _redisStore = redisStore;
        _configuration = configuration;
        _notificationClient = notificationClient;
    }

    // --- Session Heartbeat ---

    public async Task<Result<Guid>> StartSessionAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
            s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null,
            cancellationToken);

        if (sub is null)
            return Result.Failure<Guid>("Subscription not found.", ErrorCodes.BillingSubscriptionNotFound);

        var sessionId = Guid.NewGuid();
        // 15s active TTL + 60s Grace Period = 75s total TTL
        await _redisStore.SetSessionActiveAsync(sessionId, TimeSpan.FromSeconds(75), cancellationToken);

        return Result.Success(sessionId);
    }

    public async Task<Result<bool>> ProcessHeartbeatAsync(Guid sessionId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        // Just refresh the TTL in Redis (15s active + 60s grace = 75s)
        await _redisStore.SetSessionActiveAsync(sessionId, TimeSpan.FromSeconds(75), cancellationToken);

        return Result.Success(true);
    }

    // --- Cost Calculation ---

    public int CalculateCreditCost(int audioSeconds, int tokenCount, int gpuInferenceMs, bool isVoiceClone, Plan plan)
    {
        var sttRateMin = double.Parse(_configuration["BillingRates:SttPerMinute"] ?? "15.0", System.Globalization.CultureInfo.InvariantCulture);
        var transRateMin = double.Parse(_configuration["BillingRates:TranslationPerMinute"] ?? "15.0", System.Globalization.CultureInfo.InvariantCulture);
        var ttsRateMin = double.Parse(_configuration["BillingRates:StandardTtsPerMinute"] ?? "15.0", System.Globalization.CultureInfo.InvariantCulture);
        var vcRateMin = double.Parse(_configuration["BillingRates:VoiceClonePerMinute"] ?? "40.0", System.Globalization.CultureInfo.InvariantCulture);

        double ratePerMinute = 0;
        if (isVoiceClone)
        {
            ratePerMinute = vcRateMin;
        }
        else
        {
            if (audioSeconds > 0)
            {
                ratePerMinute += sttRateMin;
            }
            if (tokenCount > 0)
            {
                ratePerMinute += transRateMin;
            }
            if (gpuInferenceMs > 0)
            {
                ratePerMinute += ttsRateMin;
            }
        }

        double baseCost = (audioSeconds / 60.0) * ratePerMinute;
        if (baseCost <= 0 && (audioSeconds > 0 || tokenCount > 0 || gpuInferenceMs > 0))
        {
            return 1;
        }

        return (int)Math.Max(1, Math.Ceiling(baseCost));
    }

    // --- Credit Management ---

    public async Task<Result<CreditBalanceDto>> GetWorkspaceCreditsAsync(
        Guid workspaceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null,
                cancellationToken);

            if (sub is null)
                return Result.Failure<CreditBalanceDto>(
                    "No active subscription found for this workspace.",
                    ErrorCodes.BillingSubscriptionNotFound);

            return Result.Success(sub.ToCreditBalanceDto(workspaceId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting workspace credits for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<CreditBalanceDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public Task<Result<CreditTransactionDto>> ConsumeCreditsAsync(
        Guid workspaceId, ConsumeCreditsRequest request, CancellationToken cancellationToken = default)
    {
        return ExecuteWithConcurrencyRetryAsync(workspaceId, async () =>
        {
            if (request.Amount <= 0)
                return Result.Failure<CreditTransactionDto>("Amount must be greater than zero.", "INVALID_REQUEST");

            var sub = await GetActiveSubscriptionAsync(workspaceId, true, cancellationToken);

            if (sub is null)
                return Result.Failure<CreditTransactionDto>(
                    "No active subscription found for this workspace.",
                    ErrorCodes.BillingSubscriptionNotFound);

            if (sub.CreditsRemaining < request.Amount)
            {
                return Result.Failure<CreditTransactionDto>(
                    "Insufficient credits.",
                    ErrorCodes.BillingInsufficientCredits);
            }

            sub.CreditsRemaining -= request.Amount;
            sub.CreditsUsedThisCycle += request.Amount;
            sub.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.SubscriptionRepository.Update(sub);

            var tx = request.ToEntity(sub);

            var usage = new WarpTalk.BillingService.Domain.Entities.UsageRecord
            {
                Id = Guid.NewGuid(),
                SubscriptionId = sub.Id,
                UserId = sub.UserId,
                WorkspaceId = sub.WorkspaceId,
                TranslationRoomId = request.ReferenceId,
                UsageType = request.ReferenceType,
                Unit = "request",
                Quantity = 1,
                CreditsConsumed = request.Amount,
                RecordedAt = DateTime.UtcNow
            };

            var snapshot = new WarpTalk.BillingService.Domain.Entities.CreditBalanceSnapshot
            {
                Id = Guid.NewGuid(),
                SubscriptionId = sub.Id,
                CreditsRemaining = sub.CreditsRemaining,
                CreditsUsedThisCycle = sub.CreditsUsedThisCycle,
                SnapshotAt = DateTime.UtcNow
            };

            await _unitOfWork.CreditTransactionRepository.AddAsync(tx, cancellationToken);
            await _unitOfWork.UsageRecordRepository.AddAsync(usage, cancellationToken);
            await _unitOfWork.CreditBalanceSnapshotRepository.AddAsync(snapshot, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await PublishCreditUpdateAsync(workspaceId, sub.CreditsRemaining,
                "Credits Consumed", $"You have consumed {request.Amount} credits.", cancellationToken);

            return Result.Success(tx.ToDto());
        }, cancellationToken);
    }

    public Task<Result<CreditBalanceDto>> TopUpCreditsAsync(
        Guid workspaceId, TopUpRequest request, CancellationToken cancellationToken = default)
    {
        return ExecuteWithConcurrencyRetryAsync(workspaceId, async () =>
        {
            if (request.Amount <= 0)
                return Result.Failure<CreditBalanceDto>("Amount must be greater than zero.", "INVALID_REQUEST");

            var sub = await GetActiveSubscriptionAsync(workspaceId, false, cancellationToken);

            if (sub is null)
                return Result.Failure<CreditBalanceDto>(
                    "No active subscription found for this workspace.",
                    ErrorCodes.BillingSubscriptionNotFound);

            sub.CreditsRemaining += request.Amount;
            sub.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.SubscriptionRepository.Update(sub);

            var tx = request.ToEntity(sub);

            await _unitOfWork.CreditTransactionRepository.AddAsync(tx, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await PublishCreditUpdateAsync(workspaceId, sub.CreditsRemaining,
                "Credits Topped Up", $"You have successfully added {request.Amount} credits.", cancellationToken);

            return Result.Success(sub.ToCreditBalanceDto(workspaceId));
        }, cancellationToken);
    }

    public async Task<Result<PagedResult<CreditTransactionDto>>> GetCreditHistoryAsync(
        Guid workspaceId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default,
        string? type = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        int? minAmount = null,
        int? maxAmount = null)
    {
        try
        {
            // Removed active subscription check so transaction history is always accessible

            var size = pageSize > 0 ? pageSize : 20;
            var skip = ((pageNumber > 0 ? pageNumber : 1) - 1) * size;

            System.Linq.Expressions.Expression<Func<WarpTalk.BillingService.Domain.Entities.CreditTransaction, bool>> predicate = t =>
                t.WorkspaceId == workspaceId &&
                (string.IsNullOrEmpty(type) || t.Type == type) &&
                (!fromDate.HasValue || t.CreatedAt >= fromDate.Value) &&
                (!toDate.HasValue || t.CreatedAt <= toDate.Value) &&
                (!minAmount.HasValue || Math.Abs(t.Amount) >= minAmount.Value) &&
                (!maxAmount.HasValue || Math.Abs(t.Amount) <= maxAmount.Value);

            var items = await _unitOfWork.CreditTransactionRepository.GetPagedAsync(
                predicate,
                skip, size,
                q => q.OrderByDescending(t => t.CreatedAt),
                cancellationToken);

            var total = await _unitOfWork.CreditTransactionRepository.CountAsync(
                predicate,
                cancellationToken);

            return Result.Success(new PagedResult<CreditTransactionDto>(
                total,
                items.Select(t => t.ToDto())));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting credit history for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<PagedResult<CreditTransactionDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task TakeSnapshotAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.GetByIdAsync(subscriptionId, cancellationToken)
                ?? throw new InvalidOperationException($"Subscription {subscriptionId} not found.");

            var snapshot = new CreditBalanceSnapshot
            {
                SubscriptionId = subscriptionId,
                CreditsRemaining = sub.CreditsRemaining,
                CreditsUsedThisCycle = sub.CreditsUsedThisCycle,
                SnapshotAt = DateTime.UtcNow
            };

            await _unitOfWork.CreditBalanceSnapshotRepository.AddAsync(snapshot, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error taking snapshot for SubscriptionId {SubscriptionId}", subscriptionId);
            throw;
        }
    }

    public Task<Result<CreditBalanceDto>> RecordUsageAsync(
        RecordUsageRequest request, CancellationToken cancellationToken = default)
    {
        return ExecuteWithConcurrencyRetryAsync(request.HostWorkspaceId, async () =>
        {
            if (request.CreditsConsumed <= 0)
                return Result.Failure<CreditBalanceDto>("Credits consumed must be greater than zero.", "INVALID_REQUEST");

            var sub = await GetActiveSubscriptionAsync(request.HostWorkspaceId, true, cancellationToken);

            if (sub is null)
            {
                return Result.Failure<CreditBalanceDto>(
                    "No active subscription found for the host workspace.",
                    ErrorCodes.BillingSubscriptionNotFound);
            }

            // --- Feature Gate: block unsupported features by plan ---
            var plan = await _unitOfWork.PlanRepository.GetByIdAsync(sub.PlanId, cancellationToken);
            if (plan is null)
                return Result.Failure<CreditBalanceDto>("Plan not found.", ErrorCodes.BillingPlanNotFound);

            if (request.UsageType.Contains("voice_clone", StringComparison.OrdinalIgnoreCase))
            {
                if (!plan.VoiceCloneEnabled)
                {
                    return Result.Failure<CreditBalanceDto>(
                        $"Voice clone is not available on the '{plan.Name}' plan. Please upgrade.",
                        "FEATURE_NOT_AVAILABLE");
                }

                if (plan.VoiceCloneLimitMins > 0)
                {
                    var usedMins = await GetVoiceCloneMinutesUsedThisCycleAsync(sub.Id, sub.CurrentPeriodStart, sub.CurrentPeriodEnd, cancellationToken);
                    if (usedMins >= plan.VoiceCloneLimitMins)
                    {
                        return Result.Failure<CreditBalanceDto>(
                            $"Voice clone monthly limit of {plan.VoiceCloneLimitMins} minutes exceeded for the '{plan.Name}' plan.",
                            "VOICE_CLONE_LIMIT_EXCEEDED");
                    }
                }
            }

            if (sub.CreditsRemaining < request.CreditsConsumed)
            {
                return Result.Failure<CreditBalanceDto>(
                    "Insufficient credits in the host workspace.",
                    ErrorCodes.BillingInsufficientCredits);
            }

            // Deduct credits
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

            // Save atomically
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // Publish Realtime update for the Host
            await PublishCreditUpdateAsync(request.HostWorkspaceId, sub.CreditsRemaining,
                "Credits Deducted", $"Host-pays: {request.CreditsConsumed} credits were deducted for {request.UsageType}.", cancellationToken);

            return Result.Success(sub.ToCreditBalanceDto(request.HostWorkspaceId));
        }, cancellationToken);
    }

    public async Task<Result<CreditReservationDto>> ReserveCreditsAsync(
        ReserveCreditsRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // 1. Idempotency Check
            var existingReserve = await _unitOfWork.CreditTransactionRepository.FirstOrDefaultAsync(
                tx => tx.CorrelationId == request.IdempotencyKey && tx.Type == "reserve",
                cancellationToken);

            if (existingReserve != null)
            {
                var existingRes = new RedisCreditReservation { SubscriptionId = existingReserve.SubscriptionId, IdempotencyKey = existingReserve.CorrelationId!, Amount = existingReserve.Amount };
                return Result.Success(existingRes.ToDto());
            }

            var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == request.HostWorkspaceId && s.IsActive && s.DeletedAt == null && s.CurrentPeriodEnd >= DateTime.UtcNow,
                cancellationToken);

            if (sub is null)
                return Result.Failure<CreditReservationDto>("Subscription not found.", ErrorCodes.BillingSubscriptionNotFound);

            var plan = await _unitOfWork.PlanRepository.GetByIdAsync(sub.PlanId, cancellationToken);
            if (plan == null)
                return Result.Failure<CreditReservationDto>("Plan not found.", "PLAN_NOT_FOUND");

            // --- Feature Gate: hard-block voice clone for unsupported plans ---
            if (request.IsVoiceClone)
            {
                if (!plan.VoiceCloneEnabled)
                {
                    return Result.Failure<CreditReservationDto>(
                        $"Voice clone is not available on the '{plan.Name}' plan. Please upgrade.",
                        "FEATURE_NOT_AVAILABLE");
                }

                if (plan.VoiceCloneLimitMins > 0)
                {
                    var usedMins = await GetVoiceCloneMinutesUsedThisCycleAsync(sub.Id, sub.CurrentPeriodStart, sub.CurrentPeriodEnd, cancellationToken);
                    if (usedMins >= plan.VoiceCloneLimitMins)
                    {
                        return Result.Failure<CreditReservationDto>(
                            $"Voice clone monthly limit of {plan.VoiceCloneLimitMins} minutes exceeded for the '{plan.Name}' plan.",
                            "VOICE_CLONE_LIMIT_EXCEEDED");
                    }
                }
            }

            var cost = CalculateCreditCost(request.AudioSeconds, request.TokenCount, request.GpuInferenceMs, request.IsVoiceClone, plan);

            if (sub.CreditsRemaining < cost)
                return Result.Failure<CreditReservationDto>("Insufficient credits.", ErrorCodes.BillingInsufficientCredits);

            var reservation = new RedisCreditReservation
            {
                SubscriptionId = sub.Id,
                WorkspaceId = request.HostWorkspaceId,
                IdempotencyKey = request.IdempotencyKey,
                Amount = cost
            };

            sub.CreditsRemaining -= cost; // Reserve the amount immediately
            _unitOfWork.SubscriptionRepository.Update(sub);

            var reserveTx = new CreditTransaction
            {
                SubscriptionId = sub.Id,
                UserId = sub.UserId,
                Amount = cost,
                Type = "reserve",
                CorrelationId = request.IdempotencyKey,
                Status = "pending",
                Description = "AI Real-time reserve",
                ReferenceType = "RedisReservation",
                BalanceAfter = sub.CreditsRemaining,
                CreatedAt = DateTime.UtcNow
            };
            await _unitOfWork.CreditTransactionRepository.AddAsync(reserveTx, cancellationToken);

            try
            {
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex.GetType().Name == "DbUpdateException")
            {
                // Concurrency Race Condition Handle (Unique Constraint Violation)
                _logger.LogWarning(ex, "Idempotency violation detected for {IdempotencyKey}. Assuming already reserved.", request.IdempotencyKey);
                return Result.Success(reservation.ToDto());
            }

            await _redisStore.SetReservationAsync(reservation, TimeSpan.FromMinutes(5), cancellationToken);

            return Result.Success(reservation.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reserving credits for {WorkspaceId}", request.HostWorkspaceId);
            return Result.Failure<CreditReservationDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<CreditTransactionDto>> ConfirmConsumeAsync(
        Guid workspaceId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (existingConsume, reservation) = await ValidateAndGetReservationAsync(idempotencyKey, "consume", cancellationToken);

            if (existingConsume != null)
            {
                return Result.Success(existingConsume.ToDto());
            }

            if (reservation == null)
            {
                return Result.Failure<CreditTransactionDto>("Reservation not found or already processed.", "RESERVATION_NOT_FOUND");
            }

            var sub = await _unitOfWork.SubscriptionRepository.GetByIdAsync(reservation.SubscriptionId, cancellationToken);
            if (sub == null || sub.WorkspaceId != workspaceId)
            {
                return Result.Failure<CreditTransactionDto>("Subscription invalid.", ErrorCodes.BillingSubscriptionNotFound);
            }

            // Find the pending reserve transaction
            var reserveTx = await _unitOfWork.CreditTransactionRepository.FirstOrDefaultAsync(
                tx => tx.Type == "reserve" && tx.CorrelationId == idempotencyKey && tx.SubscriptionId == sub.Id,
                cancellationToken);

            if (reserveTx != null)
            {
                reserveTx.Status = "committed";
                _unitOfWork.CreditTransactionRepository.Update(reserveTx);
            }

            sub.CreditsUsedThisCycle += reservation.Amount;
            sub.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.SubscriptionRepository.Update(sub);

            var tx = new CreditTransaction
            {
                SubscriptionId = sub.Id,
                UserId = sub.UserId,
                WorkspaceId = sub.WorkspaceId,
                Amount = -reservation.Amount, // Negative: credit deduction
                Type = "consumption",
                Description = "AI Real-time consumption",
                ReferenceId = null,
                ReferenceType = "CreditReservation",
                CorrelationId = idempotencyKey,
                Status = "committed",
                BalanceAfter = sub.CreditsRemaining,
                CreatedAt = DateTime.UtcNow
            };

            await _unitOfWork.CreditTransactionRepository.AddAsync(tx, cancellationToken);
            try
            {
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex.GetType().Name == "DbUpdateException")
            {
                _logger.LogWarning(ex, "Idempotency violation detected for {IdempotencyKey} during consume. Assuming already consumed.", idempotencyKey);
                return Result.Success(tx.ToDto());
            }

            return Result.Success(tx.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error consuming credits for {IdempotencyKey}", idempotencyKey);
            return Result.Failure<CreditTransactionDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<bool>> RefundReservationAsync(
        Guid workspaceId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (existingRefund, reservation) = await ValidateAndGetReservationAsync(idempotencyKey, "refund", cancellationToken);

            if (existingRefund != null)
            {
                return Result.Success(true);
            }

            if (reservation == null)
            {
                return Result.Failure<bool>("Reservation not found or already processed.", "RESERVATION_NOT_FOUND");
            }

            var sub = await _unitOfWork.SubscriptionRepository.GetByIdAsync(reservation.SubscriptionId, cancellationToken);
            if (sub == null || sub.WorkspaceId != workspaceId)
            {
                return Result.Failure<bool>("Subscription invalid.", ErrorCodes.BillingSubscriptionNotFound);
            }

            // Find the pending reserve transaction
            var reserveTx = await _unitOfWork.CreditTransactionRepository.FirstOrDefaultAsync(
                tx => tx.Type == "reserve" && tx.CorrelationId == idempotencyKey && tx.SubscriptionId == sub.Id,
                cancellationToken);

            if (reserveTx != null)
            {
                reserveTx.Status = "rolled_back";
                _unitOfWork.CreditTransactionRepository.Update(reserveTx);
            }

            sub.CreditsRemaining += reservation.Amount;
            sub.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.SubscriptionRepository.Update(sub);

            var refundTx = new CreditTransaction
            {
                SubscriptionId = sub.Id,
                UserId = sub.UserId,
                WorkspaceId = sub.WorkspaceId,
                Amount = reservation.Amount, // Positive: credit return
                Type = "refund",
                Description = "AI Real-time refund (canceled or failed)",
                ReferenceId = null,
                ReferenceType = "CreditReservation",
                CorrelationId = idempotencyKey,
                Status = "committed",
                BalanceAfter = sub.CreditsRemaining,
                CreatedAt = DateTime.UtcNow
            };

            await _unitOfWork.CreditTransactionRepository.AddAsync(refundTx, cancellationToken);

            try
            {
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex.GetType().Name == "DbUpdateException")
            {
                _logger.LogWarning(ex, "Idempotency violation detected for {IdempotencyKey} during refund. Assuming already refunded.", idempotencyKey);
                return Result.Success(true);
            }

            return Result.Success(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refunding credits for {IdempotencyKey}", idempotencyKey);
            return Result.Failure<bool>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<CreditTransactionDto>> AdjustCreditsAsync(
        Guid subscriptionId,
        int amount,
        string reason,
        string adminUserId,
        CancellationToken cancellationToken = default)
    {
        if (amount == 0)
            return Result.Failure<CreditTransactionDto>("Adjustment amount cannot be zero.", "INVALID_REQUEST");
        if (string.IsNullOrWhiteSpace(adminUserId))
            return Result.Failure<CreditTransactionDto>("AdminUserId is required for audit trail.", "INVALID_REQUEST");
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.GetByIdAsync(subscriptionId, cancellationToken);
            if (sub == null)
            {
                return Result.Failure<CreditTransactionDto>("Subscription not found.", ErrorCodes.BillingSubscriptionNotFound);
            }

            sub.CreditsRemaining += amount;
            sub.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.SubscriptionRepository.Update(sub);

            var adjustmentTx = new CreditTransaction
            {
                SubscriptionId = sub.Id,
                UserId = sub.UserId, // User whose credits are affected
                WorkspaceId = sub.WorkspaceId,
                Amount = amount,
                Type = "adjustment",
                Description = string.IsNullOrEmpty(reason) ? "Manual credit adjustment" : reason,
                ReferenceType = "manual_adjustment",
                ReferenceId = null,
                CorrelationId = $"adj_{Guid.NewGuid():N}",
                Status = "committed",
                BalanceAfter = sub.CreditsRemaining,
                CreatedAt = DateTime.UtcNow
            };

            await _unitOfWork.CreditTransactionRepository.AddAsync(adjustmentTx, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // Publish realtime credit update to trigger SignalR listeners on the front-end
            await PublishCreditUpdateAsync(sub.WorkspaceId, sub.CreditsRemaining, 
                amount > 0 ? "Credits Added" : "Credits Deducted", 
                $"Admin adjusted credit balance by {(amount > 0 ? "+" : "")}{amount} credits. Reason: {reason}", 
                cancellationToken);

            return Result.Success(adjustmentTx.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error manually adjusting credits for SubscriptionId {SubscriptionId}", subscriptionId);
            return Result.Failure<CreditTransactionDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    private async Task PublishCreditUpdateAsync(Guid workspaceId, int newBalance, string title, string content, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { new_balance = newBalance });
        var msg = new WarpTalk.Shared.Models.RealtimeNotificationMessage
        {
            Id = Guid.NewGuid().ToString(),
            UserId = workspaceId.ToString(),
            Type = "billing.credits_updated",
            Title = title,
            Content = content,
            PayloadJson = payload,
            CreatedAt = DateTime.UtcNow.ToString("O")
        };

        try
        {
            await _messagePublisher.PublishAsync("warptalk:notifications:new", msg, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish realtime credit update for WorkspaceId {WorkspaceId}", workspaceId);
        }
    }
    public async Task<Result<BillingReportDto>> GetBillingReportAsync(Guid workspaceId, int year, int month, CancellationToken cancellationToken = default)
    {
        try
        {
            var startDate = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
            var endDate = startDate.AddMonths(1);

            // Get all transactions for the month
            var txs = await _unitOfWork.CreditTransactionRepository.FindAsync(
                tx => tx.WorkspaceId == workspaceId && tx.CreatedAt >= startDate && tx.CreatedAt < endDate,
                cancellationToken);
            var transactions = txs.OrderBy(tx => tx.CreatedAt).ToList();

            // Calculate starting balance from the first transaction or latest prior
            int startingBalance = 0;
            if (transactions.Any())
            {
                var firstTx = transactions.First();
                startingBalance = firstTx.BalanceAfter - firstTx.Amount;
            }
            else
            {
                var priorTxs = await _unitOfWork.CreditTransactionRepository.GetPagedAsync(
                    tx => tx.WorkspaceId == workspaceId && tx.CreatedAt < startDate,
                    0, 1,
                    q => q.OrderByDescending(tx => tx.CreatedAt),
                    cancellationToken);

                var priorTx = priorTxs.FirstOrDefault();
                if (priorTx != null)
                {
                    startingBalance = priorTx.BalanceAfter;
                }
            }

            int endingBalance = transactions.Any() ? transactions.Last().BalanceAfter : startingBalance;

            int totalTopUps = transactions.Where(tx => tx.Type == "top_up" && tx.Status == "committed").Sum(tx => tx.Amount);
            int totalConsumed = Math.Abs(transactions.Where(tx => (tx.Type == "consumption" || tx.Type == "reserve") && tx.Status == "committed").Sum(tx => tx.Amount));

            // Breakdown using UsageRecords
            var usages = await _unitOfWork.UsageRecordRepository.FindAsync(
                u => u.WorkspaceId == workspaceId && u.RecordedAt >= startDate && u.RecordedAt < endDate,
                cancellationToken);

            var breakdown = usages.GroupBy(u => u.UsageType)
                .Select(g => new UsageSummaryDto(g.Key, g.Sum(x => x.CreditsConsumed)))
                .ToList();

            // If there's consumption but no UsageRecord (e.g. testing with direct ConsumeCredits API)
            if (totalConsumed > 0 && !breakdown.Any())
            {
                breakdown.Add(new UsageSummaryDto("Unknown / Generic API Consumption", totalConsumed));
            }

            var translationUsages = usages.Where(u => u.UsageType.Contains("translation", StringComparison.OrdinalIgnoreCase) && u.Quantity > 0).ToList();
            decimal? averageTranslationCost = translationUsages.Any()
                ? Math.Round(translationUsages.Sum(u => (decimal)u.CreditsConsumed) / translationUsages.Sum(u => u.Quantity), 2)
                : null;

            var meetingGroups = usages.Where(u => u.TranslationRoomId.HasValue)
                                      .GroupBy(u => u.TranslationRoomId.Value)
                                      .ToList();
            int? averageCostPerMeeting = meetingGroups.Any()
                ? (int)Math.Round(meetingGroups.Average(g => g.Sum(u => u.CreditsConsumed)))
                : null;

            var report = new BillingReportDto(
                workspaceId, month, year, startingBalance, endingBalance,
                totalTopUps, totalConsumed, averageTranslationCost, averageCostPerMeeting, breakdown
            );

            return Result.Success(report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating billing report for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<BillingReportDto>("Failed to generate billing report.", "INTERNAL_ERROR");
        }
    }

    private async Task<WarpTalk.BillingService.Domain.Entities.Subscription?> GetActiveSubscriptionAsync(
        Guid workspaceId, bool requireActivePeriod = false, CancellationToken cancellationToken = default)
    {
        return await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
            s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null &&
                 (!requireActivePeriod || s.CurrentPeriodEnd >= DateTime.UtcNow),
            cancellationToken);
    }

    private async Task<Result<T>> ExecuteWithConcurrencyRetryAsync<T>(
        Guid workspaceId,
        Func<Task<Result<T>>> operation,
        CancellationToken cancellationToken)
    {
        int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex) when (ex.GetType().Name == "DbUpdateConcurrencyException")
            {
                _logger.LogWarning(ex, "Concurrency conflict for WorkspaceId {WorkspaceId}. Attempt {Attempt} of {MaxRetries}", workspaceId, attempt, maxRetries);
                if (attempt == maxRetries) return Result.Failure<T>("System is busy. Please try again later.", "CONCURRENCY_ERROR");

                await Task.Delay(50 * attempt, cancellationToken);
                _unitOfWork.ClearTracking();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing operation for WorkspaceId {WorkspaceId}", workspaceId);
                return Result.Failure<T>("An unexpected error occurred.", "INTERNAL_ERROR");
            }
        }
        return Result.Failure<T>("System is busy. Please try again later.", "CONCURRENCY_ERROR");
    }

    private async Task<(CreditTransaction? existingTx, RedisCreditReservation? reservation)> ValidateAndGetReservationAsync(
        string idempotencyKey, string transactionType, CancellationToken cancellationToken)
    {
        var existingTx = await _unitOfWork.CreditTransactionRepository.FirstOrDefaultAsync(
            tx => tx.CorrelationId == idempotencyKey && tx.Type == transactionType,
            cancellationToken);

        if (existingTx != null)
        {
            return (existingTx, null);
        }

        var reservation = await _redisStore.GetAndRemoveReservationAsync(idempotencyKey, cancellationToken);

        return (null, reservation);
    }

    private async Task<int> GetVoiceCloneMinutesUsedThisCycleAsync(Guid subscriptionId, DateTime periodStart, DateTime periodEnd, CancellationToken cancellationToken)
    {
        var voiceCloneUsages = await _unitOfWork.UsageRecordRepository.FindAsync(
            u => u.SubscriptionId == subscriptionId &&
                 u.UsageType.Contains("voice_clone", StringComparison.OrdinalIgnoreCase) &&
                 u.RecordedAt >= periodStart &&
                 u.RecordedAt < periodEnd,
            cancellationToken);

        var totalSeconds = voiceCloneUsages.Sum(u => u.DurationSeconds ?? 0);
        return (int)Math.Ceiling(totalSeconds / 60.0);
    }
    public async Task<Result<UsageChartDto>> GetWorkspaceUsageChartAsync(Guid workspaceId, int year, CancellationToken cancellationToken = default)
    {
        try
        {
            var startDate = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var endDate = startDate.AddYears(1);

            var txs = await _unitOfWork.CreditTransactionRepository.FindAsync(
                tx => tx.WorkspaceId == workspaceId && tx.CreatedAt >= startDate && tx.CreatedAt < endDate,
                cancellationToken);

            var monthlyData = Enumerable.Range(1, 12).Select(month =>
            {
                var monthTxs = txs.Where(t => t.CreatedAt.Month == month).ToList();
                var topUp = monthTxs.Where(t => t.Type == "top_up" && t.Status == "committed").Sum(t => t.Amount);
                var consumed = Math.Abs(monthTxs.Where(t => (t.Type == "consumption" || t.Type == "reserve") && t.Status == "committed").Sum(t => t.Amount));

                return new MonthlyUsageDto(
                    month,
                    new DateTime(year, month, 1).ToString("MMM"),
                    consumed,
                    topUp
                );
            }).ToList();

            _logger.LogInformation("Chart data for WorkspaceId {WorkspaceId} in {Year}: {Data}", workspaceId, year, System.Text.Json.JsonSerializer.Serialize(monthlyData));

            return Result.Success(new UsageChartDto(year, monthlyData));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting usage chart for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<UsageChartDto>("Failed to generate chart.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<IEnumerable<FeatureAdoptionDto>>> GetWorkspaceFeatureAdoptionAsync(Guid workspaceId, int days, CancellationToken cancellationToken = default)
    {
        try
        {
            var startDate = DateTime.UtcNow.AddDays(-days);

            var usages = await _unitOfWork.UsageRecordRepository.FindAsync(
                u => u.WorkspaceId == workspaceId && u.RecordedAt >= startDate,
                cancellationToken);

            var adoption = usages.GroupBy(u => u.UsageType)
                .Select(g => new FeatureAdoptionDto(
                    g.Key,
                    g.Count(),
                    g.Sum(x => x.CreditsConsumed)
                ))
                .OrderByDescending(x => x.TotalCreditsConsumed)
                .ToList();

            return Result.Success(adoption.AsEnumerable());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting feature adoption for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<IEnumerable<FeatureAdoptionDto>>("Failed to generate feature adoption.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<GlobalBillingMetricsDto>> GetGlobalMetricsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var subs = await _unitOfWork.SubscriptionRepository.FindAsync(s => s.IsActive && s.DeletedAt == null, cancellationToken);
            var totalBalance = subs.Sum(s => s.CreditsRemaining);
            var activeWorkspaces = subs.Select(s => s.WorkspaceId).Distinct().Count();
            
            var currentMonthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var usages = await _unitOfWork.UsageRecordRepository.FindAsync(u => u.RecordedAt >= currentMonthStart, cancellationToken);
            var monthlyUsage = usages.Sum(u => u.CreditsConsumed);

            var thirtyDaysAgo = DateTime.UtcNow.AddDays(-30);
            var auditEvents = await _unitOfWork.CreditTransactionRepository.CountAsync(t => t.CreatedAt >= thirtyDaysAgo, cancellationToken);

            return Result.Success(new GlobalBillingMetricsDto(totalBalance, activeWorkspaces, monthlyUsage, auditEvents));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting global metrics");
            return Result.Failure<GlobalBillingMetricsDto>("Failed to generate global metrics.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<UsageChartDto>> GetGlobalUsageChartAsync(int year, CancellationToken cancellationToken = default)
    {
        try
        {
            var startDate = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var endDate = startDate.AddYears(1);

            var txs = await _unitOfWork.CreditTransactionRepository.FindAsync(
                t => t.CreatedAt >= startDate && t.CreatedAt < endDate,
                cancellationToken);

            var monthlyData = new List<MonthlyUsageDto>();
            for (int i = 1; i <= 12; i++)
            {
                var monthTxs = txs.Where(t => t.CreatedAt.Month == i).ToList();
                var consumed = monthTxs.Where(t => t.Type == "consumption").Sum(t => Math.Abs(t.Amount));
                var topUp = monthTxs.Where(t => t.Type == "top_up" && t.Amount > 0).Sum(t => t.Amount);

                monthlyData.Add(new MonthlyUsageDto(i, new DateTime(year, i, 1).ToString("MMM"), consumed, topUp));
            }

            return Result.Success(new UsageChartDto(year, monthlyData));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting global usage chart");
            return Result.Failure<UsageChartDto>("Failed to generate global chart.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<IEnumerable<UsageSummaryDto>>> GetGlobalUsageBreakdownAsync(int days, CancellationToken cancellationToken = default)
    {
        try
        {
            var startDate = DateTime.UtcNow.AddDays(-days);

            var usages = await _unitOfWork.UsageRecordRepository.FindAsync(
                u => u.RecordedAt >= startDate,
                cancellationToken);

            var breakdown = usages.GroupBy(u => u.UsageType)
                .Select(g => new UsageSummaryDto(
                    g.Key,
                    g.Sum(x => x.CreditsConsumed)
                ))
                .OrderByDescending(x => x.TotalCreditsConsumed)
                .ToList();

            return Result.Success(breakdown.AsEnumerable());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting global usage breakdown");
            return Result.Failure<IEnumerable<UsageSummaryDto>>("Failed to generate global usage breakdown.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<IEnumerable<TopWorkspaceDto>>> GetTopWorkspacesAsync(int days, int limit, CancellationToken cancellationToken = default)
    {
        try
        {
            var startDate = DateTime.UtcNow.AddDays(-days);
            
            var usages = await _unitOfWork.UsageRecordRepository.FindAsync(
                u => u.RecordedAt >= startDate,
                cancellationToken);

            var topWorkspaces = usages.GroupBy(u => u.WorkspaceId)
                .Select(g => new TopWorkspaceDto(
                    g.Key,
                    $"Workspace {g.Key.ToString()[..8].ToUpper()}",
                    g.Sum(x => x.CreditsConsumed)
                ))
                .OrderByDescending(x => x.TotalCreditsConsumed)
                .Take(limit)
                .ToList();

            if (topWorkspaces.Any())
            {
                try
                {
                    // Reuse the existing EF Core DB connection from UnitOfWork
                    var connection = (Npgsql.NpgsqlConnection)_unitOfWork.GetDbConnection();
                    var wasOpen = connection.State == System.Data.ConnectionState.Open;
                    if (!wasOpen) await connection.OpenAsync(cancellationToken);

                    var ids = topWorkspaces.Select(w => w.WorkspaceId).Distinct().ToArray();
                    using var cmd = new Npgsql.NpgsqlCommand(
                        "SELECT id, name FROM workspace.workspaces WHERE id = ANY(@ids)",
                        connection);
                    cmd.Parameters.AddWithValue("ids", ids);

                    using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                    var workspaceNames = new Dictionary<Guid, string>();
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        workspaceNames.Add(reader.GetFieldValue<Guid>(0), reader.GetString(1));
                    }
                    await reader.CloseAsync();

                    var resolvedTopWorkspaces = new List<TopWorkspaceDto>();
                    foreach (var tw in topWorkspaces)
                    {
                        if (workspaceNames.TryGetValue(tw.WorkspaceId, out var realName))
                        {
                            resolvedTopWorkspaces.Add(tw with { WorkspaceName = realName });
                        }
                        else
                        {
                            resolvedTopWorkspaces.Add(tw);
                        }
                    }
                    topWorkspaces = resolvedTopWorkspaces;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resolve real workspace names for Top Workspaces");
                }
            }

            return Result.Success(topWorkspaces.AsEnumerable());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting top workspaces");
            return Result.Failure<IEnumerable<TopWorkspaceDto>>("Failed to generate top workspaces.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<PagedResult<CreditTransactionDto>>> GetGlobalCreditHistoryAsync(
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default,
        Guid? workspaceId = null,
        string? type = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        int? minAmount = null,
        int? maxAmount = null)
    {
        try
        {
            var size = pageSize > 0 ? pageSize : 20;
            var skip = ((pageNumber > 0 ? pageNumber : 1) - 1) * size;

            System.Linq.Expressions.Expression<Func<WarpTalk.BillingService.Domain.Entities.CreditTransaction, bool>> predicate = t =>
                (!workspaceId.HasValue || t.WorkspaceId == workspaceId.Value) &&
                (string.IsNullOrEmpty(type) || t.Type == type) &&
                (!fromDate.HasValue || t.CreatedAt >= fromDate.Value) &&
                (!toDate.HasValue || t.CreatedAt <= toDate.Value) &&
                (!minAmount.HasValue || Math.Abs(t.Amount) >= minAmount.Value) &&
                (!maxAmount.HasValue || Math.Abs(t.Amount) <= maxAmount.Value);

            var items = await _unitOfWork.CreditTransactionRepository.GetPagedAsync(
                predicate,
                skip, size,
                q => q.OrderByDescending(t => t.CreatedAt),
                cancellationToken);

            var total = await _unitOfWork.CreditTransactionRepository.CountAsync(
                predicate,
                cancellationToken);

            var dtos = items.Select(t => new CreditTransactionDto(
                t.Id,
                t.Amount,
                t.Type,
                t.Description,
                t.ReferenceType,
                t.ReferenceId,
                t.BalanceAfter,
                t.CreatedAt,
                t.WorkspaceId,
                null,
                t.UserId,
                null
            )).ToList();

            var workspaceIds = dtos.Select(d => d.WorkspaceId).Distinct().ToArray();
            var resolvedDtos = new List<CreditTransactionDto>();

            try
            {
                var connection = (Npgsql.NpgsqlConnection)_unitOfWork.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                    await connection.OpenAsync(cancellationToken);

                using var command = new Npgsql.NpgsqlCommand(
                    "SELECT id, name FROM workspace.workspaces WHERE id = ANY(@ids)", connection);
                command.Parameters.AddWithValue("ids", workspaceIds);

                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var workspaceNames = new Dictionary<Guid, string>();
                while (await reader.ReadAsync(cancellationToken))
                {
                    workspaceNames.Add(reader.GetFieldValue<Guid>(0), reader.GetString(1));
                }

                foreach (var tx in dtos)
                {
                    if (Guid.TryParse(tx.WorkspaceId?.ToString(), out var gId) && workspaceNames.TryGetValue(gId, out var realName))
                        resolvedDtos.Add(tx with { WorkspaceName = realName });
                    else
                        resolvedDtos.Add(tx);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve workspace names from identity schema");
                resolvedDtos = dtos.ToList();
            }

            return Result.Success(new PagedResult<CreditTransactionDto>(total, resolvedDtos));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting global credit history");
            return Result.Failure<PagedResult<CreditTransactionDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<IEnumerable<UsageAlertDto>>> GetUsageAlertsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var yesterday = DateTime.UtcNow.AddDays(-1);

            // Fetch transactions for the last 24 hours that are negative (consumption/reserve)
            var recentConsumptions = await _unitOfWork.CreditTransactionRepository.FindAsync(
                tx => tx.CreatedAt >= yesterday && tx.Amount < 0 && tx.Status == "committed",
                cancellationToken);

            var grouped = recentConsumptions
                .GroupBy(tx => tx.WorkspaceId)
                .Select(g => new
                {
                    WorkspaceId = g.Key,
                    ConsumedCredits = Math.Abs(g.Sum(tx => tx.Amount))
                })
                .Where(x => x.ConsumedCredits > 50000)
                .ToList();

            if (!grouped.Any())
                return Result.Success(Enumerable.Empty<UsageAlertDto>());

            var workspaceIds = grouped.Select(g => g.WorkspaceId).Distinct().ToArray();
            var workspaceNames = new Dictionary<Guid, string>();

            try
            {
                var connection = (Npgsql.NpgsqlConnection)_unitOfWork.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                    await connection.OpenAsync(cancellationToken);

                using var command = new Npgsql.NpgsqlCommand(
                    "SELECT id, name FROM workspace.workspaces WHERE id = ANY(@ids)", connection);
                command.Parameters.AddWithValue("ids", workspaceIds);

                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    workspaceNames.Add(reader.GetFieldValue<Guid>(0), reader.GetString(1));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve workspace names for alerts");
            }

            var alerts = grouped.Select(g => new UsageAlertDto(
                WorkspaceId: g.WorkspaceId,
                WorkspaceName: workspaceNames.TryGetValue(g.WorkspaceId, out var name) ? name : "Unknown Workspace",
                ConsumedCreditsIn24h: g.ConsumedCredits,
                Reason: $"Unusually high consumption: {g.ConsumedCredits} credits in 24h"
            ));

            return Result.Success(alerts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting usage alerts");
            return Result.Failure<IEnumerable<UsageAlertDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    // --- Service Rates ---

    private double GetRate(string key, double fallback) =>
        double.TryParse(_configuration[key], System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public Result<ServiceRatesDto> GetServiceRates()
    {
        var dto = new ServiceRatesDto(
            SttPerMinute: GetRate("BillingRates:SttPerMinute", 15.0),
            TranslationPerMinute: GetRate("BillingRates:TranslationPerMinute", 15.0),
            StandardTtsPerMinute: GetRate("BillingRates:StandardTtsPerMinute", 15.0),
            VoiceClonePerMinute: GetRate("BillingRates:VoiceClonePerMinute", 40.0),
            AiSummaryPerRequest: GetRate("BillingRates:AiSummaryPerRequest", 5.0),
            AiChatPerRequest: GetRate("BillingRates:AiChatPerRequest", 2.0)
        );
        return Result.Success(dto);
    }

    public async Task<Result<ServiceRatesDto>> UpdateServiceRatesAsync(
        UpdateServiceRatesRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Validate — all values must be positive
            if (request.SttPerMinute <= 0 || request.TranslationPerMinute <= 0 ||
                request.StandardTtsPerMinute <= 0 || request.VoiceClonePerMinute <= 0 ||
                request.AiSummaryPerRequest <= 0 || request.AiChatPerRequest <= 0)
            {
                return Result.Failure<ServiceRatesDto>("All rate values must be greater than zero.", "INVALID_REQUEST");
            }

            // Find appsettings.json next to the running assembly
            var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(appSettingsPath))
                return Result.Failure<ServiceRatesDto>("appsettings.json not found on server.", "INTERNAL_ERROR");

            // Capture old rates BEFORE writing so we can diff them for notifications
            var oldRates = GetServiceRates().Value;

            var json = await File.ReadAllTextAsync(appSettingsPath, cancellationToken);
            var doc = JsonDocument.Parse(json);
            using var stream = new System.IO.MemoryStream();
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name == "BillingRates")
                    continue; // we will rewrite this section
                prop.WriteTo(writer);
            }

            // Write updated BillingRates section
            writer.WritePropertyName("BillingRates");
            writer.WriteStartObject();
            writer.WriteNumber("SttPerMinute", request.SttPerMinute);
            writer.WriteNumber("TranslationPerMinute", request.TranslationPerMinute);
            writer.WriteNumber("StandardTtsPerMinute", request.StandardTtsPerMinute);
            writer.WriteNumber("VoiceClonePerMinute", request.VoiceClonePerMinute);
            writer.WriteNumber("AiSummaryPerRequest", request.AiSummaryPerRequest);
            writer.WriteNumber("AiChatPerRequest", request.AiChatPerRequest);
            writer.WriteEndObject();

            writer.WriteEndObject();
            await writer.FlushAsync(cancellationToken);

            var updatedJson = System.Text.Encoding.UTF8.GetString(stream.ToArray());
            await File.WriteAllTextAsync(appSettingsPath, updatedJson, cancellationToken);

            // Reload configuration so _configuration reflects the new values immediately
            if (_configuration is IConfigurationRoot configRoot)
                configRoot.Reload();

            _logger.LogInformation("BillingRates updated by admin.");

            // --- Notify all workspace owners ---
            var savedRates = GetServiceRates();
            await NotifyWorkspaceOwnersAsync(oldRates, request, cancellationToken);
            return savedRates;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating service rates");
            return Result.Failure<ServiceRatesDto>("An unexpected error occurred while saving rates.", "INTERNAL_ERROR");
        }
    }

    private async Task NotifyWorkspaceOwnersAsync(
        ServiceRatesDto? oldRates,
        UpdateServiceRatesRequest newRates,
        CancellationToken cancellationToken)
    {
        if (_notificationClient is null) return;

        try
        {
            // Build human-readable diff lines
            var changes = new List<string>();
            void AddChange(string label, double oldVal, double newVal, string unit)
            {
                if (Math.Abs(oldVal - newVal) > 0.0001)
                    changes.Add($"• {label}: {oldVal:0.##} → {newVal:0.##} {unit}");
            }

            if (oldRates is not null)
            {
                AddChange("Speech-to-Text (STT)",       oldRates.SttPerMinute,           newRates.SttPerMinute,           "credits/min");
                AddChange("Real-time Translation",      oldRates.TranslationPerMinute,   newRates.TranslationPerMinute,   "credits/min");
                AddChange("Text-to-Speech (TTS)",       oldRates.StandardTtsPerMinute,   newRates.StandardTtsPerMinute,   "credits/min");
                AddChange("Voice Clone TTS",            oldRates.VoiceClonePerMinute,    newRates.VoiceClonePerMinute,    "credits/min");
                AddChange("AI Summary",                 oldRates.AiSummaryPerRequest,    newRates.AiSummaryPerRequest,    "credits/req");
                AddChange("AI Workspace Chat",          oldRates.AiChatPerRequest,       newRates.AiChatPerRequest,       "credits/req");
            }

            if (changes.Count == 0) return; // Nothing actually changed, skip

            var changedList  = string.Join("\n", changes);
            var body = $"WarpTalk has updated the AI service credit rates that apply to your workspace:\n\n{changedList}\n\nNew rates are effective immediately for all future sessions.";

            // Get distinct owner user IDs from active subscriptions
            var ownerUserIds = new List<Guid>();
            try
            {
                using var conn = _unitOfWork.GetDbConnection();
                await conn.OpenAsync(cancellationToken);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT DISTINCT user_id FROM subscription.subscriptions WHERE is_active = true AND deleted_at IS NULL AND user_id IS NOT NULL";
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (!reader.IsDBNull(0))
                        ownerUserIds.Add(reader.GetGuid(0));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load workspace owner IDs for rate change notification.");
                return;
            }

            _logger.LogInformation("Sending AI rate change notifications to {Count} workspace owners.", ownerUserIds.Count);

            var tasks = ownerUserIds.Select(userId =>
            {
                var req = new NotificationRequest
                {
                    UserId    = userId.ToString(),
                    Type      = "billing.rate_change",
                    Title     = "AI Service Rates Updated",
                    Body      = body,
                    ActionUrl = "/billing"
                };
                req.Metadata["changed_services"] = changes.Count.ToString();
                return _notificationClient.SendNotificationAsync(req, cancellationToken: cancellationToken).ResponseAsync;
            });

            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            // Notification failure must never block the main save operation
            _logger.LogError(ex, "Failed to send rate change notifications to workspace owners.");
        }
    }
}
