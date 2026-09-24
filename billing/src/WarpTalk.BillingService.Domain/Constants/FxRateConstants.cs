namespace WarpTalk.BillingService.Domain.Constants;

/// <summary>Vocabulary of subscription.fx_rates and of the USD→VND rate every VND report converts with.</summary>
public static class FxRateConstants
{
    public const string Usd = "USD";
    public const string Vnd = "VND";

    public static class Sources
    {
        /// <summary>Stripe FX Quotes API (POST /v1/fx_quotes, preview): the fee-exclusive base rate of the day.</summary>
        public const string StripeFxQuote = "stripe_fx_quote";

        /// <summary>The rate Stripe applied when it converted a VND charge into the account's USD balance.</summary>
        public const string StripeCharge = "stripe_charge";

        /// <summary>An admin's explicit override for that day.</summary>
        public const string Manual = "manual";

        /// <summary>Not a row: <c>billing_pricing_config.fx_rate_usd_vnd</c>, used only when no rate was ever recorded.</summary>
        public const string Configured = "configured";
    }

    /// <summary>billing_pricing_config: the effective USD→VND rate (kept in step with the rate table by the refresh).</summary>
    public const string RateConfigKey = "fx_rate_usd_vnd";

    /// <summary>billing_pricing_config: 1 = the admin overrides Stripe with <see cref="RateConfigKey"/>; 0 (default) = Stripe.</summary>
    public const string ManualConfigKey = "fx_rate_usd_vnd_manual";

    /// <summary>Precedence among rows of the same day: lower wins.</summary>
    public static int Precedence(string source) => source switch
    {
        Sources.Manual => 0,
        Sources.StripeFxQuote => 1,
        Sources.StripeCharge => 2,
        _ => 3,
    };
}
