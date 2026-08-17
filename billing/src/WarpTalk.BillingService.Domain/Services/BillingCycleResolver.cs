using System;
using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Domain.Services;

/// <summary>
/// The single place that decides what a billing-cycle string MEANS.
///
/// WHY THIS EXISTS (WT-370)
///   Two independent pieces of code read <c>BillingCycle</c> off the same Stripe metadata and
///   answered two different questions with it — how often Stripe should charge, and when the
///   workspace's credits expire — and they disagreed with each other AND with the sender.
///
///   The web sends "monthly"/"yearly": the plans page holds
///   <c>useState&lt;"monthly" | "yearly"&gt;</c> and passes that value straight through to
///   checkout. The backend compared it against <see cref="PaymentConstants.PriceIntervals"/>,
///   which is Stripe's own vocabulary — "month"/"year". Neither spelling ever matched, so:
///
///     • StripePaymentService fell through to its "no cycle named" fallback on EVERY request and
///       guessed the interval from the amount (VND ≥ 1,000,000 ⇒ yearly). A yearly plan priced
///       under a million is therefore billed MONTHLY by Stripe, and the comment above that
///       fallback describes a fix that the mismatch had quietly disabled.
///
///     • SubscriptionPaymentEventHandler.CalculatePeriodEnd took the string as a parameter and
///       discarded it, returning AddMonths(1) unconditionally. So the ₫1,900,000/year purchase in
///       WT-370 buys twelve months from Stripe and thirty days of credits from us.
///
///   Guessing from the amount cannot be right — the same number means different things in
///   different currencies and after any price change — so the caller's own words are authoritative
///   and this class is where they are read. The amount heuristic survives only for a request that
///   genuinely names no cycle, which is what it was always documented to be for.
/// </summary>
public static class BillingCycleResolver
{
    /// <summary>
    /// Stripe's recurring interval for this cycle, or <c>null</c> when the caller named no cycle
    /// we recognise — the caller then decides its own fallback rather than being handed a guess
    /// dressed up as an answer.
    /// </summary>
    public static string? ToPriceInterval(string? billingCycle)
    {
        if (string.IsNullOrWhiteSpace(billingCycle))
        {
            return null;
        }

        var cycle = billingCycle.Trim();

        if (Matches(cycle, PaymentConstants.BillingCycles.YearlySpellings))
        {
            return PaymentConstants.PriceIntervals.Year;
        }

        if (Matches(cycle, PaymentConstants.BillingCycles.MonthlySpellings))
        {
            return PaymentConstants.PriceIntervals.Month;
        }

        return null;
    }

    /// <summary>
    /// When the period a customer just paid for ends.
    ///
    /// An unrecognised cycle bills monthly, which is the shorter, cheaper-to-be-wrong-about
    /// answer: granting a year for a month's money is a loss no renewal recovers, while a
    /// too-short period surfaces as a renewal question somebody can correct.
    /// </summary>
    public static DateTime AddOneCycle(DateTime from, string? billingCycle)
        => ToPriceInterval(billingCycle) == PaymentConstants.PriceIntervals.Year
            ? from.AddYears(1)
            : from.AddMonths(1);

    private static bool Matches(string cycle, string[] spellings)
    {
        foreach (var spelling in spellings)
        {
            if (string.Equals(cycle, spelling, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
