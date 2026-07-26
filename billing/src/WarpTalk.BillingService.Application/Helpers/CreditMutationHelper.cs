using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Application.Helpers;

public static class CreditMutationHelper
{
    public static void ApplyGrant(this Subscription subscription, int amount)
        => subscription.ApplyCreditDelta(amount);

    public static void ApplyAdjustment(this Subscription subscription, int amount)
        => subscription.ApplyCreditDelta(amount);

    public static void ApplyCycleAllocation(this Subscription subscription, int amount)
        => subscription.ApplyCreditDelta(amount);

    public static void ApplyRefund(this Subscription subscription, int amount)
        => subscription.ApplyCreditDelta(amount);

    private static void ApplyCreditDelta(this Subscription subscription, int amount)
    {
        subscription.CreditsRemaining += amount;
        if (subscription.CreditsRemaining >= 0)
        {
            subscription.OverageStartedAt = null;
            subscription.ServiceState = SubscriptionConstants.ServiceStates.Healthy;
            subscription.SuspendedReason = null;
        }
        subscription.UpdatedAt = DateTime.UtcNow;
    }
}
