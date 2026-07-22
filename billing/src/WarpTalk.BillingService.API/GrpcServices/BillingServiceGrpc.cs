using Grpc.Core;
using WarpTalk.Shared.Protos;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Enums;
using Dtos = WarpTalk.BillingService.Application.DTOs;

namespace WarpTalk.BillingService.API.GrpcServices;

public class BillingServiceGrpc : WarpTalk.Shared.Protos.BillingService.BillingServiceBase
{
    private readonly ICreditService _creditService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IPaymentAndLedgerService _paymentService;
    private readonly ILogger<BillingServiceGrpc> _logger;

    public BillingServiceGrpc(
        ICreditService creditService, 
        ISubscriptionService subscriptionService,
        IPaymentAndLedgerService paymentService,
        ILogger<BillingServiceGrpc> logger)
    {
        _creditService = creditService;
        _subscriptionService = subscriptionService;
        _paymentService = paymentService;
        _logger = logger;
    }

    public override async Task<GetCreditsResponse> GetWorkspaceCredits(GetCreditsRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid Workspace ID"));
        }

        var result = await _creditService.GetWorkspaceCreditsAsync(workspaceId, context.CancellationToken);
        
        if (!result.IsSuccess)
            throw new RpcException(new Status(StatusCode.NotFound, result.Error ?? "Workspace not found"));

        return new GetCreditsResponse
        {
            WorkspaceId = result.Value.WorkspaceId.ToString(),
            CurrentCredits = result.Value.CurrentCredits,
            Status = result.Value.Status
        };
    }

    public override async Task<ConsumeCreditsResponse> ConsumeCredits(ConsumeCreditsRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid Workspace ID"));
        }

        Guid? referenceId = null;
        if (!string.IsNullOrEmpty(request.ReferenceId) && Guid.TryParse(request.ReferenceId, out var parsedRefId))
        {
            referenceId = parsedRefId;
        }

        CreditReferenceType referenceType = CreditReferenceType.Unknown;
        if (Enum.TryParse<CreditReferenceType>(request.ReferenceType, true, out var parsedRefType))
        {
            referenceType = parsedRefType;
        }

        var result = await _creditService.ConsumeCreditsAsync(
            workspaceId, 
            new Dtos.ConsumeCreditsRequest(workspaceId, request.Amount, referenceType, referenceId), 
            context.CancellationToken);

        if (!result.IsSuccess)
        {
            throw new RpcException(new Status(StatusCode.Internal, result.Error ?? "Failed to consume credits"));
        }

        return new ConsumeCreditsResponse
        {
            Success = true,
            NewBalance = result.Value.BalanceAfter,
            ErrorMessage = string.Empty
        };
    }

    public override async Task<GetCreditsResponse> TopUpCredits(TopUpRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid Workspace ID"));
        }

        CreditReferenceType referenceType = CreditReferenceType.Unknown;
        if (Enum.TryParse<CreditReferenceType>(request.ReferenceType, true, out var parsedRefType))
        {
            referenceType = parsedRefType;
        }

        var result = await _creditService.TopUpCreditsAsync(
            workspaceId, 
            new Dtos.TopUpRequest(workspaceId, request.Amount, referenceType, null), 
            context.CancellationToken);

        if (!result.IsSuccess)
            throw new RpcException(new Status(StatusCode.Internal, result.Error ?? "Failed to top up credits"));

        return new GetCreditsResponse
        {
            WorkspaceId = result.Value.WorkspaceId.ToString(),
            CurrentCredits = result.Value.CurrentCredits,
            Status = result.Value.Status
        };
    }

    public override async Task<SubscriptionResponse> CreateSubscription(CreateSubscriptionRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid Workspace ID"));
        }

        if (!Guid.TryParse(request.PlanId, out var planId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid Plan ID"));

        var result = await _subscriptionService.CreateSubscriptionAsync(new Dtos.SubscriptionRequest(workspaceId, planId, Guid.Empty), context.CancellationToken);

        if (!result.IsSuccess)
        {
            throw new RpcException(new Status(StatusCode.Internal, result.Error ?? "Failed to create subscription"));
        }

        return new SubscriptionResponse
        {
            SubscriptionId = result.Value.Id.ToString(),
            Status = result.Value.Status,
            ErrorMessage = string.Empty
        };
    }

    public override async Task<SubscriptionResponse> GetActiveSubscription(GetActiveSubscriptionRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid Workspace ID"));
        }

        var result = await _subscriptionService.GetActiveSubscriptionAsync(workspaceId, context.CancellationToken);
        
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
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid Workspace ID"));
        }

        var result = await _subscriptionService.CancelSubscriptionAsync(workspaceId, request.Reason, context.CancellationToken);

        if (!result.IsSuccess)
        {
            throw new RpcException(new Status(StatusCode.Internal, result.Error ?? "Failed to cancel subscription"));
        }

        return new SubscriptionResponse
        {
            Status = "Cancelled",
            ErrorMessage = string.Empty
        };
    }

    public override async Task<CreditHistoryResponse> GetCreditHistory(GetHistoryRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid Workspace ID"));
        }

        var result = await _creditService.GetCreditHistoryAsync(
            workspaceId,
            new Dtos.CreditHistoryQuery() with
            {
                PageNumber = request.PageNumber > 0 ? request.PageNumber : 1,
                PageSize = request.PageSize > 0 ? request.PageSize : 50
            }, 
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
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid Workspace ID"));
        }

        var result = await _paymentService.GetPaymentHistoryAsync(
            workspaceId, 
            new Dtos.PaginationQuery(
                request.PageNumber > 0 ? request.PageNumber : 1, 
                request.PageSize > 0 ? request.PageSize : 50
            ), 
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
