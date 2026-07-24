using System;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Mappers;

public static class SubscriptionMapper
{
    public static SubscriptionDto ToDto(this Subscription sub, string planName, decimal price) => new(
        sub.Id,
        sub.UserId,
        sub.WorkspaceId,
        sub.PlanId,
        planName,
        price,
        sub.Status.ToLowerInvariant(),
        sub.CreditsRemaining,
        sub.CreditsUsedThisCycle,
        sub.CurrentPeriodStart,
        sub.CurrentPeriodEnd,
        sub.AutoRenew,
        !sub.AutoRenew,
        sub.CreatedAt,
        sub.CancelledAt
    );

    public static Subscription ToEntity(this SubscriptionRequest request, Plan plan)
    {
        var now = DateTime.UtcNow;
        return new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = request.UserId ?? Guid.Empty,
            WorkspaceId = request.WorkspaceId,
            PlanId = request.PlanId,
            Status = SubscriptionConstants.SubscriptionStatuses.Pending,
            CreditsRemaining = 0,
            CreditsUsedThisCycle = 0,
            CurrentPeriodStart = now,
            CurrentPeriodEnd = now,
            AutoRenew = true,
            IsActive = false,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static Subscription ToEntity(this SubscriptionRequest request, Subscription oldSub, Plan newPlan)
    {
        var now = DateTime.UtcNow;
        return new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = oldSub.UserId,
            WorkspaceId = oldSub.WorkspaceId,
            PlanId = newPlan.Id,
            Status = SubscriptionConstants.SubscriptionStatuses.Pending,
            CreditsRemaining = oldSub.CreditsRemaining,
            CreditsUsedThisCycle = 0,
            CurrentPeriodStart = now,
            CurrentPeriodEnd = now,
            AutoRenew = true,
            IsActive = false,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static void Cancel(this Subscription sub, string? reason)
    {
        var now = DateTime.UtcNow;
        sub.CancellationReason = reason;
        sub.AutoRenew = false;
        sub.Status = SubscriptionConstants.SubscriptionStatuses.Cancelled;
        sub.UpdatedAt = now;
    }

    public static void CancelImmediately(this Subscription sub, string? reason)
    {
        var now = DateTime.UtcNow;
        sub.Status = SubscriptionConstants.SubscriptionStatuses.Cancelled;
        sub.CancellationReason = reason;
        sub.CancelledAt = now;
        sub.AutoRenew = false;
        sub.IsActive = false;
        sub.UpdatedAt = now;
    }

    public static Subscription CreateNewStripeSubscription(Guid workspaceId, Guid userId, Plan plan, DateTime periodEnd)
    {
        var now = DateTime.UtcNow;
        return new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            PlanId = plan.Id,
            UserId = userId,
            Status = SubscriptionConstants.SubscriptionStatuses.Active,
            CreditsRemaining = plan.CreditsPerCycle,
            CreditsUsedThisCycle = 0,
            CurrentPeriodStart = now,
            CurrentPeriodEnd = periodEnd,
            AutoRenew = true,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

}
