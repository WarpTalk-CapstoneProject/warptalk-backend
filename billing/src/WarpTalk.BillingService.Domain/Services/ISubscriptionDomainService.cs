using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Services;

public interface ISubscriptionDomainService
{
    bool ConsumeCredits(Subscription subscription, int amount);

    /// <summary>
    /// Closes the cycle: applies the plan's rollover cap to the PLAN-GRANTED leftover only, carries
    /// purchased credits over whole, grants the new cycle's credits, and clears overage/suspension.
    /// </summary>
    /// <param name="purchasedCredits">
    /// Credits bought or granted through this subscription (top-ups, credit packs, admin grants,
    /// released frozen credits) — the same figure <c>CreditFreezeService</c> uses at expiry. Treated
    /// as spent LAST, so up to this many of the remaining balance are purchased and never capped.
    /// </param>
    /// <returns>How the balance divided; the caller writes a forfeit ledger row when
    /// <see cref="CycleRenewalOutcome.Forfeited"/> is positive.</returns>
    CycleRenewalOutcome RenewCycle(Subscription subscription, long purchasedCredits = 0);
}

/// <summary>
/// WT-878: how a cycle renewal divided the balance it closed.
/// <see cref="PurchasedCarried"/> + <see cref="PlanCarried"/> + <see cref="Forfeited"/> equals
/// <see cref="BalanceBefore"/> when that balance is positive; a zero or negative balance (overage
/// debt, already billed on the cycle's invoice) carries and forfeits nothing.
/// </summary>
public readonly record struct CycleRenewalOutcome(
    int BalanceBefore,
    int PurchasedCarried,
    int PlanCarried,
    int Forfeited,
    int Granted,
    int RolloverCap)
{
    public int Carried => PurchasedCarried + PlanCarried;

    /// <summary>The balance between the forfeit and the grant — the forfeit row's balance_after.</summary>
    public int BalanceAfterForfeit => BalanceBefore - Forfeited;
}
