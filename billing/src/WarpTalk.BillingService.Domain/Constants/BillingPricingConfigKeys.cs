namespace WarpTalk.BillingService.Domain.Constants;

/// <summary>
/// Keys of subscription.billing_pricing_config that hold USD (the accounting currency). Named once, so
/// the reader that prices a top-up and the admin service that edits the value cannot drift apart —
/// they were two string literals before, both still saying VND.
/// </summary>
public static class BillingPricingConfigKeys
{
    /// <summary>USD one credit is worth. Prices every credit top-up and the rate-card preview.</summary>
    public const string CreditValueUsd = "credit_value_usd";

    /// <summary>The lowest USD per credit a USD plan or contract may be sold at.</summary>
    public const string MinimumPricePerCreditUsd = "minimum_price_per_credit_usd";
}
