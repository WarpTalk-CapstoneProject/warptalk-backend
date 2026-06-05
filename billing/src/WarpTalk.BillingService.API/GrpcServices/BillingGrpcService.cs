using Grpc.Core;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.API.GrpcServices;

/// <summary>
/// gRPC surface — thin adapter that delegates to Application services.
/// All business logic lives in the Application layer.
/// </summary>
public class BillingGrpcService : Shared.Protos.BillingService.BillingServiceBase
{
    private readonly ICreditService _creditService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IPlanService _planService;
    private readonly IPaymentService _paymentService;
    private readonly WarpTalk.BillingService.Domain.Interfaces.IUnitOfWork _unitOfWork;
    private readonly StackExchange.Redis.IConnectionMultiplexer _redis;
    private readonly ILogger<BillingGrpcService> _logger;

    public BillingGrpcService(
        ICreditService creditService,
        ISubscriptionService subscriptionService,
        IPlanService planService,
        IPaymentService paymentService,
        WarpTalk.BillingService.Domain.Interfaces.IUnitOfWork unitOfWork,
        StackExchange.Redis.IConnectionMultiplexer redis,
        ILogger<BillingGrpcService> logger)
    {
        _creditService = creditService;
        _subscriptionService = subscriptionService;
        _planService = planService;
        _paymentService = paymentService;
        _unitOfWork = unitOfWork;
        _redis = redis;
        _logger = logger;
    }

    // ─── Credits ──────────────────────────────────────────────────────────

    public override async Task<Shared.Protos.GetCreditsResponse> GetWorkspaceCredits(
        Shared.Protos.GetCreditsRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid workspace_id."));

        var result = await _creditService.GetWorkspaceCreditsAsync(workspaceId, context.CancellationToken);

        if (!result.IsSuccess)
            return new Shared.Protos.GetCreditsResponse
            {
                WorkspaceId = request.WorkspaceId,
                CurrentCredits = 0,
                Status = "no_subscription"
            };

        var dto = result.Value!;
        return new Shared.Protos.GetCreditsResponse
        {
            WorkspaceId = request.WorkspaceId,
            CurrentCredits = dto.CurrentCredits,
            Status = dto.Status,
            CreditsUsedThisCycle = dto.CreditsUsedThisCycle,
            CurrentPeriodStart = dto.CurrentPeriodStart.ToString("O"),
            CurrentPeriodEnd = dto.CurrentPeriodEnd.ToString("O")
        };
    }

    public override async Task<Shared.Protos.ConsumeCreditsResponse> ConsumeCredits(
        Shared.Protos.ConsumeCreditsRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid workspace_id."));

        Guid.TryParse(request.ReferenceId, out var refId);

        var result = await _creditService.ConsumeCreditsAsync(workspaceId, new ConsumeCreditsRequest(
            request.Amount,
            request.ReferenceType,
            refId == Guid.Empty ? null : refId
        ), context.CancellationToken);

        if (!result.IsSuccess)
            return new Shared.Protos.ConsumeCreditsResponse
            {
                Success = false,
                ErrorMessage = result.Error
            };

        return new Shared.Protos.ConsumeCreditsResponse
        {
            Success = true,
            NewBalance = result.Value!.BalanceAfter
        };
    }

    public override async Task<Shared.Protos.GetCreditsResponse> TopUpCredits(
        Shared.Protos.TopUpRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid workspace_id."));

        Guid.TryParse(request.ReferenceId, out var refId);

        var result = await _creditService.TopUpCreditsAsync(workspaceId, new TopUpRequest(
            request.Amount,
            request.ReferenceType,
            refId == Guid.Empty ? null : refId
        ), context.CancellationToken);

        if (!result.IsSuccess)
            throw new RpcException(new Status(StatusCode.NotFound, result.Error ?? "TopUp failed."));

        var dto = result.Value!;
        return new Shared.Protos.GetCreditsResponse
        {
            WorkspaceId = request.WorkspaceId,
            CurrentCredits = dto.CurrentCredits,
            Status = dto.Status,
            CreditsUsedThisCycle = dto.CreditsUsedThisCycle,
            CurrentPeriodStart = dto.CurrentPeriodStart.ToString("O"),
            CurrentPeriodEnd = dto.CurrentPeriodEnd.ToString("O")
        };
    }

