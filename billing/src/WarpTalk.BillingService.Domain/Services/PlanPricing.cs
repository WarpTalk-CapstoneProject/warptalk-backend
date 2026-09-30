using System;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Services;

/// <summary>
/// #466: what one billing period of a plan costs, decided SERVER-side.
///
/// A recurring Stripe Price is charged every cycle without the browser in the loop, so its amount
/// cannot come from the checkout request the way the one-off amount used to. This is the server
/// copy of the web's <c>lib/billing/plan-pricing.ts</c> (<c>checkoutTotal</c>): a yearly purchase of a
/// monthly-priced plan is twelve months at <see cref="YearlyPriceMultiplier"/>; a plan already
/// priced yearly is sold at its own price. Keep the two in step — they are one rule.
/// </summary>
public static class PlanPricing
{
    /// <summary>Pay for a year, pay 79% of twelve months. Mirrors YEARLY_PRICE_MULTIPLIER on the web.</summary>
    public const decimal YearlyPriceMultiplier = 0.79m;

    /// <summary>The cycle a request means, in the plans page's vocabulary ("monthly" / "yearly").</summary>
    public static string NormalizeCycle(string? billingCycle) =>
        BillingCycleResolver.ToPriceInterval(billingCycle) == PaymentConstants.PriceIntervals.Year
            ? SubscriptionConstants.BillingCycles.Yearly
            : SubscriptionConstants.BillingCycles.Monthly;

    /// <summary>The amount charged for one period of <paramref name="plan"/> on <paramref name="billingCycle"/>.</summary>
    public static decimal PeriodTotal(Plan plan, string? billingCycle)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var yearly = NormalizeCycle(billingCycle) == SubscriptionConstants.BillingCycles.Yearly;
        var planIsYearly = string.Equals(plan.BillingCycle, SubscriptionConstants.BillingCycles.Yearly, StringComparison.OrdinalIgnoreCase);
        return yearly && !planIsYearly
            ? plan.Price * 12m * YearlyPriceMultiplier
            : plan.Price;
    }

    /// <summary>Stripe's lower-case currency code for the plan ("vnd", "usd").</summary>
    public static string StripeCurrency(Plan plan) =>
        string.IsNullOrWhiteSpace(plan.Currency) ? PaymentConstants.Currencies.Vnd : plan.Currency.Trim().ToLowerInvariant();

    /// <summary>Key of the recurring price in <see cref="Plan.StripePriceIds"/>: <c>monthly_vnd</c>, <c>yearly_usd</c>.</summary>
    public static string PriceKey(string? billingCycle, string currency) =>
        $"{NormalizeCycle(billingCycle)}_{currency.Trim().ToLowerInvariant()}";
}
