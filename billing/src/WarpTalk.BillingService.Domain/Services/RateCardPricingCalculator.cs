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
///     = provider_unit_cost_usd * fx_rate_usd_vnd * markup_multiplier / credit_value_vnd
/// </summary>
public static class RateCardPricingCalculator
{
    /// <summary>Unit prices are stored at six decimals (e.g. 1.643750).</summary>
    public const int UnitPriceDecimals = 6;

    /// <summary>VND amounts are reported to two decimals.</summary>
    public const int MoneyDecimals = 2;

    /// <summary>Ratios are reported to four decimals (0.6000 = 60%).</summary>
    public const int RatioDecimals = 4;

    /// <summary>
    /// Prices <paramref name="quantity"/> units of a billing identity.
    /// Rounding is MidpointRounding.ToEven throughout, matching decimal's default, so a
    /// preview and a later derivation of the same inputs cannot disagree by a cent.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// When an input cannot produce a meaningful price: a non-positive credit value would
    /// divide by zero, and negative cost/markup/quantity are not real pricing inputs.
    /// </exception>
    public static RateCardPricingBreakdown Calculate(
        decimal providerUnitCostUsd,
        decimal fxRateUsdVnd,
        decimal markupMultiplier,
        decimal creditValueVnd,
        decimal quantity)
    {
        if (creditValueVnd <= 0)
            throw new ArgumentOutOfRangeException(nameof(creditValueVnd), "Credit value must be greater than zero.");
        if (fxRateUsdVnd <= 0)
            throw new ArgumentOutOfRangeException(nameof(fxRateUsdVnd), "FX rate must be greater than zero.");
        if (providerUnitCostUsd < 0)
            throw new ArgumentOutOfRangeException(nameof(providerUnitCostUsd), "Provider unit cost cannot be negative.");
        if (markupMultiplier < 0)
            throw new ArgumentOutOfRangeException(nameof(markupMultiplier), "Markup multiplier cannot be negative.");
        if (quantity < 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity cannot be negative.");

        var providerUnitCostVnd = providerUnitCostUsd * fxRateUsdVnd;
        var unitPriceCredits = Math.Round(
            providerUnitCostVnd * markupMultiplier / creditValueVnd, UnitPriceDecimals);

        var creditsCharged = Math.Round(unitPriceCredits * quantity, UnitPriceDecimals);
        var customerPriceVnd = Math.Round(creditsCharged * creditValueVnd, MoneyDecimals);
        var providerCostVnd = Math.Round(providerUnitCostVnd * quantity, MoneyDecimals);
        var marginVnd = Math.Round(customerPriceVnd - providerCostVnd, MoneyDecimals);

        // A zero customer price (free tier, or a zero-cost provider) has no meaningful
        // margin ratio; reporting 0 there beats dividing by zero or reporting NaN.
        var marginRatio = customerPriceVnd == 0
            ? 0m
            : Math.Round(marginVnd / customerPriceVnd, RatioDecimals);

        return new RateCardPricingBreakdown(
            unitPriceCredits,
            creditsCharged,
            customerPriceVnd,
            providerCostVnd,
            marginVnd,
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
    decimal CustomerPriceVnd,
    decimal ProviderCostVnd,
    decimal MarginVnd,
    decimal MarginRatio);
