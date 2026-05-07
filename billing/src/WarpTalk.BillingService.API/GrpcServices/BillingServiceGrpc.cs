using Grpc.Core;
using WarpTalk.Shared.Protos;
using WarpTalk.BillingService.Application.Interfaces;

namespace WarpTalk.BillingService.API.GrpcServices;

public class BillingServiceGrpc : WarpTalk.Shared.Protos.BillingService.BillingServiceBase
{
    private readonly IBillingService _billingService;
    private readonly ILogger<BillingServiceGrpc> _logger;
    private const string DefaultTestWorkspaceId = "550e8400-e29b-41d4-a716-446655440005";

    public BillingServiceGrpc(IBillingService billingService, ILogger<BillingServiceGrpc> logger)
    {
        _billingService = billingService;
        _logger = logger;
    }

    public override async Task<GetCreditsResponse> GetWorkspaceCredits(GetCreditsRequest request, ServerCallContext context)
    {
        var rawId = request.WorkspaceId?.Trim();
        
        // Auto-fallback for testing if ID is missing or invalid
        if (string.IsNullOrEmpty(rawId) || rawId == "string")
        {
            _logger.LogInformation("No valid WorkspaceId provided, falling back to test ID: {TestId}", DefaultTestWorkspaceId);
            rawId = DefaultTestWorkspaceId;
        }

        if (!Guid.TryParse(rawId, out var workspaceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Invalid Workspace ID format: '{rawId}'. Must be a GUID."));

        var result = await _billingService.GetWorkspaceCreditsAsync(workspaceId, context.CancellationToken);
        
        if (!result.IsSuccess)
            throw new RpcException(new Status(StatusCode.NotFound, result.Error ?? "Workspace not found"));

        return new GetCreditsResponse
        {
            WorkspaceId = result.Value.WorkspaceId.ToString(),
            CurrentCredits = result.Value.CurrentCredits,
            Status = result.Value.SubscriptionStatus
        };
    }

    public override async Task<ConsumeCreditsResponse> ConsumeCredits(ConsumeCreditsRequest request, ServerCallContext context)
    {
        var rawId = request.WorkspaceId?.Trim();
        if (string.IsNullOrEmpty(rawId) || rawId == "string") rawId = DefaultTestWorkspaceId;

        if (!Guid.TryParse(rawId, out var workspaceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid Workspace ID"));

        Guid? referenceId = null;
        if (!string.IsNullOrEmpty(request.ReferenceId) && Guid.TryParse(request.ReferenceId, out var parsedRefId))
        {
            referenceId = parsedRefId;
        }

        var result = await _billingService.ConsumeCreditsAsync(
            workspaceId, 
            request.Amount, 
            request.ReferenceType, 
            referenceId, 
            context.CancellationToken);

        return new ConsumeCreditsResponse
        {
            Success = result.IsSuccess,
            NewBalance = result.IsSuccess ? result.Value.CurrentCredits : 0,
            ErrorMessage = result.Error ?? string.Empty
        };
    }

    public override async Task<GetCreditsResponse> TopUpCredits(TopUpRequest request, ServerCallContext context)
    {
        var rawId = request.WorkspaceId?.Trim();
        if (string.IsNullOrEmpty(rawId) || rawId == "string") rawId = DefaultTestWorkspaceId;

        if (!Guid.TryParse(rawId, out var workspaceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid Workspace ID"));

        var result = await _billingService.TopUpCreditsAsync(
            workspaceId, 
            request.Amount, 
            request.ReferenceType, 
            null, 
            context.CancellationToken);

        if (!result.IsSuccess)
            throw new RpcException(new Status(StatusCode.Internal, result.Error ?? "Failed to top up credits"));

        return new GetCreditsResponse
        {
            WorkspaceId = result.Value.WorkspaceId.ToString(),
            CurrentCredits = result.Value.CurrentCredits,
            Status = result.Value.SubscriptionStatus
        };
    }

    public override async Task<SubscriptionResponse> CreateSubscription(CreateSubscriptionRequest request, ServerCallContext context)
    {
        var rawId = request.WorkspaceId?.Trim();
        if (string.IsNullOrEmpty(rawId) || rawId == "string") rawId = DefaultTestWorkspaceId;

        if (!Guid.TryParse(rawId, out var workspaceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid Workspace ID"));

        if (!Guid.TryParse(request.PlanId, out var planId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid Plan ID"));

        var result = await _billingService.CreateSubscriptionAsync(workspaceId, planId, context.CancellationToken);

        return new SubscriptionResponse
        {
            SubscriptionId = result.IsSuccess ? result.Value.Id.ToString() : string.Empty,
            Status = result.IsSuccess ? result.Value.Status : "Failed",
            ErrorMessage = result.Error ?? string.Empty
        };
    }

    public override async Task<SubscriptionResponse> GetActiveSubscription(GetCreditsRequest request, ServerCallContext context)
    {
        var rawId = request.WorkspaceId?.Trim();
        if (string.IsNullOrEmpty(rawId) || rawId == "string") rawId = DefaultTestWorkspaceId;

        if (!Guid.TryParse(rawId, out var workspaceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid Workspace ID"));

        var result = await _billingService.GetActiveSubscriptionAsync(workspaceId, context.CancellationToken);
        
        if (!result.IsSuccess)
        {
            return new SubscriptionResponse
            {
                Status = "None",
                ErrorMessage = result.Error ?? "No active subscription"
            };
        }

        return new SubscriptionResponse
        {
            SubscriptionId = result.Value.Id.ToString(),
            Status = result.Value.Status,
            ErrorMessage = string.Empty
        };
    }

    public override async Task<SubscriptionResponse> CancelSubscription(CancelSubscriptionRequest request, ServerCallContext context)
    {
        var rawId = request.WorkspaceId?.Trim();
        if (string.IsNullOrEmpty(rawId) || rawId == "string") rawId = DefaultTestWorkspaceId;

        if (!Guid.TryParse(rawId, out var workspaceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid Workspace ID"));

        var result = await _billingService.CancelSubscriptionAsync(workspaceId, request.Reason, context.CancellationToken);

        return new SubscriptionResponse
        {
            Status = result.IsSuccess ? "Cancelled" : "Failed",
            ErrorMessage = result.Error ?? string.Empty
        };
    }

    public override async Task<CreditHistoryResponse> GetCreditHistory(GetHistoryRequest request, ServerCallContext context)
    {
        var rawId = request.WorkspaceId?.Trim();
        if (string.IsNullOrEmpty(rawId) || rawId == "string") rawId = DefaultTestWorkspaceId;

        if (!Guid.TryParse(rawId, out var workspaceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid Workspace ID"));

        var result = await _billingService.GetCreditHistoryAsync(
            workspaceId, 
            request.PageNumber > 0 ? request.PageNumber : 1, 
            request.PageSize > 0 ? request.PageSize : 50, 
            context.CancellationToken);

        if (!result.IsSuccess)
            throw new RpcException(new Status(StatusCode.Internal, result.Error ?? "Failed to fetch credit history"));

        var response = new CreditHistoryResponse { TotalCount = result.Value.TotalCount };
        response.Items.AddRange(result.Value.Items.Select(x => new CreditTransaction
        {
            Id = x.Id.ToString(),
            Amount = x.Amount,
            Type = x.Type,
            ReferenceType = x.ReferenceType ?? string.Empty,
            ReferenceId = x.ReferenceId?.ToString() ?? string.Empty,
            CreatedAt = x.CreatedAt.ToString("o")
        }));

        return response;
    }

    public override async Task<TransactionHistoryResponse> GetTransactionHistory(GetHistoryRequest request, ServerCallContext context)
    {
        var rawId = request.WorkspaceId?.Trim();
        if (string.IsNullOrEmpty(rawId) || rawId == "string") rawId = DefaultTestWorkspaceId;

        if (!Guid.TryParse(rawId, out var workspaceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid Workspace ID"));

        var result = await _billingService.GetTransactionHistoryAsync(
            workspaceId, 
            request.PageNumber > 0 ? request.PageNumber : 1, 
            request.PageSize > 0 ? request.PageSize : 50, 
            context.CancellationToken);

        if (!result.IsSuccess)
            throw new RpcException(new Status(StatusCode.Internal, result.Error ?? "Failed to fetch transaction history"));

        var response = new TransactionHistoryResponse { TotalCount = result.Value.TotalCount };
        response.Items.AddRange(result.Value.Items.Select(x => new PaymentTransaction
        {
            Id = x.Id.ToString(),
            Amount = (double)x.Amount,
            Status = x.Status,
            CreatedAt = x.CreatedAt.ToString("o")
        }));

        return response;
    }
}