    public override async Task<Shared.Protos.RecordUsageGrpcResponse> RecordUsage(
        Shared.Protos.RecordUsageGrpcRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.HostWorkspaceId, out var hostWorkspaceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid host_workspace_id."));
        if (!Guid.TryParse(request.UserId, out var userId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid user_id."));

        // Anti-Abuse Validations
        if (request.DurationSeconds > 4 * 3600) // 4 hours
        {
            _logger.LogWarning("[ABUSE_DETECTED] RecordUsage duration excessively high: {DurationSeconds}s for WorkspaceId: {WorkspaceId}", request.DurationSeconds, request.HostWorkspaceId);
            return new Shared.Protos.RecordUsageGrpcResponse { Success = false, ErrorMessage = "Duration exceeds maximum allowed limit." };
        }
        if (request.CreditsConsumed < 0)
        {
            _logger.LogWarning("[ABUSE_DETECTED] RecordUsage credits consumed is negative: {Credits} for WorkspaceId: {WorkspaceId}", request.CreditsConsumed, request.HostWorkspaceId);
            return new Shared.Protos.RecordUsageGrpcResponse { Success = false, ErrorMessage = "Credits consumed cannot be negative." };
        }

        // Check for anomalies via Redis (e.g. > 10,000 credits in 1 minute)
        try
        {
            var db = _redis.GetDatabase();
            var anomalyKey = $"anomaly:usage:{request.HostWorkspaceId}";
            var recentUsage = await db.StringIncrementAsync(anomalyKey, request.CreditsConsumed);
            if (recentUsage == request.CreditsConsumed) // First increment
            {
                await db.KeyExpireAsync(anomalyKey, TimeSpan.FromMinutes(1));
            }
            else if (recentUsage > 10000)
            {
                _logger.LogCritical("[ANOMALY_CREDIT_SPIKE] Workspace {WorkspaceId} attempted to consume {Amount} credits in 1 minute, exceeding safety thresholds.", request.HostWorkspaceId, recentUsage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check anomaly rate limit in Redis");
        }

        Guid? translationRoomId = Guid.TryParse(request.TranslationRoomId, out var trId) ? trId : null;

        var dtoRequest = new RecordUsageRequest(
            hostWorkspaceId,
            userId,
            request.UsageType,
            request.Unit,
            (decimal)request.Quantity,
            request.CreditsConsumed,
            request.DurationSeconds > 0 ? request.DurationSeconds : null,
            translationRoomId,
            string.IsNullOrWhiteSpace(request.DetailsJson) ? null : request.DetailsJson
        );

        var result = await _creditService.RecordUsageAsync(dtoRequest, context.CancellationToken);

        if (!result.IsSuccess)
            return new Shared.Protos.RecordUsageGrpcResponse
            {
                Success = false,
                ErrorMessage = result.Error
            };

        return new Shared.Protos.RecordUsageGrpcResponse
        {
            Success = true,
            NewBalance = result.Value!.CurrentCredits
        };
    }

    public override async Task<Shared.Protos.CreditHistoryResponse> GetCreditHistory(
        Shared.Protos.GetHistoryRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid workspace_id."));

        var result = await _creditService.GetCreditHistoryAsync(
            workspaceId, request.PageNumber, request.PageSize, context.CancellationToken);

        if (!result.IsSuccess)
            return new Shared.Protos.CreditHistoryResponse { TotalCount = 0 };

        var response = new Shared.Protos.CreditHistoryResponse
        {
            TotalCount = result.Value!.TotalCount
        };

        foreach (var tx in result.Value.Items)
        {
            response.Items.Add(new Shared.Protos.CreditTransaction
            {
                Id = tx.Id.ToString(),
                Amount = tx.Amount,
                Type = tx.Type,
                Description = tx.Description ?? string.Empty,
                ReferenceType = tx.ReferenceType ?? string.Empty,
                ReferenceId = tx.ReferenceId?.ToString() ?? string.Empty,
                BalanceAfter = tx.BalanceAfter,
                CreatedAt = tx.CreatedAt.ToString("O")
            });
        }

        return response;
    }

    // ─── Subscriptions ────────────────────────────────────────────────────

    public override async Task<Shared.Protos.SubscriptionResponse> CreateSubscription(
        Shared.Protos.CreateSubscriptionRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid workspace_id."));
        if (!Guid.TryParse(request.PlanId, out var planId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid plan_id."));

        Guid.TryParse(request.UserId, out var userId);

        var result = await _subscriptionService.CreateSubscriptionAsync(
            new CreateSubscriptionRequest(workspaceId, planId, userId),
            context.CancellationToken);

        if (!result.IsSuccess)
            return new Shared.Protos.SubscriptionResponse { ErrorMessage = result.Error };

        return ToSubscriptionResponse(result.Value!);
    }

    public override async Task<Shared.Protos.SubscriptionResponse> GetActiveSubscription(
        Shared.Protos.GetActiveSubscriptionRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid workspace_id."));

        var result = await _subscriptionService.GetActiveSubscriptionAsync(workspaceId, context.CancellationToken);

        if (!result.IsSuccess)
            return new Shared.Protos.SubscriptionResponse { ErrorMessage = result.Error };

        return ToSubscriptionResponse(result.Value!);
    }

    public override async Task<Shared.Protos.GetFeatureAccessResponse> GetWorkspaceFeatureAccess(
        Shared.Protos.GetFeatureAccessRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid workspace_id."));

        var subResult = await _subscriptionService.GetActiveSubscriptionAsync(workspaceId, context.CancellationToken);
        if (!subResult.IsSuccess || subResult.Value == null)
            return new Shared.Protos.GetFeatureAccessResponse { HasActiveSubscription = false };

        var planResult = await _planService.GetPlanByIdAsync(subResult.Value.PlanId, context.CancellationToken);
        if (!planResult.IsSuccess || planResult.Value == null)
            return new Shared.Protos.GetFeatureAccessResponse { HasActiveSubscription = false };

        var plan = planResult.Value;
        return new Shared.Protos.GetFeatureAccessResponse
        {
            HasActiveSubscription = true,
            PlanTier = plan.Tier ?? string.Empty,
            MaxParticipants = plan.MaxParticipants,
            MaxLanguages = plan.MaxLanguages,
            VoiceCloneEnabled = plan.VoiceCloneEnabled,
            AiAssistantEnabled = plan.AiAssistantEnabled,
            GlossaryEnabled = plan.GlossaryEnabled,
            DedicatedGpu = plan.DedicatedGpu,
            FeaturesJson = plan.Features ?? "{}"
        };
    }

    public override async Task<Shared.Protos.SubscriptionResponse> CancelSubscription(
        Shared.Protos.CancelSubscriptionRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid workspace_id."));

        var result = await _subscriptionService.CancelSubscriptionAsync(
            workspaceId, request.Reason, context.CancellationToken);

        if (!result.IsSuccess)
            return new Shared.Protos.SubscriptionResponse { ErrorMessage = result.Error };

        return new Shared.Protos.SubscriptionResponse
        {
            WorkspaceId = request.WorkspaceId,
            Status = "cancelled"
        };
    }

    // ─── Plans ────────────────────────────────────────────────────────────

    public override async Task<Shared.Protos.GetPlansResponse> GetPlans(
        Shared.Protos.GetPlansRequest request, ServerCallContext context)
    {
        var result = await _planService.GetActivePlansAsync(context.CancellationToken);

        var response = new Shared.Protos.GetPlansResponse();
        if (!result.IsSuccess) return response;

        foreach (var plan in result.Value!)
            response.Plans.Add(ToPlanResponse(plan));

        return response;
    }

    public override async Task<Shared.Protos.PlanResponse> GetPlanById(
        Shared.Protos.GetPlanByIdRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.PlanId, out var planId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid plan_id."));

        var result = await _planService.GetPlanByIdAsync(planId, context.CancellationToken);

        if (!result.IsSuccess)
            throw new RpcException(new Status(StatusCode.NotFound, result.Error ?? "Plan not found."));

        return ToPlanResponse(result.Value!);
    }

    // ─── Payments ─────────────────────────────────────────────────────────

    public override async Task<Shared.Protos.TransactionHistoryResponse> GetTransactionHistory(
        Shared.Protos.GetHistoryRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid workspace_id."));

        // Get Subscriptions for Workspace to filter Payments
        var subs = await _unitOfWork.SubscriptionRepository.FindAsync(
            s => s.WorkspaceId == workspaceId, context.CancellationToken);

        var subIds = subs.Select(s => s.Id).ToList();

        var totalCount = await _unitOfWork.PaymentRepository.CountAsync(
            p => subIds.Contains(p.SubscriptionId), context.CancellationToken);

        var items = await _unitOfWork.PaymentRepository.GetPagedAsync(
            p => subIds.Contains(p.SubscriptionId),
            (request.PageNumber - 1) * request.PageSize,
            request.PageSize,
            q => q.OrderByDescending(x => x.CreatedAt),
            context.CancellationToken);

        var response = new Shared.Protos.TransactionHistoryResponse
        {
            TotalCount = totalCount
        };

        foreach (var p in items)
        {
            response.Items.Add(new Shared.Protos.PaymentTransaction
            {
                Id = p.Id.ToString(),
                SubscriptionId = p.SubscriptionId.ToString(),
                Amount = (double)p.Amount,
                TaxAmount = (double)p.TaxAmount,
                TotalAmount = (double)p.TotalAmount,
                Currency = p.Currency,
                PaymentMethod = p.PaymentMethod,
                Provider = p.Provider,
                ProviderTransactionId = p.ProviderTransactionId ?? string.Empty,
                Status = p.Status,
                FailureReason = p.FailureReason ?? string.Empty,
                PaidAt = p.PaidAt?.ToString("O") ?? string.Empty,
                CreatedAt = p.CreatedAt.ToString("O")
            });
        }

        return response;
    }

    private async Task<Shared.Protos.ProcessPaymentResponse> ProcessPaymentSuccessInternal(
        Shared.Protos.ProcessPaymentEventRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            return new Shared.Protos.ProcessPaymentResponse { Success = false, ErrorMessage = "Invalid workspace_id." };

        Guid.TryParse(request.UserId, out var userId); // Still good to have but workspace is primary

        _logger.LogInformation("Processing payment success via gRPC for Workspace {WorkspaceId}, User {UserId}, Amount: {Amount}", workspaceId, userId, request.Amount);

        var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
            s => s.WorkspaceId == workspaceId && s.DeletedAt == null && s.IsActive,
            context.CancellationToken);

        if (sub == null)
        {
            var oldSub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == workspaceId && s.DeletedAt == null,
                context.CancellationToken);

            if (oldSub == null)
            {
                _logger.LogWarning("No subscription found for Workspace {WorkspaceId}. Cannot process payment.", workspaceId);
                return new Shared.Protos.ProcessPaymentResponse { Success = false, ErrorMessage = "Subscription not found." };
            }
            sub = oldSub;
        }

        var planResult = await _planService.GetPlanByIdAsync(sub.PlanId, context.CancellationToken);
        if (!planResult.IsSuccess || planResult.Value == null)
        {
            return new Shared.Protos.ProcessPaymentResponse { Success = false, ErrorMessage = "Plan not found." };
        }
        var plan = planResult.Value;

        bool isRenewal = request.PaymentType == "SubscriptionRenewal";

        if (!sub.IsActive || isRenewal)
        {
            if (!sub.IsActive)
            {
                var activeSubs = await _unitOfWork.SubscriptionRepository.FindAsync(
                    s => s.WorkspaceId == workspaceId && s.IsActive && s.Id != sub.Id && s.DeletedAt == null);
                foreach (var activeSub in activeSubs)
                {
                    activeSub.IsActive = false;
                    activeSub.Status = "cancelled";
                    activeSub.CancelledAt = DateTime.UtcNow;
                    _unitOfWork.SubscriptionRepository.Update(activeSub);
                }
            }

            sub.Status = "active";
            sub.IsActive = true;
            sub.CurrentPeriodStart = DateTime.UtcNow;
            
            var baseDate = (isRenewal && sub.CurrentPeriodEnd > DateTime.UtcNow) ? sub.CurrentPeriodEnd : DateTime.UtcNow;

            sub.CurrentPeriodEnd = plan.BillingCycle switch
            {
                "yearly" => baseDate.AddYears(1),
                "semiannual" => baseDate.AddMonths(6),
                _ => baseDate.AddMonths(1)
            };
        }

        var existingTopup = await _unitOfWork.CreditTransactionRepository.FirstOrDefaultAsync(
            c => c.CorrelationId == request.StripeSessionId,
            context.CancellationToken
        );

        if (existingTopup == null)
        {
            sub.CreditsRemaining += plan.CreditsPerCycle;
            sub.CreditsUsedThisCycle = 0;
            sub.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.SubscriptionRepository.Update(sub);

            var topupTx = new WarpTalk.BillingService.Domain.Entities.CreditTransaction
            {
                SubscriptionId = sub.Id,
                UserId = userId,
                WorkspaceId = workspaceId,
                Amount = plan.CreditsPerCycle,
                Type = "top_up",
                Description = "Stripe Payment Success (gRPC)",
                ReferenceId = Guid.NewGuid(),
                CorrelationId = request.StripeSessionId,
                ReferenceType = "stripe_payment",
                Status = "committed",
                BalanceAfter = sub.CreditsRemaining,
                CreatedAt = DateTime.UtcNow
            };
            await _unitOfWork.CreditTransactionRepository.AddAsync(topupTx);

            var paymentTx = new WarpTalk.BillingService.Domain.Entities.Payment
            {
                Id = Guid.NewGuid(),
                SubscriptionId = sub.Id,
                UserId = userId,
                WorkspaceId = workspaceId,
                Amount = (decimal)request.Amount,
                TaxAmount = 0m,
                TotalAmount = (decimal)request.Amount,
                Currency = request.Currency,
                PaymentMethod = request.PaymentType,
                Provider = "stripe",
                ProviderTransactionId = request.StripeSessionId,
                Status = "paid",
                PaidAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            await _unitOfWork.PaymentRepository.AddAsync(paymentTx);



            await _unitOfWork.SaveChangesAsync(context.CancellationToken);
            _logger.LogInformation("Successfully updated subscription and added credits for Workspace {WorkspaceId} via gRPC", workspaceId);
        }
        else
        {
            _logger.LogInformation("Payment already processed for Stripe Session {SessionId}", request.StripeSessionId);
        }

        return new Shared.Protos.ProcessPaymentResponse { Success = true };
    }

    private async Task ProcessPaymentRefundOrDisputeInternal(
        WarpTalk.BillingService.Domain.Entities.Payment payment, ServerCallContext context)
    {
        _logger.LogInformation("Processing refund/dispute for Payment {PaymentId}, ProviderTxId: {ProviderTxId}", payment.Id, payment.ProviderTransactionId);

        var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
            s => s.Id == payment.SubscriptionId,
            context.CancellationToken);

        if (sub == null) return;

        var planResult = await _planService.GetPlanByIdAsync(sub.PlanId, context.CancellationToken);
        if (!planResult.IsSuccess || planResult.Value == null) return;
        var plan = planResult.Value;

        // Cancel the subscription immediately to prevent further abuse/usage
        if (sub.IsActive)
        {
            sub.IsActive = false;
            sub.Status = "cancelled";
            sub.CancelledAt = DateTime.UtcNow;
            _unitOfWork.SubscriptionRepository.Update(sub);
        }

        // Deduct the equivalent credits for the cycle
        // Allow it to go negative if they've already spent the credits.
        sub.CreditsRemaining -= plan.CreditsPerCycle;
        sub.UpdatedAt = DateTime.UtcNow;
        _unitOfWork.SubscriptionRepository.Update(sub);

        // Record the deduction
        var tx = new WarpTalk.BillingService.Domain.Entities.CreditTransaction
        {
            SubscriptionId = sub.Id,
            UserId = payment.UserId,
            WorkspaceId = payment.WorkspaceId,
            Amount = -plan.CreditsPerCycle,
            Type = "refund_deduction",
            Description = $"Credits deducted due to {payment.Status}",
            ReferenceId = payment.Id,
            CorrelationId = payment.ProviderTransactionId,
            ReferenceType = "stripe_payment",
            Status = "committed",
            BalanceAfter = sub.CreditsRemaining,
            CreatedAt = DateTime.UtcNow
        };
        await _unitOfWork.CreditTransactionRepository.AddAsync(tx);

        _logger.LogWarning("Cancelled subscription {SubId} and deducted {Amount} credits due to {Status}. New Balance: {Balance}",
            sub.Id, plan.CreditsPerCycle, payment.Status, sub.CreditsRemaining);
    }


    public override async Task<Shared.Protos.ProcessPaymentResponse> ProcessPaymentEvent(
        Shared.Protos.ProcessPaymentEventRequest request, ServerCallContext context)
    {
        var providerTxId = string.IsNullOrEmpty(request.ProviderTransactionId) ? request.StripeSessionId : request.ProviderTransactionId;
        if (string.IsNullOrEmpty(providerTxId))
        {
            return new Shared.Protos.ProcessPaymentResponse { Success = false, ErrorMessage = "Missing provider transaction ID." };
        }

        _logger.LogInformation("Processing payment event for ProviderTxId: {ProviderTxId}, Status: {Status}", providerTxId, request.Status);

        var existingPayment = await _unitOfWork.PaymentRepository.FirstOrDefaultAsync(
            p => p.ProviderTransactionId == providerTxId,
            context.CancellationToken);

        if (existingPayment != null)
        {
            if (existingPayment.Status == request.Status)
            {
                _logger.LogInformation("Payment {ProviderTxId} already in status {Status}. Ignoring (Idempotent).", providerTxId, request.Status);
                return new Shared.Protos.ProcessPaymentResponse { Success = true };
            }

            existingPayment.Status = request.Status;
            existingPayment.FailureReason = request.FailureReason;
            existingPayment.UpdatedAt = DateTime.UtcNow;

            if (request.Status == "paid") existingPayment.PaidAt = DateTime.UtcNow;

            _unitOfWork.PaymentRepository.Update(existingPayment);

            if (request.Status == "refunded" || request.Status == "disputed")
            {
                await ProcessPaymentRefundOrDisputeInternal(existingPayment, context);
            }



            await _unitOfWork.SaveChangesAsync(context.CancellationToken);
            return new Shared.Protos.ProcessPaymentResponse { Success = true };
        }

        if (request.Status == "paid")
        {
            return await ProcessPaymentSuccessInternal(request, context);
        }

        if (request.Status == "failed" && !string.IsNullOrEmpty(request.WorkspaceId))
        {
            // Record failed payment even if it doesn't exist
            if (Guid.TryParse(request.WorkspaceId, out var workspaceId))
            {
                var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                    s => s.WorkspaceId == workspaceId && s.DeletedAt == null && s.IsActive,
                    context.CancellationToken);

                if (sub != null)
                {
                    var paymentTx = new WarpTalk.BillingService.Domain.Entities.Payment
                    {
                        Id = Guid.NewGuid(),
                        SubscriptionId = sub.Id,
                        UserId = Guid.TryParse(request.UserId, out var uid) ? uid : Guid.Empty,
                        WorkspaceId = workspaceId,
                        Amount = (decimal)request.Amount,
                        TaxAmount = 0m,
                        TotalAmount = (decimal)request.Amount,
                        Currency = request.Currency,
                        PaymentMethod = request.PaymentType,
                        Provider = "stripe",
                        ProviderTransactionId = providerTxId,
                        Status = "failed",
                        FailureReason = request.FailureReason,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    await _unitOfWork.PaymentRepository.AddAsync(paymentTx, context.CancellationToken);
                    await _unitOfWork.SaveChangesAsync(context.CancellationToken);
                }
            }
        }

        return new Shared.Protos.ProcessPaymentResponse { Success = true };
    }


    // ─── Private helpers ──────────────────────────────────────────────────

    private static Shared.Protos.SubscriptionResponse ToSubscriptionResponse(SubscriptionDto dto) => new()
    {
        SubscriptionId = dto.Id.ToString(),
        Status = dto.Status,
        PlanId = dto.PlanId.ToString(),
        PlanName = dto.PlanName,
        WorkspaceId = dto.WorkspaceId?.ToString() ?? string.Empty,
        CreditsRemaining = dto.CreditsRemaining,
        CurrentPeriodStart = dto.CurrentPeriodStart.ToString("O"),
        CurrentPeriodEnd = dto.CurrentPeriodEnd.ToString("O"),
        AutoRenew = dto.AutoRenew,
        CancelledAt = dto.CancelledAt?.ToString("O") ?? string.Empty
    };

    private static Shared.Protos.PlanResponse ToPlanResponse(PlanDto dto) => new()
    {
        PlanId = dto.Id.ToString(),
        Name = dto.Name,
        Slug = dto.Slug,
        Tier = dto.Tier,
        Price = (double)dto.Price,
        Currency = dto.Currency,
        BillingCycle = dto.BillingCycle,
        CreditsPerCycle = dto.CreditsPerCycle,
        MaxParticipants = dto.MaxParticipants,
        MaxLanguages = dto.MaxLanguages,
        VoiceCloneEnabled = dto.VoiceCloneEnabled,
        AiAssistantEnabled = dto.AiAssistantEnabled,
        GlossaryEnabled = dto.GlossaryEnabled,
        DedicatedGpu = dto.DedicatedGpu,
        Features = dto.Features,
        SortOrder = dto.SortOrder
    };
}
