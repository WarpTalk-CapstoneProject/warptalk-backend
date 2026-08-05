using System;

namespace WarpTalk.BillingService.Domain.Entities;

/// <summary>
/// Admin-editable invoice policy value in subscription.billing_policy_config
/// (for example vat_rate). Separate table from billing_pricing_config so
/// invoice policy and pricing parameters can be governed independently.
/// </summary>
public partial class BillingPolicyConfig
{
    public string Key { get; set; } = null!;

    public decimal Value { get; set; }

    public DateTime UpdatedAt { get; set; }
}
