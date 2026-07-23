using System;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using Dtos = WarpTalk.BillingService.Application.DTOs;

namespace WarpTalk.BillingService.API.GrpcServices;

public partial class BillingServiceGrpc
{
    public override async Task<TransactionHistoryResponse> GetTransactionHistory(GetHistoryRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw GrpcErrors.InvalidId("Workspace");

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

    public override async Task<ProcessPaymentResponse> ProcessPaymentEvent(ProcessPaymentEventRequest request, ServerCallContext context)
    {
        try
        {
            await _paymentAppService.ProcessPaymentEventAsync(new Dtos.StripePaymentEventRequest(
                StripeSessionId: request.StripeSessionId,
                PaymentIntentId: request.ProviderTransactionId,
                Amount: (decimal)request.Amount,
                Currency: request.Currency,
                UserIdStr: request.UserId,
                WorkspaceIdStr: request.WorkspaceId,
                PaymentType: request.PaymentType,
                Status: request.Status,
                FailureReason: request.FailureReason,
                InvoiceUrl: request.InvoiceUrl,
                InvoicePdf: request.InvoicePdf,
                PlanSlug: request.PlanSlug,
                BillingCycle: request.BillingCycle
            ));

            return new ProcessPaymentResponse
            {
                Success = true,
                ErrorMessage = string.Empty
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "gRPC ProcessPaymentEvent failed");
            return new ProcessPaymentResponse
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }
}
