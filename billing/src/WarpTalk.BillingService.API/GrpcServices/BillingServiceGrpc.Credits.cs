using System;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;

using Dtos = WarpTalk.BillingService.Application.DTOs;

namespace WarpTalk.BillingService.API.GrpcServices;

public partial class BillingServiceGrpc
{
    public override async Task<GetCreditsResponse> GetWorkspaceCredits(GetCreditsRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw GrpcErrors.InvalidId("Workspace");

        var result = await _creditService.GetWorkspaceCreditsAsync(workspaceId, context.CancellationToken);
        
        if (!result.IsSuccess)
            throw GrpcErrors.NotFound("Workspace", request.WorkspaceId);

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
            throw GrpcErrors.InvalidId("Workspace");

        Guid? referenceId = null;
        if (!string.IsNullOrEmpty(request.ReferenceId) && Guid.TryParse(request.ReferenceId, out var parsedRefId))
        {
            referenceId = parsedRefId;
        }

        var result = await _creditService.ConsumeCreditsDirectlyAsync(
            workspaceId, 
            new Dtos.ConsumeCreditsRequest(workspaceId, request.Amount, request.ReferenceType, referenceId), 
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
            throw GrpcErrors.InvalidId("Workspace");

        var result = await _creditService.TopUpCreditsAsync(
            workspaceId, 
            new Dtos.TopUpRequest(workspaceId, request.Amount, request.ReferenceType, null), 
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

    public override async Task<CreditHistoryResponse> GetCreditHistory(GetHistoryRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw GrpcErrors.InvalidId("Workspace");

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
}
