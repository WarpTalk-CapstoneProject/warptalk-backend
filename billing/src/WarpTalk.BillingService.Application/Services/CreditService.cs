using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using NotificationClient = WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient;

namespace WarpTalk.BillingService.Application.Services;

public class CreditService : ICreditService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<CreditService> _logger;
    private readonly IBillingMessagePublisher _messagePublisher;
    private readonly IRedisBillingStore _redisStore;
    private readonly IConfiguration _configuration;
    private readonly NotificationClient? _notificationClient;

    public CreditService(
        IUnitOfWork unitOfWork,
        ILogger<CreditService> logger,
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

    public async Task<Result<Guid>> StartSessionAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
            s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null,
            cancellationToken);

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
            return Result.Failure<bool>("Session is inactive or expired.", "SESSION_EXPIRED");

        await _redisStore.SetSessionActiveAsync(sessionId, TimeSpan.FromSeconds(75), cancellationToken);
        return Result.Success(true);
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

    public Task<Result<CreditTransactionDto>> ConsumeCreditsAsync(
        Guid workspaceId, ConsumeCreditsRequest request, CancellationToken cancellationToken = default)
    {
        return ExecuteWithConcurrencyRetryAsync(workspaceId, async () =>
        {
            if (request.Amount <= 0)
                return Result.Failure<CreditTransactionDto>("Amount must be greater than zero.", "INVALID_REQUEST");

            var sub = await GetActiveSubscriptionAsync(workspaceId, true, cancellationToken);

            if (sub is null)
            {
                return Result.Failure<CreditTransactionDto>(
                    "No active subscription found for this workspace or subscription expired.",
                    ErrorCodes.BillingSubscriptionNotFound);
            }

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

            await _unitOfWork.CreditTransactionRepository.AddAsync(tx, cancellationToken);

            // Also create a corresponding UsageRecord for analytics dashboard population (Feature Adoption, Cost Breakdown)
            var usageType = "voice_translation";
            if (!string.IsNullOrEmpty(request.ReferenceType))
            {
                var refLower = request.ReferenceType.ToLower();
                if (refLower.Contains("summary")) usageType = "summary";
                else if (refLower.Contains("clone")) usageType = "voice_cloning";
                else if (refLower.Contains("chat")) usageType = "chat";
                else if (refLower.Contains("tts") || refLower.Contains("speech")) usageType = "text_to_speech";
            }

            var usage = new WarpTalk.BillingService.Domain.Entities.UsageRecord
            {
                SubscriptionId = sub.Id,
                WorkspaceId = workspaceId,
                UserId = sub.UserId,
                UsageType = usageType,
                Unit = "request",
                Quantity = 1,
                CreditsConsumed = request.Amount,
                RecordedAt = DateTime.UtcNow,
                TranslationRoomId = request.ReferenceId
            };
            await _unitOfWork.UsageRecordRepository.AddAsync(usage, cancellationToken);

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
            var paymentId = Guid.NewGuid();
            tx.ReferenceId = paymentId;
            tx.ReferenceType = "stripe_payment";

            await _unitOfWork.CreditTransactionRepository.AddAsync(tx, cancellationToken);

            // Calculate approximate amount paid based on credits added (inverse logic of ProcessPaymentSuccessInternal)
            // e.g., 10 credits ~ 100 VND
            decimal credits = request.Amount;
            decimal estimatedCostVnd = credits * 10m; // standard fallback rate

            // Create Payment record
            var paymentTx = new WarpTalk.BillingService.Domain.Entities.Payment
            {
                Id = paymentId,
                SubscriptionId = sub.Id,
                UserId = sub.UserId,
                Amount = estimatedCostVnd,
                TaxAmount = 0m,
                TotalAmount = estimatedCostVnd,
                Currency = "vnd",
                PaymentMethod = "top_up_simulation",
                Provider = "stripe",
                ProviderTransactionId = request.ReferenceId?.ToString() ?? Guid.NewGuid().ToString(),
                Status = "paid",
                PaidAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            await _unitOfWork.PaymentRepository.AddAsync(paymentTx, cancellationToken);

            // Create Invoice record
            var invoice = new WarpTalk.BillingService.Domain.Entities.Invoice
            {
                Id = Guid.NewGuid(),
                UserId = sub.UserId,
                PaymentId = paymentTx.Id,
                InvoiceNumber = paymentTx.ProviderTransactionId,
                Subtotal = estimatedCostVnd,
                Tax = 0m,
                Total = estimatedCostVnd,
                Currency = "vnd",
                Status = "paid",
                PdfUrl = string.Empty,
                LineItems = JsonSerializer.Serialize(new[] {
                    new {
                        description = $"{request.Amount.ToString()} cr Credit Top-Up Package",
                        quantity = 1,
                        amount = estimatedCostVnd
                    }
                }),
                IssuedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            };
            await _unitOfWork.InvoiceRepository.AddAsync(invoice, cancellationToken);

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
                s => s.WorkspaceId == workspaceId && s.DeletedAt == null,
                cancellationToken);

            if (sub is null)
                return Result.Failure<PagedResult<CreditTransactionDto>>(
                    "No subscription found for this workspace.",
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
                includes: new System.Linq.Expressions.Expression<Func<WarpTalk.BillingService.Domain.Entities.CreditTransaction, object>>[] { t => t.Subscription },
                cancellationToken: cancellationToken);

            var total = await _unitOfWork.CreditTransactionRepository.CountAsync(
                predicate,
                cancellationToken);

            return Result.Success(new PagedResult<CreditTransactionDto>(
                total,
                items.Select(t => new CreditTransactionDto(
                    t.Id,
                    t.Amount,
                    t.Type,
                    t.Description,
                    t.ReferenceType,
                    t.ReferenceId,
                    t.BalanceAfter,
                    t.CreatedAt,
                    t.Subscription?.WorkspaceId ?? Guid.Empty,
                    null,
                    t.UserId,
                    null
                ))));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting credit history for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<PagedResult<CreditTransactionDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
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
                return Result.Failure<CreditReservationDto>("Plan not found.", "PLAN_NOT_FOUND");

            // Hardcode rate checks for local testing or resolve from config
            var sttRateMin = double.Parse(_configuration["BillingRates:SttPerMinute"] ?? "15.0", System.Globalization.CultureInfo.InvariantCulture);
            var transRateMin = double.Parse(_configuration["BillingRates:TranslationPerMinute"] ?? "15.0", System.Globalization.CultureInfo.InvariantCulture);
            var ttsRateMin = double.Parse(_configuration["BillingRates:StandardTtsPerMinute"] ?? "15.0", System.Globalization.CultureInfo.InvariantCulture);
            var vcRateMin = double.Parse(_configuration["BillingRates:VoiceClonePerMinute"] ?? "40.0", System.Globalization.CultureInfo.InvariantCulture);

            double ratePerMinute = request.IsVoiceClone ? vcRateMin : (sttRateMin + transRateMin + ttsRateMin);
            int cost = (int)Math.Max(1, Math.Ceiling((request.AudioSeconds / 60.0) * ratePerMinute));

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

            // Create Transaction immediately when reserved
            Guid.TryParse(request.IdempotencyKey, out var refIdVal);
            var tx = new CreditTransaction
            {
                SubscriptionId = sub.Id,
                UserId = sub.UserId,
                Amount = -cost,
                Type = "consumption",
                Description = "AI Real-time session reservation",
                ReferenceId = refIdVal == Guid.Empty ? Guid.NewGuid() : refIdVal,
                ReferenceType = "CreditReservation",
                BalanceAfter = sub.CreditsRemaining,
                CreatedAt = DateTime.UtcNow
            };
            await _unitOfWork.CreditTransactionRepository.AddAsync(tx, cancellationToken);

            // Intermediate reservation is fully maintained on Redis via RedisCreditReservation
            await _redisStore.SetReservationAsync(reservation, TimeSpan.FromMinutes(5), cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // Publish credit update immediately when reserved
            await PublishCreditUpdateAsync(request.HostWorkspaceId, sub.CreditsRemaining,
                "Credits Reserved", $"Real-time translation session started. Reserved {cost} credits.", cancellationToken);

            return Result.Success(new CreditReservationDto(
                Guid.NewGuid(),
                sub.Id,
                request.IdempotencyKey,
                cost,
                "Reserved",
                DateTime.UtcNow.AddMinutes(5)
            ));
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
            var (_, reservation) = await ValidateAndGetReservationAsync(idempotencyKey, "consumption", cancellationToken);

            if (reservation == null)
            {
                return Result.Failure<CreditTransactionDto>("Reservation not found or already processed.", "RESERVATION_NOT_FOUND");
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
            var tx = await _unitOfWork.CreditTransactionRepository.FirstOrDefaultAsync(
                t => t.SubscriptionId == sub.Id && t.ReferenceType == "CreditReservation" && t.ReferenceId == refId,
                cancellationToken);

            if (tx != null)
            {
                tx.Description = "AI Real-time consumption";
                _unitOfWork.CreditTransactionRepository.Update(tx);
            }
            else
            {
                // Fallback: If not found, create a new one to prevent failure
                tx = new CreditTransaction
                {
                    SubscriptionId = sub.Id,
                    UserId = sub.UserId,
                    Amount = -reservation.Amount,
                    Type = "consumption",
                    Description = "AI Real-time consumption",
                    ReferenceId = refId == Guid.Empty ? null : refId,
                    ReferenceType = "CreditReservation",
                    BalanceAfter = sub.CreditsRemaining,
                    CreatedAt = DateTime.UtcNow
                };
                await _unitOfWork.CreditTransactionRepository.AddAsync(tx, cancellationToken);
            }

            try
            {
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex.GetType().Name == "DbUpdateException")
            {
                _logger.LogWarning(ex, "Idempotency violation detected for {IdempotencyKey} during consume. Assuming already consumed.", idempotencyKey);
                return Result.Success(tx.ToDto());
            }

            // Publish credit update when consumption confirmed
            await PublishCreditUpdateAsync(workspaceId, sub.CreditsRemaining,
                "Credits Consumed", $"Real-time translation session finished.", cancellationToken);

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
            var (_, reservation) = await ValidateAndGetReservationAsync(idempotencyKey, "refund", cancellationToken);

            if (reservation == null)
            {
                return Result.Failure<bool>("Reservation not found or already processed.", "RESERVATION_NOT_FOUND");
            }

            var sub = await _unitOfWork.SubscriptionRepository.GetByIdAsync(reservation.SubscriptionId, cancellationToken);
            if (sub == null || sub.WorkspaceId != workspaceId)
            {
                return Result.Failure<bool>("Subscription invalid.", ErrorCodes.BillingSubscriptionNotFound);
            }

            sub.CreditsRemaining += reservation.Amount;
            sub.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.SubscriptionRepository.Update(sub);

            var refundTx = new CreditTransaction
            {
                SubscriptionId = sub.Id,
                UserId = sub.UserId,
                Amount = reservation.Amount,
                Type = "refund",
                Description = "AI Real-time refund (canceled or failed)",
                ReferenceId = null,
                ReferenceType = "CreditReservation",
                BalanceAfter = sub.CreditsRemaining,
                CreatedAt = DateTime.UtcNow
            };

            await _unitOfWork.CreditTransactionRepository.AddAsync(refundTx, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // Publish credit update on refund
            await PublishCreditUpdateAsync(workspaceId, sub.CreditsRemaining,
                "Credits Refunded", $"Refunded {reservation.Amount} credits.", cancellationToken);

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
                UserId = sub.UserId,
                Amount = amount,
                Type = "adjustment",
                Description = string.IsNullOrEmpty(reason) ? "Manual credit adjustment" : reason,
                ReferenceType = "manual_adjustment",
                ReferenceId = null,
                BalanceAfter = sub.CreditsRemaining,
                CreatedAt = DateTime.UtcNow
            };

            await _unitOfWork.CreditTransactionRepository.AddAsync(adjustmentTx, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

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

            Guid? targetSubId = null;
            if (workspaceId.HasValue)
            {
                var targetSub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                    s => s.WorkspaceId == workspaceId.Value && s.DeletedAt == null,
                    cancellationToken);
                if (targetSub != null) targetSubId = targetSub.Id;
            }

            System.Linq.Expressions.Expression<Func<WarpTalk.BillingService.Domain.Entities.CreditTransaction, bool>> predicate = t =>
                (!workspaceId.HasValue || t.SubscriptionId == targetSubId) &&
                (string.IsNullOrEmpty(type) || t.Type == type) &&
                (!fromDate.HasValue || t.CreatedAt >= fromDate.Value) &&
                (!toDate.HasValue || t.CreatedAt <= toDate.Value) &&
                (!minAmount.HasValue || Math.Abs(t.Amount) >= minAmount.Value) &&
                (!maxAmount.HasValue || Math.Abs(t.Amount) <= maxAmount.Value);

            var items = await _unitOfWork.CreditTransactionRepository.GetPagedAsync(
                predicate,
                skip, size,
                q => q.OrderByDescending(t => t.CreatedAt),
                cancellationToken,
                new System.Linq.Expressions.Expression<Func<WarpTalk.BillingService.Domain.Entities.CreditTransaction, object>>[] { t => t.Subscription });

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
                t.Subscription?.WorkspaceId ?? Guid.Empty,
                null,
                t.UserId,
                null
            )).ToList();

            // Resolve workspace names from workspace.workspaces cross-schema
            try
            {
                var workspaceIds = dtos
                    .Where(d => d.WorkspaceId.HasValue && d.WorkspaceId != Guid.Empty)
                    .Select(d => d.WorkspaceId!.Value)
                    .Distinct()
                    .ToArray();

                if (workspaceIds.Length > 0)
                {
                    var dbConnection = (Npgsql.NpgsqlConnection)_unitOfWork.GetDbConnection();
                    var wasOpen = dbConnection.State == System.Data.ConnectionState.Open;
                    if (!wasOpen) await dbConnection.OpenAsync(cancellationToken);

                    using var cmd = new Npgsql.NpgsqlCommand(
                        "SELECT id, name FROM workspace.workspaces WHERE id = ANY(@ids)",
                        dbConnection);
                    cmd.Parameters.AddWithValue("ids", workspaceIds);

                    var workspaceNames = new Dictionary<Guid, string>();
                    using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        workspaceNames[reader.GetGuid(0)] = reader.GetString(1);
                    }
                    await reader.CloseAsync();

                    dtos = dtos.Select(d =>
                        d.WorkspaceId.HasValue && workspaceNames.TryGetValue(d.WorkspaceId.Value, out var wName)
                            ? d with { WorkspaceName = wName }
                            : d
                    ).ToList();
                }
            }
            catch (Exception wsEx)
            {
                _logger.LogWarning(wsEx, "Failed to resolve workspace names for global credit history");
            }

            return Result.Success(new PagedResult<CreditTransactionDto>(total, dtos));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting global credit history");
            return Result.Failure<PagedResult<CreditTransactionDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
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
            tx => tx.ReferenceType == "CreditReservation" && tx.Type == transactionType, 
            cancellationToken);

        if (existingTx != null)
        {
            return (existingTx, null);
        }

        var reservation = await _redisStore.GetAndRemoveReservationAsync(idempotencyKey, cancellationToken);
        return (null, reservation);
    }
}
