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
    private readonly ICreditService       _creditService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IPlanService         _planService;
    private readonly IPaymentService      _paymentService;
    private readonly ILogger<BillingGrpcService> _logger;

    public BillingGrpcService(
        ICreditService       creditService,
        ISubscriptionService subscriptionService,
        IPlanService         planService,
        IPaymentService      paymentService,
        ILogger<BillingGrpcService> logger)
    {
        _creditService       = creditService;
        _subscriptionService = subscriptionService;
        _planService         = planService;
        _paymentService      = paymentService;
        _logger              = logger;
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
                WorkspaceId    = request.WorkspaceId,
                CurrentCredits = 0,
                Status         = "no_subscription"
            };

        var dto = result.Value!;
        return new Shared.Protos.GetCreditsResponse
        {
            WorkspaceId          = request.WorkspaceId,
            CurrentCredits       = dto.CurrentCredits,
            Status               = dto.Status,
            CreditsUsedThisCycle = dto.CreditsUsedThisCycle,
            CurrentPeriodStart   = dto.CurrentPeriodStart.ToString("O"),
            CurrentPeriodEnd     = dto.CurrentPeriodEnd.ToString("O")
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
                Success      = false,
                ErrorMessage = result.Error
            };

        return new Shared.Protos.ConsumeCreditsResponse
        {
            Success    = true,
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
            WorkspaceId          = request.WorkspaceId,
            CurrentCredits       = dto.CurrentCredits,
            Status               = dto.Status,
            CreditsUsedThisCycle = dto.CreditsUsedThisCycle,
            CurrentPeriodStart   = dto.CurrentPeriodStart.ToString("O"),
            CurrentPeriodEnd     = dto.CurrentPeriodEnd.ToString("O")
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
                Id           = tx.Id.ToString(),
                Amount       = tx.Amount,
                Type         = tx.Type,
                Description  = tx.Description ?? string.Empty,
                ReferenceType = tx.ReferenceType ?? string.Empty,
                ReferenceId  = tx.ReferenceId?.ToString() ?? string.Empty,
                BalanceAfter = tx.BalanceAfter,
                CreatedAt    = tx.CreatedAt.ToString("O")
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
            PlanTier             = plan.Tier ?? string.Empty,
            MaxParticipants      = plan.MaxParticipants,
            MaxLanguages         = plan.MaxLanguages,
            VoiceCloneEnabled    = plan.VoiceCloneEnabled,
            AiAssistantEnabled   = plan.AiAssistantEnabled,
            GlossaryEnabled      = plan.GlossaryEnabled,
            DedicatedGpu         = plan.DedicatedGpu,
            FeaturesJson         = plan.Features ?? "{}"
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

        return ToSubscriptionResponse(result.Value!);
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

        var result = await _paymentService.GetPaymentHistoryAsync(
            workspaceId, request.PageNumber, request.PageSize, context.CancellationToken);

        if (!result.IsSuccess)
            return new Shared.Protos.TransactionHistoryResponse { TotalCount = 0 };

        var response = new Shared.Protos.TransactionHistoryResponse
        {
            TotalCount = result.Value!.TotalCount
        };

        foreach (var p in result.Value.Items)
        {
            response.Items.Add(new Shared.Protos.PaymentTransaction
            {
                Id                    = p.Id.ToString(),
                SubscriptionId        = p.SubscriptionId.ToString(),
                Amount                = (double)p.Amount,
                TaxAmount             = (double)p.TaxAmount,
                TotalAmount           = (double)p.TotalAmount,
                Currency              = p.Currency,
                PaymentMethod         = p.PaymentMethod,
                Provider              = p.Provider,
                ProviderTransactionId = p.ProviderTransactionId ?? string.Empty,
                Status                = p.Status,
                FailureReason         = p.FailureReason ?? string.Empty,
                PaidAt                = p.PaidAt?.ToString("O") ?? string.Empty,
                CreatedAt             = p.CreatedAt.ToString("O")
            });
        }

        return response;
    }

    // ─── Private helpers ──────────────────────────────────────────────────

    private static Shared.Protos.SubscriptionResponse ToSubscriptionResponse(SubscriptionDto dto) => new()
    {
        SubscriptionId      = dto.Id.ToString(),
        Status              = dto.Status,
        PlanId              = dto.PlanId.ToString(),
        PlanName            = dto.PlanName,
        WorkspaceId         = dto.WorkspaceId?.ToString() ?? string.Empty,
        CreditsRemaining    = dto.CreditsRemaining,
        CurrentPeriodStart  = dto.CurrentPeriodStart.ToString("O"),
        CurrentPeriodEnd    = dto.CurrentPeriodEnd.ToString("O"),
        AutoRenew           = dto.AutoRenew,
        CancelledAt         = dto.CancelledAt?.ToString("O") ?? string.Empty
    };

    private static Shared.Protos.PlanResponse ToPlanResponse(PlanDto dto) => new()
    {
        PlanId              = dto.Id.ToString(),
        Name                = dto.Name,
        Slug                = dto.Slug,
        Tier                = dto.Tier,
        Price               = (double)dto.Price,
        Currency            = dto.Currency,
        BillingCycle        = dto.BillingCycle,
        CreditsPerCycle     = dto.CreditsPerCycle,
        MaxParticipants     = dto.MaxParticipants,
        MaxLanguages        = dto.MaxLanguages,
        VoiceCloneEnabled   = dto.VoiceCloneEnabled,
        AiAssistantEnabled  = dto.AiAssistantEnabled,
        GlossaryEnabled     = dto.GlossaryEnabled,
        DedicatedGpu        = dto.DedicatedGpu,
        Features            = dto.Features,
        SortOrder           = dto.SortOrder
    };
}
