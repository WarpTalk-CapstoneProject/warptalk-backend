using System;

namespace WarpTalk.BillingService.Domain.Services;

/// <summary>
/// The credit-economics formula, in one place (WT-208).
///
/// Until now the formula existed only as a documentation string on the pricing-config
/// response — the admin computed a unit price somewhere else and typed the result in, so
/// nothing could check that the stored price and the stated formula agreed. Pricing
/// preview and any future server-side derivation both go through here.
///
/// unit price (credits per unit)
///     = provider_unit_cost_usd * markup_multiplier / credit_value_usd
///
/// No exchange rate: providers bill in USD and USD is the accounting currency, so the price
/// of a credit and the cost of a unit are already in the same money.
/// </summary>
public static class RateCardPricingCalculator
{
    /// <summary>Unit prices are stored at six decimals (e.g. 1.643750).</summary>
    public const int UnitPriceDecimals = 6;

    /// <summary>
    /// USD amounts are reported to six decimals. A credit is worth a fraction of a cent, so the two
    /// decimals VND used would round a single unit's price, cost and margin to zero.
    /// </summary>
    public const int MoneyDecimals = 6;

    /// <summary>Ratios are reported to four decimals (0.6000 = 60%).</summary>
    public const int RatioDecimals = 4;

    /// <summary>
    /// Prices <paramref name="quantity"/> units of a billing identity.
    /// Rounding is MidpointRounding.ToEven throughout, matching decimal's default, so a
    /// preview and a later derivation of the same inputs cannot disagree.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// When an input cannot produce a meaningful price: a non-positive credit value would
    /// divide by zero, and negative cost/markup/quantity are not real pricing inputs.
    /// </exception>
    public static RateCardPricingBreakdown Calculate(
        decimal providerUnitCostUsd,
        decimal markupMultiplier,
        decimal creditValueUsd,
        decimal quantity)
    {
        if (creditValueUsd <= 0)
            throw new ArgumentOutOfRangeException(nameof(creditValueUsd), "Credit value must be greater than zero.");
        if (providerUnitCostUsd < 0)
            throw new ArgumentOutOfRangeException(nameof(providerUnitCostUsd), "Provider unit cost cannot be negative.");
        if (markupMultiplier < 0)
            throw new ArgumentOutOfRangeException(nameof(markupMultiplier), "Markup multiplier cannot be negative.");
        if (quantity < 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity cannot be negative.");

        var unitPriceCredits = Math.Round(
            providerUnitCostUsd * markupMultiplier / creditValueUsd, UnitPriceDecimals);

        var creditsCharged = Math.Round(unitPriceCredits * quantity, UnitPriceDecimals);
        var customerPriceUsd = Math.Round(creditsCharged * creditValueUsd, MoneyDecimals);
        var providerCostUsd = Math.Round(providerUnitCostUsd * quantity, MoneyDecimals);
        var marginUsd = Math.Round(customerPriceUsd - providerCostUsd, MoneyDecimals);

        // A zero customer price (free tier, or a zero-cost provider) has no meaningful
        // margin ratio; reporting 0 there beats dividing by zero or reporting NaN.
        var marginRatio = customerPriceUsd == 0
            ? 0m
            : Math.Round(marginUsd / customerPriceUsd, RatioDecimals);

        return new RateCardPricingBreakdown(
            unitPriceCredits,
            creditsCharged,
            customerPriceUsd,
            providerCostUsd,
            marginUsd,
            marginRatio);
    }
}

/// <param name="UnitPriceCredits">Credits charged per single unit.</param>
/// <param name="CreditsCharged">
/// Credits for the whole quantity, unrounded to a whole credit. Settlement owns the
/// integer rounding — see the AI billing worker, which computes the integer
/// credits_consumed that reaches RecordUsageRequest.
/// </param>
public sealed record RateCardPricingBreakdown(
    decimal UnitPriceCredits,
    decimal CreditsCharged,
    decimal CustomerPriceUsd,
    decimal ProviderCostUsd,
    decimal MarginUsd,
    decimal MarginRatio);
