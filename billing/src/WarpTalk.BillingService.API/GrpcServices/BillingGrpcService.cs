using Grpc.Core;
using System.Text.Json;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Events;

namespace WarpTalk.BillingService.API.GrpcServices;

/// <summary>
/// gRPC surface â€” thin adapter that delegates to Application services.
/// All business logic lives in the Application layer.
/// </summary>
public class BillingGrpcService : Shared.Protos.BillingService.BillingServiceBase
{
    private readonly ICreditService _creditService;
    private readonly IUsageService _usageService;
    private readonly IPlanService _planService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IPaymentAppService _paymentAppService;
    private readonly WarpTalk.BillingService.Domain.Interfaces.IUnitOfWork _unitOfWork;
    private readonly StackExchange.Redis.IConnectionMultiplexer _redis;
    private readonly ILogger<BillingGrpcService> _logger;

    public BillingGrpcService(
        ICreditService creditService,
        IUsageService usageService,
        IPlanService planService,
        ISubscriptionService subscriptionService,
        IPaymentAppService paymentAppService,
        WarpTalk.BillingService.Domain.Interfaces.IUnitOfWork unitOfWork,
        StackExchange.Redis.IConnectionMultiplexer redis,
        ILogger<BillingGrpcService> logger)
    {
        _creditService = creditService;
        _usageService = usageService;
        _planService = planService;
        _subscriptionService = subscriptionService;
        _paymentAppService = paymentAppService;
        _unitOfWork = unitOfWork;
        _redis = redis;
        _logger = logger;
    }

    // â”€â”€â”€ Credits â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

        var result = await _creditService.ConsumeCreditsDirectlyAsync(workspaceId, new ConsumeCreditsRequest(
            workspaceId,
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
        Guid? segmentId = Guid.TryParse(request.SegmentId, out var segId) ? segId : null;

        var dtoRequest = new RecordUsageRequest(
            hostWorkspaceId,
            userId,
            request.UsageType,
            request.Unit,
            (decimal)request.Quantity,
            request.CreditsConsumed,
            request.DurationSeconds > 0 ? request.DurationSeconds : null,
            translationRoomId,
            segmentId,
            string.IsNullOrWhiteSpace(request.DetailsJson) ? null : request.DetailsJson
        );

        var result = await _usageService.RecordUsageAsync(dtoRequest, context.CancellationToken);

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
            workspaceId,
            new CreditHistoryQuery { PageNumber = request.PageNumber, PageSize = request.PageSize },
            context.CancellationToken);

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

    // â”€â”€â”€ Subscriptions â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public override async Task<Shared.Protos.SubscriptionResponse> CreateSubscription(
        Shared.Protos.CreateSubscriptionRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid workspace_id."));
        if (!Guid.TryParse(request.PlanId, out var planId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid plan_id."));

        Guid.TryParse(request.UserId, out var userId);

        var result = await _subscriptionService.CreateSubscriptionAsync(
            new SubscriptionRequest(workspaceId, planId, userId),
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
            FeaturesJson = plan.Features ?? "{}",
            AllowGlossary = plan.GlossaryEnabled,
            AllowAcl = plan.AiAssistantEnabled
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

    // â”€â”€â”€ Plans â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

    // â”€â”€â”€ Payments â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

    public override async Task<Shared.Protos.ProcessPaymentResponse> ProcessPaymentEvent(
        Shared.Protos.ProcessPaymentEventRequest request, ServerCallContext context)
    {
        var appRequest = new StripePaymentEventRequest(
            request.StripeSessionId,
            request.ProviderTransactionId ?? string.Empty,
            (decimal)request.Amount,
            request.Currency ?? "VND",
            request.UserId ?? string.Empty,
            request.WorkspaceId ?? string.Empty,
            request.PaymentType ?? string.Empty,
            request.Status ?? string.Empty,
            request.FailureReason ?? string.Empty,
            request.InvoiceUrl ?? string.Empty,
            request.InvoicePdf ?? string.Empty,
            request.PlanSlug ?? string.Empty,
            request.BillingCycle ?? string.Empty
        );

        var result = await _paymentAppService.ProcessPaymentEventAsync(appRequest);
        if (!result.IsSuccess)
        {
            return new Shared.Protos.ProcessPaymentResponse { Success = false, ErrorMessage = result.Error };
        }

        // Enqueue outbox event for history/ledger tracking in legacy consumers
        await EnqueuePaymentEventAsync(request, context.CancellationToken);
        await _unitOfWork.SaveChangesAsync(context.CancellationToken);

        return new Shared.Protos.ProcessPaymentResponse { Success = true };
    }


    // â”€â”€â”€ Private helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private async Task EnqueuePaymentEventAsync(
        Shared.Protos.ProcessPaymentEventRequest request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            return;

        var eventId = Guid.NewGuid();
        var eventType = BillingEventTypes.ForStatus(request.Status);
        var payload = new BillingPaymentEventPayload(
            string.IsNullOrWhiteSpace(request.ProviderTransactionId)
                ? request.StripeSessionId
                : request.ProviderTransactionId,
            request.StripeSessionId,
            request.Status,
            (decimal)request.Amount,
            request.Currency ?? string.Empty,
            request.PaymentType ?? string.Empty,
            request.UserId ?? string.Empty,
            request.WorkspaceId,
            request.PlanSlug ?? string.Empty,
            request.BillingCycle ?? string.Empty,
            string.IsNullOrWhiteSpace(request.FailureReason)
                ? null
                : request.FailureReason);

        var envelope = new EventEnvelope<BillingPaymentEventPayload>(
            eventId,
            eventType,
            1,
            DateTime.UtcNow,
            "billing-service",
            null,
            payload.ProviderTransactionId,
            request.WorkspaceId,
            payload);

        await _unitOfWork.OutboxMessages.AddAsync(new WarpTalk.BillingService.Domain.Entities.OutboxMessage
        {
            Id = eventId,
            EventType = eventType,
            SchemaVersion = envelope.SchemaVersion,
            OccurredAt = envelope.OccurredAt,
            Producer = envelope.Producer,
            CausationId = envelope.CausationId,
            WorkspaceId = workspaceId,
            PayloadJson = JsonSerializer.Serialize(envelope),
            AvailableAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        }, cancellationToken);
    }

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
        CancelledAt = dto.CancelledAt?.ToString("O") ?? string.Empty,
        TrialEndsAt = dto.TrialEndsAt?.ToString("O") ?? string.Empty
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
        SortOrder = dto.SortOrder,
        AllowGlossary = dto.GlossaryEnabled,
        AllowAcl = dto.AiAssistantEnabled
    };
}
