using WarpTalk.BillingService.Application.DTOs;
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
        sub.Status,
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
            Status = "pending",
            CreditsRemaining = 0,
            CreditsUsedThisCycle = 0,
            CurrentPeriodStart = now,
            CurrentPeriodEnd = now, // Will be updated on activation
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
            Status = "pending",
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
        sub.Status = "cancelled";
        sub.UpdatedAt = now;
    }

    public static void CancelImmediately(this Subscription sub, string? reason)
    {
        var now = DateTime.UtcNow;
        sub.Status = "cancelled";
        sub.CancellationReason = reason;
        sub.CancelledAt = now;
        sub.AutoRenew = false;
        sub.IsActive = false;
        sub.UpdatedAt = now;
    }
}
