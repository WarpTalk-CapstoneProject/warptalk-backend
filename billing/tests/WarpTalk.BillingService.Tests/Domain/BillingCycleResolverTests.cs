using System;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Services;
using Xunit;

namespace WarpTalk.BillingService.Tests.Domain;

/// <summary>
/// WT-370. These pin a money bug, not a formatting preference.
///
/// The web's plans page holds <c>useState&lt;"monthly" | "yearly"&gt;</c> and sends that value as
/// the checkout's BillingCycle. The backend compared it against Stripe's vocabulary,
/// "month"/"year", so no comparison ever matched: Stripe's interval fell through to a
/// guess-from-the-amount fallback on every request, and the subscription's period end ignored the
/// cycle entirely and granted one month for everything.
///
/// The first test below is the exact string the client sends. If it ever goes red again, someone
/// who paid for a year is getting thirty days.
/// </summary>
public class BillingCycleResolverTests
{
    [Theory]
    [InlineData("yearly")]   // what the plans page actually sends — the regression
    [InlineData("Yearly")]
    [InlineData("year")]     // Stripe's own spelling
    [InlineData("annual")]
    [InlineData("  annually  ")]
    public void YearlySpellings_Resolve_To_The_Year_Interval(string cycle)
    {
        Assert.Equal(PaymentConstants.PriceIntervals.Year, BillingCycleResolver.ToPriceInterval(cycle));
    }

    [Theory]
    [InlineData("monthly")]  // what the plans page actually sends
    [InlineData("MONTHLY")]
    [InlineData("month")]
    public void MonthlySpellings_Resolve_To_The_Month_Interval(string cycle)
    {
        Assert.Equal(PaymentConstants.PriceIntervals.Month, BillingCycleResolver.ToPriceInterval(cycle));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("fortnightly")]
    public void An_Unnamed_Or_Unknown_Cycle_Resolves_To_Null(string? cycle)
    {
        // Null rather than a default, so the caller decides its own fallback instead of being
        // handed a guess it cannot tell apart from an answer. StripePaymentService relies on this
        // to keep its amount heuristic for requests that genuinely name no cycle.
        Assert.Null(BillingCycleResolver.ToPriceInterval(cycle));
    }

    [Fact]
    public void A_Yearly_Purchase_Buys_A_Year_Of_Credits()
    {
        // The WT-370 purchase: ₫1,900,000/năm. Stripe was already billing this annually while the
        // workspace's period end was set one month out, so the credits ran out in month two and
        // the card kept being charged once a year.
        var from = new DateTime(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2027, 8, 13, 0, 0, 0, DateTimeKind.Utc), BillingCycleResolver.AddOneCycle(from, "yearly"));
    }

    [Fact]
    public void A_Monthly_Purchase_Buys_A_Month_Of_Credits()
    {
        var from = new DateTime(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc), BillingCycleResolver.AddOneCycle(from, "monthly"));
    }

    [Fact]
    public void An_Unknown_Cycle_Grants_The_Shorter_Period()
    {
        // Deliberate asymmetry: granting a year for a month's money is a loss no renewal
        // recovers, while a too-short period surfaces as a question somebody can answer.
        var from = new DateTime(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc), BillingCycleResolver.AddOneCycle(from, "who knows"));
    }
}
