using FluentAssertions;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Tests.Application.Services;

/// <summary>
/// Profit and loss on plain values: revenue minus AI provider cost, each USD amount at its own day's
/// rate, measured Cartesia cost allocated to plans, uncovered usage named instead of priced at 0.
/// </summary>
public sealed class ProfitAndLossCalculatorTests
{
    private static readonly Guid Team = Guid.NewGuid(), Pro = Guid.NewGuid();
    private static readonly Guid W1 = Guid.NewGuid(), W2 = Guid.NewGuid(), W3 = Guid.NewGuid();
    private static readonly DateTime Now = Utc(9, 24, 12);

    private static DateTime Utc(int m, int d, int h = 0, int min = 0) => new(2026, m, d, h, min, 0, DateTimeKind.Utc);

    private static FxRate Rate(int day, decimal rate) => new()
    {
        BaseCurrency = "USD", QuoteCurrency = "VND", RateDate = new DateOnly(2026, 9, day), Rate = rate,
        Source = FxRateConstants.Sources.StripeFxQuote, FetchedAt = Utc(9, day, 1),
    };

    private static ConsumptionSlotRow Slot(DateTime at, string type, string provider, Guid? plan, long credits, long covered, decimal usd)
        => new(at, type, provider, plan, credits, 1, covered, covered > 0 ? 1 : 0, usd, 0);

    private static ProfitAndLossInputs Inputs(
        IReadOnlyList<ConsumptionSlotRow>? slots = null,
        IReadOnlyList<PaidAmountRow>? payments = null,
        IReadOnlyList<CartesiaUsageDay>? cartesia = null,
        IReadOnlyList<WorkspaceSlotRow>? workspaces = null)
        => new(
            slots ?? [],
            workspaces ?? [],
            payments ?? [],
            cartesia ?? [],
            0.00004m,
            FxRateTable.From([Rate(1, 25_000m), Rate(10, 26_000m)], configured: 26_300m),
            Now);

    [Fact]
    public void Revenue_minus_cost_gives_margin_margin_percent_and_ARPA()
    {
        var input = Inputs(
            slots:
            [
                // STT with a provider cost: 2 USD on 5 Sep (rate 25,000) and 1 USD on 12 Sep (rate 26,000).
                Slot(Utc(9, 5, 3), "STT", "openai", Team, 1_000, 1_000, 2m),
                Slot(Utc(9, 12, 3), "STT", "openai", Pro, 500, 500, 1m),
            ],
            payments:
            [
                new PaidAmountRow(Utc(9, 5), "VND", 1_000_000m, W1, Team),
                new PaidAmountRow(Utc(9, 12), "USD", 20m, W2, Pro),   // at 12 Sep's 26,000 → 520,000
            ],
            workspaces: [new WorkspaceSlotRow(Utc(9, 5, 3), W1, Team, 1_000), new WorkspaceSlotRow(Utc(9, 12, 3), W3, Pro, 500)]);

        var period = ProfitAndLossCalculator.Compute(input, Utc(9, 1), Utc(9, 20));

        period.Revenue.Value.Should().Be(1_520_000m);
        period.AiCost.Value.Should().Be(2m * 25_000m + 1m * 26_000m, "each USD amount converts at the rate of its own day");
        period.AiCostUsd.Should().Be(3m);
        period.GrossMargin.Value.Should().Be(1_520_000m - 76_000m);
        period.GrossMarginPercent.Value.Should().Be(95.0m);
        period.ActiveWorkspaces.Should().Be(3, "W1 paid and used, W2 paid, W3 used");
        period.Arpa.Value.Should().Be(506_667m);

        var team = period.Plans.Single(p => p.PlanId == Team);
        team.RevenueVnd.Should().Be(1_000_000m);
        team.CostVnd.Should().Be(50_000m);
        period.FxUsed.Select(r => r.Rate).Distinct().Should().BeEquivalentTo(new decimal?[] { 25_000m, 26_000m });
    }

    [Fact]
    public void Translation_has_no_provider_price_so_the_cost_says_what_it_leaves_out_and_the_margin_is_flagged()
    {
        var input = Inputs(
            slots:
            [
                Slot(Utc(9, 5), "STT", "openai", Team, 1_000, 1_000, 1m),
                Slot(Utc(9, 5), "TRANSLATION", "openai", Team, 3_000, 0, 0m),
            ],
            payments: [new PaidAmountRow(Utc(9, 5), "VND", 1_000_000m, W1, Team)]);

        var period = ProfitAndLossCalculator.Compute(input, Utc(9, 1), Utc(9, 20));

        period.CoveragePercent.Should().Be(25m);
        period.AiCost.Value.Should().Be(25_000m);
        period.AiCost.Note.Should().Contain("covers 25% of consumed credits").And.Contain("TRANSLATION (75%)");
        period.GrossMargin.Note.Should().Contain("overstated");
        var openAi = period.Providers.Single(p => p.Provider == "openai");
        openAi.Services.Should().Contain(s => s.ChargeType == "TRANSLATION" && s.CoveredCredits == 0);
    }

