using Grpc.Core;
using WarpTalk.Shared.Protos;
using WarpTalk.BillingService.Application.Interfaces;

namespace WarpTalk.BillingService.API.GrpcServices;

public class BillingServiceGrpc : WarpTalk.Shared.Protos.BillingService.BillingServiceBase
{
    private readonly IBillingService _billingService;
    private readonly ILogger<BillingServiceGrpc> _logger;

    public BillingServiceGrpc(IBillingService billingService, ILogger<BillingServiceGrpc> logger)
    {
        _billingService = billingService;
        _logger = logger;
    }

    public override async Task<GetCreditsResponse> GetWorkspaceCredits(GetCreditsRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid Workspace ID"));

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
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
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
}
