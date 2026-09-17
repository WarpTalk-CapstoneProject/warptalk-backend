using FluentAssertions;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared.Contracts.Admin;
using static WarpTalk.BillingService.Application.Services.AdminBillingInsightsCalculator;

namespace WarpTalk.BillingService.Tests.Application.Services;

/// <summary>Metric definitions on plain values — no database. The Postgres side is AdminBillingInsightsServiceTests.</summary>
public class AdminBillingInsightsCalculatorTests
{
    private const decimal Fx = 25_000m;

    // ── Currency ─────────────────────────────────────────────────────────────

    [Fact]
    public void ToVnd_ConvertsUsdAtTheConfiguredRate()
    {
        var total = ToVnd([new MoneyPart("vnd", 1_000_000m, 3), new MoneyPart("USD", 20m, 1)], Fx);

        total.Amount.Should().Be(1_500_000m);
        total.IncludedRows.Should().Be(4);
        total.ConvertedUsd.Should().Be(20m);
        ConversionNote(total, Fx).Should().Be(
            "includes 20.00 USD converted at 25,000 VND/USD (billing_pricing_config.fx_rate_usd_vnd)");
    }

    [Fact]
    public void ToVnd_WithoutARateExcludesUsdAndSaysSo()
    {
        var total = ToVnd([new MoneyPart("VND", 100m, 1), new MoneyPart("usd", 20m, 2)], null);

        total.Amount.Should().Be(100m);
        total.ExcludedRows.Should().Be(2);
        ConversionNote(total, null).Should().Be("excludes 2 USD rows (no fx_rate_usd_vnd configured)");
    }

    [Fact]
    public void ToVnd_NothingConvertibleIsNullNotZero()
    {
        var total = ToVnd([new MoneyPart("EUR", 10m, 1)], Fx);

        total.Amount.Should().BeNull();
        ConversionNote(total, Fx).Should().Be("excludes 1 EUR rows");
    }

    [Fact]
    public void ToVnd_NoRowsIsATrueZero()
    {
        ToVnd([], Fx).Amount.Should().Be(0m);
    }

    // ── Revenue family ───────────────────────────────────────────────────────

    [Fact]
    public void Revenue_NotesTheStripeDuplicatesItCountedOnce()
    {
        var (side, total) = Revenue([new PaymentCurrencyTotal("VND", 4, 4_000_000m)], 1, Fx);

        side.Value.Should().Be(4_000_000m);
        side.Note.Should().Be("1 Stripe subscription invoice(s) counted once with their checkout");
        RevenuePerPayment(side, total).Value.Should().Be(1_000_000m);
    }

    [Fact]
    public void RevenuePerPayment_IsNullWithoutPayments()
    {
        var (side, total) = Revenue([], 0, Fx);

        side.Value.Should().Be(0m);
        var perPayment = RevenuePerPayment(side, total);
        perPayment.Value.Should().BeNull();
        perPayment.Note.Should().Be("no paid payments in range");
    }

    // ── AI provider cost and margin ──────────────────────────────────────────

    [Fact]
    public void AiProviderCost_ConvertsTheCoveredRowsAndReportsCoverage()
    {
        var consumption = new ConsumptionTotals(1450, 3, 150, 1100, 2, 1.2m);

        var cost = AiProviderCost(consumption, Fx);

        cost.Value.Should().Be(30_000m);
        cost.Note.Should().Be(
            "covers 75.9% of consumed credits (2 of 3 transactions have a provider cost); USD converted at 25,000 VND/USD");
    }

    [Fact]
    public void AiProviderCost_WithNoCoveredRowsIsNull()
    {
        var cost = AiProviderCost(new ConsumptionTotals(500, 4, 0, 0, 0, 0m), Fx);

        cost.Value.Should().BeNull();
        cost.Note.Should().Contain("none of the 4 consume transaction(s)");
    }

    [Fact]
    public void AiProviderCost_WithoutFxIsNull()
    {
        var cost = AiProviderCost(new ConsumptionTotals(500, 4, 0, 500, 4, 2m), null);

        cost.Value.Should().BeNull();
        cost.Note.Should().Contain("no fx_rate_usd_vnd");
    }

