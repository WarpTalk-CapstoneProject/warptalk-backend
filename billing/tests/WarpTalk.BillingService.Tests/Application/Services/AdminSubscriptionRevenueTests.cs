using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

/// <summary>
/// Recurring revenue, tested directly on rows.
///
/// Every rule here has a wrong answer that would have been believed: a trial counted as revenue,
/// a yearly plan counted twelve times over, a contract price relabelled into the plan's currency,
/// or VND and USD added together. The arithmetic is pure and static precisely so it can be pinned
/// without a database.
/// </summary>
public class AdminSubscriptionRevenueTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static AdminSubscriptionRow Row(
        decimal planPrice = 100m,
        string currency = "VND",
        string cycle = "monthly",
        decimal? contractPriceVnd = null,
        string status = "active",
        DateTime? trialEndsAt = null,
        DateTime? cancelledAt = null,
        DateTime? periodEnd = null) => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            status,
            SubscriptionConstants.ServiceStates.Healthy,
            null,
            "Plan",
            "plan",
            "tier",
            cycle,
            planPrice,
            currency,
            contractPriceVnd,
            CreditsRemaining: 100,
            CreditsUsedThisCycle: 0,
            CurrentPeriodStart: Now.AddDays(-10),
            CurrentPeriodEnd: periodEnd ?? Now.AddDays(20),
            AutoRenew: true,
            TrialEndsAt: trialEndsAt,
            CancelledAt: cancelledAt,
            CreatedAt: Now.AddDays(-40));

    [Fact]
    public void A_monthly_plan_is_worth_its_price()
    {
        AdminSubscriptionRevenue.MonthlyAmount(Row(planPrice: 1_900_000m)).Amount
            .Should().Be(1_900_000m);
    }

    [Fact]
    public void A_yearly_plan_is_worth_a_twelfth_of_its_price()
    {
        AdminSubscriptionRevenue.MonthlyAmount(Row(planPrice: 1200m, cycle: "yearly")).Amount
            .Should().Be(100m);
    }

    [Fact]
    public void The_cycle_comparison_is_case_insensitive()
    {
        // The column is free text. "Yearly" stored with a capital would otherwise be billed as
        // monthly and inflate MRR twelvefold for that subscription.
        AdminSubscriptionRevenue.MonthlyAmount(Row(planPrice: 1200m, cycle: "Yearly")).Amount
            .Should().Be(100m);
    }

    [Fact]
    public void A_contract_price_overrides_the_plan_price()
    {
        AdminSubscriptionRevenue.MonthlyAmount(
            Row(planPrice: 29m, currency: "USD", contractPriceVnd: 1_900_000m)).Amount
            .Should().Be(1_900_000m);
    }

    [Fact]
    public void A_contract_price_is_always_reported_in_VND()
    {
        // Its NAME states its currency. Reading the plan's currency for a contract price would
        // relabel 1,900,000 VND as 1,900,000 USD on any USD-priced plan.
        AdminSubscriptionRevenue.MonthlyAmount(
            Row(planPrice: 29m, currency: "USD", contractPriceVnd: 1_900_000m)).Currency
            .Should().Be("VND");
    }

    [Fact]
    public void A_yearly_contract_price_is_still_divided_by_twelve()
    {
        AdminSubscriptionRevenue.MonthlyAmount(
            Row(cycle: "yearly", contractPriceVnd: 24_000_000m)).Amount
            .Should().Be(2_000_000m);
    }

    [Fact]
    public void A_subscription_inside_its_trial_is_not_recurring_revenue()
    {
        // It is active, it has a plan with a price, and nobody has paid anything. Counting trials
        // is the classic way an MRR figure comes out flattering and wrong.
        AdminSubscriptionRevenue.IsRecurring(Row(trialEndsAt: Now.AddDays(7)))
            .Should().BeFalse();
    }

    [Fact]
    public void A_subscription_whose_trial_has_ended_is_recurring_revenue()
    {
        AdminSubscriptionRevenue.IsRecurring(Row(trialEndsAt: Now.AddDays(-1)))
            .Should().BeTrue();
    }

    [Fact]
    public void A_cancelled_subscription_is_not_recurring_revenue()
    {
        AdminSubscriptionRevenue.IsRecurring(Row(cancelledAt: Now.AddDays(-2)))
            .Should().BeFalse();
    }

    [Fact]
    public void A_non_active_status_is_not_recurring_revenue()
    {
        AdminSubscriptionRevenue.IsRecurring(Row(status: "expired")).Should().BeFalse();
    }

    [Fact]
    public void Currencies_are_reported_separately_and_never_summed_together()
    {
        // The whole point. 1,900,000 VND + 29 USD is 1,900,029 of nothing.
        var rows = new List<AdminSubscriptionRow>
        {
            Row(planPrice: 1_900_000m, currency: "VND"),
            Row(planPrice: 500_000m, currency: "VND"),
            Row(planPrice: 29m, currency: "USD"),
        };

        var mrr = AdminSubscriptionRevenue.MonthlyRecurring(rows);

        mrr.Should().HaveCount(2);
        mrr.Single(m => m.Currency == "VND").Amount.Should().Be(2_400_000m);
        mrr.Single(m => m.Currency == "USD").Amount.Should().Be(29m);
    }

    [Fact]
    public void Currency_grouping_is_case_insensitive()
    {
        var rows = new List<AdminSubscriptionRow>
        {
            Row(planPrice: 10m, currency: "usd"),
            Row(planPrice: 20m, currency: "USD"),
        };

        AdminSubscriptionRevenue.MonthlyRecurring(rows).Should().ContainSingle()
            .Which.Amount.Should().Be(30m);
    }

    [Fact]
    public void Rounding_happens_once_on_the_group_total_not_per_subscription()
    {
        // Three yearly subscriptions at 100/12 = 33.333… each. Rounded individually and summed
        // that is 100.02; rounded once on the true total it is 100.00. The drift is small here
        // and grows with the number of subscriptions, which is exactly the kind of error nobody
        // notices until an accountant does.
        var rows = Enumerable.Range(0, 3)
            .Select(_ => Row(planPrice: 100m, cycle: "yearly", currency: "USD"))
            .ToList();

        AdminSubscriptionRevenue.MonthlyRecurring(rows).Single().Amount.Should().Be(25m);
    }

    [Fact]
    public void Trials_and_cancellations_are_excluded_from_the_total()
    {
        var rows = new List<AdminSubscriptionRow>
        {
            Row(planPrice: 100m, currency: "USD"),
            Row(planPrice: 999m, currency: "USD", trialEndsAt: Now.AddDays(5)),
            Row(planPrice: 999m, currency: "USD", cancelledAt: Now.AddDays(-1)),
        };

        AdminSubscriptionRevenue.MonthlyRecurring(rows).Single().Amount.Should().Be(100m);
    }

    [Fact]
    public void An_empty_platform_reports_no_currencies_rather_than_a_zero()
    {
        // Zero of WHICH currency? With nothing sold there is no answer, and inventing "0 USD"
        // would be picking one.
        AdminSubscriptionRevenue.MonthlyRecurring(Array.Empty<AdminSubscriptionRow>())
            .Should().BeEmpty();
    }

    [Fact]
    public void Ending_soon_counts_periods_inside_the_window_only()
    {
        var rows = new List<AdminSubscriptionRow>
        {
            Row(periodEnd: Now.AddDays(3)),
            Row(periodEnd: Now.AddDays(13)),
            Row(periodEnd: Now.AddDays(40)),
            // Already over: not "ending soon", it has ended.
            Row(periodEnd: Now.AddDays(-1)),
        };

        AdminSubscriptionRevenue.EndingWithin(rows, 14, Now).Should().Be(2);
    }

    [Fact]
    public void Ending_soon_includes_auto_renewing_subscriptions()
    {
        // "Renews in three days" and "expires in three days" are both things worth seeing coming.
        // Filtering out renewals would quietly change the number's meaning.
        var rows = new List<AdminSubscriptionRow> { Row(periodEnd: Now.AddDays(3)) with { AutoRenew = true } };

        AdminSubscriptionRevenue.EndingWithin(rows, 14, Now).Should().Be(1);
    }
}
