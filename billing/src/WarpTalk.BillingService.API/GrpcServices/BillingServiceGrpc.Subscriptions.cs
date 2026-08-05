using WarpTalk.BillingService.Domain.Constants;

using WarpTalk.BillingService.API.Mappers;
using System;
using System.Threading.Tasks;
using Grpc.Core;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using Entities = WarpTalk.BillingService.Domain.Entities;
namespace WarpTalk.BillingService.API.GrpcServices;

public partial class BillingServiceGrpc
{
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
            var plan = await _unitOfWork.Plans.GetByIdAsync(latestSub.PlanId, context.CancellationToken);
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

        var plan = await _unitOfWork.Plans.GetByIdAsync(latestSub.PlanId, context.CancellationToken);

        return latestSub.ToFeatureAccessResponse(plan);
    }

    /// <summary>
    /// WT-263: accepts a workspace's own entitlement settings, enforcing tighten-not-loosen at the
    /// boundary, then re-resolves and enqueues a fresh snapshot.
    ///
    /// A loosening request is REJECTED rather than clamped. Clamping would tell the owner their
    /// setting was saved while quietly storing a different number, and the next screen they opened
    /// would disagree with what they typed.
    /// </summary>
    public override async Task<ApplyWorkspaceEntitlementOverridesResponse> ApplyWorkspaceEntitlementOverrides(
        ApplyWorkspaceEntitlementOverridesRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
            throw GrpcErrors.InvalidId(BillingMessageConstants.Grpc.Workspace);

        var ct = context.CancellationToken;
        Guid? setBy = Guid.TryParse(request.SetByUserId, out var parsedUser) ? parsedUser : null;

        // Validate EVERY requested value before writing ANY of them. A partial apply would leave the
        // workspace with half of a settings save, which is worse than rejecting the whole thing.
        foreach (var item in request.Overrides)
        {
            var rejection = await _entitlementResolver.ValidateWorkspaceOverrideAsync(
                workspaceId, item.EntitlementKey, item.Value, ct);
            if (rejection != null)
            {
                return new ApplyWorkspaceEntitlementOverridesResponse
                {
                    Accepted = false,
                    ErrorMessage = rejection
                };
            }
        }

        foreach (var item in request.Overrides)
        {
            var existing = await _unitOfWork.WorkspaceEntitlementOverrides.GetAsync(
                workspaceId, item.EntitlementKey, ct);

            if (existing == null)
            {
                await _unitOfWork.WorkspaceEntitlementOverrides.AddAsync(
                    new Entities.WorkspaceEntitlementOverride
                    {
                        WorkspaceId = workspaceId,
                        EntitlementKey = item.EntitlementKey,
                        Value = item.Value,
                        SetBy = setBy,
                        UpdatedAt = DateTime.UtcNow
                    },
                    ct);
            }
            else
            {
                existing.Value = item.Value;
                existing.SetBy = setBy;
                existing.UpdatedAt = DateTime.UtcNow;
                _unitOfWork.WorkspaceEntitlementOverrides.Update(existing);
            }
        }

        await _unitOfWork.SaveChangesAsync(ct);

        await _entitlementChangePublisher.EnqueueAsync(
            workspaceId,
            EntitlementConstants.Reasons.WorkspaceOverrideChanged,
            ct);
        await _unitOfWork.SaveChangesAsync(ct);

        return new ApplyWorkspaceEntitlementOverridesResponse { Accepted = true, ErrorMessage = "" };
    }
}
