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

    [Fact]
    public void AiProviderCost_NamesEachUncoveredChargeType_LargestFirst()
    {
        var consumption = new ConsumptionTotals(10_000, 40, 0, 7_000, 30, 3m,
        [
            new ChargeTypeCoverage("AUDIO_DUBBING_STANDARD", 7_000, 7_000),
            new ChargeTypeCoverage("TRANSLATION", 2_995, 0),
            new ChargeTypeCoverage("UNKNOWN", 4, 0),
            new ChargeTypeCoverage("STT", 1, 0),
        ]);

        AiProviderCost(consumption, Fx).Note.Should().Be(
            "covers 70% of consumed credits (30 of 40 transactions have a provider cost); "
            + "no provider cost for TRANSLATION (30%), UNKNOWN (<0.1%), STT (<0.1%); USD converted at 25,000 VND/USD");
    }

    [Fact]
    public void AiProviderCost_FullyCovered_SaysSoAndNamesNothing()
    {
        var consumption = new ConsumptionTotals(73, 3, 0, 73, 3, 0.1078m,
        [
            new ChargeTypeCoverage("AUDIO_DUBBING_VOICE_CLONE", 40, 40),
            new ChargeTypeCoverage("AUDIO_DUBBING_STANDARD", 33, 33),
        ]);

        var cost = AiProviderCost(consumption, Fx);

        cost.Value.Should().Be(2_695m);
        cost.Note.Should().Be("covers 100% of consumed credits (3 of 3 transactions have a provider cost); USD converted at 25,000 VND/USD");
        UncoveredChargeTypes(consumption).Should().BeNull();
        GrossMargin(new MetricSide(10_000m, null), cost, consumption).Should().Be(new MetricSide(7_305m, null));
    }

    [Fact]
    public void AiProviderCost_WithNoCoveredRows_NamesWhatIsMissing()
    {
        var cost = AiProviderCost(
            new ConsumptionTotals(500, 4, 0, 0, 0, 0m, [new ChargeTypeCoverage("TRANSLATION", 500, 0)]), Fx);

        cost.Value.Should().BeNull();
        cost.Note.Should().Be(
            "cannot be reconstructed: none of the 4 consume transaction(s) was settled on a rate card with a provider_unit_cost (no provider cost for TRANSLATION (100%))");
    }

    [Fact]
    public void GrossMargin_NamesTheUncoveredChargeTypes()
    {
        var consumption = new ConsumptionTotals(1000, 2, 0, 500, 1, 1m,
            [new ChargeTypeCoverage("TRANSLATION", 500, 0), new ChargeTypeCoverage("AUDIO_DUBBING_STANDARD", 500, 500)]);

        GrossMargin(new MetricSide(100_000m, null), new MetricSide(25_000m, null), consumption).Note.Should().Be(
            "AI cost covers only 50% of consumed credits (no provider cost for TRANSLATION (50%)), so this margin is overstated");
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

    private static readonly TimeZoneInfo Vietnam = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

    private static DateTime Utc(int month, int day, int hour = 0) => new(2026, month, day, hour, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RevenueByDay_ZeroFillsEveryUtcDay()
    {
        var days = AdminComparisonRange.DaysOf(Utc(9, 1), Utc(9, 4), TimeZoneInfo.Utc);

        var (rows, note) = RevenueByDay(days,
            [new PaidAmountRow(Utc(9, 2, 3), "VND", 1_200_000m), new PaidAmountRow(Utc(9, 2, 20), "USD", 2m)], Fx);

        rows.Should().Equal(
            new AdminRevenueByDayDto("2026-09-01", 0m),
            new AdminRevenueByDayDto("2026-09-02", 1_250_000m),
            new AdminRevenueByDayDto("2026-09-03", 0m));
        note.Should().BeNull("a converted currency is not an exclusion");
    }

    [Fact]
    public void RevenueByDay_OnTheVietnamCalendar_MovesAPaymentAfter1700UtcToTheNextDay()
    {
        // Vietnam's 1–2 September as instants.
        var days = AdminComparisonRange.DaysOf(Utc(8, 31, 17), Utc(9, 2, 17), Vietnam);

        var (rows, _) = RevenueByDay(days,
        [
            new PaidAmountRow(new DateTime(2026, 9, 1, 16, 59, 59, DateTimeKind.Utc), "VND", 100m), // 23:59:59 on 1 Sep
            new PaidAmountRow(Utc(9, 1, 17), "VND", 200m),                                          // 00:00 on 2 Sep
        ], Fx);

        rows.Should().Equal(new AdminRevenueByDayDto("2026-09-01", 100m), new AdminRevenueByDayDto("2026-09-02", 200m));
    }

    [Fact]
    public void RevenueByDay_ADayOfOnlyUnconvertibleCurrency_IsNullAndNoted()
    {
        var days = AdminComparisonRange.DaysOf(Utc(9, 1), Utc(9, 3), TimeZoneInfo.Utc);

        var (rows, note) = RevenueByDay(days,
            [new PaidAmountRow(Utc(9, 1, 5), "EUR", 10m), new PaidAmountRow(Utc(9, 2, 5), "VND", 5m), new PaidAmountRow(Utc(9, 2, 6), "EUR", 1m)], Fx);

        rows.Should().Equal(new AdminRevenueByDayDto("2026-09-01", null), new AdminRevenueByDayDto("2026-09-02", 5m));
        note.Should().Be("excludes 2 EUR rows");
    }

    [Fact]
    public void RevenueByMonth_IsTheSixMonthsEndingWithTheMonthOfTo()
    {
        // `to` = 1 Oct 00:00 exclusive, so the last month is September, not October.
        var to = Utc(10, 1);

        RevenueMonthWindow(to, TimeZoneInfo.Utc).Should().Be(new AdminInsightRange(Utc(4, 1), Utc(10, 1)));

        var (months, note) = RevenueByMonth(to, TimeZoneInfo.Utc, [new PaidAmountRow(Utc(7, 1), "VND", 300m)], Fx);
        months.Select(m => m.Month).Should().Equal("2026-04", "2026-05", "2026-06", "2026-07", "2026-08", "2026-09");
        months.Single(m => m.Month == "2026-07").Revenue.Should().Be(300m);
        note.Should().BeNull();
    }

    [Fact]
    public void RevenueByMonth_OnTheVietnamCalendar_UsesLocalMonths()
    {
        // `to` = Vietnam's 1 Oct 00:00. The window is Vietnam's April–September.
        var to = Utc(9, 30, 17);

        RevenueMonthWindow(to, Vietnam).Should().Be(new AdminInsightRange(Utc(3, 31, 17), Utc(9, 30, 17)));

        // 31 Aug 18:00Z is 1 Sep 01:00 in Vietnam: September's revenue, not August's.
        var (months, _) = RevenueByMonth(to, Vietnam, [new PaidAmountRow(Utc(8, 31, 18), "VND", 300m)], Fx);
        months.Single(m => m.Month == "2026-09").Revenue.Should().Be(300m);
        months.Single(m => m.Month == "2026-08").Revenue.Should().Be(0m);
    }

    [Fact]
    public void RevenueBetween_NotesWhatItConvertedAndWhatItLeftOut()
    {
        PaidAmountRow[] payments = [new(Utc(9, 1, 1), "EUR", 10m), new(Utc(9, 1, 2), "USD", 2m)];

        RevenueBetween(payments, Utc(9, 1), Utc(9, 2), Fx).Should().Be(
            (50_000m, "includes 2.00 USD converted at 25,000 VND/USD (billing_pricing_config.fx_rate_usd_vnd); excludes 1 EUR rows"));
        RevenueBetween([payments[0]], Utc(9, 1), Utc(9, 2), Fx).Should().Be(((decimal?)null, "excludes 1 EUR rows"));
        RevenueBetween([], Utc(9, 1), Utc(9, 2), Fx).Should().Be((0m, (string?)null));
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