    [Fact]
    public void Usage_with_no_cost_at_all_is_null_never_zero()
    {
        var period = ProfitAndLossCalculator.Compute(
            Inputs(slots: [Slot(Utc(9, 5), "TRANSLATION", "openai", Team, 3_000, 0, 0m)]), Utc(9, 1), Utc(9, 20));

        period.AiCost.Value.Should().BeNull();
        period.AiCost.Note.Should().Contain("cannot be reconstructed");
        period.GrossMargin.Value.Should().BeNull();
        period.GrossMarginPercent.Value.Should().BeNull();
    }

    [Fact]
    public void No_revenue_means_no_margin_percent_and_no_ARPA_rather_than_a_fabricated_zero()
    {
        var period = ProfitAndLossCalculator.Compute(Inputs(), Utc(9, 1), Utc(9, 20));

        period.Revenue.Value.Should().Be(0m);
        period.AiCost.Value.Should().Be(0m);
        period.GrossMarginPercent.Value.Should().BeNull();
        period.Arpa.Value.Should().BeNull();
    }

    [Fact]
    public void A_currency_with_no_rate_is_excluded_and_named()
    {
        var period = ProfitAndLossCalculator.Compute(
            Inputs(payments: [new PaidAmountRow(Utc(9, 5), "EUR", 10m, W1, Team)]), Utc(9, 1), Utc(9, 20));

        period.Revenue.Value.Should().BeNull();
        period.Revenue.Note.Should().Contain("EUR");
    }

    [Fact]
    public void Measured_Cartesia_days_replace_the_rate_card_estimate_and_are_allocated_to_plans_by_dubbing_share()
    {
        var input = Inputs(
            slots:
            [
                // Rate-card estimates that the measured day replaces.
                Slot(Utc(9, 12, 2), "AUDIO_DUBBING_STANDARD", "cartesia", Team, 300, 300, 5m),
                Slot(Utc(9, 12, 9), "AUDIO_DUBBING_STANDARD", "cartesia", Pro, 100, 100, 5m),
            ],
            // 1,000,000 Cartesia dubbing credits on 12 Sep × 0.00004 = 40 USD.
            cartesia: [new CartesiaUsageDay(new DateOnly(2026, 9, 12), 1_000_000, 0, Utc(9, 13, 1))]);

        var period = ProfitAndLossCalculator.Compute(input, Utc(9, 1), Utc(9, 20));

        period.AiCostUsd.Should().Be(40m);
        period.AiCost.Value.Should().Be(40m * 26_000m);
        period.MeasuredDays.Should().Be(1);
        period.Plans.Single(p => p.PlanId == Team).CostUsd.Should().Be(30m);
        period.Plans.Single(p => p.PlanId == Pro).CostUsd.Should().Be(10m);
        period.Providers.Single().MeasuredUsd.Should().Be(40m);
    }

    [Fact]
    public void Measured_usage_with_no_WarpTalk_dubbing_that_day_is_a_cost_on_no_plan()
    {
        var period = ProfitAndLossCalculator.Compute(
            Inputs(cartesia: [new CartesiaUsageDay(new DateOnly(2026, 9, 12), 100_000, 0, Utc(9, 13, 1))]),
            Utc(9, 1), Utc(9, 20));

        period.AiCostUsd.Should().Be(4m);
        period.Plans.Should().ContainSingle(p => p.PlanId == null && p.CostUsd == 4m);
    }

    [Fact]
    public void Local_day_buckets_split_a_UTC_day_at_the_zone_offset()
    {
        // Vietnam: 11 Sep local = [10 Sep 17:00Z, 11 Sep 17:00Z), 12 Sep local = [11 Sep 17:00Z, 12 Sep 17:00Z).
        // Rows at 16:30Z and 17:00Z on 11 Sep UTC land on two different local days.
        var input = Inputs(slots:
        [
            Slot(Utc(9, 11, 16, 30), "STT", "openai", Team, 100, 100, 1m),
            Slot(Utc(9, 11, 17, 0), "STT", "openai", Team, 100, 100, 1m),
        ]);

        ProfitAndLossCalculator.Compute(input, Utc(9, 10, 17), Utc(9, 11, 17)).AiCostUsd.Should().Be(1m);
        ProfitAndLossCalculator.Compute(input, Utc(9, 11, 17), Utc(9, 12, 17)).AiCostUsd.Should().Be(1m);
    }
}
