using System;
using WarpTalk.BillingService.API.Mappers;
using WarpTalk.BillingService.Domain.Constants;
using System.Threading.Tasks;
using Grpc.Core;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
namespace WarpTalk.BillingService.API.GrpcServices;

public partial class BillingServiceGrpc
{
    public override async Task<GetCreditsResponse> GetWorkspaceCredits(GetCreditsRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw GrpcErrors.InvalidId(BillingMessageConstants.Grpc.Workspace);

        var result = await _creditService.GetWorkspaceCreditsAsync(workspaceId, context.CancellationToken);
        
        if (!result.IsSuccess)
            throw GrpcErrors.NotFound(BillingMessageConstants.Grpc.Workspace, request.WorkspaceId);

        return result.Value.ToGrpc();
    }

    public override async Task<ConsumeCreditsResponse> ConsumeCredits(ConsumeCreditsRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw GrpcErrors.InvalidId(BillingMessageConstants.Grpc.Workspace);

        var result = await _creditService.ConsumeCreditsDirectlyAsync(
            workspaceId, 
            request.ToDto(workspaceId), 
            context.CancellationToken);

        if (!result.IsSuccess)
        {
            throw new RpcException(new Status(StatusCode.Internal, result.Error ?? BillingMessageConstants.Grpc.FailedToConsumeCredits));
        }

        return result.Value.ToConsumeCreditsResponse();
    }

    public override async Task<GetCreditsResponse> TopUpCredits(TopUpRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw GrpcErrors.InvalidId(BillingMessageConstants.Grpc.Workspace);

        var result = await _creditService.TopUpCreditsAsync(
            workspaceId, 
            request.ToDto(workspaceId), 
            context.CancellationToken);

        if (!result.IsSuccess)
            throw new RpcException(new Status(StatusCode.Internal, result.Error ?? BillingMessageConstants.Grpc.FailedToTopUpCredits));

        return result.Value.ToGrpc();
    }

    public override async Task<CreditHistoryResponse> GetCreditHistory(GetHistoryRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw GrpcErrors.InvalidId(BillingMessageConstants.Grpc.Workspace);

        var result = await _creditService.GetCreditHistoryAsync(
            workspaceId,
            request.ToCreditHistoryQuery(), 
            context.CancellationToken);

        if (!result.IsSuccess)
            throw new RpcException(new Status(StatusCode.Internal, result.Error ?? BillingMessageConstants.Grpc.FailedToFetchCreditHistory));

        return result.Value.ToGrpc();
    }
}
