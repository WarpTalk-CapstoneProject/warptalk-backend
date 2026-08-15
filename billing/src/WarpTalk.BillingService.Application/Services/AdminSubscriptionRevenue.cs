using System;
using System.Collections.Generic;
using System.Linq;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.Application.Services;

/// <summary>
/// Recurring revenue, computed in one place because it is the number people act on.
///
/// Pure and static so it can be tested directly on rows, without a database or a service. Every
/// rule below is a decision that has a wrong answer, and the wrong answer is a money figure
/// somebody would have believed.
/// </summary>
public static class AdminSubscriptionRevenue
{
    /// <summary>
    /// Monthly recurring revenue per currency, never as one number.
    ///
    /// The platform sells in more than one currency: <c>plans.currency</c> defaults to VND, and
    /// <c>PlanDefaults</c> carries both a MinimumVndPlanPrice and a MinimumUsdPlanPrice, so a USD
    /// plan row is an expected thing. Adding 1,900,000 VND to 29 USD produces 1,900,029 of
    /// nothing.
    ///
    /// Converting instead of grouping was the other option and is worse: the only rate available
    /// is <c>RateCardDefaults.FxRateUsdVnd</c>, a seed constant that nobody updates, and a
    /// dashboard that silently applies a stale rate reports a revenue figure that is confidently
    /// wrong rather than obviously split.
    /// </summary>
    public static IReadOnlyList<AdminMoney> MonthlyRecurring(IReadOnlyList<AdminSubscriptionRow> rows)
        => rows
            .Where(IsRecurring)
            .Select(MonthlyAmount)
            .GroupBy(money => money.Currency, StringComparer.OrdinalIgnoreCase)
            .Select(group => AdminMoney.Of(group.Sum(money => money.Amount), group.Key))
            .OrderBy(money => money.Currency, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Whether this subscription contributes recurring revenue at all.
    ///
    /// A subscription still inside its TRIAL does not. It is active, it consumes credits, and it
    /// has a plan with a price — and nobody has paid anything. Counting trials is the classic way
    /// an MRR figure comes out flattering and wrong, so the trial window is excluded explicitly
    /// rather than by hoping the status covers it.
    /// </summary>
    public static bool IsRecurring(AdminSubscriptionRow row)
        => row.Status == SubscriptionConstants.SubscriptionStatuses.Active
           && row.CancelledAt == null
           && (row.TrialEndsAt == null || row.TrialEndsAt <= DateTime.UtcNow);

    /// <summary>
    /// What one subscription is worth per month, in its own currency.
    ///
    /// <c>ContractPriceVnd</c> wins over the plan's price when it is set: an enterprise contract
    /// is the agreement, and the catalog row is only what the agreement started from. Its name
    /// states its currency, so it is VND regardless of what the plan says — reading the plan's
    /// currency for a contract price would relabel a VND figure as USD on any USD-priced plan.
    ///
    /// A yearly cycle is divided by twelve. It is NOT rounded here: rounding twelve subscriptions
    /// individually and then summing drifts from the true total, so rounding happens once, when
    /// the group total becomes AdminMoney.
    /// </summary>
    public static AdminMoney MonthlyAmount(AdminSubscriptionRow row)
    {
        var (price, currency) = row.ContractPriceVnd is { } contract
            ? (contract, PaymentConstants.Currencies.VndAccounting)
            : (row.PlanPrice, row.PlanCurrency);

        var monthly = string.Equals(
            row.BillingCycle,
            SubscriptionConstants.BillingCycles.Yearly,
            StringComparison.OrdinalIgnoreCase)
                ? price / 12m
                : price;

        // Deliberately unrounded — see the summary. `Of` is applied to the group total instead.
        return new AdminMoney(monthly, currency);
    }

    /// <summary>
    /// Subscriptions whose current period ends within <paramref name="days"/>.
    ///
    /// Auto-renewing ones are included. "Renews in three days" and "expires in three days" are
    /// both things an administrator wants to see coming, and filtering out the renewals would
    /// leave the count meaning "imminent problems" — a different question, silently answered.
    /// </summary>
    public static int EndingWithin(IReadOnlyList<AdminSubscriptionRow> rows, int days, DateTime now)
    {
        var cutoff = now.AddDays(days);
        return rows.Count(row =>
            row.Status == SubscriptionConstants.SubscriptionStatuses.Active
            && row.CurrentPeriodEnd > now
            && row.CurrentPeriodEnd <= cutoff);
    }
}
