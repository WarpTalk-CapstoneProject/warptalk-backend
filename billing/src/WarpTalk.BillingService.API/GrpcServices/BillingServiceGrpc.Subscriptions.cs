using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.API.Extensions;
using WarpTalk.BillingService.API.Mappers;
using System;
using System.Threading.Tasks;
using Grpc.Core;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
namespace WarpTalk.BillingService.API.GrpcServices;

public partial class BillingServiceGrpc
{
    public override async Task<SubscriptionResponse> CreateSubscription(CreateSubscriptionRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw GrpcErrors.InvalidId(BillingMessageConstants.Grpc.Workspace);

        await _workspaceAuthService.AuthorizeWorkspaceAsync(workspaceId, context);

        if (!Guid.TryParse(request.PlanId, out var planId))
            throw GrpcErrors.InvalidId(BillingMessageConstants.Grpc.Plan);

        var result = await _subscriptionService.CreateSubscriptionAsync(request.ToDto(workspaceId, planId), context.CancellationToken);

        if (!result.IsSuccess)
        {
            throw new RpcException(new Status(StatusCode.Internal, result.Error ?? BillingMessageConstants.Grpc.FailedToCreateSubscription));
        }

        return result.Value!.ToGrpc();
    }

    public override async Task<SubscriptionResponse> GetActiveSubscription(GetActiveSubscriptionRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw GrpcErrors.InvalidId(BillingMessageConstants.Grpc.Workspace);

        var result = await _subscriptionService.GetActiveSubscriptionAsync(workspaceId, context.CancellationToken);

        if (!result.IsSuccess)
        {
            return GrpcBillingMapper.ToEmptySubscriptionResponse(result.Error ?? BillingMessageConstants.Grpc.NoActiveSubscription);
        }

        return result.Value!.ToGrpc();
    }

    public override async Task<SubscriptionResponse> CancelSubscription(CancelSubscriptionRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw GrpcErrors.InvalidId(BillingMessageConstants.Grpc.Workspace);

        var result = await _subscriptionService.CancelSubscriptionAsync(workspaceId, request.Reason, context.CancellationToken);

        if (!result.IsSuccess)
        {
            throw new RpcException(new Status(StatusCode.Internal, result.Error ?? BillingMessageConstants.Grpc.FailedToCancelSubscription));
        }

        var latestSub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
            s => s.WorkspaceId == workspaceId && s.DeletedAt == null,
            context.CancellationToken);

        if (latestSub != null)
        {
            var plan = await _unitOfWork.PlanRepository.GetByIdAsync(latestSub.PlanId, context.CancellationToken);
            return latestSub.ToGrpc(plan?.Name ?? BillingMessageConstants.Grpc.UnknownPlan);
        }

        return GrpcBillingMapper.ToCancelledSubscriptionResponse();
    }

    public override async Task<GetFeatureAccessResponse> GetWorkspaceFeatureAccess(GetFeatureAccessRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw GrpcErrors.InvalidId(BillingMessageConstants.Grpc.Workspace);

        var latestSub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
            s => s.WorkspaceId == workspaceId && s.DeletedAt == null,
            context.CancellationToken);

        if (latestSub == null)
        {
            return GrpcBillingMapper.ToEmptyFeatureAccessResponse();
        }

        var plan = await _unitOfWork.PlanRepository.GetByIdAsync(latestSub.PlanId, context.CancellationToken);

        return latestSub.ToFeatureAccessResponse(plan);
    }
}
