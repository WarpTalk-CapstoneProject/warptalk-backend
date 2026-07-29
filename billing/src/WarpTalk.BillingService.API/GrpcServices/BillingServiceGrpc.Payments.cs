using System;
using WarpTalk.BillingService.API.Mappers;
using WarpTalk.BillingService.Domain.Constants;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;

namespace WarpTalk.BillingService.API.GrpcServices;

public partial class BillingServiceGrpc
{
    public override async Task<TransactionHistoryResponse> GetTransactionHistory(GetHistoryRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw GrpcErrors.InvalidId(BillingMessageConstants.Grpc.Workspace);

        var result = await _paymentService.GetPaymentHistoryAsync(
            workspaceId,
            request.ToPaginationQuery(),
            context.CancellationToken);

        if (!result.IsSuccess)
            throw new RpcException(new Status(StatusCode.Internal, result.Error ?? BillingMessageConstants.Grpc.FailedToFetchTransactionHistory));

        return result.Value!.ToGrpc();
    }

    public override async Task<ProcessPaymentResponse> ProcessPaymentEvent(ProcessPaymentEventRequest request, ServerCallContext context)
    {
        try
        {
            var result = await _paymentAppService.ProcessPaymentEventAsync(request.ToDto());
            return result.ToProcessPaymentResponse();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.Grpc.ProcessPaymentEventFailed);
            return new ProcessPaymentResponse
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }
}
