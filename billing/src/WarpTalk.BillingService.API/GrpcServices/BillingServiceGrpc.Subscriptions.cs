using WarpTalk.BillingService.Domain.Constants;
using System;
using System.Threading.Tasks;
using Grpc.Core;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;

using Dtos = WarpTalk.BillingService.Application.DTOs;

namespace WarpTalk.BillingService.API.GrpcServices;

public partial class BillingServiceGrpc
{
    public override async Task<SubscriptionResponse> CreateSubscription(CreateSubscriptionRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw GrpcErrors.InvalidId("Workspace");

        await AuthorizeWorkspaceAsync(workspaceId, context);

        if (!Guid.TryParse(request.PlanId, out var planId))
            throw GrpcErrors.InvalidId("Plan");

        var result = await _subscriptionService.CreateSubscriptionAsync(new Dtos.SubscriptionRequest(workspaceId, planId, Guid.Empty), context.CancellationToken);

        if (!result.IsSuccess)
        {
            throw new RpcException(new Status(StatusCode.Internal, result.Error ?? "Failed to create subscription"));
        }

        return MapToSubscriptionResponse(result.Value);
    }

    public override async Task<SubscriptionResponse> GetActiveSubscription(GetActiveSubscriptionRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw GrpcErrors.InvalidId("Workspace");

        var result = await _subscriptionService.GetActiveSubscriptionAsync(workspaceId, context.CancellationToken);
        
        if (!result.IsSuccess)
        {
            return new SubscriptionResponse
            {
                Status = "None",
                ErrorMessage = result.Error ?? "No active subscription"
            };
        }

        return MapToSubscriptionResponse(result.Value);
    }

    public override async Task<SubscriptionResponse> CancelSubscription(CancelSubscriptionRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw GrpcErrors.InvalidId("Workspace");

        var result = await _subscriptionService.CancelSubscriptionAsync(workspaceId, request.Reason, context.CancellationToken);

        if (!result.IsSuccess)
        {
            throw new RpcException(new Status(StatusCode.Internal, result.Error ?? "Failed to cancel subscription"));
        }

        var latestSub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
            s => s.WorkspaceId == workspaceId && s.DeletedAt == null,
            context.CancellationToken);

        if (latestSub != null)
        {
            var plan = await _unitOfWork.PlanRepository.GetByIdAsync(latestSub.PlanId, context.CancellationToken);
            return MapToSubscriptionResponse(latestSub, plan?.Name ?? "Unknown Plan");
        }

        return new SubscriptionResponse
        {
            Status = "Cancelled",
            ErrorMessage = string.Empty
        };
    }

    public override async Task<GetFeatureAccessResponse> GetWorkspaceFeatureAccess(GetFeatureAccessRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw GrpcErrors.InvalidId("Workspace");

        var latestSub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
            s => s.WorkspaceId == workspaceId && s.DeletedAt == null,
            context.CancellationToken);

        if (latestSub == null)
        {
            return new GetFeatureAccessResponse
            {
                HasActiveSubscription = false
            };
        }

        var plan = await _unitOfWork.PlanRepository.GetByIdAsync(latestSub.PlanId, context.CancellationToken);

        bool hasActiveSubscription = latestSub.IsActive && 
                                     latestSub.Status == SubscriptionConstants.SubscriptionStatuses.Active && 
                                     latestSub.CurrentPeriodEnd >= DateTime.UtcNow;

        return new GetFeatureAccessResponse
        {
            HasActiveSubscription = hasActiveSubscription,
            PlanTier = plan?.Tier ?? "Free",
            MaxParticipants = plan?.MaxParticipants ?? 2,
            MaxLanguages = 999, // Unrestricted by plan
            VoiceCloneEnabled = true, // Universal access, paid per usage
            AiAssistantEnabled = true, // Universal access, paid per usage
            GlossaryEnabled = true,
            DedicatedGpu = plan?.Tier == "Enterprise",
            FeaturesJson = plan?.Features ?? "{}",
            AllowGlossary = true,
            AllowAcl = plan?.Tier == "Enterprise"
        };
    }
}
