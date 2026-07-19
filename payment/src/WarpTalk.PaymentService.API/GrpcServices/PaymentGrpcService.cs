using System;
using System.Threading.Tasks;
using Grpc.Core;
using WarpTalk.PaymentService.Application.Interfaces;
using WarpTalk.Shared.Protos;

namespace WarpTalk.PaymentService.API.GrpcServices;

public class PaymentGrpcService : WarpTalk.Shared.Protos.PaymentService.PaymentServiceBase
{
    private readonly IStripePaymentService _stripePaymentService;

    public PaymentGrpcService(IStripePaymentService stripePaymentService)
    {
        _stripePaymentService = stripePaymentService;
    }

    public override async Task<CreateCheckoutSessionResponse> CreateCheckoutSession(
        CreateCheckoutSessionRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.UserId, out var userId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid user_id"));
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid workspace_id"));

        var url = await _stripePaymentService.CreateCheckoutSessionAsync(
            userId, workspaceId, (decimal)request.Amount, request.Currency, request.PaymentType);

        return new CreateCheckoutSessionResponse { Url = url };
    }

    public override async Task<CancelStripeSubscriptionResponse> CancelStripeSubscription(
        CancelStripeSubscriptionRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid workspace_id"));

        var success = await _stripePaymentService.CancelSubscriptionAsync(workspaceId);

        return new CancelStripeSubscriptionResponse
        {
            Success = success,
            ErrorMessage = success ? string.Empty : "Subscription not found or cancellation failed."
        };
    }

    public override async Task<UpdateStripeSubscriptionResponse> UpdateStripeSubscription(
        UpdateStripeSubscriptionRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid workspace_id"));

        var success = await _stripePaymentService.UpdateSubscriptionAsync(workspaceId, (decimal)request.NewAmount, request.Currency, request.NewPlanName);

        return new UpdateStripeSubscriptionResponse
        {
            Success = success,
            ErrorMessage = success ? string.Empty : "Subscription not found or update failed."
        };
    }

    public override async Task<GetPaymentStatusResponse> GetPaymentStatus(
        GetPaymentStatusRequest request, ServerCallContext context)
    {
        if (string.IsNullOrEmpty(request.ProviderTransactionId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Provider transaction ID is required"));

        var (status, failureReason) = await _stripePaymentService.GetPaymentStatusAsync(request.ProviderTransactionId);

        return new GetPaymentStatusResponse
        {
            Status = status,
            FailureReason = failureReason
        };
    }

    public override async Task<RefundPaymentResponse> RefundPayment(
        RefundPaymentRequest request, ServerCallContext context)
    {
        if (string.IsNullOrEmpty(request.ProviderTransactionId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Provider transaction ID is required"));

        var success = await _stripePaymentService.RefundPaymentAsync(request.ProviderTransactionId);

        return new RefundPaymentResponse
        {
            Success = success,
            ErrorMessage = success ? string.Empty : "Refund failed or not eligible."
        };
    }
}
