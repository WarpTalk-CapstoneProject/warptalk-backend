using System;

namespace WarpTalk.BillingService.Domain.Entities;

/// <summary>
/// Admin-editable pricing parameter in subscription.billing_pricing_config
/// (FX rate, credit value, price floors, sales estimator weights). Keyed by
/// name, not by surrogate id — the key is the primary key in the database.
/// </summary>
public partial class BillingPricingConfig
{
    public string Key { get; set; } = null!;

    public decimal Value { get; set; }

    public DateTime UpdatedAt { get; set; }
}
