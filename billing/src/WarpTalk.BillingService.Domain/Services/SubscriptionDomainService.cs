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

    /// <summary>
    /// WT-878: the rollover cap applies to what is left of the PLAN's credits, never to credits the
    /// workspace bought. Before this, <c>min(max(balance, 0), cap) + creditsPerCycle</c> capped the
    /// whole balance, so a top-up or credit pack above the cap vanished at the next renewal with no
    /// ledger line saying so — while the expiry path (CreditFreezeService) already kept purchased
    /// credits whole. Both events now divide the balance by the same rule: purchased credits are
    /// spent LAST, so up to <paramref name="purchasedCredits"/> of the balance is purchased and
    /// carries over whole; only the rest is plan leftover, and only that is capped.
    ///
    /// A zero or negative balance behaves exactly as before: the overage debt was billed on the
    /// closing cycle's invoice, nothing carries, and the new balance is the cycle's grant.
    /// </summary>
    public CycleRenewalOutcome RenewCycle(Subscription subscription, long purchasedCredits = 0)
    {
        if (subscription == null) throw new ArgumentNullException(nameof(subscription));

        var rolloverCap = Math.Max(0, subscription.Plan?.RolloverCapCredits ?? 0);
        var creditsPerCycle = subscription.CreditsPerCycleOverride ?? subscription.Plan?.CreditsPerCycle ?? 0;
        var balance = subscription.CreditsRemaining;

        var purchasedCarried = 0;
        var planCarried = 0;
        var forfeited = 0;
        if (balance > 0)
        {
            purchasedCarried = (int)Math.Clamp(purchasedCredits, 0L, balance);
            var planLeftover = balance - purchasedCarried;
            planCarried = Math.Min(planLeftover, rolloverCap);
            forfeited = planLeftover - planCarried;
        }

        subscription.CreditsRemaining = purchasedCarried + planCarried + creditsPerCycle;
        subscription.CreditsUsedThisCycle = 0;
        subscription.OverageCreditsThisCycle = 0;
        subscription.OverageStartedAt = null;

        subscription.ServiceState = SubscriptionConstants.ServiceStates.Healthy;
        subscription.SuspendedReason = null;

        return new CycleRenewalOutcome(balance, purchasedCarried, planCarried, forfeited, creditsPerCycle, rolloverCap);
    }
}
