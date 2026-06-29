using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class CreditAndUsageService : ICreditAndUsageService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<CreditAndUsageService> _logger;
    private readonly IBillingMessagePublisher _messagePublisher;
    private readonly IRedisBillingStore _redisStore;
    private readonly IConfiguration _configuration;

    public CreditAndUsageService(
        IUnitOfWork unitOfWork, 
        ILogger<CreditAndUsageService> logger, 
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
        var sttRateMin = double.Parse(_configuration["BillingRates:SttPerMinute"] ?? "10.0", System.Globalization.CultureInfo.InvariantCulture);
        var transRateMin = double.Parse(_configuration["BillingRates:TranslationPerMinute"] ?? "10.0", System.Globalization.CultureInfo.InvariantCulture);
        var ttsRateMin = double.Parse(_configuration["BillingRates:StandardTtsPerMinute"] ?? "5.0", System.Globalization.CultureInfo.InvariantCulture);
        var vcRateMin = double.Parse(_configuration["BillingRates:VoiceClonePerMinute"] ?? "25.0", System.Globalization.CultureInfo.InvariantCulture);

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
            var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null,
                cancellationToken);

            if (sub is null)
                return Result.Failure<PagedResult<CreditTransactionDto>>(
                    "No active subscription found for this workspace.",
                    ErrorCodes.BillingSubscriptionNotFound);

            var size = pageSize > 0 ? pageSize : 20;
            var skip = ((pageNumber > 0 ? pageNumber : 1) - 1) * size;

            System.Linq.Expressions.Expression<Func<WarpTalk.BillingService.Domain.Entities.CreditTransaction, bool>> predicate = t => 
                t.SubscriptionId == sub.Id &&
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

            var report = new BillingReportDto(
                workspaceId, month, year, startingBalance, endingBalance,
                totalTopUps, totalConsumed, breakdown
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
}
