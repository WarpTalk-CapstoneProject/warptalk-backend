using FluentAssertions;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Services;

namespace WarpTalk.BillingService.Tests.Domain;

/// <summary>
/// WT-208. The credit-economics formula:
///     unit price = provider_unit_cost_usd * fx_rate_usd_vnd * markup / credit_value_vnd
/// </summary>
public class RateCardPricingCalculatorTests
{
    private const decimal Fx = SubscriptionConstants.RateCardDefaults.FxRateUsdVnd;      // 26 300
    private const decimal CreditValue = SubscriptionConstants.RateCardDefaults.CreditValueVnd; // 4

    /// <summary>
    /// The strongest available check that the formula is the one production actually uses:
    /// the seeded STT rate card (openai / gpt-4o-transcribe) stores UnitPrice 1.643750, and
    /// its recorded provider cost and markup must reproduce exactly that number at the
    /// default FX rate and credit value.
    /// </summary>
    [Fact]
    public void Calculate_ReproducesTheSeededSttUnitPriceExactly()
    {
        var breakdown = RateCardPricingCalculator.Calculate(
            providerUnitCostUsd: 0.0001000000m,
            fxRateUsdVnd: Fx,
            markupMultiplier: 2.5m,
            creditValueVnd: CreditValue,
            quantity: 1m);

        breakdown.UnitPriceCredits.Should().Be(1.643750m);
    }

    [Fact]
    public void Calculate_CustomerPriceEqualsProviderCostTimesMarkup()
    {
        // Algebraically the credit value cancels out: customer price is just the provider
        // cost marked up. If a refactor ever breaks that, credits have stopped being a
        // pure unit of account and started moving revenue.
        const decimal costUsd = 0.002m;
        const decimal markup = 3m;
        const decimal quantity = 250m;

        var breakdown = RateCardPricingCalculator.Calculate(costUsd, Fx, markup, CreditValue, quantity);

        breakdown.CustomerPriceVnd.Should().Be(decimal.Round(costUsd * Fx * markup * quantity, 2));
        breakdown.ProviderCostVnd.Should().Be(decimal.Round(costUsd * Fx * quantity, 2));
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(2.5)]
    [InlineData(4.0)]
    public void Calculate_MarginRatioDependsOnlyOnTheMarkup(double markup)
    {
        var markupDecimal = (decimal)markup;

        var breakdown = RateCardPricingCalculator.Calculate(0.0005m, Fx, markupDecimal, CreditValue, 1_000m);

        // margin / price = (markup - 1) / markup, independent of cost, FX and credit value.
        var expected = decimal.Round((markupDecimal - 1m) / markupDecimal, RateCardPricingCalculator.RatioDecimals);
        breakdown.MarginRatio.Should().Be(expected);
    }

    [Fact]
    public void Calculate_MarkupOfOne_LeavesNoMargin()
    {
        var breakdown = RateCardPricingCalculator.Calculate(0.01m, Fx, 1m, CreditValue, 10m);

        breakdown.MarginVnd.Should().Be(0m);
        breakdown.MarginRatio.Should().Be(0m);
    }

    [Fact]
    public void Calculate_MarkupBelowOne_ReportsALoss()
    {
        // Selling below provider cost is a real (if undesirable) configuration; the preview
        // has to show it rather than clamp it to zero, otherwise the admin never sees it.
        var breakdown = RateCardPricingCalculator.Calculate(0.01m, Fx, 0.5m, CreditValue, 10m);

        breakdown.MarginVnd.Should().BeNegative();
        breakdown.MarginRatio.Should().BeNegative();
    }

    [Fact]
    public void Calculate_ZeroQuantity_IsZeroedWithoutDividingByZero()
    {
        var breakdown = RateCardPricingCalculator.Calculate(0.01m, Fx, 2.5m, CreditValue, 0m);

        breakdown.CreditsCharged.Should().Be(0m);
        breakdown.CustomerPriceVnd.Should().Be(0m);
        breakdown.MarginVnd.Should().Be(0m);
        breakdown.MarginRatio.Should().Be(0m);

        // Unit price is a property of the rate, not of the quantity, so it stays priced.
        breakdown.UnitPriceCredits.Should().BePositive();
    }

    [Fact]
    public void Calculate_ZeroProviderCost_ReportsNoMarginRatherThanNaN()
    {
        var breakdown = RateCardPricingCalculator.Calculate(0m, Fx, 2.5m, CreditValue, 100m);

        breakdown.CustomerPriceVnd.Should().Be(0m);
        breakdown.MarginRatio.Should().Be(0m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void Calculate_NonPositiveCreditValue_Throws(double creditValue)
    {
        var act = () => RateCardPricingCalculator.Calculate(0.01m, Fx, 2.5m, (decimal)creditValue, 1m);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("creditValueVnd");
    }

    [Fact]
    public void Calculate_NonPositiveFxRate_Throws()
    {
        var act = () => RateCardPricingCalculator.Calculate(0.01m, 0m, 2.5m, CreditValue, 1m);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("fxRateUsdVnd");
    }

    [Theory]
    [InlineData(-0.01, 2.5, 1, "providerUnitCostUsd")]
    [InlineData(0.01, -2.5, 1, "markupMultiplier")]
    [InlineData(0.01, 2.5, -1, "quantity")]
    public void Calculate_NegativeInput_Throws(double cost, double markup, double quantity, string parameter)
    {
        var act = () => RateCardPricingCalculator.Calculate(
            (decimal)cost, Fx, (decimal)markup, CreditValue, (decimal)quantity);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName(parameter);
    }

    [Fact]
    public void Calculate_RoundsToTheDocumentedPrecision()
    {
        var breakdown = RateCardPricingCalculator.Calculate(0.000123456789m, Fx, 2.3333m, CreditValue, 7m);

        breakdown.UnitPriceCredits.Should().Be(
            decimal.Round(breakdown.UnitPriceCredits, RateCardPricingCalculator.UnitPriceDecimals));
        breakdown.CustomerPriceVnd.Should().Be(
            decimal.Round(breakdown.CustomerPriceVnd, RateCardPricingCalculator.MoneyDecimals));
        breakdown.MarginRatio.Should().Be(
            decimal.Round(breakdown.MarginRatio, RateCardPricingCalculator.RatioDecimals));
    }
}
