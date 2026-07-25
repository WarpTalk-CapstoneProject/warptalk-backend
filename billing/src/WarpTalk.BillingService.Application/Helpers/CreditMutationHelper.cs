using WarpTalk.BillingService.Domain.Entities;

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
        subscription.UpdatedAt = DateTime.UtcNow;
    }
}
