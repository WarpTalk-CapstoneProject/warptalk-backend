using System;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Services;

public class SubscriptionDomainService : ISubscriptionDomainService
{
    public bool ConsumeCredits(Subscription subscription, int amount)
    {
        if (subscription == null) throw new ArgumentNullException(nameof(subscription));
        if (amount < 0) throw new ArgumentException("Amount must be positive", nameof(amount));

        var effectiveOverageCap = subscription.OverageCapCreditsOverride ?? subscription.Plan?.OverageCapCredits ?? 0;
        var lowBalanceThreshold = subscription.Plan?.LowBalanceThresholdCredits ?? 0;
        
        if (subscription.CreditsRemaining - amount < -effectiveOverageCap)
        {
            subscription.ServiceState = SubscriptionConstants.ServiceStates.Suspended;
            subscription.SuspendedReason = SubscriptionConstants.SuspendedReasons.OverageCap;
            return false;
        }

        var oldCredits = subscription.CreditsRemaining;
        subscription.CreditsRemaining -= amount;
        subscription.CreditsUsedThisCycle += amount;

        var overageAdded = Math.Max(0, Math.Min(amount, amount - oldCredits));
        subscription.OverageCreditsThisCycle += overageAdded;
        
        if (subscription.OverageCreditsThisCycle > 0 && subscription.OverageStartedAt == null)
        {
            subscription.OverageStartedAt = DateTime.UtcNow;
        }

        if (subscription.CreditsRemaining < 0)
        {
            subscription.ServiceState = SubscriptionConstants.ServiceStates.InOverage;
        }
        else if (subscription.CreditsRemaining < lowBalanceThreshold)
        {
            subscription.ServiceState = SubscriptionConstants.ServiceStates.LowBalance;
        }
        else
        {
            subscription.ServiceState = SubscriptionConstants.ServiceStates.Healthy;
        }

        return true;
    }

    public void RenewCycle(Subscription subscription)
    {
        if (subscription == null) throw new ArgumentNullException(nameof(subscription));

        var rolloverCap = subscription.Plan?.RolloverCapCredits ?? 0;
        var creditsPerCycle = subscription.CreditsPerCycleOverride ?? subscription.Plan?.CreditsPerCycle ?? 0;
        
        var carry = Math.Min(Math.Max(subscription.CreditsRemaining, 0), rolloverCap);
        
        subscription.CreditsRemaining = carry + creditsPerCycle;
        subscription.CreditsUsedThisCycle = 0;
        subscription.OverageCreditsThisCycle = 0;
        subscription.OverageStartedAt = null;

        subscription.ServiceState = SubscriptionConstants.ServiceStates.Healthy;
        subscription.SuspendedReason = null;
    }
}