    [Fact]
    public void AiProviderCost_WithNoUsageIsATrueZero()
    {
        AiProviderCost(new ConsumptionTotals(0, 0, 0, 0, 0, 0m), null).Should().Be(new MetricSide(0m, null));
    }

    [Fact]
    public void GrossMargin_IsNullWhenEitherSideIsNull_AndFlagsPartialCoverage()
    {
        var consumption = new ConsumptionTotals(1000, 2, 0, 500, 1, 1m);

        GrossMargin(new MetricSide(null, "x"), new MetricSide(1m, null), consumption).Value.Should().BeNull();
        GrossMargin(new MetricSide(1m, null), new MetricSide(null, "x"), consumption).Value.Should().BeNull();

        var margin = GrossMargin(new MetricSide(100_000m, null), new MetricSide(25_000m, null), consumption);
        margin.Value.Should().Be(75_000m);
        margin.Note.Should().Be("AI cost covers only 50% of consumed credits, so this margin is overstated");
    }

    // ── Churn ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(3, 168, 1.79)]
    [InlineData(1, 4, 25.00)]
    [InlineData(0, 10, 0.00)]
    public void ChurnRate_IsAPercentageWithTwoDecimals(int cancelled, int atStart, double expected)
    {
        ChurnRate(cancelled, atStart).Should().Be((decimal)expected);
    }

    [Fact]
    public void ChurnRate_IsNullWhenNothingWasActive()
    {
        ChurnRate(2, 0).Should().BeNull();
    }

    // ── Composition ──────────────────────────────────────────────────────────

    [Fact]
    public void Metric_CarriesAPreviousOnlyReason()
    {
        var metric = Metric("revenue", AdminInsightUnits.Money, true,
            new MetricSide(5m, null), MetricSide.Unavailable("the comparison period is empty"));

        metric.Should().Be(new AdminInsightMetric(
            "revenue", 5m, null, "money", true, "previous period: the comparison period is empty"));
    }

    // ── Series ───────────────────────────────────────────────────────────────

    [Fact]
    public void RevenueByDay_ZeroFillsEveryUtcDay()
    {
        var from = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc); // exclusive

        var days = RevenueByDay(from, to,
            [new PaymentBucketTotal(2026, 9, 2, "VND", 1_200_000m), new PaymentBucketTotal(2026, 9, 2, "USD", 2m)], Fx);

        days.Should().Equal(
            new AdminRevenueByDayDto("2026-09-01", 0m),
            new AdminRevenueByDayDto("2026-09-02", 1_250_000m),
            new AdminRevenueByDayDto("2026-09-03", 0m));
    }

    [Fact]
    public void RevenueByMonth_IsTheSixMonthsEndingWithTheMonthOfTo()
    {
        // `to` = 1 Oct 00:00 exclusive, so the last month is September, not October.
        var to = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        RevenueMonthWindow(to).Should().Be(new AdminInsightRange(
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)));

        var months = RevenueByMonth(to, [new PaymentBucketTotal(2026, 7, 1, "VND", 300m)], Fx);
        months.Select(m => m.Month).Should().Equal("2026-04", "2026-05", "2026-06", "2026-07", "2026-08", "2026-09");
        months.Single(m => m.Month == "2026-07").Revenue.Should().Be(300m);
    }

    // ── Snapshot helpers ─────────────────────────────────────────────────────

    [Fact]
    public void ActiveByCycle_ReadsBothBillingCycleVocabularies()
    {
        AdminSubscriptionRow Row(string cycle) => new(Guid.NewGuid(), Guid.NewGuid(), "active", "healthy", null,
            "Team", "team", "team", cycle, 1m, "VND", null, 0, 0, DateTime.UtcNow, DateTime.UtcNow, true, null, null, DateTime.UtcNow);

        ActiveByCycle([Row("monthly"), Row("month"), Row("yearly"), Row("year"), Row("quarterly")])
            .Should().Be(new WarpTalk.BillingService.Application.DTOs.AdminActiveByCycleDto(2, 2, 1));
    }

    [Theory]
    [InlineData("stripe", "card", "Stripe card")]
    [InlineData("internal_invoice", "invoice", "Invoice")]
    public void PaymentMethodLabel_NamesTheChannel(string provider, string method, string expected)
    {
        PaymentMethodLabel(provider, method).Should().Be(expected);
    }
}
