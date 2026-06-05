using System.Text.Json;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class CreditService : ICreditService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<CreditService> _logger;
    private readonly IBillingMessagePublisher _messagePublisher;
    private readonly IRealtimeCostCalculator _costCalculator;
    private readonly IRedisBillingStore _redisStore;

    public CreditService(IUnitOfWork unitOfWork, ILogger<CreditService> logger, IBillingMessagePublisher messagePublisher, IRealtimeCostCalculator costCalculator, IRedisBillingStore redisStore)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _messagePublisher = messagePublisher;
        _costCalculator = costCalculator;
        _redisStore = redisStore;
    }

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

    public async Task<Result<CreditTransactionDto>> ConsumeCreditsAsync(
        Guid workspaceId, ConsumeCreditsRequest request, CancellationToken cancellationToken = default)
    {
        int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                    s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null && s.CurrentPeriodEnd >= DateTime.UtcNow,
                    cancellationToken);

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
            }
            catch (Exception ex) when (ex.GetType().Name == "DbUpdateConcurrencyException")
            {
                _logger.LogWarning(ex, "Concurrency conflict during ConsumeCredits for WorkspaceId {WorkspaceId}. Attempt {Attempt} of {MaxRetries}", workspaceId, attempt, maxRetries);
                if (attempt == maxRetries) return Result.Failure<CreditTransactionDto>("System is busy. Please try again later.", "CONCURRENCY_ERROR");
                
                await Task.Delay(50 * attempt, cancellationToken);
                _unitOfWork.ClearTracking();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error consuming credits for WorkspaceId {WorkspaceId}", workspaceId);
                return Result.Failure<CreditTransactionDto>("An unexpected error occurred.", "INTERNAL_ERROR");
            }
        }
        return Result.Failure<CreditTransactionDto>("System is busy. Please try again later.", "CONCURRENCY_ERROR");
    }

    public async Task<Result<CreditBalanceDto>> TopUpCreditsAsync(
        Guid workspaceId, TopUpRequest request, CancellationToken cancellationToken = default)
    {
        int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
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

                sub.CreditsRemaining += request.Amount;
                sub.UpdatedAt = DateTime.UtcNow;
                _unitOfWork.SubscriptionRepository.Update(sub);

                var tx = request.ToEntity(sub);

                await _unitOfWork.CreditTransactionRepository.AddAsync(tx, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);

                await PublishCreditUpdateAsync(workspaceId, sub.CreditsRemaining,
                    "Credits Topped Up", $"You have successfully added {request.Amount} credits.", cancellationToken);

                return Result.Success(sub.ToCreditBalanceDto(workspaceId));
            }
            catch (Exception ex) when (ex.GetType().Name == "DbUpdateConcurrencyException")
            {
                _logger.LogWarning(ex, "Concurrency conflict during TopUpCredits for WorkspaceId {WorkspaceId}. Attempt {Attempt} of {MaxRetries}", workspaceId, attempt, maxRetries);
                if (attempt == maxRetries) return Result.Failure<CreditBalanceDto>("System is busy. Please try again later.", "CONCURRENCY_ERROR");
                
                await Task.Delay(50 * attempt, cancellationToken);
                _unitOfWork.ClearTracking();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error topping up credits for WorkspaceId {WorkspaceId}", workspaceId);
                return Result.Failure<CreditBalanceDto>("An unexpected error occurred.", "INTERNAL_ERROR");
            }
        }
        return Result.Failure<CreditBalanceDto>("System is busy. Please try again later.", "CONCURRENCY_ERROR");
    }

    public async Task<Result<PagedResult<CreditTransactionDto>>> GetCreditHistoryAsync(
        Guid workspaceId, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
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

            var items = await _unitOfWork.CreditTransactionRepository.GetPagedAsync(
                t => t.SubscriptionId == sub.Id,
                skip, size,
                q => q.OrderByDescending(t => t.CreatedAt),
                cancellationToken);

            var total = await _unitOfWork.CreditTransactionRepository.CountAsync(
                t => t.SubscriptionId == sub.Id,
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

    public async Task<Result<CreditBalanceDto>> RecordUsageAsync(
        RecordUsageRequest request, CancellationToken cancellationToken = default)
    {
        int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                    s => s.WorkspaceId == request.HostWorkspaceId && s.IsActive && s.DeletedAt == null && s.CurrentPeriodEnd >= DateTime.UtcNow,
                    cancellationToken);

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

                if (request.UsageType.Contains("voice_clone", StringComparison.OrdinalIgnoreCase) && !plan.VoiceCloneEnabled)
                    return Result.Failure<CreditBalanceDto>(
                        $"Voice clone is not available on the '{plan.Name}' plan. Please upgrade.",
                        "FEATURE_NOT_AVAILABLE");

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
            }
            catch (Exception ex) when (ex.GetType().Name == "DbUpdateConcurrencyException")
            {
                _logger.LogWarning(ex, "Concurrency conflict during RecordUsage for HostWorkspaceId {HostWorkspaceId}. Attempt {Attempt} of {MaxRetries}", request.HostWorkspaceId, attempt, maxRetries);
                if (attempt == maxRetries) return Result.Failure<CreditBalanceDto>("System is busy. Please try again later.", "CONCURRENCY_ERROR");
                
                await Task.Delay(50 * attempt, cancellationToken);
                _unitOfWork.ClearTracking();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recording usage for HostWorkspaceId {HostWorkspaceId}", request.HostWorkspaceId);
                return Result.Failure<CreditBalanceDto>("An unexpected error occurred.", "INTERNAL_ERROR");
            }
        }
        return Result.Failure<CreditBalanceDto>("System is busy. Please try again later.", "CONCURRENCY_ERROR");
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
                var existingDto = new CreditReservationDto(Guid.Empty, existingReserve.SubscriptionId, existingReserve.CorrelationId!, existingReserve.Amount, "Reserved", DateTime.UtcNow.AddMinutes(5));
                return Result.Success(existingDto);
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
            if (request.IsVoiceClone && !plan.VoiceCloneEnabled)
                return Result.Failure<CreditReservationDto>(
                    $"Voice clone is not available on the '{plan.Name}' plan. Please upgrade to Pro or Premium.",
                    "FEATURE_NOT_AVAILABLE");

            var cost = _costCalculator.CalculateCreditCost(request.AudioSeconds, request.TokenCount, request.GpuInferenceMs, request.IsVoiceClone, plan);

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
                var existingDto = new CreditReservationDto(Guid.Empty, sub.Id, request.IdempotencyKey, cost, "Reserved", DateTime.UtcNow.AddMinutes(5));
                return Result.Success(existingDto);
            }

            await _redisStore.SetReservationAsync(reservation, TimeSpan.FromMinutes(5), cancellationToken);

            var dto = new CreditReservationDto(Guid.Empty, reservation.SubscriptionId, reservation.IdempotencyKey, reservation.Amount, "Reserved", DateTime.UtcNow.AddMinutes(5));
            return Result.Success(dto);
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

            // 1. Idempotency Check: Already consumed?
            var existingConsume = await _unitOfWork.CreditTransactionRepository.FirstOrDefaultAsync(
                tx => tx.CorrelationId == idempotencyKey && tx.Type == "consume",
                cancellationToken);

            if (existingConsume != null)
            {
                var existingDto = new CreditTransactionDto(existingConsume.Id, -existingConsume.Amount, existingConsume.Type, existingConsume.Description ?? "", existingConsume.ReferenceType ?? "", existingConsume.ReferenceId, existingConsume.BalanceAfter, existingConsume.CreatedAt);
                return Result.Success(existingDto);
            }

            var reservation = await _redisStore.GetAndRemoveReservationAsync(idempotencyKey, cancellationToken);

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
                var existingDto = new CreditTransactionDto(tx.Id, -tx.Amount, tx.Type, tx.Description ?? "", tx.ReferenceType ?? "", tx.ReferenceId, tx.BalanceAfter, tx.CreatedAt);
                return Result.Success(existingDto);
            }

            return Result.Success(new CreditTransactionDto(tx.Id, tx.Amount, tx.Type, tx.Description, tx.ReferenceType, tx.ReferenceId, tx.BalanceAfter, tx.CreatedAt));
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
            // 1. Idempotency Check: Already refunded?
            var existingRefund = await _unitOfWork.CreditTransactionRepository.FirstOrDefaultAsync(
                tx => tx.CorrelationId == idempotencyKey && tx.Type == "refund",
                cancellationToken);

            if (existingRefund != null)
            {
                return Result.Success(true);
            }

            var reservation = await _redisStore.GetAndRemoveReservationAsync(idempotencyKey, cancellationToken);

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

            return Result.Success(new CreditTransactionDto(
                adjustmentTx.Id,
                adjustmentTx.Amount,
                adjustmentTx.Type,
                adjustmentTx.Description,
                adjustmentTx.ReferenceType,
                adjustmentTx.ReferenceId,
                adjustmentTx.BalanceAfter,
                adjustmentTx.CreatedAt
            ));
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
}
